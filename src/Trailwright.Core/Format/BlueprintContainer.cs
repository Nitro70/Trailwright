using System.Text;

namespace Trailwright.Format;

/// <summary>
/// The Trailmakers blueprint file: a normal PNG picture whose fully transparent pixels carry the data.
///
/// Layout (worked out from StructureFileHelper.InfuseStructureIntoTexture / ExtractByteArr):
///  * Pixels are walked in Unity order (bottom row first, left to right). Only pixels with alpha 0
///    carry data, three bytes each in R, G, B. Opaque pixels (the picture) are skipped.
///  * The last pixel (top-right) holds the header length as a little-endian int32 in A, R, G, B.
///  * Stream = header, padded to a whole pixel, then structure + identifier + meta back to back.
///  * Header: byte 8 + protobuf SaveGameInPNGInfo {1 version, 2 creator, 3 structureByteSize,
///    4 structureIdentifierSize, 5 structureMetaByteSize}. Older files (before Oct 2026) use a .NET
///    BinaryFormatter record of the same class instead; both are read, the new one is written.
///  * structure = LZ4 frame of protobuf StructureGraphSaveDataProto
///  * identifier = protobuf StructureSaveIdentifierProto (not compressed)
///  * meta = Smaz-compressed protobuf StructureMetaDataProto
/// </summary>
public sealed class BlueprintContainer
{
    public RgbaImage Image { get; set; }
    public uint Version { get; set; } = 1;
    public string Creator { get; set; } = "            ";
    public bool LegacyHeader { get; set; }
    public byte[] StructureRaw { get; set; } = [];   // decompressed protobuf
    public byte[] IdentifierRaw { get; set; } = [];  // protobuf
    public byte[] MetaRaw { get; set; } = [];        // decompressed protobuf

    public BlueprintContainer(RgbaImage image) => Image = image;

    public static BlueprintContainer Read(byte[] pngFile) => Read(Png.Decode(pngFile));

