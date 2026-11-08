using System.Buffers.Binary;
using System.Numerics;

namespace Trailwright.Format;

/// <summary>
/// LZ4 frame format (lz4.org spec v1.6), enough for Trailmakers blueprints: the game writes frames
/// with linked blocks, 64 KB max block size and a content checksum (FLG 0x44, BD 0x40); we write the
/// same flags. Blocks we write are compressed independently, which a linked-block reader accepts.
/// </summary>
public static class Lz4Frame
{
    const uint Magic = 0x184D2204;

    public static byte[] Decode(ReadOnlySpan<byte> src) => Decode(src, out _);

    public static byte[] Decode(ReadOnlySpan<byte> src, out int consumed)
    {
        try { return DecodeUnchecked(src, out consumed); }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException)
        { throw new InvalidDataException("malformed LZ4 data", ex); }
    }

    static byte[] DecodeUnchecked(ReadOnlySpan<byte> src, out int consumed)
    {
        if (src.Length < 7 || BinaryPrimitives.ReadUInt32LittleEndian(src) != Magic)
            throw new InvalidDataException("not an LZ4 frame");
        byte flg = src[4], bd = src[5];
        if ((flg >> 6) != 1) throw new InvalidDataException("unsupported LZ4 frame version");
        bool blockChecksum = (flg & 0x10) != 0, contentSize = (flg & 0x08) != 0, contentChecksum = (flg & 0x04) != 0, dictId = (flg & 0x01) != 0;
        int p = 6;
        if (contentSize) p += 8;
        if (dictId) p += 4;
        byte hc = src[p];
        byte expect = (byte)((XxHash32.Hash(src[4..p], 0) >> 8) & 0xFF);
        if (hc != expect) throw new InvalidDataException("LZ4 frame header checksum mismatch");
        p++;
        int maxBlock = ((bd >> 4) & 7) switch { 4 => 64 << 10, 5 => 256 << 10, 6 => 1 << 20, 7 => 4 << 20, _ => throw new InvalidDataException("bad LZ4 block size") };
        var output = new List<byte>(maxBlock);
        var buffer = new byte[0];
        int outLen = 0;
        while (true)
        {
            if (p + 4 > src.Length) throw new InvalidDataException("truncated LZ4 frame");
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(src[p..]);
            p += 4;
            if (size == 0) break;
            bool raw = (size & 0x80000000u) != 0;
            int n = (int)(size & 0x7FFFFFFF);
            if (n > maxBlock || p + n > src.Length) throw new InvalidDataException("bad LZ4 block size");
            if (buffer.Length < outLen + maxBlock) Array.Resize(ref buffer, Math.Max(buffer.Length * 2, outLen + maxBlock));
            if (raw)
            {
                src.Slice(p, n).CopyTo(buffer.AsSpan(outLen));
                outLen += n;
            }
            else
            {
                outLen = DecodeBlock(src.Slice(p, n), buffer, outLen, outLen + maxBlock);
            }
            p += n;
            if (blockChecksum) p += 4;
        }
        if (contentChecksum)
        {
            if (p + 4 > src.Length) throw new InvalidDataException("truncated LZ4 content checksum");
            uint want = BinaryPrimitives.ReadUInt32LittleEndian(src[p..]);
            if (XxHash32.Hash(buffer.AsSpan(0, outLen), 0) != want) throw new InvalidDataException("LZ4 content checksum mismatch");
            p += 4;
        }
        consumed = p;
        return buffer.AsSpan(0, outLen).ToArray();
    }

    /// <summary>Decodes one block into <paramref name="dst"/> at <paramref name="start"/>; earlier output is usable history.</summary>
    static int DecodeBlock(ReadOnlySpan<byte> src, byte[] dst, int start, int limit)
    {
        int s = 0, d = start;
        while (s < src.Length)
        {
            int token = src[s++];
            int lit = token >> 4;
            if (lit == 15) { int b; do { b = src[s++]; lit += b; } while (b == 255); }
            if (d + lit > limit || s + lit > src.Length) throw new InvalidDataException("LZ4 literal overrun");
            src.Slice(s, lit).CopyTo(dst.AsSpan(d));
            s += lit; d += lit;
            if (s >= src.Length) break;
            int off = src[s] | (src[s + 1] << 8);
            s += 2;
            int ml = token & 15;
            if (ml == 15) { int b; do { b = src[s++]; ml += b; } while (b == 255); }
            ml += 4;
            if (off == 0 || off > d) throw new InvalidDataException("LZ4 bad match offset");
            if (d + ml > limit) throw new InvalidDataException("LZ4 match overrun");
            for (int i = 0; i < ml; i++, d++) dst[d] = dst[d - off];
        }
        return d;
    }

    public static byte[] Encode(ReadOnlySpan<byte> data)
    {
        const int maxBlock = 64 << 10;
        using var ms = new MemoryStream();
        Span<byte> hdr = [0x04, 0x22, 0x4D, 0x18, 0x44, 0x40, 0];
        hdr[6] = (byte)((XxHash32.Hash(hdr[4..6], 0) >> 8) & 0xFF);
        ms.Write(hdr);
        Span<byte> b4 = stackalloc byte[4];
        for (int off = 0; off < data.Length; off += maxBlock)
        {
            var chunk = data.Slice(off, Math.Min(maxBlock, data.Length - off));
            var comp = EncodeBlock(chunk);
            if (comp.Length < chunk.Length)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(b4, (uint)comp.Length);
                ms.Write(b4);
                ms.Write(comp);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(b4, (uint)chunk.Length | 0x80000000u);
                ms.Write(b4);
                ms.Write(chunk);
            }
        }
        BinaryPrimitives.WriteUInt32LittleEndian(b4, 0);
        ms.Write(b4);
        BinaryPrimitives.WriteUInt32LittleEndian(b4, XxHash32.Hash(data, 0));
        ms.Write(b4);
        return ms.ToArray();
    }

    /// <summary>Greedy hash-chain-free LZ4 block compressor (respects MFLIMIT=12 / LASTLITERALS=5).</summary>
    public static byte[] EncodeBlock(ReadOnlySpan<byte> src)
    {
        const int MinMatch = 4, LastLiterals = 5, MfLimit = 12, HashLog = 14;
        var outp = new List<byte>(src.Length / 2 + 16);
        var table = new int[1 << HashLog];
        Array.Fill(table, -1);
        int anchor = 0, i = 0;
        int limit = src.Length - MfLimit;
        static uint Hash(uint v) => (v * 2654435761u) >> (32 - HashLog);
        while (i < limit)
        {
            uint seq = BinaryPrimitives.ReadUInt32LittleEndian(src[i..]);
            uint h = Hash(seq);
            int cand = table[h];
            table[h] = i;
            if (cand >= 0 && i - cand <= 65535 && BinaryPrimitives.ReadUInt32LittleEndian(src[cand..]) == seq)
            {
                int ml = MinMatch;
                int maxEnd = src.Length - LastLiterals;
                while (i + ml < maxEnd && src[cand + ml] == src[i + ml]) ml++;
                WriteSequence(outp, src.Slice(anchor, i - anchor), i - cand, ml);
                i += ml;
                anchor = i;
                if (i - 2 >= 0 && i - 2 < limit) table[Hash(BinaryPrimitives.ReadUInt32LittleEndian(src[(i - 2)..]))] = i - 2;
                continue;
            }
            i++;
        }
        // last literals
        int litLen = src.Length - anchor;
        int tokenPos = outp.Count;
        outp.Add((byte)(Math.Min(litLen, 15) << 4));
        if (litLen >= 15) WriteLen(outp, litLen - 15);
        for (int k = anchor; k < src.Length; k++) outp.Add(src[k]);
        return outp.ToArray();
    }

    static void WriteSequence(List<byte> outp, ReadOnlySpan<byte> literals, int offset, int matchLen)
    {
        int lit = literals.Length, ml = matchLen - 4;
        outp.Add((byte)((Math.Min(lit, 15) << 4) | Math.Min(ml, 15)));
        if (lit >= 15) WriteLen(outp, lit - 15);
        foreach (var b in literals) outp.Add(b);
        outp.Add((byte)(offset & 0xFF));
        outp.Add((byte)(offset >> 8));
        if (ml >= 15) WriteLen(outp, ml - 15);
    }

    static void WriteLen(List<byte> outp, int n)
    {
        while (n >= 255) { outp.Add(255); n -= 255; }
        outp.Add((byte)n);
    }
}

