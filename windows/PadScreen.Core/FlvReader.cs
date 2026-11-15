namespace PadScreen.Core;

/// <summary>Un pacchetto video H.264 estratto dal flusso FLV prodotto da FFmpeg.</summary>
public sealed record VideoPacket(bool IsHeader, bool IsKeyframe, bool IsAnnexB, uint TimestampMs, byte[] Data);

/// <summary>
/// Lettore FLV minimale. FFmpeg scrive un tag per fotogramma con la dimensione esplicita,
/// così sappiamo subito dove finisce ogni fotogramma (nessun ritardo di un frame).
/// Supporta sia l'header classico (CodecID 7 = AVC) sia l'"Enhanced FLV" (FourCC avc1).
/// </summary>
public sealed class FlvReader
{
    private readonly Stream _s;
    private readonly byte[] _tagHeader = new byte[11];
    private readonly byte[] _prevSize = new byte[4];

    public FlvReader(Stream s) => _s = s;

    public async Task ReadHeaderAsync(CancellationToken ct)
    {
        var h = new byte[9];
        await _s.ReadExactlyAsync(h, ct).ConfigureAwait(false);
        if (h[0] != (byte)'F' || h[1] != (byte)'L' || h[2] != (byte)'V')
            throw new InvalidDataException("L'output di FFmpeg non è FLV");
        int dataOffset = h[5] << 24 | h[6] << 16 | h[7] << 8 | h[8];
        if (dataOffset > 9)
        {
            var skip = new byte[dataOffset - 9];
            await _s.ReadExactlyAsync(skip, ct).ConfigureAwait(false);
        }
        await _s.ReadExactlyAsync(_prevSize, ct).ConfigureAwait(false); // PreviousTagSize0
    }

    /// <summary>Restituisce il prossimo pacchetto video, oppure null a fine flusso.</summary>
    public async Task<VideoPacket?> ReadNextVideoAsync(CancellationToken ct)
    {
        while (true)
        {
            try
            {
                await _s.ReadExactlyAsync(_tagHeader, ct).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                return null;
            }
            int tagType = _tagHeader[0] & 0x1F;
            int dataSize = _tagHeader[1] << 16 | _tagHeader[2] << 8 | _tagHeader[3];
            uint ts = (uint)(_tagHeader[4] << 16 | _tagHeader[5] << 8 | _tagHeader[6]) | (uint)_tagHeader[7] << 24;
            var data = new byte[dataSize];
            await _s.ReadExactlyAsync(data, ct).ConfigureAwait(false);
            await _s.ReadExactlyAsync(_prevSize, ct).ConfigureAwait(false);

            if (tagType != 9 || dataSize < 1) continue; // non video (metadati, audio)
            var pkt = ParseVideoTag(data, ts);
            if (pkt != null) return pkt;
        }
    }

    internal static VideoPacket? ParseVideoTag(byte[] d, uint ts)
    {
        byte b0 = d[0];
        if ((b0 & 0x80) != 0)
        {
            // Enhanced FLV
            if (d.Length < 5) return null;
            int frameType = (b0 >> 4) & 0x07;
            int packetType = b0 & 0x0F;
            bool avc = d[1] == (byte)'a' && d[2] == (byte)'v' && d[3] == (byte)'c' && d[4] == (byte)'1';
            if (!avc) return null;
            return packetType switch
            {
                0 => new VideoPacket(true, false, false, ts, d[5..]),
                1 when d.Length > 8 => Frame(d[8..], frameType == 1, ts),
                3 => Frame(d[5..], frameType == 1, ts),
                _ => null,
            };
        }
        else
        {
            int frameType = b0 >> 4;
            int codecId = b0 & 0x0F;
            if (codecId != 7 || d.Length < 5) return null;
            int avcPacketType = d[1];
            return avcPacketType switch
            {
                0 => new VideoPacket(true, false, false, ts, d[5..]),
                1 => Frame(d[5..], frameType == 1, ts),
                _ => null,
            };
        }
    }

    private static VideoPacket? Frame(byte[] payload, bool key, uint ts)
    {
        if (payload.Length == 0) return null;
        // Di norma FFmpeg converte in AVCC (lunghezze a 4 byte). Se le lunghezze non tornano
        // e il dato inizia con uno start code, è Annex B.
        bool annexB = !LooksLikeAvcc(payload) &&
                      payload.Length > 4 &&
                      payload[0] == 0 && payload[1] == 0 &&
                      (payload[2] == 1 || (payload[2] == 0 && payload[3] == 1));
        return new VideoPacket(false, key, annexB, ts, payload);
    }

    private static bool LooksLikeAvcc(byte[] p)
    {
        int i = 0;
        while (i + 4 <= p.Length)
        {
            long len = (long)p[i] << 24 | (long)p[i + 1] << 16 | (long)p[i + 2] << 8 | p[i + 3];
            if (len <= 0) return false;
            i += 4;
            if (i + len > p.Length) return false;
            i += (int)len;
        }
        return i == p.Length;
    }
}
