using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace PadScreen.Core;

public sealed record UsbDevice(int DeviceId, string Serial, string ConnectionType, int ProductId);

/// <summary>
/// Client del protocollo usbmux (plist) per aprire un tunnel TCP verso una porta dell'iPad via cavo.
/// Su Windows parla con "Apple Mobile Device Service" (127.0.0.1:27015, installato con
/// l'app Dispositivi Apple o iTunes); su macOS con /var/run/usbmuxd.
/// </summary>
public static class UsbMux
{
    private const int WindowsPort = 27015;
    private static uint _tag;

    public static async Task<List<UsbDevice>> ListDevicesAsync(CancellationToken ct = default)
    {
        await using var s = await OpenAsync(ct).ConfigureAwait(false);
        uint tag = Interlocked.Increment(ref _tag);
        await SendAsync(s, Plist(("MessageType", "ListDevices")), tag, ct).ConfigureAwait(false);
        var reply = await ReceiveAsync(s, ct).ConfigureAwait(false);
        var list = new List<UsbDevice>();
        if (reply.TryGetValue("DeviceList", out var dl) && dl is List<object?> arr)
        {
            foreach (var item in arr)
            {
                if (item is not Dictionary<string, object?> dev) continue;
                if (dev.GetValueOrDefault("Properties") is not Dictionary<string, object?> props) continue;
                int id = ToInt(dev.GetValueOrDefault("DeviceID") ?? props.GetValueOrDefault("DeviceID"));
                string serial = props.GetValueOrDefault("SerialNumber") as string ?? "";
                string conn = props.GetValueOrDefault("ConnectionType") as string ?? "USB";
                int pid = ToInt(props.GetValueOrDefault("ProductID"));
                if (id > 0) list.Add(new UsbDevice(id, serial, conn, pid));
            }
        }
        return list;
    }

    /// <summary>Apre un tunnel verso <paramref name="port"/> sul dispositivo. Lo Stream restituito è la connessione TCP.</summary>
    public static async Task<Stream> ConnectAsync(int deviceId, int port, CancellationToken ct = default)
    {
        var s = await OpenAsync(ct).ConfigureAwait(false);
        try
        {
            int portBE = ((port & 0xFF) << 8) | ((port >> 8) & 0xFF);
            uint tag = Interlocked.Increment(ref _tag);
            await SendAsync(s, Plist(("MessageType", "Connect"), ("DeviceID", deviceId), ("PortNumber", portBE)), tag, ct).ConfigureAwait(false);
            var reply = await ReceiveAsync(s, ct).ConfigureAwait(false);
            int result = ToInt(reply.GetValueOrDefault("Number"));
            if (result != 0)
            {
                string why = result switch
                {
                    2 => "iPad non più collegato",
                    3 => "l'app PadScreen non è aperta sull'iPad (porta chiusa)",
                    5 => "richiesta non valida",
                    _ => $"codice {result}",
                };
                throw new IOException($"Connessione USB rifiutata: {why}");
            }
            return s;
        }
        catch
        {
            await s.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<Stream> OpenAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            var tcp = new TcpClient { NoDelay = true };
            try
            {
                using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
                t.CancelAfter(TimeSpan.FromSeconds(3));
                await tcp.ConnectAsync(IPAddress.Loopback, WindowsPort, t.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                tcp.Dispose();
                throw new IOException("Servizio Apple Mobile Device non trovato: installa l'app «Dispositivi Apple» (Microsoft Store) o iTunes.", ex);
            }
            return tcp.GetStream();
        }
        var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await sock.ConnectAsync(new UnixDomainSocketEndPoint("/var/run/usbmuxd"), ct).ConfigureAwait(false);
        }
        catch
        {
            sock.Dispose();
            throw;
        }
        return new NetworkStream(sock, ownsSocket: true);
    }

    private static async Task SendAsync(Stream s, string xml, uint tag, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(xml);
        var hdr = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(hdr, (uint)(16 + payload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(4), 1);  // versione: plist
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(8), 8);  // messaggio: plist
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(12), tag);
        await s.WriteAsync(hdr, ct).ConfigureAwait(false);
        await s.WriteAsync(payload, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<Dictionary<string, object?>> ReceiveAsync(Stream s, CancellationToken ct)
    {
        using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
        t.CancelAfter(TimeSpan.FromSeconds(5));
        var hdr = new byte[16];
        await s.ReadExactlyAsync(hdr, t.Token).ConfigureAwait(false);
        int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(hdr);
        if (len < 16 || len > 4 * 1024 * 1024) throw new InvalidDataException("Risposta usbmux non valida");
        var body = new byte[len - 16];
        await s.ReadExactlyAsync(body, t.Token).ConfigureAwait(false);
        return ParsePlist(body);
    }

    private static string Plist(params (string Key, object Value)[] entries)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        sb.Append("<plist version=\"1.0\">\n<dict>\n");
        foreach (var (k, v) in entries.Append(("ClientVersionString", (object)"PadScreen 1.0")).Append(("ProgName", (object)"PadScreen")))
        {
            sb.Append("\t<key>").Append(k).Append("</key>\n");
            sb.Append(v is int i ? $"\t<integer>{i}</integer>\n" : $"\t<string>{System.Security.SecurityElement.Escape(v.ToString())}</string>\n");
        }
        sb.Append("</dict>\n</plist>\n");
        return sb.ToString();
    }

    internal static Dictionary<string, object?> ParsePlist(byte[] xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var reader = XmlReader.Create(new MemoryStream(xml), settings);
        var doc = XDocument.Load(reader);
        var root = doc.Root?.Elements().FirstOrDefault();
        return root != null && ParseValue(root) is Dictionary<string, object?> d ? d : new();
    }

    private static object? ParseValue(XElement e) => e.Name.LocalName switch
    {
        "dict" => ParseDict(e),
        "array" => e.Elements().Select(ParseValue).ToList(),
        "string" => e.Value,
        "integer" => long.TryParse(e.Value, out var l) ? l : 0L,
        "true" => true,
        "false" => false,
        "data" => TryBase64(e.Value),
        "real" => double.TryParse(e.Value, System.Globalization.CultureInfo.InvariantCulture, out var dbl) ? dbl : 0.0,
        _ => e.Value,
    };

    private static Dictionary<string, object?> ParseDict(XElement e)
    {
        var d = new Dictionary<string, object?>();
        string? key = null;
        foreach (var child in e.Elements())
        {
            if (child.Name.LocalName == "key") key = child.Value;
            else if (key != null)
            {
                d[key] = ParseValue(child);
                key = null;
            }
        }
        return d;
    }

    private static byte[] TryBase64(string s)
    {
        try { return Convert.FromBase64String(string.Concat(s.Where(c => !char.IsWhiteSpace(c)))); }
        catch { return Array.Empty<byte>(); }
    }

    private static int ToInt(object? o) => o switch
    {
        long l => (int)l,
        int i => i,
        string s when int.TryParse(s, out var v) => v,
        _ => 0,
    };
}
