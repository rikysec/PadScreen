using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using PadScreen.Core;

namespace PadScreen;

/// <summary>Un iPad raggiungibile: via USB (usbmux) o via rete.</summary>
internal sealed record PadTarget(bool Usb, string Name, string Address, int Port, int UsbDeviceId, string? Serial)
{
    public string Key => Usb ? $"usb:{Serial}" : $"tcp:{Address}:{Port}";
    public string Connection => Usb ? "Cavo USB" : "Wi-Fi / rete";
}

/// <summary>
/// Cuore del programma Windows: apre la connessione, prepara lo schermo (virtuale o duplicato),
/// sceglie l'encoder, inoltra l'input e si riconnette se il collegamento cade.
/// </summary>
internal sealed class AppController : IStreamHost, IDisposable
{
    private readonly Settings _settings;
    private readonly InputInjector _input;
    private readonly object _logLock = new();
    private readonly object _displayLock = new();
    private ParsecVdd? _vdd;
    private int? _vddIndex;
    private string? _vddDevice;
    private (int W, int H)? _vddMode;
    private CancellationTokenSource? _connCts;
    private Task? _connTask;
    private string? _ffmpeg;
    private bool _hasDdagrab = true;
    private string _ffmpegVersion = "";

    public event Action<string>? LogLine;
    public event Action<string>? StatusChanged;
    public event Action<SessionStats?>? StatsChanged;
    public event Action<bool>? RunningChanged;

    public AppController(Settings settings)
    {
        _settings = settings;
        _input = new InputInjector(Log);
    }

    public bool IsRunning => _connTask is { IsCompleted: false };
    public string HostName => Environment.MachineName;
    public string? FfmpegPath => _ffmpeg;
    public string FfmpegVersion => _ffmpegVersion;

    public static string LogFile => Path.Combine(Settings.Folder, "padscreen.log");

    // ---------- FFmpeg ----------

    public async Task<bool> RefreshFfmpegAsync()
    {
        _ffmpeg = FfmpegSetup.Locate(_settings.FfmpegPath);
        if (_ffmpeg == null)
        {
            _ffmpegVersion = "";
            return false;
        }
        try
        {
            (_ffmpegVersion, _hasDdagrab) = await FfmpegSetup.InspectAsync(_ffmpeg, CancellationToken.None);
            if (!_hasDdagrab)
                Log("Attenzione: questa versione di FFmpeg non ha «ddagrab» (serve FFmpeg 6.1 o successivo). Userò la cattura GDI, più lenta.");
            return true;
        }
        catch (Exception ex)
        {
            Log("FFmpeg trovato ma non eseguibile: " + ex.Message);
            _ffmpeg = null;
            return false;
        }
    }

    // ---------- Ricerca iPad ----------

    public async Task<List<PadTarget>> DiscoverAsync()
    {
        var list = new List<PadTarget>();
        var usbTask = Task.Run(async () =>
        {
            try { return await UsbMux.ListDevicesAsync(); }
            catch (Exception ex) { return (object)ex.Message; }
        });
        var wifiTask = MdnsBrowser.BrowseAsync(Proto.ServiceType, TimeSpan.FromSeconds(2.5));

        var usb = await usbTask;
        if (usb is List<UsbDevice> devs)
        {
            foreach (var d in devs.Where(d => d.ConnectionType.Equals("USB", StringComparison.OrdinalIgnoreCase)))
            {
                string shortSerial = d.Serial.Length > 8 ? d.Serial[^8..] : d.Serial;
                list.Add(new PadTarget(true, $"Dispositivo Apple …{shortSerial}", "USB", Proto.DefaultPort, d.DeviceId, d.Serial));
            }
        }
        try
        {
            foreach (var p in await wifiTask)
                list.Add(new PadTarget(false, p.Name, p.Address.ToString(), p.Port, 0, null));
        }
        catch (Exception ex)
        {
            Log("Ricerca Wi-Fi non riuscita: " + ex.Message);
        }
        return list;
    }

    // ---------- Connessione ----------

    public void Start(PadTarget target)
    {
        if (IsRunning) return;
        _connCts = new CancellationTokenSource();
        var ct = _connCts.Token;
        _connTask = Task.Run(() => ConnectionLoopAsync(target, ct));
        RunningChanged?.Invoke(true);
    }

