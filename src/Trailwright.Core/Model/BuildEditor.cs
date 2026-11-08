using System.Text;
using Trailwright.Format;

namespace Trailwright.Model;

public sealed class EditException(string message) : Exception(message);

public sealed record PlaceResult(PlacedBlock Block, int Connections, List<string> Warnings);

/// <summary>
/// All edits to a <see cref="Build"/> go through here: validation, connection edges, undo/redo.
/// The UI and the AI port share one instance; every public method takes the lock.
/// </summary>
public sealed class BuildEditor
{
    readonly object _gate = new();
    readonly List<Build.Snapshot> _undo = [];
    readonly List<Build.Snapshot> _redo = [];
    const int MaxUndo = 200;

    public Build Build { get; private set; }
    public string? SourcePath { get; set; }
    /// <summary>Set by the app's checkbox only (the AI port cannot change it): lifts the in-game slider caps.</summary>
    public bool Unrestricted { get; set; }
    public int Revision { get; private set; }
    public bool Dirty { get; private set; }
    public event Action? Changed;

    public BuildEditor(Build build) => Build = build;

    public object Gate => _gate;

    public void Replace(Build build, string? sourcePath)
    {
        lock (_gate)
        {
            Build = build;
            SourcePath = sourcePath;
            _undo.Clear(); _redo.Clear();
            Dirty = false;
            Revision++;
        }
        Changed?.Invoke();
    }

    T Mutate<T>(Func<T> action)
    {
        T r;
        lock (_gate)
        {
            var snap = Build.TakeSnapshot();
            try { r = action(); }
            catch { Build.Restore(snap); throw; }
            _undo.Add(snap);
            if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
            _redo.Clear();
            Dirty = true;
            Revision++;
        }
        Changed?.Invoke();
        return r;
    }

    public bool Undo() => Step(_undo, _redo);
    public bool Redo() => Step(_redo, _undo);

    bool Step(List<Build.Snapshot> from, List<Build.Snapshot> to)
    {
        lock (_gate)
        {
            if (from.Count == 0) return false;
            to.Add(Build.TakeSnapshot());
            var s = from[^1];
            from.RemoveAt(from.Count - 1);
            Build.Restore(s);
            Dirty = true;
            Revision++;
        }
        Changed?.Invoke();
        return true;
    }

    public void MarkSaved() { lock (_gate) Dirty = false; }

    // ---- placement --------------------------------------------------------------------------------

    public PlaceResult Place(BlockType type, Int3 pos, Rot rot, int? color = null, int? color2 = null,
                             bool allowFloating = false, bool allowOverlap = false)
        => Mutate(() => PlaceInternal(type, pos, rot, color, color2, allowFloating, allowOverlap));

    public List<PlaceResult> PlaceMany(IEnumerable<(BlockType Type, Int3 Pos, Rot Rot, int? Color, int? Color2)> items,
                                       bool allowFloating = false, bool allowOverlap = false)
        => Mutate(() => items.Select(i => PlaceInternal(i.Type, i.Pos, i.Rot, i.Color, i.Color2, allowFloating, allowOverlap)).ToList());

    PlaceResult PlaceInternal(BlockType type, Int3 pos, Rot rot, int? color, int? color2, bool allowFloating, bool allowOverlap)
    {
        var b = Build;
        var warnings = new List<string>();
        if (!allowOverlap)
        {
            var occ = Geometry.Occupancy(b);
            foreach (var c in Geometry.Cells(type, pos, rot))
                if (occ.TryGetValue(c, out var other))
                    throw new EditException($"cell {c} is already taken by block #{other.Id} ({other.Name})");
        }
        var blk = new PlacedBlock { Id = Build.NextId(), Type = type };
        var templateNodes = TemplateNodes(type);
        foreach (var (msg, parentLocal) in templateNodes)
        {
            var n = new Node(msg) { Block = blk };
            var tPos = n.Pos;
            var tRot = n.Rot;
            n.Pos = rot.Apply(tPos) + pos;
            n.Rot = tRot.Then(rot);
            n.TypeGuid = type.Id;
            blk.Nodes.Add(n);
        }
        for (int i = 0; i < templateNodes.Count; i++)
        {
            int p = templateNodes[i].parentLocal;
            blk.Nodes[i].Parent = p >= 0 && p < blk.Nodes.Count && p != i ? blk.Nodes[p] : null;
            if (blk.Nodes[i].Parent == null) blk.Nodes[i].Msg.SetInt(4, -1);
        }
        b.Nodes.AddRange(blk.Nodes);
        b.Blocks.Add(blk);
        foreach (var (a, c) in TemplateEdges(type))
            if (a < blk.Nodes.Count && c < blk.Nodes.Count) b.Edges.Add(blk.Nodes[a], blk.Nodes[c]);
        int conns = Connect(blk);
        if (conns == 0 && b.Blocks.Count > 1)
        {
            if (!allowFloating)
                throw new EditException($"{type.Name} at {pos} would not attach to anything: no matching attachment point faces it. " +
                                        "Place it against a block face that has attachment points, or pass allow_floating.");
            warnings.Add("block is not attached to the rest of the build");
        }
        if (color != null || color2 != null) ApplyColor(blk, color, color2);
        b.StructureChanged = true;
        return new PlaceResult(blk, conns, warnings);
    }

