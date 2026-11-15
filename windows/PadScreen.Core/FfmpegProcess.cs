using System.Diagnostics;

namespace PadScreen.Core;

/// <summary>Processo FFmpeg che scrive FLV su stdout. Lo stderr viene conservato per la diagnostica.</summary>
public sealed class FfmpegProcess : IAsyncDisposable
{
    private readonly Process _p;
    private readonly LinkedList<string> _tail = new();
    private readonly object _tailLock = new();

    private FfmpegProcess(Process p) => _p = p;

    public Stream Stdout => _p.StandardOutput.BaseStream;
    public bool HasExited => _p.HasExited;
    public int? ExitCode => _p.HasExited ? _p.ExitCode : null;

    public string StderrTail
    {
        get { lock (_tailLock) return string.Join(Environment.NewLine, _tail); }
    }

    public static FfmpegProcess Start(string exe, IEnumerable<string> args, Action<string>? onStderr = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var ff = new FfmpegProcess(p);
        p.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (ff._tailLock)
            {
                ff._tail.AddLast(e.Data);
                while (ff._tail.Count > 40) ff._tail.RemoveFirst();
            }
            onStderr?.Invoke(e.Data);
        };
        if (!p.Start()) throw new InvalidOperationException("Impossibile avviare FFmpeg");
        p.BeginErrorReadLine();
        return ff;
    }

    /// <summary>Esegue FFmpeg fino alla fine (per le prove degli encoder) e restituisce codice d'uscita e stderr.</summary>
    public static async Task<(int ExitCode, string Stderr)> RunAsync(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        p.Start();
        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            if (ct.IsCancellationRequested) throw;
            return (-1, "timeout");
        }
        string err = "";
        try { err = await stderrTask.ConfigureAwait(false); } catch { }
        try { await stdoutTask.ConfigureAwait(false); } catch { }
        return (p.ExitCode, err);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_p.HasExited) _p.Kill(true);
        }
        catch { }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch { }
        _p.Dispose();
    }
}
