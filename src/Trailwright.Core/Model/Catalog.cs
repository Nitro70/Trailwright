using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trailwright.Model;

public sealed class SliderInfo
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("min")] public float? Min { get; set; }
    [JsonPropertyName("max")] public float? Max { get; set; }
    [JsonPropertyName("unit")] public float? Unit { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
}

public sealed class BlockType
{
    [JsonPropertyName("guid")] public string Guid { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("asset")] public string Asset { get; set; } = "";
    [JsonPropertyName("category")] public int Category { get; set; }
    [JsonPropertyName("categories")] public List<int> Categories { get; set; } = [];
    [JsonPropertyName("subCategory")] public int SubCategory { get; set; }
    [JsonPropertyName("sort")] public int Sort { get; set; }
    [JsonPropertyName("gridMin")] public int[] GridMin { get; set; } = [0, 0, 0];
    [JsonPropertyName("gridMax")] public int[] GridMax { get; set; } = [0, 0, 0];
    [JsonPropertyName("credits")] public int Credits { get; set; }
    [JsonPropertyName("powerCores")] public int PowerCores { get; set; }
    [JsonPropertyName("complexity")] public int Complexity { get; set; }
    [JsonPropertyName("tintable")] public bool Tintable { get; set; }
    [JsonPropertyName("secondaryColor")] public bool SecondaryColor { get; set; }
    /// <summary>Per part: knobs as [px,py,pz,nx,ny,nz] in half-cell ("double grid") units.</summary>
    [JsonPropertyName("knobs")] public List<List<int[]>> Knobs { get; set; } = [];
    /// <summary>Default saved node(s) the game ships for this block (protobuf StructureGraphSaveData, base64).</summary>
    [JsonPropertyName("template")] public string? Template { get; set; }
    [JsonPropertyName("behaviour")] public string? Behaviour { get; set; }
    [JsonPropertyName("sliders")] public List<SliderInfo> Sliders { get; set; } = [];

    [JsonIgnore] public Guid Id => System.Guid.Parse(Guid);
    [JsonIgnore] public Int3 Min => new(GridMin[0], GridMin[1], GridMin[2]);
    [JsonIgnore] public Int3 Max => new(GridMax[0], GridMax[1], GridMax[2]);
    [JsonIgnore] public Int3 Size => Max - Min + new Int3(1, 1, 1);
}

public sealed class Catalog
{
    [JsonPropertyName("palette")] public List<string> Palette { get; set; } = [];
    [JsonPropertyName("actions")] public Dictionary<string, string> Actions { get; set; } = [];
    [JsonPropertyName("categoryNames")] public Dictionary<string, string> CategoryNames { get; set; } = [];
    [JsonPropertyName("inventoryCategoryNames")] public Dictionary<string, string> InventoryCategoryNames { get; set; } = [];
    [JsonPropertyName("subCategoryNames")] public Dictionary<string, string> SubCategoryNames { get; set; } = [];
    [JsonPropertyName("blocks")] public List<BlockType> Blocks { get; set; } = [];

    [JsonIgnore] public Dictionary<Guid, BlockType> ById { get; private set; } = [];

    static Catalog? _default;
    public static Catalog Default => _default ??= LoadEmbedded();

    public static Catalog LoadEmbedded()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Trailwright.catalog.json")
                      ?? throw new InvalidOperationException("catalog resource missing");
        var c = JsonSerializer.Deserialize<Catalog>(s) ?? throw new InvalidDataException("bad catalog");
        c.ById = c.Blocks.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First());
        return c;
    }

    public BlockType? Find(Guid id) => ById.GetValueOrDefault(id);

    /// <summary>Resolve a block type from a GUID string or an (exact, then partial) name.</summary>
    public BlockType? Resolve(string guidOrName)
    {
        if (System.Guid.TryParse(guidOrName, out var g)) return Find(g);
        var exact = Blocks.Where(b => string.Equals(b.Name, guidOrName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) return exact.OrderByDescending(b => b.Template != null).ThenBy(b => b.Asset.Length).First();
        return Blocks.FirstOrDefault(b => b.Name.Contains(guidOrName, StringComparison.OrdinalIgnoreCase));
    }

    public string CategoryName(BlockType b) => CategoryNames.GetValueOrDefault(b.Category.ToString(), b.Category.ToString());

    public int? ActionValue(string name) =>
        Actions.FirstOrDefault(kv => string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase)) is { Key: not null } kv
            ? int.Parse(kv.Key) : null;

    public string ActionName(int v) => Actions.GetValueOrDefault(v.ToString(), v.ToString());
}
