using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace PadScreen.Core;

public sealed record DiscoveredPad(string Name, IPAddress Address, int Port);

/// <summary>
/// Ricerca Bonjour/mDNS minimale (nessuna dipendenza): invia una query PTR "legacy unicast"
/// da una porta effimera; l'iPad risponde direttamente a noi con PTR/SRV/A.
/// </summary>
public static class MdnsBrowser
{
    private static readonly IPEndPoint MdnsGroup = new(IPAddress.Parse("224.0.0.251"), 5353);

    public static async Task<IReadOnlyList<DiscoveredPad>> BrowseAsync(string serviceType, TimeSpan duration, CancellationToken ct = default)
    {
        string service = serviceType.TrimEnd('.') + ".local";
        byte[] query = BuildQuery(service);
        var state = new BrowseState(service);

        var sockets = new List<Socket>();
        foreach (var addr in LocalIPv4Addresses())
        {
            try
            {
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                s.Bind(new IPEndPoint(addr, 0));
                s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, addr.GetAddressBytes());
                s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                sockets.Add(s);
            }
            catch
            {
                // interfaccia non utilizzabile
            }
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var receivers = sockets.Select(s => ReceiveLoopAsync(s, state, cts.Token)).ToList();
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int[] sendAtMs = { 0, 400, 1200 };
            foreach (int at in sendAtMs)
            {
                var wait = TimeSpan.FromMilliseconds(at) - sw.Elapsed;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, cts.Token).ConfigureAwait(false);
                if (sw.Elapsed > duration) break;
                foreach (var s in sockets)
                {
                    try { await s.SendToAsync(query, SocketFlags.None, MdnsGroup, cts.Token).ConfigureAwait(false); }
                    catch { }
                }
            }
            var remaining = duration - sw.Elapsed;
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        finally
        {
            cts.Cancel();
            foreach (var s in sockets) s.Dispose();
            try { await Task.WhenAll(receivers).ConfigureAwait(false); } catch { }
        }
        return state.Results();
    }

    private static IEnumerable<IPAddress> LocalIPv4Addresses()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (!ni.SupportsMulticast) continue;
            IPInterfaceProperties props;
            try { props = ni.GetIPProperties(); } catch { continue; }
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var b = ua.Address.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) continue;
                yield return ua.Address;
            }
        }
    }

    private static async Task ReceiveLoopAsync(Socket s, BrowseState state, CancellationToken ct)
    {
        var buf = new byte[9000];
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try
            {
                r = await s.ReceiveFromAsync(buf, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct).ConfigureAwait(false);
            }
            catch
            {
                return;
            }
            var from = (r.RemoteEndPoint as IPEndPoint)?.Address;
            try { state.Parse(buf.AsSpan(0, r.ReceivedBytes).ToArray(), from); }
            catch { /* pacchetto malformato */ }
        }
    }

    internal static byte[] BuildQuery(string name)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }); // id 0, flags 0, 1 domanda
        WriteName(ms, name);
        ms.Write(new byte[] { 0, 12, 0, 1 }); // PTR, IN
        return ms.ToArray();
    }

    private static void WriteName(Stream s, string name)
    {
        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var b = Encoding.UTF8.GetBytes(label);
            s.WriteByte((byte)b.Length);
            s.Write(b);
        }
        s.WriteByte(0);
    }

    private sealed class BrowseState
    {
        private readonly string _service;
        private readonly object _lock = new();
        private readonly Dictionary<string, IPAddress?> _instances = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (int Port, string Target)> _srv = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IPAddress> _a = new(StringComparer.OrdinalIgnoreCase);

        public BrowseState(string service) => _service = service;

        public void Parse(byte[] p, IPAddress? from)
        {
            int pos = 0;
            ushort flags = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(2));
            if ((flags & 0x8000) == 0) return; // non è una risposta
            int qd = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(4));
            int total = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(6))
                      + BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(8))
                      + BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(10));
            pos = 12;
            for (int i = 0; i < qd; i++)
            {
                ReadName(p, ref pos);
                pos += 4;
            }
            lock (_lock)
            {
                for (int i = 0; i < total && pos + 10 <= p.Length; i++)
                {
                    string owner = ReadName(p, ref pos);
                    ushort type = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(pos));
                    int rdlen = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(pos + 8));
                    pos += 10;
                    int rdStart = pos;
                    if (rdStart + rdlen > p.Length) break;
                    switch (type)
                    {
                        case 12: // PTR
                            if (owner.Equals(_service, StringComparison.OrdinalIgnoreCase))
                            {
                                int q = rdStart;
                                string inst = ReadName(p, ref q);
                                if (!_instances.ContainsKey(inst) || _instances[inst] == null) _instances[inst] = from;
                            }
                            break;
                        case 33: // SRV
                            if (rdlen >= 7)
                            {
                                int port = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(rdStart + 4));
                                int q = rdStart + 6;
                                string target = ReadName(p, ref q);
                                _srv[owner] = (port, target);
                                if (owner.EndsWith("." + _service, StringComparison.OrdinalIgnoreCase) && !_instances.ContainsKey(owner))
                                    _instances[owner] = from;
                            }
                            break;
                        case 1: // A
                            if (rdlen == 4) _a[owner] = new IPAddress(p.AsSpan(rdStart, 4));
                            break;
                    }
                    pos = rdStart + rdlen;
                }
            }
        }

        public IReadOnlyList<DiscoveredPad> Results()
        {
            lock (_lock)
            {
                var list = new List<DiscoveredPad>();
                foreach (var (inst, from) in _instances)
                {
                    int port = Proto.DefaultPort;
                    IPAddress? addr = null;
                    if (_srv.TryGetValue(inst, out var srv))
                    {
                        port = srv.Port;
                        _a.TryGetValue(srv.Target, out addr);
                    }
                    addr ??= from;
                    if (addr == null) continue;
                    string name = inst.EndsWith("." + _service, StringComparison.OrdinalIgnoreCase)
                        ? inst[..^(_service.Length + 1)]
                        : inst;
                    list.Add(new DiscoveredPad(name.Replace("\\", ""), addr, port));
                }
                return list;
            }
        }

        private static string ReadName(byte[] p, ref int pos)
        {
            var sb = new StringBuilder();
            int jumps = 0;
            int cur = pos;
            bool jumped = false;
            while (true)
            {
                if (cur >= p.Length) throw new InvalidDataException();
                int len = p[cur];
                if (len == 0)
                {
                    cur++;
                    break;
                }
                if ((len & 0xC0) == 0xC0)
                {
                    if (cur + 1 >= p.Length || ++jumps > 20) throw new InvalidDataException();
                    int ptr = ((len & 0x3F) << 8) | p[cur + 1];
                    if (!jumped) pos = cur + 2;
                    jumped = true;
                    cur = ptr;
                    continue;
                }
                cur++;
                if (cur + len > p.Length) throw new InvalidDataException();
                if (sb.Length > 0) sb.Append('.');
                sb.Append(Encoding.UTF8.GetString(p, cur, len));
                cur += len;
            }
            if (!jumped) pos = cur;
            return sb.ToString();
        }
    }
}
