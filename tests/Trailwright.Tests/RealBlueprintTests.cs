using Trailwright.Format;
using Trailwright.Model;
using Xunit;
using Xunit.Abstractions;

namespace Trailwright.Tests;

/// <summary>Runs against the blueprints in the local Trailmakers folder when it exists (read-only; nothing is written there).</summary>
public class RealBlueprintTests(ITestOutputHelper log)
{
    static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TrailMakers");

    public static IEnumerable<object[]> Files()
    {
        foreach (var dir in new[] { "Blueprints", "autosave", "Transformation slots" })
        {
            var d = Path.Combine(Root, dir);
            if (!Directory.Exists(d)) continue;
            foreach (var f in Directory.EnumerateFiles(d, "*.png", SearchOption.AllDirectories)) yield return [f];
        }
    }

    [Fact]
    public void AllRealBlueprintsRoundTrip()
    {
        var files = Files().Select(f => (string)f[0]).ToList();
        if (files.Count == 0) { log.WriteLine("no local blueprints, skipped"); return; }
        int ok = 0;
        foreach (var f in files)
        {
            var bytes = File.ReadAllBytes(f);
            var c = BlueprintContainer.Read(bytes);
            var build = Build.Load(c, Catalog.Default);
            var again = build.ToContainer();
            Assert.True(c.StructureRaw.AsSpan().SequenceEqual(again.StructureRaw), $"structure differs: {f}");
            Assert.True(c.MetaRaw.AsSpan().SequenceEqual(again.MetaRaw), $"meta differs: {f}");
            Assert.True(c.IdentifierRaw.AsSpan().SequenceEqual(again.IdentifierRaw), $"identifier differs: {f}");
            var png = again.Write();
            var back = BlueprintContainer.Read(png);
            Assert.Equal(c.StructureRaw, back.StructureRaw);
            Assert.Equal(c.MetaRaw, back.MetaRaw);
            Assert.Equal(c.IdentifierRaw, back.IdentifierRaw);
            // the visible picture survives: every opaque pixel except the length pixel is unchanged
            var a = c.Image.Pixels; var b = Png.Decode(png).Pixels;
            int last = c.Image.UnityOffset(c.Image.Width * c.Image.Height - 1);
            for (int i = 0; i < a.Length; i += 4)
                if (a[i + 3] != 0 && i != last)
                    Assert.True(a[i] == b[i] && a[i + 1] == b[i + 1] && a[i + 2] == b[i + 2] && a[i + 3] == b[i + 3], $"picture changed: {f}");
            ok++;
        }
        log.WriteLine($"{ok} blueprints round-tripped");
    }

    [Fact]
    public void EditsKeepTheFileReadable()
    {
        var f = Files().Select(x => (string)x[0]).FirstOrDefault();
        if (f == null) return;
        var build = Build.Load(BlueprintContainer.Read(File.ReadAllBytes(f)), Catalog.Default);
        var ed = new BuildEditor(build);
        int before = build.Blocks.Count;
        var cube = Catalog.Default.Blocks.First(b => b.Name == "Block 1x2" && b.Template != null);
        var (mn, mx) = build.GridBounds();
        // stack a column of blocks on top of the highest block at the build's top layer
        var top = build.Blocks.Where(b => b.Type != null).SelectMany(b => Geometry.Cells(b).Select(c => (b, c))).OrderByDescending(t => t.c.Y).First();
        var r = ed.Place(cube, top.c + new Int3(0, 1, 0), Rot.Identity, color: 5, allowFloating: true);
        log.WriteLine($"placed #{r.Block.Id} with {r.Connections} connections; warnings: {string.Join(";", r.Warnings)}");
        ed.SetColor([r.Block.Id], 1, null);
        var png = build.ToPng();
        var reread = Build.Load(BlueprintContainer.Read(png), Catalog.Default);
        Assert.Equal(before + 1, reread.Blocks.Count);
        Assert.False(reread.Structure.Has(10)); // weld data dropped so the game recomputes it
        ed.Remove([r.Block.Id]);
        Assert.Equal(before, build.Blocks.Count);
        Assert.True(ed.Undo());
        Assert.Equal(before + 1, build.Blocks.Count);
    }
}

public class ConnectionTests(ITestOutputHelper log)
{
    [Fact]
    public void ReplacingABlockRecreatesItsOriginalConnections()
    {
        var files = RealBlueprintTests.Files().Select(f => (string)f[0]).Take(25).ToList();
        if (files.Count == 0) return;
        int checkedBlocks = 0, matched = 0;
        foreach (var f in files)
        {
            var build = Build.Load(BlueprintContainer.Read(File.ReadAllBytes(f)), Catalog.Default);
            foreach (var blk in build.Blocks.Where(b => b.Type?.Template != null && b.Nodes.Count == 1).Take(15).ToList())
            {
                var original = build.Edges.Where(e => (e.A.Block == blk) != (e.B.Block == blk))
                    .Select(e => e.A.Block == blk ? e.B : e.A).ToHashSet();
                var computed = Geometry.Connections(build, blk).Select(c => c.Other).ToHashSet();
                checkedBlocks++;
                if (original.SetEquals(computed)) matched++;
            }
        }
        log.WriteLine($"{matched}/{checkedBlocks} blocks: knob rule gives exactly the saved connections");
        Assert.True(matched >= checkedBlocks * 0.95, $"{matched}/{checkedBlocks}");
    }

    [Fact]
    public void WritesAFileForExternalCheck()
    {
        var f = RealBlueprintTests.Files().Select(x => (string)x[0]).LastOrDefault();
        if (f == null) return;
        var build = Build.Load(BlueprintContainer.Read(File.ReadAllBytes(f)), Catalog.Default);
        build.Name = "Trailwright external check";
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "trailwright-check.png"), build.ToPng());
    }
}
