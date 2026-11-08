using System.Buffers.Binary;
using Trailwright.Format;

namespace Trailwright.Model;

/// <summary>
/// One StructureNodeSaveData. Field numbers (from the game's [ProtoMember] tags):
/// 1 SocketPosition{X,Y,Z short}, 2 SocketRotation{EulerX,Y,Z quarter turns}, 3 SocketIndex,
/// 4 parentSocket (node index or -1), 5 StructureIndex (= own index), 6 BlockSetup,
/// 7 BlockMetadataGuid {1 fixed64 lo, 2 fixed64 hi}, 9 CloneNodeID.
/// </summary>
public sealed class Node
{
    public ProtoMessage Msg { get; }
    public PlacedBlock Block { get; internal set; } = null!;
    public Node? Parent { get; internal set; }
    /// <summary>Logic links in BlockSetup fields 1/2, resolved to the nodes they point at.</summary>
    internal List<(int listField, int entryIndex, Node? structureTarget, Node? socketTarget)> Links { get; } = [];

    static long _serials;
    internal long Serial { get; } = Interlocked.Increment(ref _serials);

    public Node(ProtoMessage msg) => Msg = msg;

    public int Socket => Msg.GetInt32(3);

    public Int3 Pos
    {
        get { var p = Msg.GetMessage(1); return p == null ? default : new(p.GetInt32(1), p.GetInt32(2), p.GetInt32(3)); }
        set => Msg.SetMessage(1, Vec(value.X, value.Y, value.Z));
    }

    public Rot Rot
    {
        get { var r = Msg.GetMessage(2); return r == null ? Rot.Identity : Rot.FromEuler(r.GetInt32(1), r.GetInt32(2), r.GetInt32(3)); }
        set { var c = value.Canonical(); Msg.SetMessage(2, Vec(c.Ex, c.Ey, c.Ez)); }
    }

    static ProtoMessage Vec(int x, int y, int z)
    {
        var m = new ProtoMessage();
        if (x != 0) m.SetInt(1, x);
        if (y != 0) m.SetInt(2, y);
        if (z != 0) m.SetInt(3, z);
        return m;
    }

    public Guid TypeGuid
    {
        get
        {
            var g = Msg.GetMessage(7);
            if (g == null) return Guid.Empty;
            Span<byte> b = stackalloc byte[16];
            (g.Get(1)?.Bytes ?? new byte[8]).CopyTo(b);
            (g.Get(2)?.Bytes ?? new byte[8]).CopyTo(b[8..]);
            return new Guid(b);
        }
        set
        {
            var b = value.ToByteArray();
            var m = new ProtoMessage();
            m.Fields.Add(new ProtoField { Number = 1, Wire = WireType.Fixed64, Bytes = b[..8] });
            m.Fields.Add(new ProtoField { Number = 2, Wire = WireType.Fixed64, Bytes = b[8..] });
            Msg.SetMessage(7, m);
        }
    }

    public ProtoMessage Setup
    {
        get => Msg.GetMessage(6) ?? new ProtoMessage();
        set => Msg.SetMessage(6, value);
    }
}

/// <summary>A block as the player sees it: a base node plus any extra part nodes (hinge tops, piston heads...).</summary>
public sealed class PlacedBlock
{
    public int Id { get; internal set; }
    public BlockType? Type { get; internal set; }
    public List<Node> Nodes { get; } = [];
    public Node Base => Nodes[0];
    public Guid TypeGuid => Base.TypeGuid;
    public string Name => Type?.Name ?? TypeGuid.ToString();
}

