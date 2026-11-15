using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace PadScreen.Core;

/// <summary>Cosa catturare e come codificarlo, deciso dall'host dopo l'HELLO dell'iPad.</summary>
public sealed class CapturePlan
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Fps { get; init; }
    public required string EncoderLabel { get; init; }
    public required string FfmpegPath { get; init; }
    public required int BitrateKbps { get; init; }
    /// <summary>Argomenti FFmpeg per un dato bitrate (kbit/s). L'output deve essere FLV su stdout.</summary>
    public required Func<int, IReadOnlyList<string>> BuildArgs { get; init; }
}

/// <summary>Parte dipendente dalla piattaforma: schermo, encoder, iniezione dell'input.</summary>
public interface IStreamHost
{
    string HostName { get; }
    Task<CapturePlan> PrepareAsync(HelloInfo hello, Func<string, Task> status, CancellationToken ct);
    void OnInput(IReadOnlyList<TouchSample> samples);
    void OnScroll(float dx, float dy);
    void OnEncoderFailed(CapturePlan plan, string stderrTail);
    void Log(string message);
}

public readonly record struct SessionStats(double Fps, double Mbps, double RttMs, int Width, int Height, string Encoder, int BitrateKbps);

/// <summary>
/// Una sessione con un iPad già collegato (TCP o tunnel USB): handshake, video, input, ping.
/// </summary>
public sealed class StreamSession
{
    private readonly IStreamHost _host;
    private readonly Channel<Out> _out = Channel.CreateUnbounded<Out>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _prepareLock = new(1, 1);
    private readonly object _restartLock = new();
    private MessageChannel _ch = null!;
    private CancellationTokenSource? _restartCts;
    private volatile CapturePlan? _plan;
    private int _generation;
    private int _pendingVideo;
    private int _kbps;
    private long _lastReceiveTicks;
    private long _lastKeyframeRestartTicks;
    private double _rttMs;
    private int _framesSent;
    private long _bytesSent;

    private readonly record struct Out(MsgType Type, byte[] Payload, int Generation);

    public StreamSession(IStreamHost host) => _host = host;

    public event Action<SessionStats>? Stats;
    public HelloInfo? Hello { get; private set; }
    public string EndReason { get; private set; } = "";
    /// <summary>True dopo che almeno un fotogramma è arrivato all'iPad.</summary>
    public bool HasStreamed { get; private set; }

    public async Task RunAsync(Stream transport, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        var ct = cts.Token;
        _ch = new MessageChannel(transport);
        _lastReceiveTicks = Stopwatch.GetTimestamp();

        // 1) HELLO dall'iPad
        Message first;
        using (var t = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            t.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                first = await _ch.ReadAsync(t.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("L'iPad non ha risposto: l'app PadScreen è aperta e in primo piano?");
            }
        }
        if (first.Type != MsgType.Hello)
            throw new InvalidDataException("Risposta inattesa: sull'iPad non c'è l'app PadScreen?");
        Hello = ParseHello(first.Payload);
        _host.Log($"iPad collegato: {Hello}");

        // 2) Cicli di servizio sempre attivi (anche mentre si prepara lo schermo)
        var background = new List<Task>
        {
            WriterLoopAsync(ct),
            ReaderLoopAsync(ct),
            PingLoopAsync(ct),
            StatsLoopAsync(ct),
        };

        Task? failed = null;
        Task? prepare = null;
        try
        {
            prepare = PrepareAndConfigureAsync(Hello, ct);
            var early = await Task.WhenAny(background.Append(prepare)).ConfigureAwait(false);
            if (early != prepare)
            {
                failed = early;
            }
            else
            {
                try
                {
                    await prepare.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await TryWriteDirectAsync(MsgType.Status, Encoding.UTF8.GetBytes("Errore sul PC: " + ex.Message)).ConfigureAwait(false);
                    throw new PrepareFailedException(ex.Message, ex);
                }
                // 3) Video
                background.Add(VideoLoopAsync(ct));
                failed = await Task.WhenAny(background).ConfigureAwait(false);
            }
        }
        finally
        {
            if (outer.IsCancellationRequested)
                await TryWriteDirectAsync(MsgType.Bye, Array.Empty<byte>()).ConfigureAwait(false);
            cts.Cancel();
            _out.Writer.TryComplete();
            lock (_restartLock) _restartCts?.Cancel();
            // Aspettiamo anche la preparazione: non deve continuare in parallelo alla sessione successiva.
            var all = prepare == null ? background : background.Append(prepare);
            try { await Task.WhenAll(all).ConfigureAwait(false); } catch { }
        }

        if (failed is { IsFaulted: true })
            throw failed.Exception!.InnerException ?? failed.Exception;
    }

    private static HelloInfo ParseHello(byte[] payload) =>
        JsonSerializer.Deserialize<HelloInfo>(payload, Proto.Json) ?? throw new InvalidDataException("HELLO non valido");