    static List<(ProtoMessage msg, int parentLocal)> TemplateNodes(BlockType type)
    {
        var list = new List<(ProtoMessage, int)>();
        if (type.Template != null)
        {
            var t = ProtoMessage.Parse(Convert.FromBase64String(type.Template));
            foreach (var n in t.GetMessages(1))
            {
                int parent = n.GetInt32(4, 0);
                list.Add((n, parent));
            }
        }
        if (list.Count == 0)
        {
            var m = new ProtoMessage();
            m.SetMessage(1, new ProtoMessage());
            m.SetMessage(2, new ProtoMessage());
            m.SetInt(4, -1);
            m.SetMessage(6, new ProtoMessage());
            m.SetInt(9, -1);
            list.Add((m, -1));
        }
        return list;
    }

    static List<(int, int)> TemplateEdges(BlockType type)
    {
        if (type.Template == null) return [];
        var ints = ProtoMessage.Parse(Convert.FromBase64String(type.Template)).GetRepeatedInts(2).Select(v => (int)v).ToList();
        var r = new List<(int, int)>();
        for (int i = 0; i + 1 < ints.Count; i += 2) r.Add((ints[i], ints[i + 1]));
        return r;
    }

    int Connect(PlacedBlock blk)
    {
        int n = 0;
        foreach (var (mine, other) in Geometry.Connections(Build, blk))
            if (Build.Edges.Add(mine, other)) n++;
        return n;
    }

    public void Remove(IEnumerable<int> ids) => Mutate(() =>
    {
        var set = ids.Select(Get).ToHashSet();
        foreach (var blk in set)
        {
            Build.Blocks.Remove(blk);
            foreach (var n in blk.Nodes) Build.Nodes.Remove(n);
        }
        Build.Edges.RemoveWhere((a, c) => set.Contains(a.Block) || set.Contains(c.Block));
        Build.StructureChanged = true;
        return 0;
    });

    /// <summary>Move and/or rotate a block about its base position; its connections are recomputed.</summary>
    public PlaceResult Transform(int id, Int3? newPos, Rot? newRot, bool allowFloating = false, bool allowOverlap = false) => Mutate(() =>
    {
        var blk = Get(id);
        var basePos = blk.Base.Pos;
        var targetPos = newPos ?? basePos;
        var baseRot = blk.Base.Rot;
        var targetRot = newRot ?? baseRot;
        if (!allowOverlap && blk.Type != null)
        {
            var occ = Geometry.Occupancy(Build, blk);
            foreach (var c in Geometry.Cells(blk.Type, targetPos, targetRot))
                if (occ.TryGetValue(c, out var other))
                    throw new EditException($"cell {c} is already taken by block #{other.Id} ({other.Name})");
        }
        // delta rotation D so that D * baseRot = targetRot; apply D about the base position to every node
        var delta = Rot.All.First(d => baseRot.Then(d).SameMatrix(targetRot));
        foreach (var n in blk.Nodes)
        {
            var rel = n.Pos - basePos;
            n.Pos = delta.Apply(rel) + targetPos;
            n.Rot = n.Rot.Then(delta);
        }
        Build.Edges.RemoveWhere((a, c) => (a.Block == blk) != (c.Block == blk));
        int conns = Connect(blk);
        var warnings = new List<string>();
        if (conns == 0 && Build.Blocks.Count > 1)
        {
            if (!allowFloating) throw new EditException("after moving, the block would not attach to anything");
            warnings.Add("block is not attached to the rest of the build");
        }
        Build.StructureChanged = true;
        return new PlaceResult(blk, conns, warnings);
    });

    // ---- block settings -------------------------------------------------------------------------

    public void SetColor(IEnumerable<int> ids, int? primary, int? secondary) => Mutate(() =>
    {
        foreach (var id in ids) ApplyColor(Get(id), primary, secondary);
        return 0;
    });

    void ApplyColor(PlacedBlock blk, int? primary, int? secondary)
    {
        int max = Build.Catalog.Palette.Count;
        foreach (var c in new[] { primary, secondary })
            if (c is < 0 || c >= max) throw new EditException($"colour index must be 0..{max - 1}");
        foreach (var n in blk.Nodes)
        {
            var setup = n.Setup;
            if (primary != null) setup.SetMessage(25, IndexedColor(primary.Value));
            if (secondary != null) setup.SetMessage(26, IndexedColor(secondary.Value));
            n.Setup = setup;
        }
    }

    static ProtoMessage IndexedColor(int paletteIndex)
    {
        var m = new ProtoMessage();
        m.SetInt(1, paletteIndex + 1);
        return m;
    }

    public static (int? Primary, int? Secondary) GetColor(PlacedBlock blk)
    {
        var s = blk.Base.Setup;
        int? Read(int f) => s.GetMessage(f) is { } m && m.Has(1) ? m.GetInt32(1) - 1 : null;
        return (Read(25), Read(26));
    }

