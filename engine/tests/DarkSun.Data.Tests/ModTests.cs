using System.Text;
using DarkSun.Data.Assets;
using DarkSun.Data.Gff;
using DarkSun.Data.Images;
using DarkSun.Data.Mods;

namespace DarkSun.Data.Tests;

public class PngTests
{
    [Fact]
    public void EncodesAndDecodesRgba()
    {
        byte[] rgba = [255, 0, 0, 255, 0, 255, 0, 128, 0, 0, 255, 0, 10, 20, 30, 255, 1, 2, 3, 4, 5, 6, 7, 8];

        var (w, h, back) = Png.Decode(Png.Encode(3, 2, rgba));

        Assert.Equal((3, 2), (w, h));
        Assert.Equal(rgba, back);
    }

    [Fact]
    public void ImagesFromPngAreTrueColour()
    {
        byte[] png = Png.Encode(2, 1, [9, 8, 7, 255, 0, 0, 0, 0]);

        ImageFrame f = Assert.Single(ImageDecoder.ReadFrames(png));

        Assert.Equal([true, false], f.Opaque);
        Assert.Equal(new Rgb(9, 8, 7), f.ColorAt(0, new Palette([])));
        var palette = new Palette([new Rgb(0, 0, 0), new Rgb(10, 10, 10), new Rgb(200, 0, 0)]);
        Assert.Equal(1, f.IndexedFor(palette)[0]);
    }

    [Fact]
    public void ModFramesReplaceOnlyTheirFrames()
    {
        byte[] original = AcfTests.Dot(5, frames: 3);
        byte[] png = Png.Encode(1, 1, [1, 2, 3, 255]);

        var frames = ImageDecoder.ReadFrames(ModFrames.Build(original, new Dictionary<int, byte[]> { [1] = png }));

        Assert.Equal(3, frames.Count);
        Assert.Null(frames[0].Rgba);
        Assert.Equal([1, 2, 3, 255], frames[1].Rgba);
        Assert.Equal(5, frames[2].Pixels[0]);
    }
}

public sealed class ModSetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "darksun-mods-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void Write(string relative, byte[] bytes)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static GffFile Gff() => GffFile.FromBytes(GffFileTests.BuildGff());

    private static string Text(GffFile gff, string tag, int n) => Encoding.Latin1.GetString(gff.Read(tag, n).Span);

    [Fact]
    public void ReplacesResourcesInAnyFileOnlyWhereTheyExist()
    {
        Write("A/TILE/100.bin", "new!"u8.ToArray());
        Write("A/TILE/999.bin", "nope"u8.ToArray());

        GffFile gff = ModSet.Load([_root]).Apply(Gff(), "TEST.GFF");

        Assert.Equal("new!", Text(gff, "TILE", 100));
        Assert.Equal("t200", Text(gff, "TILE", 200));
        Assert.False(gff.Contains("TILE", 999));
    }

    [Fact]
    public void FileFoldersCanAddResourcesAndConvertText()
    {
        Write("A/TEST.GFF/TEXT/7.txt", Encoding.UTF8.GetBytes("Hello\nAthas"));
        Write("A/TEST.GFF/NEW/3.bin", [1, 2, 3]);
        Write("A/OTHER.GFF/TEXT/9.bin", "other"u8.ToArray());

        GffFile gff = ModSet.Load([_root]).Apply(Gff(), "test.gff");

        Assert.Equal("Hello\r\nAthas\0", Text(gff, "TEXT", 7));
        Assert.Equal("nine!", Text(gff, "TEXT", 9));
        Assert.Equal([1, 2, 3], gff.Read("NEW", 3).ToArray());
        Assert.Contains(gff.DescribeResources(), r => r.Tag == "NEW " && r.Number == 3 && r.Offset == -1);
    }

    [Fact]
    public void LoadOrderDecidesAndDisabledModsAreSkipped()
    {
        Write("Alpha/TILE/100.bin", "alfa"u8.ToArray());
        Write("Beta/TILE/100.bin", "beta"u8.ToArray());
        Write("_Off/TILE/200.bin", "off!"u8.ToArray());

        ModSet alphabetical = ModSet.Load([_root]);
        Assert.Equal(["Alpha", "Beta"], alphabetical.Mods.Select(m => m.Name));
        Assert.Equal("alfa", Text(alphabetical.Apply(Gff(), "T.GFF"), "TILE", 100));
        Assert.Equal("t200", Text(alphabetical.Apply(Gff(), "T.GFF"), "TILE", 200));

        Write(ModSet.LoadOrderFile, "# highest first\nBeta\n"u8.ToArray());
        ModSet ordered = ModSet.Load([_root]);
        Assert.Equal("beta", Text(ordered.Apply(Gff(), "T.GFF"), "TILE", 100));
        Assert.Equal("Beta", ordered.Replacing("T.GFF", "TILE", 100)!.Name);
    }

    [Fact]
    public void ASoundEffectCanBeAWavFile()
    {
        var sound = new Audio.VocSound(8000, [10, 20, 30]);
        Write("A/TEST.GFF/BVOC/19.wav", Audio.Wav.Encode(sound));

        GffFile gff = ModSet.Load([_root]).Apply(Gff(), "TEST.GFF");

        Assert.Equal(sound.Samples, Audio.Voc.Decode(gff.Read("BVOC", 19).Span).Samples);
    }

    [Fact]
    public void TheFontCanBeAPicture()
    {
        var glyphs = Enumerable.Range(0, 256).Select(c => new DarkSun.Data.Text.Glyph(c == 'x' ? 1 : 0, c == 'x' ? [0xFE, 0x14] : [])).ToList();
        byte[] font = DarkSun.Data.Text.GameFont.Build(2, glyphs);
        Write("A/TEST.GFF/FONT/100.png", DarkSun.Data.Text.FontSheet.Export(DarkSun.Data.Text.GameFont.Parse(font)));

        GffFile gff = ModSet.Load([_root]).Apply(Gff(), "TEST.GFF");

        Assert.Equal(font, gff.Read("FONT", 100).ToArray());
    }

    [Fact]
    public void WithoutModsTheFileIsUnchanged()
    {
        GffFile gff = Gff();
        Assert.Same(gff, ModSet.Load([_root, null]).Apply(gff, "T.GFF"));
    }
}

public class AssetCacheTests
{
    [Fact]
    public void DecodesEachResourceOnce()
    {
        var cache = new AssetCache();
        byte[] file = [0xEE, .. AcfTests.Dot(4)];
        var data = new ReadOnlyMemory<byte>(file, 1, file.Length - 1);

        var first = cache.Frames(data);
        var again = cache.Frames(new ReadOnlyMemory<byte>(file, 1, file.Length - 1));
        var copy = cache.Frames(data.ToArray());

        Assert.Same(first, again);
        Assert.NotSame(first, copy);
        Assert.Equal(2, cache.FrameSets);
    }

    [Fact]
    public void SamePaletteBytesGiveTheSamePalette()
    {
        var cache = new AssetCache();
        byte[] pal = new byte[768];
        Assert.Same(cache.Palette(pal), cache.Palette(pal));
        cache.Clear();
        Assert.Equal(0, cache.Palettes);
    }
}
