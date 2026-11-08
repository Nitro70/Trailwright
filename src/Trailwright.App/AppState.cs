using System.Text.Json;
using System.Text.Json.Serialization;
using Trailwright.Library;
using Trailwright.Model;

namespace Trailwright.App;

public sealed class Settings
{
    public bool AiEnabled { get; set; }
    public int AiPort { get; set; } = 7341;
    public bool AiCanSave { get; set; } = true;
    public string? TrailmakersFolder { get; set; }

    /// <summary>Set when command-line overrides are in effect (testing): nothing is written to disk then.</summary>
    [JsonIgnore] public bool Transient { get; set; }

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Trailwright", "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        if (Transient) return;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>Everything the window, the UI server and the AI port share.</summary>
public sealed class AppState
{
    public Catalog Catalog { get; } = Catalog.Default;
    public Settings Settings { get; }
    public BlueprintLibrary Library { get; }
    public BuildEditor Editor { get; }
    public McpHost Mcp { get; }
    public string? LastAiActivity { get; set; }

    public AppState(Settings settings)
    {
        Settings = settings;
        Library = new BlueprintLibrary(Catalog, settings.TrailmakersFolder);
        Editor = new BuildEditor(Library.New("New build"));
        Mcp = new McpHost(this);
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    // ---- shared views of the build (UI and AI port use the same shapes) ---------------------------

    public object BlockView(PlacedBlock b, bool detail = false)
    {
        var (c1, c2) = BuildEditor.GetColor(b);
        var (kp, kn) = BuildEditor.GetKeys(b);
        var cells = Geometry.Cells(b).ToList();
        var mn = cells.Aggregate(Int3.Min);
        var mx = cells.Aggregate(Int3.Max);
        var sliders = (b.Type?.Sliders ?? []).Select(s => new
        {
            index = s.Index,
            label = s.Label ?? $"Setting {s.Index + 1}",
            min = s.Min,
            max = s.Max,
            value = BuildEditor.GetSlider(b, s.Index),
        }).ToList();
        var bp = b.Base.Pos;
        var r = b.Base.Rot;
        object view = new
        {
            id = b.Id,
            type = b.Name,
            guid = b.TypeGuid,
            pos = new[] { bp.X, bp.Y, bp.Z },
            rot = new[] { r.Ex, r.Ey, r.Ez },
            matrix = r.Matrix,
            cellsMin = mn.ToArray(),
            cellsMax = mx.ToArray(),
            color = c1,
            color2 = c2,
            settings = sliders.Count > 0 ? sliders : null,
            keys = kp != 0 || kn != 0 ? new { positive = Catalog.ActionName(kp), negative = Catalog.ActionName(kn) } : null,
            parts = b.Nodes.Count > 1 ? b.Nodes.Count : (int?)null,
        };
        if (!detail) return view;
        var connected = Editor.Build.Edges
            .Where(e => (e.A.Block == b) != (e.B.Block == b))
            .Select(e => (e.A.Block == b ? e.B : e.A).Block.Id).Distinct().OrderBy(i => i).ToList();
        return new { block = view, connectedTo = connected };
    }

    public object TypeView(BlockType t, bool detail = false)
    {
        var size = t.Size;
        object v = new
        {
            guid = t.Guid,
            name = t.Name,
            category = Catalog.CategoryName(t),
            size = new[] { size.X, size.Y, size.Z },
            gridMin = t.GridMin,
            gridMax = t.GridMax,
            paintable = t.Tintable,
            settings = t.Sliders.Count > 0 ? t.Sliders.Select(s => new { index = s.Index, label = s.Label, min = s.Min, max = s.Max }) : null,
            parts = t.Knobs.Count > 1 ? t.Knobs.Count : (int?)null,
        };
        if (!detail) return v;
        // attachment points of the unrotated block: the cell they sit on and the face direction
        var knobs = t.Knobs.Count == 0 ? [] : t.Knobs[0].Select(k =>
        {
            var n = new Int3(k[3], k[4], k[5]);
            var cell = new Int3((k[0] - n.X) / 2, (k[1] - n.Y) / 2, (k[2] - n.Z) / 2);
            return new { cell = cell.ToArray(), face = FaceName(n) };
        }).ToList();
        return new { type = v, attachPoints = knobs, credits = t.Credits, powerCores = t.PowerCores, complexity = t.Complexity };
    }

    public static string FaceName(Int3 n) => (n.X, n.Y, n.Z) switch
    {
        (1, 0, 0) => "+x (right)",
        (-1, 0, 0) => "-x (left)",
        (0, 1, 0) => "+y (top)",
        (0, -1, 0) => "-y (bottom)",
        (0, 0, 1) => "+z (front)",
        (0, 0, -1) => "-z (back)",
        _ => n.ToString(),
    };

    public object StateView()
    {
        lock (Editor.Gate) return StateViewUnlocked();
    }

    object StateViewUnlocked() => new
    {
        revision = Editor.Revision,
        name = Editor.Build.Name,
        description = Editor.Build.Description,
        blocks = Editor.Build.Blocks.Count,
        dirty = Editor.Dirty,
        source = Editor.SourcePath,
        unrestricted = Editor.Unrestricted,
        ai = new { enabled = Settings.AiEnabled, port = Settings.AiPort, running = Mcp.Running, url = Mcp.Url, error = Mcp.Error, canSave = Settings.AiCanSave, lastActivity = LastAiActivity },
        folder = Library.Root,
    };

    public object SceneView()
    {
        lock (Editor.Gate)
        {
            var (mn, mx) = Editor.Build.GridBounds();
            return new
            {
                revision = Editor.Revision,
                name = Editor.Build.Name,
                description = Editor.Build.Description,
                boundsMin = mn.ToArray(),
                boundsMax = mx.ToArray(),
                blocks = Editor.Build.Blocks.Select(b => BlockView(b)).ToList(),
            };
        }
    }

    /// <summary>Rotation from quarter turns or degrees about X, Y, Z (multiples of 90).</summary>
    public static Rot ParseRot(int? x, int? y, int? z)
    {
        static int Q(int? v)
        {
            long a = v ?? 0;
            if (Math.Abs(a) >= 4)
            {
                if (a % 90 != 0) throw new EditException("rotations are multiples of 90 degrees (or quarter turns 0..3)");
                a /= 90;
            }
            return (int)(((a % 4) + 4) % 4);
        }
        return Rot.FromEuler(Q(x), Q(y), Q(z));
    }

    public int? ParseColor(string? c)
    {
        if (string.IsNullOrWhiteSpace(c)) return null;
        if (int.TryParse(c, out var i)) return i;
        var hex = c.StartsWith('#') ? c : null;
        var target = hex ?? NamedColor(c) ?? throw new EditException($"unknown colour '{c}': use a palette index 0..{Catalog.Palette.Count - 1}, a #rrggbb value or a basic colour name");
        return NearestPalette(target);
    }

    static string? NamedColor(string name) => name.Trim().ToLowerInvariant() switch
    {
        "red" => "#e60000", "dark red" => "#710000", "pink" => "#e91e63", "purple" => "#9c27b0", "navy" => "#17369f",
        "blue" => "#0068ff", "light blue" => "#00b1ff", "cyan" => "#3af8ff", "dark green" => "#314d32", "green" => "#34bc39",
        "lime" => "#7de00d", "yellow" => "#ffeb3b", "amber" => "#ffc107", "orange" => "#ff9800", "brown" => "#715337",
        "tan" => "#be9b77", "grey" or "gray" => "#9e9e9e", "dark grey" or "dark gray" => "#404040", "white" => "#ffffff",
        "black" => "#000000", _ => null,
    };

    int NearestPalette(string hex)
    {
        int R(string h, int o) => Convert.ToInt32(h.Substring(o, 2), 16);
        int tr = R(hex, 1), tg = R(hex, 3), tb = R(hex, 5);
        int best = 0, bestD = int.MaxValue;
        for (int i = 0; i < Catalog.Palette.Count; i++)
        {
            var p = Catalog.Palette[i];
            int d = (R(p, 1) - tr) * (R(p, 1) - tr) + (R(p, 3) - tg) * (R(p, 3) - tg) + (R(p, 5) - tb) * (R(p, 5) - tb);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    public int? ParseAction(string? a)
    {
        if (string.IsNullOrWhiteSpace(a)) return null;
        if (int.TryParse(a, out var i)) return i;
        return Catalog.ActionValue(a) ?? throw new EditException($"unknown key action '{a}' (see list_key_actions)");
    }

    public string Save(bool asNew)
    {
        lock (Editor.Gate)
        {
            var b = Editor.Build;
            string? path = asNew ? null : Editor.SourcePath;
            if (path != null && !Path.GetFullPath(path).StartsWith(Library.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) path = null;
            var saved = Library.Save(b, path, rerenderPicture: b.StructureChanged || path == null);
            Editor.SourcePath = saved;
            Editor.MarkSaved();
            return saved;
        }
    }
}