    public async Task StopAsync()
    {
        _connCts?.Cancel();
        if (_connTask != null)
        {
            try { await _connTask.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false); } catch { }
        }
    }

    private async Task ConnectionLoopAsync(PadTarget target, CancellationToken ct)
    {
        bool everConnected = false;
        DateTime? giveUpAt = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Stream? stream = null;
                try
                {
                    SetStatus($"Connessione a {target.Name} ({target.Connection})…");
                    stream = await OpenTransportAsync(target, ct);
                    everConnected = true;
                    SetStatus($"Collegato a {target.Name} — preparo lo schermo…");
                    var session = new StreamSession(this);
                    session.Stats += s =>
                    {
                        StatsChanged?.Invoke(s);
                        if (s.Fps > 0) SetStatusQuiet($"In trasmissione su {target.Name} ({target.Connection})");
                    };
                    try
                    {
                        await session.RunAsync(stream, ct);
                    }
                    finally
                    {
                        // Il minuto di tentativi riparte solo se la sessione ha davvero trasmesso.
                        if (session.HasStreamed) giveUpAt = null;
                    }
                    Log("Sessione terminata" + (string.IsNullOrEmpty(session.EndReason) ? "" : ": " + session.EndReason));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (PrepareFailedException ex)
                {
                    // Driver, FFmpeg o encoder mancanti: ritentare non serve.
                    SetStatus("Impossibile avviare: " + ex.Message);
                    return;
                }
                catch (Exception ex)
                {
                    Log("Errore: " + ex.Message);
                    if (!everConnected)
                    {
                        SetStatus("Connessione non riuscita: " + ex.Message);
                        return;
                    }
                }
                finally
                {
                    _input.ReleaseAll();
                    StatsChanged?.Invoke(null);
                    if (stream != null) await stream.DisposeAsync();
                }

                if (ct.IsCancellationRequested) break;
                giveUpAt ??= DateTime.UtcNow.AddSeconds(60);
                if (DateTime.UtcNow > giveUpAt)
                {
                    SetStatus("Collegamento perso: iPad non raggiungibile.");
                    return;
                }
                SetStatus("Collegamento perso — riprovo… (riapri PadScreen sull'iPad se è in background)");
                await Task.Delay(2000, ct);
                if (target.Usb) target = await RefreshUsbTargetAsync(target);
            }
            SetStatus("Disconnesso.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Disconnesso.");
        }
        finally
        {
            RemoveVirtualDisplay();
            RunningChanged?.Invoke(false);
        }
    }

    private static async Task<PadTarget> RefreshUsbTargetAsync(PadTarget t)
    {
        try
        {
            var devs = await UsbMux.ListDevicesAsync();
            var same = devs.FirstOrDefault(d => d.Serial == t.Serial);
            if (same != null && same.DeviceId != t.UsbDeviceId) return t with { UsbDeviceId = same.DeviceId };
        }
        catch { }
        return t;
    }

    private static async Task<Stream> OpenTransportAsync(PadTarget t, CancellationToken ct)
    {
        if (t.Usb) return await UsbMux.ConnectAsync(t.UsbDeviceId, t.Port, ct);

        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            if (IPAddress.TryParse(t.Address, out var ip))
                await tcp.ConnectAsync(ip, t.Port, timeout.Token);
            else
                await tcp.ConnectAsync(t.Address, t.Port, timeout.Token);
            return tcp.GetStream();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new TimeoutException($"{t.Address}:{t.Port} non risponde (stessa rete? app aperta sull'iPad?)");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    // ---------- IStreamHost ----------

    public async Task<CapturePlan> PrepareAsync(HelloInfo hello, Func<string, Task> status, CancellationToken ct)
    {
        if (_ffmpeg == null && !await RefreshFfmpegAsync())
            throw new InvalidOperationException("FFmpeg non trovato. Installalo dalla finestra di PadScreen (pulsante «Installa FFmpeg»).");
        string ffmpeg = _ffmpeg!;

        string device;
        if (_settings.VirtualDisplay)
        {
            await status("Preparo lo schermo virtuale…");
            int w = hello.Width, h = hello.Height;
            if (_settings.HalfResolution) { w /= 2; h /= 2; }
            w &= ~1; h &= ~1;
            device = await Task.Run(() => EnsureVirtualDisplay(w, h, _settings.Fps, ct), ct);
        }
        else
        {
            var mons = Displays.GetAttached();
            var mon = mons.FirstOrDefault(m => m.DeviceName == _settings.MirrorDevice) ?? mons.FirstOrDefault(m => m.Primary) ?? mons.FirstOrDefault()
                      ?? throw new InvalidOperationException("Nessun monitor trovato.");
            device = mon.DeviceName;
            Log($"Duplico il monitor {mon}");
        }

        Rectangle bounds = Rectangle.Empty;
        (int Adapter, int Output, string AdapterName)? dxgi = null;
        for (int i = 0; i < 30 && !ct.IsCancellationRequested; i++)
        {
            bounds = Displays.GetBounds(device) ?? Rectangle.Empty;
            try { dxgi = Displays.FindDxgiOutput(device); } catch (Exception ex) { Log("DXGI: " + ex.Message); }
            if (bounds.Width > 0 && dxgi != null) break;
            await Task.Delay(200, ct);
        }
        if (bounds.Width == 0) throw new InvalidOperationException($"Lo schermo {device} non è attivo.");
        if (dxgi == null) Log($"Uscita DXGI per {device} non trovata: userò la cattura GDI.");
        else Log($"Schermo {device}: {bounds.Width}×{bounds.Height} in ({bounds.X},{bounds.Y}), scheda video #{dxgi.Value.Adapter} «{dxgi.Value.AdapterName}», uscita #{dxgi.Value.Output}");

        string dev = device;
        Rectangle lastBounds = bounds;
        _input.MouseMode = _settings.MouseMode;
        _input.SetTarget(() =>
        {
            var b = Displays.GetBounds(dev);
            if (b != null) lastBounds = b.Value;
            return lastBounds;
        });

        var src = new CaptureSource(dxgi?.Adapter ?? 0, dxgi?.Output ?? 0, bounds, dxgi != null);
        string cacheKey = $"{dxgi?.AdapterName ?? "gdi"}|{_settings.Encoder}|{_hasDdagrab}";
        EncoderCandidate? enc = null;
        if (_settings.EncoderCache.TryGetValue(cacheKey, out var cachedId))
            enc = FfmpegSetup.All.FirstOrDefault(c => c.Id == cachedId && (c.Path == CapturePath.Gdi || src.HasDxgi));
        if (enc == null)
        {
            await status("Cerco l'encoder video migliore per questo PC (solo la prima volta)…");
            enc = await FfmpegSetup.ProbeAsync(ffmpeg, src, _settings.Fps, _settings.Encoder, _hasDdagrab, Log, ct)
                  ?? throw new InvalidOperationException("Nessun encoder H.264 funziona con questo schermo. Dettagli nel registro.");
            _settings.EncoderCache[cacheKey] = enc.Id;
            _settings.Save();
        }

        int fps = _settings.Fps;
        if (enc.Codec == "libx264" && (long)bounds.Width * bounds.Height > 2_500_000) fps = Math.Min(fps, 30);
        var encoder = enc;
        await status($"Avvio la trasmissione ({encoder.Label})…");
        return new CapturePlan
        {
            Width = bounds.Width,
            Height = bounds.Height,
            Fps = fps,
            EncoderLabel = encoder.Label,
            FfmpegPath = ffmpeg,
            BitrateKbps = Math.Clamp(_settings.BitrateMbps, 2, 150) * 1000,
            BuildArgs = kbps => FfmpegSetup.BuildArgs(encoder, src, fps, kbps),
        };
    }

    public void OnInput(IReadOnlyList<TouchSample> samples) => _input.Handle(samples);

    public void OnScroll(float dx, float dy) => _input.Scroll(dx, dy);

    public void OnEncoderFailed(CapturePlan plan, string stderrTail)
    {
        // Al prossimo tentativo si rifà la prova degli encoder.
        var stale = _settings.EncoderCache.Where(kv => FfmpegSetup.All.Any(c => c.Id == kv.Value && c.Label == plan.EncoderLabel)).Select(kv => kv.Key).ToList();
        foreach (var k in stale) _settings.EncoderCache.Remove(k);
        _settings.Save();
    }

    // ---------- Schermo virtuale ----------

    private string EnsureVirtualDisplay(int w, int h, int fps, CancellationToken ct)
    {
        // Una sola preparazione alla volta (es. riconnessione mentre la precedente è ancora in corso).
        lock (_displayLock)
        {
            ct.ThrowIfCancellationRequested();
            _vdd ??= ParsecVdd.Open();

            var modes = new List<(int, int)> { (w, h), (h, w) };
            if (!ParsecModes.ContainsAll(modes))
            {
                Log($"Registro la risoluzione {w}×{h} nel driver (Windows chiederà il permesso di amministratore)…");
                if (RegisterModesElevated(modes, ct))
                {
                    Log("Risoluzione registrata.");
                    if (_vddIndex != null) RemoveVirtualDisplayLocked(); // il driver legge le risoluzioni quando lo schermo viene collegato
                }
                else
                {
                    Log("Risoluzione non registrata: userò quella disponibile più simile.");
                }
            }

            if (_vddIndex == null || _vddDevice == null || Displays.GetBounds(_vddDevice) == null)
            {
                if (_vddIndex != null) RemoveVirtualDisplayLocked();
                var before = Displays.ParsecDevices().Where(d => d.Attached).Select(d => d.Name).ToHashSet();
                _vddIndex = _vdd.AddDisplay();
                Log($"Schermo virtuale collegato (indice {_vddIndex}).");
                string? found = null;
                for (int i = 0; i < 60 && found == null; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    Thread.Sleep(150);
                    found = Displays.ParsecDevices().Where(d => d.Attached && !before.Contains(d.Name)).Select(d => d.Name).FirstOrDefault();
                    if (found == null && i == 20)
                    {
                        // Collegato ma non esteso (es. Windows in modalità "duplica" o "solo secondo schermo").
                        Log("Lo schermo virtuale non è stato esteso: imposto la modalità «Estendi» di Windows.");
                        int r = Native.SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, Native.SDC_APPLY | Native.SDC_TOPOLOGY_EXTEND);
                        if (r != 0) Log($"SetDisplayConfig: codice {r}");
                    }
                }
                found ??= Displays.ParsecDevices().Where(d => d.Attached).Select(d => d.Name).LastOrDefault();
                _vddDevice = found ?? throw new InvalidOperationException("Lo schermo virtuale non è comparso in Windows.");
                _vddMode = null;
            }

            if (_vddMode != (w, h))
            {
                var mode = Displays.PickMode(_vddDevice, w, h, fps);
                if (mode != null)
                {
                    int r = Displays.SetMode(_vddDevice, mode.Value.W, mode.Value.H, mode.Value.Hz);
                    Log(r == Native.DISP_CHANGE_SUCCESSFUL
                        ? $"Risoluzione schermo virtuale: {mode.Value.W}×{mode.Value.H} @ {mode.Value.Hz} Hz"
                        : $"Cambio risoluzione non riuscito (codice {r}).");
                    if (mode.Value.W != w || mode.Value.H != h)
                        Log($"Nota: {w}×{h} non disponibile, uso {mode.Value.W}×{mode.Value.H} (bande nere sull'iPad).");
                }
                _vddMode = (w, h);
            }
            return _vddDevice;
        }
    }

    private void RemoveVirtualDisplay()
    {
        lock (_displayLock) RemoveVirtualDisplayLocked();
    }

    private void RemoveVirtualDisplayLocked()
    {
        if (_vdd != null && _vddIndex != null)
        {
            string? dev = _vddDevice;
            try
            {
                _vdd.RemoveDisplay(_vddIndex.Value);
                // Aspetta che Windows lo stacchi davvero, così il prossimo "nuovo schermo" si riconosce subito.
                for (int i = 0; i < 20 && dev != null && Displays.ParsecDevices().Any(d => d.Name == dev && d.Attached); i++)
                    Thread.Sleep(100);
                Log("Schermo virtuale rimosso.");
            }
            catch (Exception ex)
            {
                Log("Rimozione schermo virtuale: " + ex.Message);
            }
        }
        _vddIndex = null;
        _vddDevice = null;
        _vddMode = null;
    }

    private bool RegisterModesElevated(List<(int W, int H)> modes, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!, "--register-modes " + string.Join(",", modes.Select(m => $"{m.W}x{m.H}")))
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            for (int i = 0; i < 300 && !p.WaitForExit(200); i++)
                ct.ThrowIfCancellationRequested();
            return p.HasExited && p.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false; // l'utente ha rifiutato l'UAC
        }
    }

    // ---------- Registro ----------

    public void Log(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss} {message}";
        lock (_logLock)
        {
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                var fi = new FileInfo(LogFile);
                if (fi.Exists && fi.Length > 2_000_000) fi.Delete();
                File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd} {line}{Environment.NewLine}");
            }
            catch { }
        }
        LogLine?.Invoke(line);
    }

    private void SetStatus(string s)
    {
        _lastQuietStatus = null;
        StatusChanged?.Invoke(s);
        Log(s);
    }

    private string? _lastQuietStatus;

    /// <summary>Aggiorna lo stato senza riempire il registro (chiamato ogni secondo).</summary>
    private void SetStatusQuiet(string s)
    {
        if (s == _lastQuietStatus) return;
        _lastQuietStatus = s;
        StatusChanged?.Invoke(s);
    }

    public void Dispose()
    {
        try { StopAsync().Wait(TimeSpan.FromSeconds(5)); } catch { }
        RemoveVirtualDisplay();
        _vdd?.Dispose();
        _input.Dispose();
    }
}
