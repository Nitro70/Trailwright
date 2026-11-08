using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Trailwright.Model;

namespace Trailwright.App;

/// <summary>
/// MCP tools for the builder. Every tool returns compact JSON; errors come back as
/// {"error": "..."} so a model can read why something was refused and try again.
/// </summary>
[McpServerToolType]
public sealed class BuilderTools(AppState s)
{
    BuildEditor Ed => s.Editor;

    string Run(string what, Func<object> f)
    {
        s.LastAiActivity = $"{DateTime.Now:HH:mm:ss} {what}";
        try
        {
            // one lock around the whole tool: the build cannot change between reading and editing,
            // and the reply is built from the state the edit produced (Monitor locks are re-entrant)
            lock (Ed.Gate) return JsonSerializer.Serialize(f(), AppState.Json);
        }
        catch (EditException ex) { return JsonSerializer.Serialize(new { error = ex.Message }); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or UnauthorizedAccessException
                                      or IOException or InvalidDataException or OverflowException)
        { return JsonSerializer.Serialize(new { error = ex.Message }); }
    }

    BlockType Type(string t) => s.Catalog.Resolve(t) ?? throw new EditException($"no block type '{t}' (use list_block_types)");

    [McpServerTool(Name = "builder_status", ReadOnly = true), Description("Name, block count, grid bounds, whether there are unsaved changes, whether the user enabled Unrestricted settings, and the most used block types.")]
    public string Status() => Run("builder_status", () =>
    {
        lock (Ed.Gate)
        {
            var (mn, mx) = Ed.Build.GridBounds();
            return new
            {
                name = Ed.Build.Name,
                description = Ed.Build.Description,
                blocks = Ed.Build.Blocks.Count,
                boundsMin = mn.ToArray(),
                boundsMax = mx.ToArray(),
                unsavedChanges = Ed.Dirty,
                savedAs = Ed.SourcePath is { } p ? Path.GetFileName(p) : null,
                unrestricted = Ed.Unrestricted,
                types = Ed.Build.Blocks.GroupBy(b => b.Name).OrderByDescending(g => g.Count()).Take(25).ToDictionary(g => g.Key, g => g.Count()),
            };
        }
    });

    [McpServerTool(Name = "list_block_types", ReadOnly = true), Description("Search the block catalog. Filter by text in the name and/or category (FRAME, WHEEL, PROPULSION, SEAT, MECHANIC, AERODYNAMIC, GADGETS, LIGHTS...). Returns name, size in cells, category and adjustable settings with their in-game ranges.")]
    public string ListTypes(
        [Description("Text to look for in the block name (optional)")] string? query = null,
        [Description("Category name (optional)")] string? category = null,
        [Description("Maximum results, default 40")] int limit = 40) => Run("list_block_types", () =>
    {
        var q = s.Catalog.Blocks.Where(b => b.Template != null);
        if (!string.IsNullOrWhiteSpace(query)) q = q.Where(b => b.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(category)) q = q.Where(b => s.Catalog.CategoryName(b).Contains(category, StringComparison.OrdinalIgnoreCase));
        var list = q.OrderBy(b => s.Catalog.CategoryName(b)).ThenBy(b => b.Name).ToList();
        return new
        {
            total = list.Count,
            categories = s.Catalog.CategoryNames.Values,
            types = list.Take(Math.Clamp(limit, 1, 200)).Select(b => s.TypeView(b)),
        };
    });

    [McpServerTool(Name = "block_type_info", ReadOnly = true), Description("Full details of one block type (name or GUID): occupied cells relative to its origin, every attachment point (cell and face) when unrotated, settings with ranges, costs.")]
    public string TypeInfo([Description("Block type name or GUID")] string type) => Run("block_type_info", () => s.TypeView(Type(type), detail: true));

