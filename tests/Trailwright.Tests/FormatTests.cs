using Trailwright.Format;
using Trailwright.Model;
using Xunit;

namespace Trailwright.Tests;

public class FormatTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(13)]
    [InlineData(5000)]
    [InlineData(200_000)]
    public void Lz4RoundTrip(int size)
    {
        var rnd = new Random(size);
        var data = new byte[size];
        for (int i = 0; i < size; i++) data[i] = (byte)(rnd.Next(4) == 0 ? rnd.Next(256) : i % 7);
        Assert.Equal(data, Lz4Frame.Decode(Lz4Frame.Encode(data)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Blueprint #209")]
    [InlineData("a car with the fastest wheels éè \U0001F680")]
    public void SmazRoundTrip(string s)
    {
        var b = System.Text.Encoding.UTF8.GetBytes(s);
        Assert.Equal(b, Smaz.Decompress(Smaz.Compress(b)));
    }

    [Fact]
    public void SmazRoundTripAllBytes()
    {
        var b = Enumerable.Range(0, 256).Select(i => (byte)i).Concat(Enumerable.Repeat((byte)'x', 600)).ToArray();
        Assert.Equal(b, Smaz.Decompress(Smaz.Compress(b)));
    }

    [Fact]
    public void PngRoundTrip()
    {
        var img = new RgbaImage(37, 21);
        new Random(1).NextBytes(img.Pixels);
        var back = Png.Decode(Png.Encode(img));
        Assert.Equal(img.Pixels, back.Pixels);
    }

    [Fact]
    public void ProtoNegativeIntsUseTenByteVarints()
    {
        var m = new ProtoMessage();
        m.SetInt(4, -1);
        Assert.Equal(11, m.ToBytes().Length);
        Assert.Equal(-1, ProtoMessage.Parse(m.ToBytes()).GetInt32(4));
    }

    [Fact]
    public void RotationsAre24AndCompose()
    {
        Assert.Equal(24, Rot.All.Count);
        foreach (var a in Rot.All)
            foreach (var b in Rot.All)
            {
                var v = new Int3(1, 2, 3);
                Assert.Equal(b.Apply(a.Apply(v)), a.Then(b).Apply(v));
            }
        Assert.All(Rot.All, r => Assert.NotEqual(2, r.Ex));
    }

    [Fact]
    public void CatalogLoads()
    {
        var c = Catalog.Default;
        Assert.True(c.Blocks.Count > 400);
        Assert.Equal(56, c.Palette.Count);
        Assert.NotNull(c.Resolve("Propeller"));
    }
}