/// <summary>An editable blueprint: picture, name/description, and the block structure.</summary>
public sealed class Build
{
    public Catalog Catalog { get; }
    public RgbaImage Picture { get; set; }
    public ProtoMessage Structure { get; private set; } = new();   // top-level fields other than nodes/edges
    public ProtoMessage Identifier { get; set; } = new();
    public ProtoMessage Meta { get; set; } = new();
    public uint HeaderVersion { get; set; } = 1;
    public string HeaderCreator { get; set; } = "            ";
    public List<Node> Nodes { get; } = [];
    public List<PlacedBlock> Blocks { get; } = [];
    public EdgeSet Edges { get; } = new();
    /// <summary>Set once blocks were added, removed, moved or rotated: weld data and bounds get rebuilt on save.</summary>
    public bool StructureChanged { get; set; }
    /// <summary>Block ids are unique for the whole app run, so a stale id (e.g. from an AI working on a
    /// build that was since replaced) fails instead of hitting an unrelated block.</summary>
    static int _nextId;

    public Build(Catalog catalog, RgbaImage picture)
    {
        Catalog = catalog;
        Picture = picture;
    }

    public string Name
    {
        get => Meta.GetString(1) ?? "";
        set => Meta.SetString(1, value);
    }

    public string Description
    {
        get => Meta.GetString(2) ?? "";
        set => Meta.SetString(2, value);
    }

    public string BlueprintGuid => Identifier.GetString(2) ?? "";

    // ---- load ----------------------------------------------------------------------------------

    public static Build Load(BlueprintContainer c, Catalog catalog)
    {
        var b = new Build(catalog, c.Image)
        {
            Identifier = ProtoMessage.Parse(c.IdentifierRaw),
            Meta = ProtoMessage.Parse(c.MetaRaw),
            HeaderVersion = c.Version,
            HeaderCreator = c.Creator,
        };
        b.LoadStructure(ProtoMessage.Parse(c.StructureRaw), null);
        return b;
    }

    internal void LoadStructure(ProtoMessage s, IReadOnlyList<int>? ids)
    {
        Nodes.Clear(); Blocks.Clear(); Edges.Clear();
        var top = new ProtoMessage();
        var edgeInts = new List<long>();
        foreach (var f in s.Fields)
        {
            if (f.Number == 1 && f.Wire == WireType.Len) Nodes.Add(new Node(ProtoMessage.Parse(f.Bytes)));
            else if (f.Number == 2) edgeInts.AddRange(new ProtoMessage { Fields = { f } }.GetRepeatedInts(2));
            else top.Fields.Add(f);
        }
        Structure = top;
        for (int i = 0; i < Nodes.Count; i++)
        {
            var n = Nodes[i];
            int p = n.Msg.GetInt32(4, 0); // protobuf-net omits a 0 parentSocket: absent means node 0
            n.Parent = p >= 0 && p < Nodes.Count && p != i ? Nodes[p] : null;
        }
        int idPos = 0;
        foreach (var n in Nodes)
        {
            var root = n;
            for (int guard = 0; root.Parent != null && guard < 64; guard++) root = root.Parent;
            if (root == n)
            {
                var blk = new PlacedBlock { Id = ids != null && idPos < ids.Count ? ids[idPos] : NextId(), Type = Catalog.Find(n.TypeGuid) };
                idPos++;
                blk.Nodes.Add(n);
                n.Block = blk;
                Blocks.Add(blk);
            }
        }
        foreach (var n in Nodes)
        {
            if (n.Block != null) continue;
            var root = n;
            for (int guard = 0; root.Parent != null && guard < 64; guard++) root = root.Parent;
            if (root.Block == null)
            {   // parent chain loops: treat the node as its own block rather than lose it
                var own = new PlacedBlock { Id = NextId(), Type = Catalog.Find(n.TypeGuid) };
                own.Nodes.Add(n);
                n.Block = own;
                n.Parent = null;
                Blocks.Add(own);
                continue;
            }
            n.Block = root.Block;
            root.Block.Nodes.Add(n);
        }
        for (int i = 0; i + 1 < edgeInts.Count; i += 2)
        {
            int a = (int)edgeInts[i], bb = (int)edgeInts[i + 1];
            if (a >= 0 && bb >= 0 && a < Nodes.Count && bb < Nodes.Count) Edges.Add(Nodes[a], Nodes[bb]);
        }
        for (int i = 0; i < Nodes.Count; i++) ResolveLinks(Nodes[i], i);
    }