    [McpServerTool(Name = "get_blocks", ReadOnly = true), Description("List placed blocks (id, type, position, rotation, occupied cell box, colour, settings, keys). Optionally only one type, or only blocks touching a box.")]
    public string GetBlocks(
        [Description("Only blocks whose type name contains this text")] string? type = null,
        [Description("Box corner, e.g. [-5,0,-5]")] int[]? from = null,
        [Description("Opposite box corner, e.g. [5,4,5]")] int[]? to = null,
        int offset = 0, int limit = 200) => Run("get_blocks", () =>
    {
        lock (Ed.Gate)
        {
            IEnumerable<PlacedBlock> q = Ed.Build.Blocks;
            if (!string.IsNullOrWhiteSpace(type)) q = q.Where(b => b.Name.Contains(type, StringComparison.OrdinalIgnoreCase));
            if (from is { Length: 3 } && to is { Length: 3 })
            {
                var mn = Int3.Min(new(from[0], from[1], from[2]), new(to[0], to[1], to[2]));
                var mx = Int3.Max(new(from[0], from[1], from[2]), new(to[0], to[1], to[2]));
                q = q.Where(b => Geometry.Cells(b).Any(c => c.X >= mn.X && c.X <= mx.X && c.Y >= mn.Y && c.Y <= mx.Y && c.Z >= mn.Z && c.Z <= mx.Z));
            }
            var list = q.ToList();
            return new { total = list.Count, blocks = list.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 1000)).Select(b => s.BlockView(b)) };
        }
    });

    [McpServerTool(Name = "get_block", ReadOnly = true), Description("One placed block by id, with the ids of the blocks it is connected to.")]
    public string GetBlock(int id) => Run("get_block", () => { lock (Ed.Gate) return s.BlockView(Ed.Get(id), detail: true); });

    [McpServerTool(Name = "layer_map", ReadOnly = true), Description("Text drawing of one horizontal layer (fixed Y) seen from above: each block gets a letter, '.' is empty. Good for checking shapes and gaps.")]
    public string LayerMap(int y, int[]? from = null, int[]? to = null) => Run("layer_map", () => new
    {
        map = Ed.LayerMap(y, from is { Length: 3 } ? new Int3(from[0], from[1], from[2]) : null, to is { Length: 3 } ? new Int3(to[0], to[1], to[2]) : null)
    });

    [McpServerTool(Name = "check_placement", ReadOnly = true), Description("Dry run of place_block: which cells the block would occupy, which are already taken, and which blocks it would attach to. Changes nothing.")]
    public string CheckPlacement(string type, int x, int y, int z,
        [Description("Rotation about X: quarter turns 0-3 or degrees")] int rx = 0, int ry = 0, int rz = 0) => Run("check_placement", () =>
    {
        lock (Ed.Gate)
        {
            var t = Type(type);
            var rot = AppState.ParseRot(rx, ry, rz);
            var pos = new Int3(x, y, z);
            var occ = Geometry.Occupancy(Ed.Build);
            var cells = Geometry.Cells(t, pos, rot).ToList();
            var taken = cells.Where(occ.ContainsKey).Select(c => new { cell = c.ToArray(), by = occ[c].Id }).ToList();
            var probe = ProbeConnections(t, pos, rot);
            return new
            {
                ok = taken.Count == 0 && (probe.Count > 0 || Ed.Build.Blocks.Count == 0),
                cells = cells.Select(c => c.ToArray()),
                taken,
                attachesTo = probe,
                note = probe.Count == 0 && Ed.Build.Blocks.Count > 0 ? "no attachment point of this block meets one of a neighbour; try another rotation or position" : null,
            };
        }
    });

    List<int> ProbeConnections(BlockType t, Int3 pos, Rot rot)
    {
        // knobs of the (first part of the) new block against existing knobs
        var idx = Geometry.KnobIndex(Ed.Build);
        var res = new HashSet<int>();
        if (t.Knobs.Count == 0) return [];
        foreach (var k in t.Knobs[0])
        {
            var p = rot.Apply(new Int3(k[0], k[1], k[2])) + pos * 2;
            var n = rot.Apply(new Int3(k[3], k[4], k[5]));
            if (idx.TryGetValue(p, out var others))
                foreach (var (o, on) in others) if (on == -n) res.Add(o.Block.Id);
        }
        return res.OrderBy(i => i).ToList();
    }

    [McpServerTool(Name = "find_attach_spots", ReadOnly = true), Description("Free attachment points on a placed block: for each, the empty cell in front of it and the face direction. A new block attaches there if it has an attachment point on its side facing back.")]
    public string FindAttachSpots(int id) => Run("find_attach_spots", () =>
    {
        lock (Ed.Gate)
        {
            var blk = Ed.Get(id);
            var occ = Geometry.Occupancy(Ed.Build);
            var spots = new List<object>();
            foreach (var n in blk.Nodes)
                foreach (var (p, nrm) in Geometry.Knobs(n))
                {
                    var cell = new Int3((p.X + nrm.X) / 2, (p.Y + nrm.Y) / 2, (p.Z + nrm.Z) / 2);
                    if (!occ.ContainsKey(cell)) spots.Add(new { emptyCell = cell.ToArray(), facing = AppState.FaceName(nrm) });
                }
            return new { block = id, free = spots };
        }
    });

    [McpServerTool(Name = "fit_block", ReadOnly = true), Description("Find ways to place a block type so that it covers a given cell, overlaps nothing and attaches to the build. Tries all 24 rotations and every offset; returns position + rotation pairs ready for place_block, best attached first. Use it with an emptyCell from find_attach_spots.")]
    public string FitBlock(string type, int x, int y, int z, int max = 8) => Run("fit_block", () =>
    {
        lock (Ed.Gate)
        {
            var fits = Geometry.FitsCovering(Ed.Build, Type(type), new Int3(x, y, z), Math.Clamp(max, 1, 40));
            return new
            {
                count = fits.Count,
                options = fits.Select(f => new { x = f.Pos.X, y = f.Pos.Y, z = f.Pos.Z, rx = f.Rot.Ex, ry = f.Rot.Ey, rz = f.Rot.Ez, attachesTo = f.AttachesTo }),
                note = fits.Count == 0 ? "no rotation or offset fits there; try another cell" : null,
            };
        }
    });

    public sealed record BlockSpec(string Type, int X, int Y, int Z, int Rx = 0, int Ry = 0, int Rz = 0, string? Color = null, string? Color2 = null);

    [McpServerTool(Name = "place_block"), Description("Place one block. Fails (with the reason) if cells are taken or it would not attach to the build. Colour: palette index, #rrggbb or a basic name (red, blue, black, white, grey, yellow, orange, green...).")]
    public string Place(string type, int x, int y, int z, int rx = 0, int ry = 0, int rz = 0,
        string? color = null, string? color2 = null,
        [Description("Allow a block that touches nothing (only for the first block of a separate part)")] bool allow_floating = false,
        [Description("If the given rotation does not attach, use the first rotation (same position) that does")] bool auto_rotate = false) => Run("place_block", () =>
    {
        var t = Type(type);
        var pos = new Int3(x, y, z);
        var rot = AppState.ParseRot(rx, ry, rz);
        if (auto_rotate)
        {
            lock (Ed.Gate)
            {
                var occ = Geometry.Occupancy(Ed.Build);
                var knobs = Geometry.KnobIndex(Ed.Build);
                bool Good(Rot r) => !Geometry.Cells(t, pos, r).Any(occ.ContainsKey) && (Ed.Build.Blocks.Count == 0 || Geometry.WouldConnect(knobs, t, pos, r).Count > 0);
                if (!Good(rot)) rot = Rot.All.FirstOrDefault(Good, rot);
            }
        }
        var r = Ed.Place(t, pos, rot, s.ParseColor(color), s.ParseColor(color2), allow_floating);
        return new { placed = r.Block.Id, connections = r.Connections, warnings = r.Warnings.Count > 0 ? r.Warnings : null, block = s.BlockView(r.Block) };
    });

    [McpServerTool(Name = "place_blocks"), Description("Place many blocks in one step (one undo step). Each item: {type, x, y, z, rx, ry, rz, color, color2}. Blocks are placed in order, so later ones can attach to earlier ones. If any fails, nothing is placed and the error names the item.")]
    public string PlaceMany(List<BlockSpec> blocks, bool allow_floating = false) => Run("place_blocks", () =>
    {
        var items = blocks.Select((b, i) =>
        {
            try { return (Type(b.Type), new Int3(b.X, b.Y, b.Z), AppState.ParseRot(b.Rx, b.Ry, b.Rz), s.ParseColor(b.Color), s.ParseColor(b.Color2)); }
            catch (EditException ex) { throw new EditException($"item {i}: {ex.Message}"); }
        }).ToList();
        try
        {
            var res = Ed.PlaceMany(items, allow_floating);
            return new { placed = res.Select(r => new { id = r.Block.Id, type = r.Block.Name, connections = r.Connections }) };
        }
        catch (EditException ex) { throw new EditException($"nothing placed: {ex.Message}"); }
    });

    [McpServerTool(Name = "remove_blocks", Destructive = true), Description("Remove blocks by id (one undo step).")]
    public string Remove(List<int> ids) => Run("remove_blocks", () => { Ed.Remove(ids); return new { removed = ids }; });

    [McpServerTool(Name = "move_block"), Description("Move and/or rotate a placed block (rotation is about its own position). Omitted values stay as they are.")]
    public string Move(int id, int? x = null, int? y = null, int? z = null, int? rx = null, int? ry = null, int? rz = null, bool allow_floating = false) => Run("move_block", () =>
    {
        PlacedBlock b;
        lock (Ed.Gate) b = Ed.Get(id);
        var p = b.Base.Pos;
        Int3? pos = x != null || y != null || z != null ? new Int3(x ?? p.X, y ?? p.Y, z ?? p.Z) : null;
        var cur = b.Base.Rot;
        Rot? rot = rx != null || ry != null || rz != null ? AppState.ParseRot(rx ?? cur.Ex, ry ?? cur.Ey, rz ?? cur.Ez) : null;
        var r = Ed.Transform(id, pos, rot, allow_floating);
        return new { moved = id, connections = r.Connections, block = s.BlockView(r.Block) };
    });

    [McpServerTool(Name = "paint_blocks"), Description("Paint blocks. color/color2: palette index (see get_palette), #rrggbb (nearest palette colour is used) or a basic name.")]
    public string Paint(List<int> ids, string? color = null, string? color2 = null) => Run("paint_blocks", () =>
    {
        Ed.SetColor(ids, s.ParseColor(color), s.ParseColor(color2));
        return new { painted = ids };
    });

    [McpServerTool(Name = "get_palette", ReadOnly = true), Description("The game's paint palette: index -> #rrggbb.")]
    public string Palette() => Run("get_palette", () => s.Catalog.Palette.Select((c, i) => new { index = i, color = c }));

    [McpServerTool(Name = "set_block_setting"), Description("Change one block setting (the sliders in the game's block menu, e.g. Speed, Strength, Power). setting = index or label. Values are capped to the in-game range unless the user enabled Unrestricted in the app.")]
    public string SetSetting(int id, string setting, float value) => Run("set_block_setting", () =>
    {
        PlacedBlock b;
        lock (Ed.Gate) b = Ed.Get(id);
        int index = int.TryParse(setting, out var i) ? i
            : b.Type?.Sliders.FirstOrDefault(x => string.Equals(x.Label, setting, StringComparison.OrdinalIgnoreCase))?.Index
              ?? throw new EditException($"{b.Name} has no setting '{setting}'");
        var (applied, capped) = Ed.SetSlider(id, index, value);
        return new
        {
            id,
            setting = b.Type?.Sliders.FirstOrDefault(x => x.Index == index)?.Label ?? $"#{index}",
            applied,
            capped = capped ? $"value was limited to the in-game range; only the user can switch on Unrestricted" : null,
        };
    });

    [McpServerTool(Name = "list_key_actions", ReadOnly = true), Description("Names of the input actions a block can be bound to (e.g. StructureForward, StructureBack, StructureLeft, StructureRight, StructureUp, Boost, Custom1..Custom38).")]
    public string KeyActions() => Run("list_key_actions", () =>
        s.Catalog.Actions.Values.Where(a => a.StartsWith("Structure") || a.StartsWith("Custom") || a.Contains("Boost") || a.Contains("Shoot") || a is "Brake" or "Extend" or "Compress" or "RotateCW" or "RotateCCW" or "Detach" or "None"));

    [McpServerTool(Name = "set_block_keys"), Description("Bind a block's positive/negative input to key actions (see list_key_actions), e.g. a wheel: positive StructureForward, negative StructureBack.")]
    public string SetKeys(int id, string? positive = null, string? negative = null) => Run("set_block_keys", () =>
    {
        Ed.SetKeys(id, s.ParseAction(positive), s.ParseAction(negative));
        lock (Ed.Gate) return s.BlockView(Ed.Get(id));
    });

    [McpServerTool(Name = "set_build_info"), Description("Set the build's name and/or description (shown in the game's blueprint list).")]
    public string SetInfo(string? name = null, string? description = null) => Run("set_build_info", () =>
    {
        Ed.SetInfo(name, description);
        return new { name = Ed.Build.Name, description = Ed.Build.Description };
    });

    [McpServerTool(Name = "undo"), Description("Undo the last change.")]
    public string Undo() => Run("undo", () => new { undone = Ed.Undo() });

    [McpServerTool(Name = "redo"), Description("Redo the last undone change.")]
    public string Redo() => Run("redo", () => new { redone = Ed.Redo() });

    [McpServerTool(Name = "new_build", Destructive = true), Description("Start an empty build in the builder. Refuses if there are unsaved changes unless discard_changes is true.")]
    public string NewBuild(string name = "New build", bool discard_changes = false) => Run("new_build", () =>
    {
        if (Ed.Dirty && !discard_changes) throw new EditException("the current build has unsaved changes; save it first or pass discard_changes=true");
        Ed.Replace(s.Library.New(name), null);
        return new { name };
    });

    [McpServerTool(Name = "save_build"), Description("Save the build as a Trailmakers blueprint. as_new=true writes a new file in the Blueprints folder; false overwrites the file it was opened from (the old file is backed up first). The game shows new files the next time its blueprint menu is opened.")]
    public string Save(bool as_new = true) => Run("save_build", () =>
    {
        if (!s.Settings.AiCanSave) throw new EditException("the user has not allowed the AI port to save; ask them to save from the app");
        var path = s.Save(as_new);
        return new { saved = Path.GetFileName(path), folder = Path.GetDirectoryName(path) };
    });
}
