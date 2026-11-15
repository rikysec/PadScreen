using System.Drawing;
using PadScreen.Core;

namespace PadScreen;

internal enum CapturePath
{
    Gpu,      // ddagrab → encoder hardware direttamente su texture D3D11 (più veloce)
    GpuQsv,   // ddagrab → mappatura su Intel Quick Sync
    Cpu,      // ddagrab → copia in RAM → conversione → encoder
    Gdi,      // gdigrab (ultima spiaggia, lento)
}

internal sealed record EncoderCandidate(string Id, string Label, string Codec, CapturePath Path);

/// <summary>Dove catturare: indici DXGI per ddagrab e area dello schermo per gdigrab.</summary>
internal sealed record CaptureSource(int AdapterIndex, int OutputIndex, Rectangle Bounds, bool HasDxgi);

/// <summary>Ricerca di FFmpeg, prova degli encoder e costruzione degli argomenti.</summary>
internal static class FfmpegSetup
{
    public static readonly EncoderCandidate[] All =
    {
        new("nvenc", "NVIDIA NVENC", "h264_nvenc", CapturePath.Gpu),
        new("amf", "AMD AMF", "h264_amf", CapturePath.Gpu),
        new("qsv", "Intel Quick Sync", "h264_qsv", CapturePath.GpuQsv),
        new("nvenc-cpu", "NVIDIA NVENC (copia in RAM)", "h264_nvenc", CapturePath.Cpu),
        new("amf-cpu", "AMD AMF (copia in RAM)", "h264_amf", CapturePath.Cpu),
        new("qsv-cpu", "Intel Quick Sync (copia in RAM)", "h264_qsv", CapturePath.Cpu),
        new("mf", "Media Foundation", "h264_mf", CapturePath.Cpu),
        new("x264", "Software x264", "libx264", CapturePath.Cpu),
        new("x264-gdi", "Software x264 (GDI)", "libx264", CapturePath.Gdi),
    };

    /// <summary>Preferenze mostrate nell'interfaccia → prefisso degli id da provare per primi.</summary>
    public static readonly (string Key, string Label)[] Preferences =
    {
        ("auto", "Automatico"),
        ("nvenc", "NVIDIA (NVENC)"),
        ("amf", "AMD (AMF)"),
        ("qsv", "Intel (Quick Sync)"),
        ("mf", "Media Foundation"),
        ("x264", "Software (x264)"),
    };

    public static IEnumerable<EncoderCandidate> Ordered(string preference)
    {
        var first = preference == "auto" ? Array.Empty<EncoderCandidate>() : All.Where(c => c.Id.StartsWith(preference)).ToArray();
        return first.Concat(All.Except(first));
    }

