using System.Buffers.Binary;
using System.Text;

namespace Trailwright.Format;

public enum WireType { Varint = 0, Fixed64 = 1, Len = 2, StartGroup = 3, EndGroup = 4, Fixed32 = 5 }

/// <summary>One raw protobuf field. Unknown fields survive a load/save round trip untouched.</summary>
public sealed class ProtoField
{
    public int Number;
    public WireType Wire;
    public ulong Varint;
    public byte[] Bytes = [];

    public ProtoField Clone() => new() { Number = Number, Wire = Wire, Varint = Varint, Bytes = (byte[])Bytes.Clone() };
}

/// <summary>
/// A lossless protobuf message: an ordered list of raw fields with typed accessors. protobuf-net
/// (which Trailmakers uses) writes fields in tag order, int32 negatives as 10-byte varints and floats
/// as fixed32; the setters below follow the same conventions.
/// </summary>
public sealed class ProtoMessage
{
    public List<ProtoField> Fields { get; } = [];

    public static ProtoMessage Parse(ReadOnlySpan<byte> b)
    {
        try { return ParseUnchecked(b); }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException)
        { throw new InvalidDataException("malformed protobuf data", ex); }
    }

    static ProtoMessage ParseUnchecked(ReadOnlySpan<byte> b)
    {
        var m = new ProtoMessage();
        int p = 0;
        while (p < b.Length)
        {
            ulong key = ReadVarint(b, ref p);
            var f = new ProtoField { Number = (int)(key >> 3), Wire = (WireType)(key & 7) };
            if (f.Number <= 0) throw new InvalidDataException($"invalid protobuf field number {f.Number}");
            switch (f.Wire)
            {
                case WireType.Varint: f.Varint = ReadVarint(b, ref p); break;
                case WireType.Fixed64: f.Bytes = b.Slice(p, 8).ToArray(); p += 8; break;
                case WireType.Fixed32: f.Bytes = b.Slice(p, 4).ToArray(); p += 4; break;
                case WireType.Len:
                    ulong len = ReadVarint(b, ref p);
                    if (len > (ulong)(b.Length - p)) throw new InvalidDataException("protobuf length out of range");
                    int n = (int)len;
                    f.Bytes = b.Slice(p, n).ToArray();
                    p += n;
                    break;
                default: throw new InvalidDataException($"unsupported protobuf wire type {f.Wire}");
            }
            m.Fields.Add(f);
        }
        return m;
    }

    public byte[] ToBytes()
    {
        var ms = new MemoryStream();
        foreach (var f in Fields)
        {
            WriteVarint(ms, ((ulong)f.Number << 3) | (ulong)f.Wire);
            switch (f.Wire)
            {
                case WireType.Varint: WriteVarint(ms, f.Varint); break;
                case WireType.Len: WriteVarint(ms, (ulong)f.Bytes.Length); ms.Write(f.Bytes); break;
                default: ms.Write(f.Bytes); break;
            }
        }
        return ms.ToArray();
    }

    public ProtoMessage Clone()
    {
        var m = new ProtoMessage();
        foreach (var f in Fields) m.Fields.Add(f.Clone());
        return m;
    }

    // ---- reading -----------------------------------------------------------------------------

    public ProtoField? Get(int n) => Fields.FirstOrDefault(f => f.Number == n);
    public IEnumerable<ProtoField> GetAll(int n) => Fields.Where(f => f.Number == n);
    public bool Has(int n) => Fields.Any(f => f.Number == n);

    public long GetInt(int n, long dflt = 0) => Get(n) is { Wire: WireType.Varint } f ? (long)f.Varint : dflt;
    public int GetInt32(int n, int dflt = 0) => Get(n) is { Wire: WireType.Varint } f ? unchecked((int)(long)f.Varint) : dflt;
    public float GetFloat(int n, float dflt = float.NaN) => Get(n) is { Wire: WireType.Fixed32 } f ? BinaryPrimitives.ReadSingleLittleEndian(f.Bytes) : dflt;
    public string? GetString(int n) => Get(n) is { Wire: WireType.Len } f ? Encoding.UTF8.GetString(f.Bytes) : null;
    public ProtoMessage? GetMessage(int n) => Get(n) is { Wire: WireType.Len } f ? Parse(f.Bytes) : null;
    public IEnumerable<ProtoMessage> GetMessages(int n) => GetAll(n).Where(f => f.Wire == WireType.Len).Select(f => Parse(f.Bytes));

    public IEnumerable<long> GetRepeatedInts(int n)
    {
        foreach (var f in GetAll(n))
        {
            if (f.Wire == WireType.Varint) yield return (long)f.Varint;
            else if (f.Wire == WireType.Len)
            {
                int p = 0;
                while (p < f.Bytes.Length) yield return (long)ReadVarint(f.Bytes, ref p);
            }
        }
    }

    // ---- writing (keeps tag order; first occurrence replaced) --------------------------------

    public void Remove(int n) => Fields.RemoveAll(f => f.Number == n);

    void Put(ProtoField nf)
    {
        int i = Fields.FindIndex(f => f.Number == nf.Number);
        if (i >= 0)
        {
            Fields[i] = nf;
            for (int k = Fields.Count - 1; k > i; k--) if (Fields[k].Number == nf.Number) Fields.RemoveAt(k);
            return;
        }
        int at = Fields.FindIndex(f => f.Number > nf.Number);
        if (at < 0) Fields.Add(nf); else Fields.Insert(at, nf);
    }

    public void SetInt(int n, long v) => Put(new ProtoField { Number = n, Wire = WireType.Varint, Varint = unchecked((ulong)v) });

    public void SetFloat(int n, float v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(b, v);
        Put(new ProtoField { Number = n, Wire = WireType.Fixed32, Bytes = b });
    }

    public void SetString(int n, string v) => Put(new ProtoField { Number = n, Wire = WireType.Len, Bytes = Encoding.UTF8.GetBytes(v) });
    public void SetMessage(int n, ProtoMessage m) => Put(new ProtoField { Number = n, Wire = WireType.Len, Bytes = m.ToBytes() });

    /// <summary>Replace all occurrences of field n by repeated entries, inserted where the field was.</summary>
    public void SetRepeated(int n, IEnumerable<ProtoField> items)
    {
        int at = Fields.FindIndex(f => f.Number == n);
        if (at < 0) at = Fields.FindIndex(f => f.Number > n);
        Fields.RemoveAll(f => f.Number == n);
        if (at < 0 || at > Fields.Count) at = Fields.Count;
        Fields.InsertRange(at, items);
    }

    public static ProtoField IntField(int n, long v) => new() { Number = n, Wire = WireType.Varint, Varint = unchecked((ulong)v) };
    public static ProtoField MessageField(int n, ProtoMessage m) => new() { Number = n, Wire = WireType.Len, Bytes = m.ToBytes() };
    public static ProtoField StringField(int n, string s) => new() { Number = n, Wire = WireType.Len, Bytes = Encoding.UTF8.GetBytes(s) };

    // ---- varints -----------------------------------------------------------------------------

    public static ulong ReadVarint(ReadOnlySpan<byte> b, ref int p)
    {
        ulong r = 0;
        for (int shift = 0; shift < 70; shift += 7)
        {
            if (p >= b.Length) throw new InvalidDataException("truncated varint");
            byte x = b[p++];
            r |= (ulong)(x & 0x7F) << shift;
            if ((x & 0x80) == 0) return r;
        }
        throw new InvalidDataException("varint too long");
    }

    public static void WriteVarint(Stream s, ulong v)
    {
        while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; }
        s.WriteByte((byte)v);
    }
}