public static class XxHash32
{
    const uint P1 = 2654435761u, P2 = 2246822519u, P3 = 3266489917u, P4 = 668265263u, P5 = 374761393u;

    public static uint Hash(ReadOnlySpan<byte> data, uint seed)
    {
        int p = 0, len = data.Length;
        uint h;
        if (len >= 16)
        {
            uint v1 = seed + P1 + P2, v2 = seed + P2, v3 = seed, v4 = seed - P1;
            int limit = len - 16;
            while (p <= limit)
            {
                v1 = Round(v1, BinaryPrimitives.ReadUInt32LittleEndian(data[p..])); p += 4;
                v2 = Round(v2, BinaryPrimitives.ReadUInt32LittleEndian(data[p..])); p += 4;
                v3 = Round(v3, BinaryPrimitives.ReadUInt32LittleEndian(data[p..])); p += 4;
                v4 = Round(v4, BinaryPrimitives.ReadUInt32LittleEndian(data[p..])); p += 4;
            }
            h = BitOperations.RotateLeft(v1, 1) + BitOperations.RotateLeft(v2, 7) + BitOperations.RotateLeft(v3, 12) + BitOperations.RotateLeft(v4, 18);
        }
        else h = seed + P5;
        h += (uint)len;
        while (p + 4 <= len)
        {
            h += BinaryPrimitives.ReadUInt32LittleEndian(data[p..]) * P3;
            h = BitOperations.RotateLeft(h, 17) * P4;
            p += 4;
        }
        while (p < len)
        {
            h += data[p] * P5;
            h = BitOperations.RotateLeft(h, 11) * P1;
            p++;
        }
        h ^= h >> 15; h *= P2; h ^= h >> 13; h *= P3; h ^= h >> 16;
        return h;
    }

    static uint Round(uint acc, uint input)
    {
        acc += input * P2;
        acc = BitOperations.RotateLeft(acc, 13);
        return acc * P1;
    }
}