    public static string? Locate(string? custom)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(custom)) candidates.Add(custom);
        string baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, "ffmpeg.exe"));
        candidates.Add(Path.Combine(baseDir, "ffmpeg", "bin", "ffmpeg.exe"));
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.Combine(dir.Trim('"'), "ffmpeg.exe")); } catch { }
        }
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe"));
        try
        {
            string pkgs = Path.Combine(local, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(pkgs))
            {
                foreach (var d in Directory.GetDirectories(pkgs, "Gyan.FFmpeg*"))
                    candidates.AddRange(Directory.GetFiles(d, "ffmpeg.exe", SearchOption.AllDirectories));
            }
        }
        catch { }
        candidates.Add(@"C:\ffmpeg\bin\ffmpeg.exe");
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin", "ffmpeg.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Versione di FFmpeg e presenza del filtro ddagrab (FFmpeg 6.1 o successivo).</summary>
    public static async Task<(string Version, bool HasDdagrab)> InspectAsync(string exe, CancellationToken ct)
    {
        var (_, verOut) = await RunStdoutAsync(exe, new[] { "-hide_banner", "-version" }, ct);
        string version = verOut.Split('\n').FirstOrDefault()?.Trim() ?? "?";
        var (_, filters) = await RunStdoutAsync(exe, new[] { "-hide_banner", "-filters" }, ct);
        return (version, filters.Contains(" ddagrab ", StringComparison.Ordinal));
    }

    private static async Task<(int, string)> RunStdoutAsync(string exe, string[] args, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, await outTask + await errTask);
    }

    /// <summary>Prova gli encoder in ordine catturando davvero lo schermo per qualche fotogramma.</summary>
    public static async Task<EncoderCandidate?> ProbeAsync(string exe, CaptureSource src, int fps, string preference,
        bool hasDdagrab, Action<string> log, CancellationToken ct)
    {
        foreach (var c in Ordered(preference))
        {
            if (c.Path != CapturePath.Gdi && (!hasDdagrab || !src.HasDxgi)) continue;
            var args = BuildArgs(c, src, fps, 8000, probe: true);
            log($"Provo l'encoder {c.Label}…");
            var (code, err) = await FfmpegProcess.RunAsync(exe, args, TimeSpan.FromSeconds(20), ct);
            if (code == 0)
            {
                log($"Encoder scelto: {c.Label}");
                return c;
            }
            string reason = err.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? $"codice {code}";
            log($"  non disponibile: {reason}");
        }
        return null;
    }

    public static IReadOnlyList<string> BuildArgs(EncoderCandidate enc, CaptureSource src, int fps, int kbps, bool probe = false)
    {
        var a = new List<string> { "-hide_banner", "-nostdin", "-loglevel", probe ? "error" : "warning" };
        bool cpu = enc.Path is CapturePath.Cpu or CapturePath.Gdi;
        string pixFmt = enc.Codec == "libx264" ? "yuv420p" : "nv12";

        if (enc.Path == CapturePath.Gdi)
        {
            a.AddRange(new[]
            {
                "-f", "gdigrab", "-framerate", fps.ToString(), "-draw_mouse", "1",
                "-offset_x", src.Bounds.X.ToString(), "-offset_y", src.Bounds.Y.ToString(),
                "-video_size", $"{src.Bounds.Width}x{src.Bounds.Height}",
                "-i", "desktop",
                "-vf", $"scale=out_color_matrix=bt709:out_range=tv,format={pixFmt}",
            });
        }
        else
        {
            a.AddRange(new[] { "-init_hw_device", $"d3d11va=dx:{src.AdapterIndex}", "-filter_hw_device", "dx" });
            string chain = $"ddagrab=output_idx={src.OutputIndex}:framerate={fps}:draw_mouse=1";
            chain += enc.Path switch
            {
                CapturePath.GpuQsv => ",hwmap=derive_device=qsv,format=qsv,vpp_qsv=format=nv12",
                CapturePath.Cpu => $",hwdownload,format=bgra,scale=out_color_matrix=bt709:out_range=tv,format={pixFmt}",
                _ => "",
            };
            a.AddRange(new[] { "-filter_complex", chain });
        }

        int gop = fps * 4;
        string br = $"{kbps}k";
        string buf = $"{Math.Max(500, kbps / 2)}k";
        switch (enc.Codec)
        {
            case "h264_nvenc":
                a.AddRange(new[] { "-c:v", "h264_nvenc", "-preset", "p1", "-tune", "ull", "-zerolatency", "1",
                    "-rc", "cbr", "-b:v", br, "-maxrate", br, "-bufsize", buf, "-g", gop.ToString(), "-bf", "0", "-profile:v", "high" });
                break;
            case "h264_amf":
                a.AddRange(new[] { "-c:v", "h264_amf", "-usage", "ultralowlatency", "-quality", "speed",
                    "-rc", "cbr", "-b:v", br, "-maxrate", br, "-bufsize", buf, "-g", gop.ToString(), "-bf", "0", "-profile:v", "high" });
                break;
            case "h264_qsv":
                a.AddRange(new[] { "-c:v", "h264_qsv", "-preset", "veryfast", "-async_depth", "1",
                    "-b:v", br, "-maxrate", br, "-bufsize", buf, "-g", gop.ToString(), "-bf", "0", "-profile:v", "high" });
                break;
            case "h264_mf":
                a.AddRange(new[] { "-c:v", "h264_mf", "-hw_encoding", "1", "-scenario", "display_remoting",
                    "-rate_control", "cbr", "-b:v", br, "-g", gop.ToString() });
                break;
            default:
                a.AddRange(new[] { "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency",
                    "-b:v", br, "-maxrate", br, "-bufsize", buf, "-g", gop.ToString(), "-bf", "0", "-profile:v", "high" });
                break;
        }
        if (cpu)
            a.AddRange(new[] { "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv" });

        a.Add("-an");
        // La prova usa le stesse impostazioni della trasmissione (fps e FLV), scrivendo su NUL.
        if (probe) a.AddRange(new[] { "-frames:v", "20" });
        a.AddRange(new[] { "-flush_packets", "1", "-flvflags", "no_duration_filesize", "-f", "flv", probe ? "NUL" : "pipe:1" });
        return a;
    }
}