    void ResolveLinks(Node n, int index)
    {
        n.Links.Clear();
        var setup = n.Msg.GetMessage(6);
        if (setup == null) return;
        foreach (int lf in new[] { 1, 2 })
        {
            int k = 0;
            foreach (var e in setup.GetMessages(lf))
            {
                // field 1 (m_structureOffset) = linked node index minus own index. Field 7
                // (m_socketOffset) is not a node offset (it falls outside the node list in about a
                // quarter of real links), so it is carried through unchanged.
                Node? st = null;
                if (e.Has(1)) { int t = index + e.GetInt32(1); if (t >= 0 && t < Nodes.Count) st = Nodes[t]; }
                n.Links.Add((lf, k++, st, null));
            }
        }
    }


    internal static int NextId() => Interlocked.Increment(ref _nextId);

    public PlacedBlock? FindBlock(int id) => Blocks.FirstOrDefault(b => b.Id == id);

    // ---- save ----------------------------------------------------------------------------------

    /// <param name="forSnapshot">Undo snapshots keep the node links up to date but leave weld data and
    /// bounds alone (they are rebuilt only when the file is actually written).</param>
    public ProtoMessage BuildStructureMessage(bool forSnapshot = false)
    {
        var index = new Dictionary<Node, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < Nodes.Count; i++) index[Nodes[i]] = i;
        var s = new ProtoMessage();
        for (int i = 0; i < Nodes.Count; i++)
        {
            var n = Nodes[i];
            int parent = n.Parent != null && index.TryGetValue(n.Parent, out var pi) ? pi : -1;
            int cur = n.Msg.GetInt32(4, 0);
            bool same = parent == -1 ? cur == -1 || cur == i : cur == parent;
            if (!same) { if (parent == 0) n.Msg.Remove(4); else n.Msg.SetInt(4, parent); }
            if (i != 0 || n.Msg.Has(5)) n.Msg.SetInt(5, i);
            else n.Msg.Remove(5);
            if (StructureChanged) RewriteLinks(n, i, index);
            s.Fields.Add(ProtoMessage.MessageField(1, n.Msg));
        }
        foreach (var (a, b) in Edges)
        {
            if (!index.TryGetValue(a, out var ia) || !index.TryGetValue(b, out var ib)) continue;
            s.Fields.Add(ProtoMessage.IntField(2, ia));
            s.Fields.Add(ProtoMessage.IntField(2, ib));
        }
        var top = Structure.Clone();
        if (StructureChanged && !forSnapshot)
        {
            top.Remove(10); // WeldingData: without it the game computes welding itself (StructureGraph.ApplyWelding)
            WriteBounds(top);
        }
        s.Fields.AddRange(top.Fields.Select(f => f.Clone()));
        // keep tag order like protobuf-net
        var ordered = s.Fields.Select((f, i) => (f, i)).OrderBy(t => t.f.Number).ThenBy(t => t.i).Select(t => t.f).ToList();
        s.Fields.Clear();
        s.Fields.AddRange(ordered);
        return s;
    }

    void RewriteLinks(Node n, int self, Dictionary<Node, int> index)
    {
        if (n.Links.Count == 0) return;
        var setup = n.Setup;
        var newLinks = new List<(int, int, Node?, Node?)>();
        foreach (int lf in new[] { 1, 2 })
        {
            var entries = setup.GetMessages(lf).ToList();
            var keep = new List<ProtoField>();
            for (int k = 0; k < entries.Count; k++)
            {
                var link = n.Links.FirstOrDefault(l => l.listField == lf && l.entryIndex == k);
                var e = entries[k];
                // drop a link only when the block it points at was removed
                if (e.Has(1) && link.structureTarget != null)
                {
                    if (!index.TryGetValue(link.structureTarget, out var t)) continue;
                    e.SetInt(1, t - self);
                }
                // the surviving entries move up: keep Links pointing at their new positions
                newLinks.Add((lf, keep.Count, link.structureTarget, link.socketTarget));
                keep.Add(ProtoMessage.MessageField(lf, e));
            }
            if (entries.Count > 0) setup.SetRepeated(lf, keep);
        }
        n.Setup = setup;
        n.Links.Clear();
        n.Links.AddRange(newLinks);
    }

    /// <summary>Bounds = grid box of all blocks in metres (0.25 m per cell): field 3 {center, size}, 4 lowest point, 5 mid point.</summary>
    void WriteBounds(ProtoMessage top)
    {
        var (min, max) = GridBounds();
        float cx = (min.X + max.X) * 0.125f, cy = (min.Y + max.Y) * 0.125f, cz = (min.Z + max.Z) * 0.125f;
        var bounds = new ProtoMessage();
        bounds.SetMessage(1, Vec3(cx, cy, cz));
        bounds.SetMessage(2, Vec3((max.X - min.X + 1) * 0.25f, (max.Y - min.Y + 1) * 0.25f, (max.Z - min.Z + 1) * 0.25f));
        top.SetMessage(3, bounds);
        top.SetFloat(4, (min.Y - 0.5f) * 0.25f);
        top.SetMessage(5, Vec3(cx, cy, cz));
    }

    static ProtoMessage Vec3(float x, float y, float z)
    {
        var m = new ProtoMessage();
        m.SetFloat(1, x); m.SetFloat(2, y); m.SetFloat(3, z);
        return m;
    }

    public (Int3 Min, Int3 Max) GridBounds()
    {
        bool any = false;
        Int3 min = default, max = default;
        foreach (var b in Blocks)
        {
            foreach (var c in Geometry.Cells(b))
            {
                if (!any) { min = max = c; any = true; }
                else { min = Int3.Min(min, c); max = Int3.Max(max, c); }
            }
        }
        return (min, max);
    }

    public BlueprintContainer ToContainer() => new(Picture)
    {
        Version = HeaderVersion,
        Creator = HeaderCreator,
        StructureRaw = BuildStructureMessage().ToBytes(),
        IdentifierRaw = Identifier.ToBytes(),
        MetaRaw = Meta.ToBytes(),
    };

    public byte[] ToPng() => ToContainer().Write();

    // ---- snapshots for undo ----------------------------------------------------------------------

    internal sealed record Snapshot(byte[] Structure, byte[] Meta, int[] Ids, bool Changed);

    internal Snapshot TakeSnapshot() =>
        new(BuildStructureMessage(forSnapshot: true).ToBytes(), Meta.ToBytes(), Blocks.Select(b => b.Id).ToArray(), StructureChanged);

    internal void Restore(Snapshot snap)
    {
        LoadStructure(ProtoMessage.Parse(snap.Structure), snap.Ids);
        Meta = ProtoMessage.Parse(snap.Meta);
        StructureChanged = snap.Changed;
    }

    public static float ReadFloat(byte[] b) => BinaryPrimitives.ReadSingleLittleEndian(b);
}

/// <summary>Structure edges in their original order and orientation (so an untouched save is byte-identical).</summary>
public sealed class EdgeSet : IEnumerable<(Node A, Node B)>
{
    readonly List<(Node A, Node B)> _list = [];
    readonly HashSet<(long, long)> _keys = [];

    static (long, long) Key(Node a, Node b) => a.Serial < b.Serial ? (a.Serial, b.Serial) : (b.Serial, a.Serial);

    public int Count => _list.Count;
    public bool Contains(Node a, Node b) => _keys.Contains(Key(a, b));

    public bool Add(Node a, Node b)
    {
        if (ReferenceEquals(a, b) || !_keys.Add(Key(a, b))) return false;
        _list.Add((a, b));
        return true;
    }

    public int RemoveWhere(Func<Node, Node, bool> pred)
    {
        int n = 0;
        for (int i = _list.Count - 1; i >= 0; i--)
        {
            var (a, b) = _list[i];
            if (!pred(a, b)) continue;
            _keys.Remove(Key(a, b));
            _list.RemoveAt(i);
            n++;
        }
        return n;
    }

    public void Clear() { _list.Clear(); _keys.Clear(); }
    public IEnumerator<(Node A, Node B)> GetEnumerator() => _list.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
