using Trailwright.Format;
using Trailwright.Model;

namespace Trailwright.Library;

public sealed record LibraryEntry(string Path, string Folder, string File, DateTime Modified, string Name, int Blocks, string? Error);

/// <summary>
/// The user's blueprint folders (Documents\TrailMakers\...). Reading is free; every overwrite first copies
/// the original into %LOCALAPPDATA%\Trailwright\backups, and nothing is ever deleted.
/// </summary>
public sealed class BlueprintLibrary
{
    readonly Dictionary<string, (DateTime Stamp, LibraryEntry Entry)> _cache = new(StringComparer.OrdinalIgnoreCase);
    readonly Catalog _catalog;

    public string Root { get; }
    public string BackupRoot { get; }

    public BlueprintLibrary(Catalog catalog, string? root = null)
    {
        _catalog = catalog;
        Root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TrailMakers"));
        BackupRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Trailwright", "backups");
    }

    public string BlueprintsFolder => Path.Combine(Root, "Blueprints");
    static readonly string[] Folders = ["Blueprints", "autosave", "Transformation slots"];

    public List<LibraryEntry> List()
    {
        var list = new List<LibraryEntry>();
        foreach (var folder in Folders)
        {
            var dir = Path.Combine(Root, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.png", SearchOption.AllDirectories))
            {
                var stamp = System.IO.File.GetLastWriteTimeUtc(f);
                lock (_cache)
                    if (_cache.TryGetValue(f, out var c) && c.Stamp == stamp) { list.Add(c.Entry); continue; }
                LibraryEntry e;
                try
                {
                    var bp = BlueprintContainer.Read(System.IO.File.ReadAllBytes(f));
                    var meta = ProtoMessage.Parse(bp.MetaRaw);
                    int blocks = Build.Load(bp, _catalog).Blocks.Count;
                    e = new(f, Path.GetRelativePath(Root, Path.GetDirectoryName(f)!), Path.GetFileName(f), stamp.ToLocalTime(), meta.GetString(1) ?? "", blocks, null);
                }
                catch (Exception ex)
                {
                    e = new(f, Path.GetRelativePath(Root, Path.GetDirectoryName(f)!), Path.GetFileName(f), stamp.ToLocalTime(), Path.GetFileNameWithoutExtension(f), 0, ex.Message);
                }
                lock (_cache) _cache[f] = (stamp, e);
                list.Add(e);
            }
        }
        return list.OrderByDescending(e => e.Modified).ToList();
    }

    /// <summary>Only files under the TrailMakers folder may be opened or overwritten.</summary>
    public string Check(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(Root) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !full.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("only .png files inside the TrailMakers folder can be used");
        return full;
    }

    public Build Open(string path) => Build.Load(BlueprintContainer.Read(System.IO.File.ReadAllBytes(Check(path))), _catalog);

    public Build New(string name)
    {
        var b = new Build(_catalog, new RgbaImage(512, 512));
        b.Identifier.SetInt(1, 2); // StructureSaveGUID
        b.Identifier.SetString(2, Guid.NewGuid().ToString());
        b.Meta.SetString(1, name);
        b.Meta.SetString(2, "");
        b.Meta.SetString(3, "");
        var creator = LatestCreator();
        if (creator != null)
        {
            if (creator.GetString(4) is { } cn) b.Meta.SetString(4, cn);
            if (creator.GetString(5) is { } cid) b.Meta.SetString(5, cid);
            if (creator.GetString(6) is { } pl) b.Meta.SetString(6, pl);
        }
        // version fields copied from the newest real blueprint so the game sees a current save
        var latest = LatestStructure();
        if (latest != null)
            foreach (var n in new[] { 6, 7, 8 })
                if (latest.Get(n) is { } f) b.Structure.Fields.Add(f.Clone());
        b.StructureChanged = true;
        return b;
    }

    ProtoMessage? LatestCreator() => LatestContainer() is { } c ? ProtoMessage.Parse(c.MetaRaw) : null;
    ProtoMessage? LatestStructure() => LatestContainer() is { } c ? ProtoMessage.Parse(c.StructureRaw) : null;

    BlueprintContainer? LatestContainer()
    {
        if (!Directory.Exists(BlueprintsFolder)) return null;
        foreach (var f in Directory.EnumerateFiles(BlueprintsFolder, "*.png").OrderByDescending(System.IO.File.GetLastWriteTimeUtc).Take(5))
        {
            try
            {
                var c = BlueprintContainer.Read(System.IO.File.ReadAllBytes(f));
                if (!c.LegacyHeader) return c;
            }
            catch { /* not a blueprint */ }
        }
        return null;
    }

    /// <summary>Save. Overwrite backs the old file up first; "new" writes Blueprint_{timestamp}.png with a fresh identity.</summary>
    public string Save(Build build, string? overwritePath, bool rerenderPicture)
    {
        if (rerenderPicture || build.Picture.Pixels.All(b => b == 0)) build.Picture = Thumbnail.Render(build);
        string target;
        if (overwritePath != null)
        {
            target = Check(overwritePath);
            if (System.IO.File.Exists(target))
            {
                var dir = Path.Combine(BackupRoot, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}_{Random.Shared.Next(0x10000):x4}");
                Directory.CreateDirectory(dir);
                System.IO.File.Copy(target, Path.Combine(dir, Path.GetFileName(target)), overwrite: false);
            }
        }
        else
        {
            Directory.CreateDirectory(BlueprintsFolder);
            build.Identifier.SetInt(1, 2);
            build.Identifier.SetString(2, Guid.NewGuid().ToString());
            target = Path.Combine(BlueprintsFolder, $"Blueprint_{DateTime.Now:yyyyMMddHHmmssffff}.png");
            while (System.IO.File.Exists(target))
                target = Path.Combine(BlueprintsFolder, $"Blueprint_{DateTime.Now.AddTicks(Random.Shared.Next(1, 1000) * 1000):yyyyMMddHHmmssffff}.png");
        }
        var bytes = build.ToPng();
        BlueprintContainer.Read(bytes); // never write a file we cannot read back
        var tmp = target + ".trailwright-tmp";
        System.IO.File.WriteAllBytes(tmp, bytes);
        System.IO.File.Move(tmp, target, overwrite: true);
        return target;
    }
}
