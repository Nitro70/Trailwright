using Trailwright.Format;
using Trailwright.Model;
using Xunit;
using Xunit.Abstractions;

namespace Trailwright.Tests;

/// <summary>Regressions found in review: logic links after removals, undo and repeated saves; Power Coupling sliders.</summary>
public class EditRegressionTests(ITestOutputHelper log)
{
    /// <summary>Every logic link as (self position, self type, list, target position, target type), read back from saved bytes.</summary>
    static HashSet<string> LinkSignature(byte[] structure)
    {
        var s = ProtoMessage.Parse(structure);
        var nodes = s.GetMessages(1).ToList();
        string Id(ProtoMessage n)
        {
            var p = n.GetMessage(1) ?? new ProtoMessage();
            var g = n.GetMessage(7);
            return $"{p.GetInt32(1)},{p.GetInt32(2)},{p.GetInt32(3)}:{n.GetInt32(3)}:{Convert.ToHexString(g?.Get(1)?.Bytes ?? [])}";
        }
        var sig = new HashSet<string>();
        for (int i = 0; i < nodes.Count; i++)
        {
            var setup = nodes[i].GetMessage(6);
            if (setup == null) continue;
            foreach (int lf in new[] { 1, 2 })
                foreach (var e in setup.GetMessages(lf))
                {
                    int t = i + e.GetInt32(1);
                    string target = t >= 0 && t < nodes.Count ? Id(nodes[t]) : "out-of-range";
                    sig.Add($"{Id(nodes[i])} [{lf}] -> {target}");
                }
        }
        return sig;
    }

    static IEnumerable<string> FilesWithLinks()
    {
        foreach (var f in RealBlueprintTests.Files().Select(x => (string)x[0]))
        {
            var c = BlueprintContainer.Read(File.ReadAllBytes(f));
            if (LinkSignature(c.StructureRaw).Count > 3) yield return f;
        }
    }

    [Fact]
    public void LinksSurviveRemovalUndoRedoAndRepeatedSaves()
    {
        int tested = 0;
        foreach (var f in FilesWithLinks().Take(6))
        {
            var c = BlueprintContainer.Read(File.ReadAllBytes(f));
            var build = Build.Load(c, Catalog.Default);
            var ed = new BuildEditor(build);
            var before = LinkSignature(c.StructureRaw);
            // remove a block that has no links of its own, roughly in the middle of the node list
            var victim = build.Blocks.Skip(build.Blocks.Count / 3)
                .FirstOrDefault(b => b.Nodes.All(n => (n.Msg.GetMessage(6)?.Has(1) ?? false) == false && (n.Msg.GetMessage(6)?.Has(2) ?? false) == false)
                                     && !build.Nodes.Any(n => n.Links.Any(l => l.structureTarget != null && b.Nodes.Contains(l.structureTarget))));
            if (victim == null) continue;
            ed.Remove([victim.Id]);
            var afterRemove = LinkSignature(build.BuildStructureMessage().ToBytes());
            Assert.Equal(before, afterRemove);
            Assert.True(ed.Undo());
            Assert.True(ed.Redo());
            Assert.Throws<EditException>(() => ed.SetColor([build.Blocks[0].Id], 9999, null)); // failed edit rolls back
            var first = build.BuildStructureMessage().ToBytes();
            var second = build.BuildStructureMessage().ToBytes();
            Assert.Equal(before, LinkSignature(first));
            Assert.Equal(before, LinkSignature(second));
            var reread = Build.Load(BlueprintContainer.Read(build.ToPng()), Catalog.Default);
            Assert.Equal(before, LinkSignature(reread.BuildStructureMessage().ToBytes()));
            tested++;
        }
        log.WriteLine($"{tested} linked blueprints checked");
    }

    [Fact]
    public void RemovingALinkTargetDropsOnlyThatLinkAcrossTwoSaves()
    {
        foreach (var f in FilesWithLinks().Take(4))
        {
            var build = Build.Load(BlueprintContainer.Read(File.ReadAllBytes(f)), Catalog.Default);
            var ed = new BuildEditor(build);
            var owner = build.Nodes.FirstOrDefault(n => n.Links.Count >= 2 && n.Links.All(l => l.structureTarget != null));
            if (owner == null) continue;
            var target = owner.Links[0].structureTarget!.Block;
            if (target == owner.Block) continue;
            int expected = owner.Links.Count(l => l.structureTarget!.Block != target);
            ed.Remove([target.Id]);
            build.BuildStructureMessage();
            build.BuildStructureMessage();
            var setup = owner.Setup;
            int left = setup.GetMessages(1).Count() + setup.GetMessages(2).Count();
            Assert.Equal(expected, left);
            Assert.All(owner.Links, l => Assert.NotEqual(target, l.structureTarget!.Block));
            return;
        }
    }

    [Fact]
    public void PowerCouplingSlidersUseTheirOwnSetupType()
    {
        var pc = Catalog.Default.Blocks.First(b => b.Behaviour == "PowerConduitBlockBehaviour");
        var anchor = Catalog.Default.Blocks.First(b => b.Name == "Block 2x2x2");
        var build = new Build(Catalog.Default, new RgbaImage(64, 64));
        var ed = new BuildEditor(build);
        ed.Place(anchor, new Int3(0, 0, 0), Rot.Identity);
        var fit = Geometry.FitsCovering(build, pc, new Int3(0, 2, 0)).First();
        var blk = ed.Place(pc, fit.Pos, fit.Rot).Block;
        ed.SetSlider(blk.Id, 1, 0.5f);
        var setup = blk.Base.Setup;
        Assert.False(setup.Has(6));
        Assert.Equal(0.5f, setup.GetMessage(19)!.GetFloat(3));
        Assert.Equal(0.5f, BuildEditor.GetSlider(blk, 1));
    }

    [Fact]
    public void BlockIdsAreNeverReusedAcrossBuilds()
    {
        var anchor = Catalog.Default.Blocks.First(b => b.Name == "Block 2x2x2");
        var a = new BuildEditor(new Build(Catalog.Default, new RgbaImage(8, 8)));
        var id = a.Place(anchor, default, Rot.Identity).Block.Id;
        var b = new BuildEditor(new Build(Catalog.Default, new RgbaImage(8, 8)));
        var id2 = b.Place(anchor, default, Rot.Identity).Block.Id;
        Assert.NotEqual(id, id2);
        Assert.Throws<EditException>(() => b.Get(id));
    }

    [Fact]
    public void MalformedFilesFailWithInvalidData()
    {
        var rnd = new Random(7);
        for (int i = 0; i < 300; i++)
        {
            var junk = new byte[rnd.Next(1, 200)];
            rnd.NextBytes(junk);
            try { ProtoMessage.Parse(junk); } catch (InvalidDataException) { }
            try { Lz4Frame.Decode([0x04, 0x22, 0x4D, 0x18, .. junk]); } catch (InvalidDataException) { }
            try { Png.Decode([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. junk]); } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { }
        }
    }
}
