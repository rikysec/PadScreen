using System.Buffers.Binary;
using System.Text.Json;

namespace PadScreen.Core;

/// <summary>Tipi di messaggio (vedi PROTOCOL.md).</summary>
public enum MsgType : byte
{
    Hello = 0x01,
    Size = 0x02,
    Ping = 0x05,
    Pong = 0x06,
    Bye = 0x07,
    Touch = 0x10,
    Scroll = 0x11,
    KeyframeRequest = 0x20,
    Config = 0x81,
    VideoHeader = 0x82,
    VideoFrame = 0x83,
    Status = 0x91,
}

public static class Proto
{
    public const int Version = 1;
    public const int DefaultPort = 47800;
    public const string ServiceType = "_padscreen._tcp";
    public const int MaxMessageSize = 32 * 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

public sealed class HelloInfo
{
    public int Proto { get; set; }
    public string Name { get; set; } = "iPad";
    public string Model { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public double Scale { get; set; } = 2;
    public int MaxFps { get; set; } = 60;
    public string AppVersion { get; set; } = "";

    public override string ToString() => $"{Name} ({Model}) {Width}×{Height} @{Scale}x";
}

public sealed class ConfigInfo
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int Fps { get; set; }
    public string Encoder { get; set; } = "";
    public string Host { get; set; } = "";
}

public enum TouchPhase : byte { Down = 0, Move = 1, Up = 2, Cancel = 3, Hover = 4, HoverExit = 5 }

public enum TouchKind : byte { Finger = 0, Pencil = 1, Mouse = 2 }

public readonly record struct TouchSample(
    uint Id, TouchPhase Phase, TouchKind Kind,
    float X, float Y, float Pressure, float Altitude, float Azimuth)
{
    public const int WireSize = 26;

    public static List<TouchSample> ParseList(ReadOnlySpan<byte> p)
    {
        var list = new List<TouchSample>();
        if (p.Length < 1) return list;
        int n = p[0];
        int off = 1;
        for (int i = 0; i < n && off + WireSize <= p.Length; i++, off += WireSize)
        {
            var s = p.Slice(off, WireSize);
            list.Add(new TouchSample(
                BinaryPrimitives.ReadUInt32BigEndian(s),
                (TouchPhase)s[4],
                (TouchKind)s[5],
                BinaryPrimitives.ReadSingleBigEndian(s[6..]),
                BinaryPrimitives.ReadSingleBigEndian(s[10..]),
                BinaryPrimitives.ReadSingleBigEndian(s[14..]),
                BinaryPrimitives.ReadSingleBigEndian(s[18..]),
                BinaryPrimitives.ReadSingleBigEndian(s[22..])));
        }
        return list;
    }

    public static byte[] EncodeList(IReadOnlyList<TouchSample> list)
    {
        int n = Math.Min(255, list.Count);
        var buf = new byte[1 + n * WireSize];
        buf[0] = (byte)n;
        for (int i = 0; i < n; i++)
        {
            var t = list[i];
            var s = buf.AsSpan(1 + i * WireSize, WireSize);
            BinaryPrimitives.WriteUInt32BigEndian(s, t.Id);
            s[4] = (byte)t.Phase;
            s[5] = (byte)t.Kind;
            BinaryPrimitives.WriteSingleBigEndian(s[6..], t.X);
            BinaryPrimitives.WriteSingleBigEndian(s[10..], t.Y);
            BinaryPrimitives.WriteSingleBigEndian(s[14..], t.Pressure);
            BinaryPrimitives.WriteSingleBigEndian(s[18..], t.Altitude);
            BinaryPrimitives.WriteSingleBigEndian(s[22..], t.Azimuth);
        }
        return buf;
    }
}

public readonly record struct Message(MsgType Type, byte[] Payload);

/// <summary>Lettura/scrittura dei messaggi incorniciati su uno Stream (TCP o tunnel usbmux).</summary>
public sealed class MessageChannel
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _readHeader = new byte[5];

    public MessageChannel(Stream stream) => _stream = stream;

    public async Task<Message> ReadAsync(CancellationToken ct)
    {
        await _stream.ReadExactlyAsync(_readHeader, ct).ConfigureAwait(false);
        var type = (MsgType)_readHeader[0];
        int len = BinaryPrimitives.ReadInt32BigEndian(_readHeader.AsSpan(1));
        if (len < 0 || len > Proto.MaxMessageSize)
            throw new InvalidDataException($"Messaggio di lunghezza non valida ({len})");
        var payload = len == 0 ? Array.Empty<byte>() : new byte[len];
        if (len > 0) await _stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return new Message(type, payload);
    }

    public async Task WriteAsync(MsgType type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (payload.Length <= 64 * 1024)
            {
                var buf = new byte[5 + payload.Length];
                buf[0] = (byte)type;
                BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(1), payload.Length);
                payload.CopyTo(buf.AsMemory(5));
                await _stream.WriteAsync(buf, ct).ConfigureAwait(false);
            }
            else
            {
                var hdr = new byte[5];
                hdr[0] = (byte)type;
                BinaryPrimitives.WriteInt32BigEndian(hdr.AsSpan(1), payload.Length);
                await _stream.WriteAsync(hdr, ct).ConfigureAwait(false);
                await _stream.WriteAsync(payload, ct).ConfigureAwait(false);
            }
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