    /// <summary>
    /// Where a block's slider values live inside BlockBehaviourSetup. Most blocks use SliderBlockSetup
    /// (field 6; slider i in its field 1,2,3,4,6). The Power Coupling uses PowerConduitBlockSetup
    /// (field 19; sliders in fields 1 and 3, field 2 is a toggle).
    /// </summary>
    static (int Container, int[] Fields) SliderLayout(PlacedBlock blk) =>
        blk.Type?.Behaviour == "PowerConduitBlockBehaviour" ? (19, [1, 3]) : (6, [1, 2, 3, 4, 6]);

    static readonly int[] SetupSubtypes = [3, 6, 19]; // InputSeat, Slider, PowerConduit (ProtoInclude tags)

    public static float? GetSlider(PlacedBlock blk, int index)
    {
        var (container, fields) = SliderLayout(blk);
        if (index < 0 || index >= fields.Length) return null;
        var sl = blk.Base.Setup.GetMessage(container);
        if (sl == null) return null;
        var v = sl.GetFloat(fields[index]);
        return float.IsNaN(v) ? null : v;
    }

    public (float Applied, bool Capped) SetSlider(int id, int index, float value) => Mutate(() =>
    {
        var blk = Get(id);
        var (container, fields) = SliderLayout(blk);
        if (index < 0 || index >= fields.Length) throw new EditException($"{blk.Name} has settings 0..{fields.Length - 1}");
        var info = blk.Type?.Sliders.FirstOrDefault(s => s.Index == index);
        if (info == null && !Unrestricted) throw new EditException($"{blk.Name} has no setting #{index}");
        if (float.IsNaN(value) || float.IsInfinity(value)) throw new EditException("value must be a finite number");
        bool capped = false;
        if (!Unrestricted && info?.Min != null && info.Max != null)
        {
            float c = Math.Clamp(value, info.Min.Value, info.Max.Value);
            capped = c != value;
            value = c;
        }
        var setup = blk.Base.Setup;
        // protobuf-net writes exactly one subtype per setup; never add a second one next to it
        var other = SetupSubtypes.FirstOrDefault(t => t != container && setup.Has(t));
        if (other != 0) throw new EditException($"{blk.Name} stores its settings in a different way (setup type {other}); not changed");
        var sl = setup.GetMessage(container) ?? new ProtoMessage();
        sl.SetFloat(fields[index], value);
        setup.SetMessage(container, sl);
        blk.Base.Setup = setup;
        return (value, capped);
    });

    /// <summary>Key bindings: BlockBehaviourSetup 15 PositiveAction, 14 NegativeAction (EAction values).</summary>
    public void SetKeys(int id, int? positive, int? negative) => Mutate(() =>
    {
        var blk = Get(id);
        var setup = blk.Base.Setup;
        if (positive != null) setup.SetInt(15, positive.Value);
        if (negative != null) setup.SetInt(14, negative.Value);
        blk.Base.Setup = setup;
        return 0;
    });

    public static (int Positive, int Negative) GetKeys(PlacedBlock blk)
    {
        var s = blk.Base.Setup;
        return (s.GetInt32(15), s.GetInt32(14));
    }

    public void SetInfo(string? name, string? description) => Mutate(() =>
    {
        if (name != null) Build.Name = name;
        if (description != null) Build.Description = description;
        return 0;
    });

    public PlacedBlock Get(int id) => Build.FindBlock(id) ?? throw new EditException($"no block with id {id}");

    // ---- queries ----------------------------------------------------------------------------------

    /// <summary>Text picture of one horizontal layer, for models that reason better over a grid than a list.</summary>
    public string LayerMap(int y, Int3? from = null, Int3? to = null)
    {
        lock (_gate)
        {
            var occ = Geometry.Occupancy(Build);
            var (mn, mx) = Build.GridBounds();
            int x0 = from?.X ?? mn.X - 1, x1 = to?.X ?? mx.X + 1, z0 = from?.Z ?? mn.Z - 1, z1 = to?.Z ?? mx.Z + 1;
            if ((long)(x1 - x0 + 1) * (z1 - z0 + 1) > 120L * 120 || x1 < x0 || z1 < z0)
                throw new EditException("area too large (max 120 x 120 cells); pass a smaller from/to box");
            var legend = new Dictionary<int, char>();
            const string glyphs = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            var sb = new StringBuilder();
            sb.AppendLine($"layer y={y}, x {x0}..{x1} left to right, z {z1}..{z0} top to bottom ('.' = empty)");
            for (int z = z1; z >= z0; z--)
            {
                sb.Append($"{z,5} ");
                for (int x = x0; x <= x1; x++)
                {
                    if (occ.TryGetValue(new Int3(x, y, z), out var b))
                    {
                        if (!legend.TryGetValue(b.Id, out var ch))
                            legend[b.Id] = ch = legend.Count < glyphs.Length ? glyphs[legend.Count] : '#';
                        sb.Append(ch);
                    }
                    else sb.Append('.');
                }
                sb.AppendLine();
            }
            foreach (var (id, ch) in legend) sb.AppendLine($"{ch} = #{id} {Build.FindBlock(id)?.Name}");
            return sb.ToString();
        }
    }
}
