using System.Net;
using System.Net.Sockets;
using PadScreen.Core;

// Host di prova: invia un'immagine di test (ffmpeg testsrc2) all'app iPad e stampa l'input ricevuto.
// Uso:
//   dotnet run -- --discover                 cerca gli iPad via Bonjour
//   dotnet run -- --usb                      elenca i dispositivi usbmux
//   dotnet run -- --host 127.0.0.1 [--seconds 20] [--encoder libx264|h264_videotoolbox]

string? host = null;
int port = Proto.DefaultPort;
int seconds = 20;
string encoder = "h264_videotoolbox";
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--discover":
            var found = await MdnsBrowser.BrowseAsync(Proto.ServiceType, TimeSpan.FromSeconds(3));
            Console.WriteLine($"Trovati {found.Count} iPad:");
            foreach (var f in found) Console.WriteLine($"  {f.Name}  {f.Address}:{f.Port}");
            return 0;
        case "--usb":
            var devs = await UsbMux.ListDevicesAsync();
            Console.WriteLine($"Dispositivi usbmux: {devs.Count}");
            foreach (var d in devs) Console.WriteLine($"  id={d.DeviceId} serial={d.Serial} tipo={d.ConnectionType} pid=0x{d.ProductId:x}");
            return 0;
        case "--host": host = args[++i]; break;
        case "--port": port = int.Parse(args[++i]); break;
        case "--seconds": seconds = int.Parse(args[++i]); break;
        case "--encoder": encoder = args[++i]; break;
    }
}
if (host == null)
{
    Console.WriteLine("Specifica --host, --discover o --usb");
    return 1;
}

using var tcp = new TcpClient { NoDelay = true };
await tcp.ConnectAsync(IPAddress.Parse(host), port);
Console.WriteLine($"Connesso a {host}:{port}");
var session = new StreamSession(new TestHost(encoder));
session.Stats += s => Console.WriteLine($"[stats] {s.Fps:0} fps {s.Mbps:0.0} Mbit/s RTT {s.RttMs:0.0} ms {s.Width}x{s.Height}");
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
try
{
    await session.RunAsync(tcp.GetStream(), cts.Token);
    Console.WriteLine("Sessione terminata: " + session.EndReason);
}
catch (OperationCanceledException)
{
    Console.WriteLine("Fine del test (timeout)");
}
catch (Exception ex)
{
    Console.WriteLine("ERRORE: " + ex);
    return 2;
}
return 0;

sealed class TestHost(string encoder) : IStreamHost
{
    public string HostName => Environment.MachineName;

    public Task<CapturePlan> PrepareAsync(HelloInfo hello, Func<string, Task> status, CancellationToken ct)
    {
        status("Prova: preparo il video di test…");
        int w = hello.Width / 2 * 2, h = hello.Height / 2 * 2;
        const int fps = 60;
        return Task.FromResult(new CapturePlan
        {
            Width = w,
            Height = h,
            Fps = fps,
            EncoderLabel = encoder,
            FfmpegPath = "ffmpeg",
            BitrateKbps = 12000,
            BuildArgs = kbps =>
            {
                var a = new List<string>
                {
                    "-hide_banner", "-nostdin", "-loglevel", "warning",
                    "-re", "-f", "lavfi", "-i", $"testsrc2=size={w}x{h}:rate={fps}",
                    "-c:v", encoder,
                };
                if (encoder == "libx264") a.AddRange(["-preset", "ultrafast", "-tune", "zerolatency", "-pix_fmt", "yuv420p"]);
                else a.AddRange(["-realtime", "1", "-pix_fmt", "nv12"]);
                a.AddRange([
                    "-b:v", $"{kbps}k", "-maxrate", $"{kbps}k", "-bufsize", $"{kbps / 2}k",
                    "-g", $"{fps * 4}", "-bf", "0", "-profile:v", "high",
                    "-an", "-flush_packets", "1", "-flvflags", "no_duration_filesize", "-f", "flv", "pipe:1",
                ]);
                return a;
            },
        });
    }

    public void OnInput(IReadOnlyList<TouchSample> samples)
    {
        foreach (var s in samples)
            Console.WriteLine($"[input] {s.Kind} id={s.Id} {s.Phase} x={s.X:0.000} y={s.Y:0.000} p={s.Pressure:0.00} alt={s.Altitude:0.00} az={s.Azimuth:0.00}");
    }

    public void OnScroll(float dx, float dy) => Console.WriteLine($"[scroll] dx={dx:0.0} dy={dy:0.0}");

    public void OnEncoderFailed(CapturePlan plan, string stderrTail) => Console.WriteLine("[encoder failed] " + stderrTail);

    public void Log(string message) => Console.WriteLine("[log] " + message);
}