    public static BlueprintContainer Read(RgbaImage img)
    {
        try { return ReadUnchecked(img); }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException)
        { throw new InvalidDataException("this file's blueprint data is damaged", ex); }
    }

    static BlueprintContainer ReadUnchecked(RgbaImage img)
    {
        int n = img.Width * img.Height;
        int lastOff = img.UnityOffset(n - 1);
        var px = img.Pixels;
        int headerLen = px[lastOff + 3] | px[lastOff] << 8 | px[lastOff + 1] << 16 | px[lastOff + 2] << 24;
        if (headerLen <= 0 || headerLen > 1 << 20) throw new InvalidDataException("this PNG does not contain Trailmakers blueprint data");
        var data = ExtractStream(img);
        if (data.Length < headerLen) throw new InvalidDataException("blueprint header is truncated");
        var c = new BlueprintContainer(img);
        int structureSize, idSize, metaSize;
        var header = data.AsSpan(0, headerLen);
        if (header[0] == 8)
        {
            var h = ProtoMessage.Parse(header[1..]);
            c.Version = (uint)h.GetInt(1, 1);
            c.Creator = h.GetString(2) ?? "";
            structureSize = h.GetInt32(3);
            idSize = h.GetInt32(4);
            metaSize = h.GetInt32(5);
        }
        else if (header[0] == 0)
        {
            c.LegacyHeader = true;
            (c.Version, c.Creator, structureSize, idSize, metaSize) = ParseBinaryFormatterHeader(header);
        }
        else throw new InvalidDataException($"unknown blueprint header format {header[0]}");
        int off = (headerLen + 2) / 3 * 3;
        if (structureSize <= 0 || idSize < 0 || metaSize < 0 || (long)off + structureSize + idSize + metaSize > data.Length)
            throw new InvalidDataException("blueprint data sizes are out of range");
        c.StructureRaw = Lz4Frame.Decode(data.AsSpan(off, structureSize));
        c.IdentifierRaw = data.AsSpan(off + structureSize, idSize).ToArray();
        c.MetaRaw = Smaz.Decompress(data.AsSpan(off + structureSize + idSize, metaSize));
        return c;
    }

    /// <summary>RGB bytes of every alpha-0 pixel except the last one, in Unity order.</summary>
    public static byte[] ExtractStream(RgbaImage img)
    {
        int n = img.Width * img.Height;
        var o = new byte[(n - 1) * 3];
        int k = 0;
        for (int i = 0; i < n - 1; i++)
        {
            int p = img.UnityOffset(i);
            if (img.Pixels[p + 3] != 0) continue;
            o[k++] = img.Pixels[p];
            o[k++] = img.Pixels[p + 1];
            o[k++] = img.Pixels[p + 2];
        }
        return o.AsSpan(0, k).ToArray();
    }

    static (uint, string, int, int, int) ParseBinaryFormatterHeader(ReadOnlySpan<byte> h)
    {
        // ... ClassWithMembersAndTypes "SaveGameInPNGInfo" ... then member values:
        // uint version, string creator (BinaryObjectString record), int, int, int, MessageEnd(0x0B).
        int end = h.Length - 1;
        while (end > 0 && h[end] != 0x0B) end--;
        if (end < 12) throw new InvalidDataException("legacy blueprint header not understood");
        int sb = BitConverter.ToInt32(h.Slice(end - 12, 4));
        int ids = BitConverter.ToInt32(h.Slice(end - 8, 4));
        int ms = BitConverter.ToInt32(h.Slice(end - 4, 4));
        string creator = "";
        // creator is a BinaryObjectString (0x06, int32 id, 7-bit length, bytes) right before the ints
        for (int p = end - 13; p > 0; p--)
        {
            if (h[p] == 0x06 && p + 5 < end - 12)
            {
                int len = h[p + 5];
                if (len < 128 && p + 6 + len == end - 12)
                {
                    creator = Encoding.UTF8.GetString(h.Slice(p + 6, len));
                    break;
                }
            }
        }
        return (1, creator, sb, ids, ms);
    }

    public static byte[] BuildHeader(uint version, string creator, int structureSize, int idSize, int metaSize)
    {
        var m = new ProtoMessage();
        m.SetInt(1, version);
        m.SetString(2, creator);
        m.SetInt(3, structureSize);
        m.SetInt(4, idSize);
        m.SetInt(5, metaSize);
        return [8, .. m.ToBytes()];
    }

    /// <summary>How many data bytes the picture can hold (3 per transparent pixel, minus the length pixel).</summary>
    public static int Capacity(RgbaImage img)
    {
        int n = img.Width * img.Height, count = 0;
        for (int i = 0; i < n - 1; i++) if (img.Pixels[img.UnityOffset(i) + 3] == 0) count++;
        return count * 3;
    }

    /// <summary>Write this blueprint into a copy of <see cref="Image"/> and return the PNG file bytes.</summary>
    public byte[] Write()
    {
        var structure = Lz4Frame.Encode(StructureRaw);
        var meta = Smaz.Compress(MetaRaw);
        var header = BuildHeader(Version, Creator, structure.Length, IdentifierRaw.Length, meta.Length);
        int headerPadded = (header.Length + 2) / 3 * 3;
        int need = headerPadded + structure.Length + IdentifierRaw.Length + meta.Length;
        var img = Image.Clone();
        var px = img.Pixels;
        int n = img.Width * img.Height;
        // The game clears the colour of every transparent pixel before writing (so leftover noise
        // never looks like data). Do the same, then make room if the picture is too opaque.
        for (int i = 0; i < n; i++)
        {
            int p = i * 4;
            if (px[p + 3] == 0) px[p] = px[p + 1] = px[p + 2] = 0;
        }
        int cap = Capacity(img);
        for (int i = 0; i < n - 1 && cap < need; i++)
        {
            int p = img.UnityOffset(i);
            if (px[p + 3] != 0) { px[p] = px[p + 1] = px[p + 2] = px[p + 3] = 0; cap += 3; }
        }
        if (cap < need) throw new InvalidOperationException($"blueprint is too large for a {img.Width}x{img.Height} picture");
        var stream = new byte[need];
        header.CopyTo(stream, 0);
        structure.CopyTo(stream, headerPadded);
        IdentifierRaw.CopyTo(stream, headerPadded + structure.Length);
        meta.CopyTo(stream, headerPadded + structure.Length + IdentifierRaw.Length);
        int k = 0;
        for (int i = 0; i < n - 1 && k < need; i++)
        {
            int p = img.UnityOffset(i);
            if (px[p + 3] != 0) continue;
            px[p] = stream[k++];
            if (k < need) px[p + 1] = stream[k++];
            if (k < need) px[p + 2] = stream[k++];
        }
        int last = img.UnityOffset(n - 1);
        int hl = header.Length;
        px[last + 3] = (byte)hl;
        px[last] = (byte)(hl >> 8);
        px[last + 1] = (byte)(hl >> 16);
        px[last + 2] = (byte)(hl >> 24);
        return Png.Encode(img);
    }
}
