using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Trailwright.Format;

/// <summary>
/// Smaz small-string compression as used by the SmazSharp copy inside Trailmakers (blueprint
/// name/description block). Codes 0..253 index the 254-entry codebook; 254 = one verbatim byte;
/// 255 n = n verbatim bytes (note: n, not n+1 as in the original C smaz). The codebook was read out
/// of the game's own SmazSharp static data (tools/re/smaz_extract.py).
/// </summary>
public static class Smaz
{
    static readonly byte[][] Codebook = Load();
    static readonly Dictionary<string, byte> Lookup = BuildLookup();
    static readonly int MaxEntry = Codebook.Max(c => c.Length);

    static byte[][] Load()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Trailwright.smaz_codebook.json")
                      ?? throw new InvalidOperationException("smaz codebook resource missing");
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(s)!;
        var cb = new byte[254][];
        foreach (var (k, v) in map) cb[int.Parse(k)] = Encoding.Latin1.GetBytes(v);
        if (cb.Any(e => e == null)) throw new InvalidDataException("smaz codebook incomplete");
        return cb;
    }

    static Dictionary<string, byte> BuildLookup()
    {
        var d = new Dictionary<string, byte>(StringComparer.Ordinal);
        for (int i = 0; i < Codebook.Length; i++) d.TryAdd(Encoding.Latin1.GetString(Codebook[i]), (byte)i);
        return d;
    }

    public static byte[] Decompress(ReadOnlySpan<byte> src)
    {
        var o = new List<byte>(src.Length * 2);
        int i = 0;
        while (i < src.Length)
        {
            byte c = src[i];
            if (c == 254)
            {
                if (i + 1 >= src.Length) throw new InvalidDataException("truncated smaz data");
                o.Add(src[i + 1]);
                i += 2;
            }
            else if (c == 255)
            {
                if (i + 1 >= src.Length) throw new InvalidDataException("truncated smaz data");
                int n = src[i + 1];
                if (i + 2 + n > src.Length) throw new InvalidDataException("truncated smaz data");
                o.AddRange(src.Slice(i + 2, n).ToArray());
                i += 2 + n;
            }
            else
            {
                o.AddRange(Codebook[c]);
                i++;
            }
        }
        return o.ToArray();
    }

    public static byte[] Compress(ReadOnlySpan<byte> src)
    {
        var o = new List<byte>(src.Length);
        var verbatim = new List<byte>();
        void Flush()
        {
            int k = 0;
            while (k < verbatim.Count)
            {
                int n = Math.Min(255, verbatim.Count - k);
                if (n == 1) { o.Add(254); o.Add(verbatim[k]); }
                else { o.Add(255); o.Add((byte)n); for (int j = 0; j < n; j++) o.Add(verbatim[k + j]); }
                k += n;
            }
            verbatim.Clear();
        }
        int i = 0;
        while (i < src.Length)
        {
            int best = 0;
            byte code = 0;
            for (int len = Math.Min(MaxEntry, src.Length - i); len > 0; len--)
            {
                if (Lookup.TryGetValue(Encoding.Latin1.GetString(src.Slice(i, len)), out var c)) { best = len; code = c; break; }
            }
            if (best > 0)
            {
                Flush();
                o.Add(code);
                i += best;
            }
            else
            {
                verbatim.Add(src[i]);
                i++;
            }
        }
        Flush();
        return o.ToArray();
    }
}