    private async Task TryWriteDirectAsync(MsgType type, byte[] payload)
    {
        try
        {
            using var t = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
            await _ch.WriteAsync(type, payload, t.Token).ConfigureAwait(false);
        }
        catch { }
    }

    private void EnqueueControl(MsgType type, byte[] payload) =>
        _out.Writer.TryWrite(new Out(type, payload, -1));

    private Task EnqueueStatus(string text)
    {
        EnqueueControl(MsgType.Status, Encoding.UTF8.GetBytes(text));
        return Task.CompletedTask;
    }

    private async Task PrepareAndConfigureAsync(HelloInfo hello, CancellationToken ct)
    {
        await _prepareLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var plan = await _host.PrepareAsync(hello, EnqueueStatus, ct).ConfigureAwait(false);
            _plan = plan;
            if (Volatile.Read(ref _kbps) == 0) Volatile.Write(ref _kbps, plan.BitrateKbps);
            var cfg = new ConfigInfo
            {
                Width = plan.Width,
                Height = plan.Height,
                Fps = plan.Fps,
                Encoder = plan.EncoderLabel,
                Host = _host.HostName,
            };
            EnqueueControl(MsgType.Config, JsonSerializer.SerializeToUtf8Bytes(cfg, Proto.Json));
            RequestRestart();
        }
        finally
        {
            _prepareLock.Release();
        }
    }

    private void RequestRestart()
    {
        lock (_restartLock) _restartCts?.Cancel();
    }

    private async Task WriterLoopAsync(CancellationToken ct)
    {
        await foreach (var m in _out.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (m.Generation >= 0)
            {
                Interlocked.Decrement(ref _pendingVideo);
                if (m.Generation != Volatile.Read(ref _generation)) continue; // encoder vecchio: scarta
            }
            await _ch.WriteAsync(m.Type, m.Payload, ct).ConfigureAwait(false);
            if (m.Type == MsgType.VideoFrame)
            {
                Interlocked.Increment(ref _framesSent);
                HasStreamed = true;
                Interlocked.Add(ref _bytesSent, m.Payload.Length);
            }
        }
    }

    private async Task ReaderLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var m = await _ch.ReadAsync(ct).ConfigureAwait(false);
            Volatile.Write(ref _lastReceiveTicks, Stopwatch.GetTimestamp());
            switch (m.Type)
            {
                case MsgType.Touch:
                    if (_plan != null)
                    {
                        try { _host.OnInput(TouchSample.ParseList(m.Payload)); }
                        catch (Exception ex) { _host.Log("Errore input: " + ex.Message); }
                    }
                    break;
                case MsgType.Scroll:
                    if (_plan != null && m.Payload.Length >= 8)
                    {
                        try
                        {
                            _host.OnScroll(BinaryPrimitives.ReadSingleBigEndian(m.Payload),
                                           BinaryPrimitives.ReadSingleBigEndian(m.Payload.AsSpan(4)));
                        }
                        catch (Exception ex) { _host.Log("Errore scroll: " + ex.Message); }
                    }
                    break;
                case MsgType.Ping:
                    EnqueueControl(MsgType.Pong, m.Payload);
                    break;
                case MsgType.Pong:
                    if (m.Payload.Length == 8)
                    {
                        long sent = BinaryPrimitives.ReadInt64BigEndian(m.Payload);
                        _rttMs = Stopwatch.GetElapsedTime(sent).TotalMilliseconds;
                    }
                    break;
                case MsgType.Size:
                    try
                    {
                        var h = ParseHello(m.Payload);
                        Hello = h;
                        _host.Log($"L'iPad ha cambiato orientamento: {h.Width}×{h.Height}");
                        _ = Task.Run(async () =>
                        {
                            try { await PrepareAndConfigureAsync(h, ct).ConfigureAwait(false); }
                            catch (OperationCanceledException) { }
                            catch (Exception ex) { _host.Log("Riconfigurazione non riuscita: " + ex.Message); }
                        }, ct);
                    }
                    catch (Exception ex) { _host.Log("SIZE non valido: " + ex.Message); }
                    break;
                case MsgType.KeyframeRequest:
                    long now = Stopwatch.GetTimestamp();
                    if (Stopwatch.GetElapsedTime(Volatile.Read(ref _lastKeyframeRestartTicks), now) > TimeSpan.FromSeconds(1.5))
                    {
                        Volatile.Write(ref _lastKeyframeRestartTicks, now);
                        RequestRestart();
                    }
                    break;
                case MsgType.Bye:
                    EndReason = "L'iPad ha chiuso la sessione";
                    return;
            }
        }
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(1000, ct).ConfigureAwait(false);
            var payload = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(payload, Stopwatch.GetTimestamp());
            EnqueueControl(MsgType.Ping, payload);
            if (Stopwatch.GetElapsedTime(Volatile.Read(ref _lastReceiveTicks)) > TimeSpan.FromSeconds(10))
                throw new TimeoutException("L'iPad non risponde più");
        }
    }

    private async Task StatsLoopAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            await Task.Delay(1000, ct).ConfigureAwait(false);
            double secs = sw.Elapsed.TotalSeconds;
            sw.Restart();
            int frames = Interlocked.Exchange(ref _framesSent, 0);
            long bytes = Interlocked.Exchange(ref _bytesSent, 0);
            var plan = _plan;
            Stats?.Invoke(new SessionStats(
                frames / secs,
                bytes * 8 / secs / 1_000_000,
                _rttMs,
                plan?.Width ?? 0,
                plan?.Height ?? 0,
                plan?.EncoderLabel ?? "",
                Volatile.Read(ref _kbps)));
        }
    }

    private static int MaxPending(int fps) => Math.Max(8, fps / 2);

    private async Task VideoLoopAsync(CancellationToken ct)
    {
        int consecutiveFailures = 0;
        bool everStreamed = false;
        while (!ct.IsCancellationRequested)
        {
            var plan = _plan!;
            int kbps = Volatile.Read(ref _kbps);
            int gen = Interlocked.Increment(ref _generation);
            CancellationTokenSource restart;
            lock (_restartLock)
            {
                _restartCts?.Dispose();
                _restartCts = restart = new CancellationTokenSource();
            }
            using var run = CancellationTokenSource.CreateLinkedTokenSource(ct, restart.Token);

            _host.Log($"Avvio encoder: {plan.EncoderLabel}, {plan.Width}×{plan.Height} a {plan.Fps} fps, {kbps / 1000.0:0.#} Mbit/s");
            int frames = 0;
            bool overflow = false;
            bool restarted = false;
            string tail;
            var ff = FfmpegProcess.Start(plan.FfmpegPath, plan.BuildArgs(kbps));
            try
            {
                var flv = new FlvReader(ff.Stdout);
                await flv.ReadHeaderAsync(run.Token).ConfigureAwait(false);
                while (true)
                {
                    var pkt = await flv.ReadNextVideoAsync(run.Token).ConfigureAwait(false);
                    if (pkt == null) break;
                    Interlocked.Increment(ref _pendingVideo);
                    if (pkt.IsHeader)
                    {
                        _out.Writer.TryWrite(new Out(MsgType.VideoHeader, pkt.Data, gen));
                        continue;
                    }
                    var payload = new byte[5 + pkt.Data.Length];
                    payload[0] = (byte)((pkt.IsKeyframe ? 1 : 0) | (pkt.IsAnnexB ? 2 : 0));
                    BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(1), pkt.TimestampMs);
                    pkt.Data.CopyTo(payload, 5);
                    _out.Writer.TryWrite(new Out(MsgType.VideoFrame, payload, gen));
                    frames++;
                    if (Volatile.Read(ref _pendingVideo) > MaxPending(plan.Fps))
                    {
                        overflow = true;
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                restarted = true; // riavvio richiesto (keyframe, rotazione, nuova configurazione)
            }
            catch (OperationCanceledException)
            {
                // fine sessione
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException or InvalidDataException)
            {
                _host.Log("Flusso FFmpeg interrotto: " + ex.Message);
            }
            finally
            {
                await ff.DisposeAsync().ConfigureAwait(false);
                tail = ff.StderrTail;
            }

            if (ct.IsCancellationRequested) break;
            if (frames > 0) everStreamed = true;
            if (restarted) continue;
            if (overflow)
            {
                int newKbps = Math.Max(2000, (int)(kbps * 0.7));
                Volatile.Write(ref _kbps, newKbps);
                _host.Log($"Connessione troppo lenta (fotogrammi in coda): riduco il bitrate a {newKbps / 1000.0:0.#} Mbit/s");
                continue;
            }

            // FFmpeg si è fermato da solo
            consecutiveFailures = frames == 0 ? consecutiveFailures + 1 : 0;
            _host.Log("L'encoder si è fermato." + (string.IsNullOrWhiteSpace(tail) ? "" : Environment.NewLine + tail));
            if (!everStreamed && consecutiveFailures >= 3)
            {
                // Non ha mai funzionato in questa sessione: l'encoder scelto non va bene.
                _host.OnEncoderFailed(plan, tail);
                throw new InvalidOperationException("FFmpeg non riesce a catturare lo schermo. Dettagli nel registro.");
            }
            // Se prima funzionava, di solito è il blocco schermo o una richiesta UAC (desktop protetto,
            // non catturabile): riprova con pause crescenti finché il desktop torna disponibile.
            int delay = everStreamed ? Math.Min(5, 1 + consecutiveFailures) : 1;
            if (everStreamed && consecutiveFailures == 3)
                await TryWriteDirectAsync(MsgType.Status, Encoding.UTF8.GetBytes("Il PC è bloccato o mostra una richiesta di sicurezza: in attesa…")).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);
        }
    }
}

/// <summary>La preparazione (schermo/encoder) è fallita: ritentare subito non serve.</summary>
public sealed class PrepareFailedException(string message, Exception inner) : Exception(message, inner);
