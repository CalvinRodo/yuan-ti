using DarkSun.Data.Text;

namespace DarkSun.Data.Tests;

public class GameFontTests
{
    /// <summary>A 3-high font: 'A' 2 wide (face over shadow), space 1 wide, the rest empty.</summary>
    private static byte[] Font()
    {
        var glyphs = new Glyph[256];
        for (int c = 0; c < 256; c++)
        {
            glyphs[c] = new Glyph(0, []);
        }
        glyphs['A'] = new Glyph(2, [0xFE, 0, 0xFE, 0x14, 0x14, 0x14]);
        glyphs[' '] = new Glyph(1, [0, 0, 0]);
        return GameFont.Build(3, glyphs);
    }

    [Fact]
    public void ReadsTheLayoutTheGameUses()
    {
        byte[] data = Font();

        GameFont font = GameFont.Parse(data);

        Assert.Equal((256, 3), (data[0] | data[1] << 8, data[2] | data[3] << 8));
        Assert.Equal(3, font.Height);
        Assert.Equal(Enumerable.Range(0, 256).Select(i => (byte)i), font.ColourMap);
        Assert.Equal(2, font.Glyphs['A'].Width);
        Assert.Equal(0x14, font.Glyphs['A'][1, 1]);
        Assert.Equal(GameFont.FacePixel, font.Glyphs['A'][0, 0]);
        Assert.Equal(5, font.Measure("A A"));
        Assert.Null(font.GlyphFor('♦'));
    }

    [Fact]
    public void BrokenFontsSayWhy()
    {
        byte[] data = Font();
        Assert.Throws<InvalidDataException>(() => GameFont.Parse(data.AsSpan(0, 100)));
        data[0x108 + 2 * 'A'] = 0xFF; // glyph offset past the end
        data[0x108 + 2 * 'A' + 1] = 0xFF;
        Assert.Throws<InvalidDataException>(() => GameFont.Parse(data));
    }

    [Fact]
    public void ThePictureFormReadsBackTheSame()
    {
        byte[] data = Font();

        byte[] back = FontSheet.Import(FontSheet.Export(GameFont.Parse(data)));

        Assert.Equal(data, back);
    }
}
