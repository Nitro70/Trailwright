namespace Trailwright.Model;

/// <summary>Grid geometry shared by the editor, the 3D view and the AI port.</summary>
public static class Geometry
{
    /// <summary>Cells a block occupies: its type's grid box, rotated and moved by the base node.</summary>
    public static IEnumerable<Int3> Cells(PlacedBlock b) => b.Type == null ? [b.Base.Pos] : Cells(b.Type, b.Base.Pos, b.Base.Rot);

    public static IEnumerable<Int3> Cells(BlockType t, Int3 pos, Rot rot)
    {
        var mn = t.Min; var mx = t.Max;
        for (int x = mn.X; x <= mx.X; x++)
            for (int y = mn.Y; y <= mx.Y; y++)
                for (int z = mn.Z; z <= mx.Z; z++)
                    yield return rot.Apply(new Int3(x, y, z)) + pos;
    }

    /// <summary>
    /// Attachment points of one node, in half-cell units: position = R * knob + 2 * nodePos, normal = R * n.
    /// Two nodes are connected exactly when they have knobs at the same point facing each other
    /// (this reproduces 15708 of 16227 edges in real blueprints; the rest are a block's internal part links).
    /// </summary>
    public static IEnumerable<(Int3 Pos, Int3 Normal)> Knobs(Node n)
    {
        var t = n.Block.Type;
        if (t == null || n.Socket < 0 || n.Socket >= t.Knobs.Count) yield break;
        var rot = n.Rot;
        var p2 = n.Pos * 2;
        foreach (var k in t.Knobs[n.Socket])
            yield return (rot.Apply(new Int3(k[0], k[1], k[2])) + p2, rot.Apply(new Int3(k[3], k[4], k[5])));
    }

    public static Dictionary<Int3, PlacedBlock> Occupancy(Build b, PlacedBlock? except = null)
    {
        var d = new Dictionary<Int3, PlacedBlock>();
        foreach (var blk in b.Blocks)
        {
            if (blk == except) continue;
            foreach (var c in Cells(blk)) d.TryAdd(c, blk);
        }
        return d;
    }

    public static Dictionary<Int3, List<(Node Node, Int3 Normal)>> KnobIndex(Build b, PlacedBlock? except = null)
    {
        var d = new Dictionary<Int3, List<(Node, Int3)>>();
        foreach (var n in b.Nodes)
        {
            if (n.Block == except) continue;
            foreach (var (p, nrm) in Knobs(n))
            {
                if (!d.TryGetValue(p, out var l)) d[p] = l = [];
                l.Add((n, nrm));
            }
        }
        return d;
    }

    /// <summary>External connections a block would get: (its node, other node) pairs.</summary>
    public static List<(Node Mine, Node Other)> Connections(Build b, PlacedBlock blk)
    {
        var idx = KnobIndex(b, blk);
        var res = new List<(Node, Node)>();
        foreach (var n in blk.Nodes)
            foreach (var (p, nrm) in Knobs(n))
                if (idx.TryGetValue(p, out var others))
                    foreach (var (o, onrm) in others)
                        if (onrm == -nrm) res.Add((n, o));
        return res;
    }

    /// <summary>Blocks a not-yet-placed block would attach to (uses the knobs of its first part).</summary>
    public static HashSet<int> WouldConnect(Dictionary<Int3, List<(Node Node, Int3 Normal)>> knobIndex, BlockType t, Int3 pos, Rot rot)
    {
        var res = new HashSet<int>();
        if (t.Knobs.Count == 0) return res;
        foreach (var k in t.Knobs[0])
        {
            var p = rot.Apply(new Int3(k[0], k[1], k[2])) + pos * 2;
            var n = rot.Apply(new Int3(k[3], k[4], k[5]));
            if (knobIndex.TryGetValue(p, out var others))
                foreach (var (o, on) in others) if (on == -n) res.Add(o.Block.Id);
        }
        return res;
    }

    public sealed record Fit(Int3 Pos, Rot Rot, int[] AttachesTo);

    /// <summary>Every way (rotation and offset) to put a block so that it covers <paramref name="target"/>, overlaps nothing and attaches.</summary>
    public static List<Fit> FitsCovering(Build b, BlockType t, Int3 target, int max = 12)
    {
        var occ = Occupancy(b);
        var knobs = KnobIndex(b);
        var seen = new HashSet<(Int3, int)>();
        var res = new List<Fit>();
        foreach (var rot in Rot.All)
        {
            int ri = Rot.All.ToList().FindIndex(r => r.SameMatrix(rot));
            foreach (var local in Cells(t, default, rot).ToList())
            {
                var pos = target - local;
                if (!seen.Add((pos, ri))) continue;
                if (Cells(t, pos, rot).Any(occ.ContainsKey)) continue;
                var conn = WouldConnect(knobs, t, pos, rot);
                if (conn.Count == 0 && b.Blocks.Count > 0) continue;
                res.Add(new Fit(pos, rot, conn.OrderBy(i => i).ToArray()));
            }
        }
        return res.OrderByDescending(f => f.AttachesTo.Length).ThenBy(f => f.Rot.Ex + f.Rot.Ey + f.Rot.Ez).Take(max).ToList();
    }
}
