using System.Buffers.Binary;
using System.IO.Compression;

namespace Trailwright.Format;

/// <summary>An 8-bit RGBA image, rows top-down (PNG order).</summary>
public sealed class RgbaImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public RgbaImage(int width, int height, byte[]? pixels = null)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width;
        Height = height;
        Pixels = pixels ?? new byte[width * height * 4];
        if (Pixels.Length != width * height * 4) throw new ArgumentException("pixel buffer size mismatch");
    }

    public RgbaImage Clone() => new(Width, Height, (byte[])Pixels.Clone());

    /// <summary>Byte offset of pixel <paramref name="unityIndex"/> in Unity's raw order (bottom row first).</summary>
    public int UnityOffset(int unityIndex)
    {
        int x = unityIndex % Width;
        int yUp = unityIndex / Width;
        int row = Height - 1 - yUp;
        return (row * Width + x) * 4;
    }
}

/// <summary>Minimal PNG reader/writer: 8-bit RGB/RGBA/grey/palette in, 8-bit RGBA out. No interlacing.</summary>
public static class Png
{
    static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    const long MaxPixels = 4096L * 4096;

    public static RgbaImage Decode(byte[] file)
    {
        try { return DecodeUnchecked(file); }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException or EndOfStreamException)
        { throw new InvalidDataException("malformed PNG file", ex); }
    }

    static RgbaImage DecodeUnchecked(byte[] file)
    {
        if (file.Length < 8 || !file.AsSpan(0, 8).SequenceEqual(Signature))
            throw new InvalidDataException("not a PNG file");
        int pos = 8, width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        byte[]? palette = null, trns = null;
        using var idat = new MemoryStream();
        while (pos + 8 <= file.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(pos));
            string type = System.Text.Encoding.ASCII.GetString(file, pos + 4, 4);
            if (len < 0 || (long)pos + 12 + len > file.Length) throw new InvalidDataException("truncated PNG chunk");
            var data = file.AsSpan(pos + 8, len);
            switch (type)
            {
                case "IHDR":
                    if (len < 13) throw new InvalidDataException("bad PNG header");
                    width = BinaryPrimitives.ReadInt32BigEndian(data);
                    height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                    bitDepth = data[8];
                    colorType = data[9];
                    interlace = data[12];
                    break;
                case "PLTE": palette = data.ToArray(); break;
                case "tRNS": trns = data.ToArray(); break;
                case "IDAT": idat.Write(data); break;
            }
            pos += 12 + len;
            if (type == "IEND") break;
        }
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels) throw new InvalidDataException($"PNG size {width}x{height} is not supported");
        if (bitDepth != 8) throw new NotSupportedException($"PNG bit depth {bitDepth} is not supported");
        if (interlace != 0) throw new NotSupportedException("interlaced PNG is not supported");
        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new NotSupportedException($"PNG color type {colorType}") };
        int stride = width * channels;
        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        var raw = new byte[(stride + 1) * height];
        int read = 0;
        while (read < raw.Length)
        {
            int n = z.Read(raw, read, raw.Length - read);
            if (n == 0) throw new InvalidDataException("PNG image data is truncated");
            read += n;
        }
        var cur = new byte[stride];
        var prev = new byte[stride];
        var img = new RgbaImage(width, height);
        for (int y = 0; y < height; y++)
        {
            int f = raw[y * (stride + 1)];
            Buffer.BlockCopy(raw, y * (stride + 1) + 1, cur, 0, stride);
            Unfilter(f, cur, prev, channels);
            int o = y * width * 4;
            for (int x = 0; x < width; x++, o += 4)
            {
                switch (colorType)
                {
                    case 6: img.Pixels[o] = cur[x * 4]; img.Pixels[o + 1] = cur[x * 4 + 1]; img.Pixels[o + 2] = cur[x * 4 + 2]; img.Pixels[o + 3] = cur[x * 4 + 3]; break;
                    case 2: img.Pixels[o] = cur[x * 3]; img.Pixels[o + 1] = cur[x * 3 + 1]; img.Pixels[o + 2] = cur[x * 3 + 2]; img.Pixels[o + 3] = 255; break;
                    case 0: img.Pixels[o] = img.Pixels[o + 1] = img.Pixels[o + 2] = cur[x]; img.Pixels[o + 3] = 255; break;
                    case 4: img.Pixels[o] = img.Pixels[o + 1] = img.Pixels[o + 2] = cur[x * 2]; img.Pixels[o + 3] = cur[x * 2 + 1]; break;
                    case 3:
                        int i = cur[x];
                        if (palette == null || i * 3 + 2 >= palette.Length) throw new InvalidDataException("bad PNG palette index");
                        img.Pixels[o] = palette[i * 3]; img.Pixels[o + 1] = palette[i * 3 + 1]; img.Pixels[o + 2] = palette[i * 3 + 2];
                        img.Pixels[o + 3] = trns != null && i < trns.Length ? trns[i] : (byte)255;
                        break;
                }
            }
            (prev, cur) = (cur, prev);
        }
        return img;
    }

    static void Unfilter(int filter, byte[] cur, byte[] prev, int bpp)
    {
        switch (filter)
        {
            case 0: return;
            case 1: for (int i = bpp; i < cur.Length; i++) cur[i] += cur[i - bpp]; return;
            case 2: for (int i = 0; i < cur.Length; i++) cur[i] += prev[i]; return;
            case 3:
                for (int i = 0; i < cur.Length; i++)
                    cur[i] += (byte)(((i >= bpp ? cur[i - bpp] : 0) + prev[i]) >> 1);
                return;
            case 4:
                for (int i = 0; i < cur.Length; i++)
                    cur[i] += Paeth(i >= bpp ? cur[i - bpp] : (byte)0, prev[i], i >= bpp ? prev[i - bpp] : (byte)0);
                return;
            default: throw new InvalidDataException($"bad PNG filter type {filter}");
        }
    }

    static byte Paeth(byte a, byte b, byte c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    public static byte[] Encode(RgbaImage img)
    {
        int stride = img.Width * 4;
        var filtered = new byte[(stride + 1) * img.Height];
        var prev = new byte[stride];
        var cur = new byte[stride];
        var trial = new byte[stride];
        var best = new byte[stride];
        for (int y = 0; y < img.Height; y++)
        {
            Buffer.BlockCopy(img.Pixels, y * stride, cur, 0, stride);
            long bestScore = long.MaxValue;
            int bestFilter = 0;
            for (int f = 0; f <= 4; f++)
            {
                long score = 0;
                for (int i = 0; i < stride; i++)
                {
                    byte a = i >= 4 ? cur[i - 4] : (byte)0, b = prev[i], c = i >= 4 ? prev[i - 4] : (byte)0;
                    byte v = f switch
                    {
                        0 => cur[i],
                        1 => (byte)(cur[i] - a),
                        2 => (byte)(cur[i] - b),
                        3 => (byte)(cur[i] - ((a + b) >> 1)),
                        _ => (byte)(cur[i] - Paeth(a, b, c)),
                    };
                    trial[i] = v;
                    score += v < 128 ? v : 256 - v;
                }
                if (score < bestScore) { bestScore = score; bestFilter = f; Buffer.BlockCopy(trial, 0, best, 0, stride); }
            }
            filtered[y * (stride + 1)] = (byte)bestFilter;
            Buffer.BlockCopy(best, 0, filtered, y * (stride + 1) + 1, stride);
            (prev, cur) = (cur, prev);
        }
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            z.Write(filtered);
        using var outp = new MemoryStream();
        outp.Write(Signature);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, img.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), img.Height);
        ihdr[8] = 8; ihdr[9] = 6;
        WriteChunk(outp, "IHDR", ihdr);
        var all = compressed.ToArray();
        for (int off = 0; off < all.Length; off += 8192)
            WriteChunk(outp, "IDAT", all.AsSpan(off, Math.Min(8192, all.Length - off)));
        WriteChunk(outp, "IEND", []);
        return outp.ToArray();
    }

    static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> b4 = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b4, data.Length);
        s.Write(b4);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        uint crc = Crc32.Update(Crc32.Update(0xFFFFFFFFu, t), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(b4, crc);
        s.Write(b4);
    }
}

static class Crc32
{
    static readonly uint[] Table = Build();

    static uint[] Build()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
