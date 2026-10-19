using System.Buffers.Binary;

namespace DarkSun.Data.Text;

/// <summary>One character of a <see cref="GameFont"/>: its advance width and <see cref="GameFont.Height"/> rows of pixel bytes.</summary>
public sealed record Glyph(int Width, byte[] Pixels)
{
    public byte this[int x, int y] => Pixels[y * Width + x];
}

/// <summary>
/// The game's font, <c>FONT</c> 100 in <c>RESOURCE.GFF</c>, loaded once at
/// start-up into the drawing context (<c>2531</c>, kept at
/// <c>577D:5EA3</c>):
/// <list type="bullet">
/// <item><c>+0</c> word: glyphs (256); <c>+2</c> word: height (9); <c>+4</c> 4 zero bytes;</item>
/// <item><c>+8</c> 256 bytes: the colour map, pixel value → palette index (identity in the file);</item>
/// <item><c>+0x108</c> 256 words: each character's glyph, as an offset from the start;</item>
/// <item>a glyph: a word, its width (2–7, the space after it included), then height × width bytes, one per pixel, row by row.</item>
/// </list>
/// The blitter (<c>2E86:06C0</c>) draws pixel p in colour <c>map[p]</c> and
/// skips 0. The pixels are 254 (<see cref="FacePixel"/>, the letters) and 20
/// (<see cref="ShadowPixel"/>, a shadow below and to the right): before
/// drawing text the game sets <c>map[0] = 0</c> and <c>map[254]</c> to the
/// text colour (<c>2BF9:055D</c>, with <c>DS:3C02</c> = 0xFE and
/// <c>DS:A1C9</c>), so the shadow keeps palette colour 20. Character codes are
/// the text's bytes (Latin-1).
/// </summary>
public sealed class GameFont
{
    public const int FacePixel = 0xFE;
    public const int ShadowPixel = 0x14;

    private GameFont(int height, byte[] colourMap, Glyph[] glyphs)
    {
        Height = height;
        ColourMap = colourMap;
        Glyphs = glyphs;
    }

    public int Height { get; }

    /// <summary>The map at +8: pixel value → palette index.</summary>
    public IReadOnlyList<byte> ColourMap { get; }

    /// <summary>By character code, 0–255.</summary>
    public IReadOnlyList<Glyph> Glyphs { get; }

    /// <summary>The glyph for <paramref name="c"/>; characters past 255 have none (null).</summary>
    public Glyph? GlyphFor(char c) => c < Glyphs.Count ? Glyphs[c] : null;

    /// <summary>How wide <paramref name="text"/> is drawn, in pixels (<c>2E86:07E0</c>: the widths added up).</summary>
    public int Measure(string text) => text.Sum(c => GlyphFor(c)?.Width ?? 0);

    /// <exception cref="InvalidDataException">Not a font this reads.</exception>
    public static GameFont Parse(ReadOnlySpan<byte> data)
    {
        const int MapAt = 8, OffsetsAt = 0x108;
        if (data.Length < OffsetsAt + 2)
        {
            throw new InvalidDataException("FONT: too short");
        }
        int count = BinaryPrimitives.ReadUInt16LittleEndian(data);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        if (count is < 1 or > 256 || height is < 1 or > 64 || data.Length < OffsetsAt + 2 * count)
        {
            throw new InvalidDataException($"FONT: {count} glyphs of height {height}");
        }
        var glyphs = new Glyph[256];
        for (int c = 0; c < 256; c++)
        {
            glyphs[c] = new Glyph(0, []);
        }
        for (int c = 0; c < count; c++)
        {
            int at = BinaryPrimitives.ReadUInt16LittleEndian(data[(OffsetsAt + 2 * c)..]);
            if (at + 2 > data.Length)
            {
                throw new InvalidDataException($"FONT: glyph {c} at 0x{at:x} is past the end");
            }
            int width = BinaryPrimitives.ReadUInt16LittleEndian(data[at..]);
            int size = width * height;
            if (width > 64 || at + 2 + size > data.Length)
            {
                throw new InvalidDataException($"FONT: glyph {c} ({width} wide) is past the end");
            }
            glyphs[c] = new Glyph(width, data.Slice(at + 2, size).ToArray());
        }
        return new GameFont(height, data.Slice(MapAt, 256).ToArray(), glyphs);
    }

    /// <summary>
    /// The font as a resource again (for a mod's font made from a picture):
    /// the same layout, glyphs in character order.
    /// </summary>
    public static byte[] Build(int height, IReadOnlyList<Glyph> glyphs, IReadOnlyList<byte>? colourMap = null)
    {
        const int OffsetsAt = 0x108;
        using var o = new MemoryStream();
        using var w = new BinaryWriter(o);
        w.Write((ushort)256);
        w.Write((ushort)height);
        w.Write(0);
        for (int i = 0; i < 256; i++)
        {
            w.Write(colourMap is { Count: 256 } ? colourMap[i] : (byte)i);
        }
        int at = OffsetsAt + 2 * 256;
        for (int c = 0; c < 256; c++)
        {
            Glyph g = c < glyphs.Count ? glyphs[c] : new Glyph(0, []);
            w.Write((ushort)at);
            at += 2 + g.Width * height;
        }
        if (at > 0xFFFF)
        {
            throw new InvalidDataException("FONT: too big for 16-bit offsets");
        }
        for (int c = 0; c < 256; c++)
        {
            Glyph g = c < glyphs.Count ? glyphs[c] : new Glyph(0, []);
            w.Write((ushort)g.Width);
            w.Write(g.Pixels.AsSpan(0, g.Width * height));
        }
        w.Flush();
        return o.ToArray();
    }
}

/// <summary>
/// The font as a picture, for mods: <c>ModTool export</c> writes
/// <c>FONT/100.png</c>, a 16 × 16 grid of cells 8 wide and height + 1 tall,
/// character n in column n % 16, row n / 16. In a cell, white pixels are the
/// letter's face (254) and dark ones its shadow (20); the row under the
/// glyph marks its width (that many opaque pixels from the left). A mod's
/// <c>FONT/100.png</c> in that layout is read back into the font.
/// </summary>
public static class FontSheet
{
    public const int CellWidth = 8;

    private static readonly byte[] Face = [255, 255, 255, 255], Shadow = [56, 56, 84, 255], Mark = [255, 64, 64, 255];

    public static byte[] Export(GameFont font)
    {
        int cellHeight = font.Height + 1;
        int width = 16 * CellWidth, height = 16 * cellHeight;
        var rgba = new byte[width * height * 4];
        void Put(int x, int y, byte[] colour) => colour.CopyTo(rgba, (y * width + x) * 4);
        for (int c = 0; c < 256; c++)
        {
            Glyph g = font.Glyphs[c];
            int left = c % 16 * CellWidth, top = c / 16 * cellHeight;
            for (int y = 0; y < font.Height; y++)
            {
                for (int x = 0; x < Math.Min(g.Width, CellWidth); x++)
                {
                    byte p = g[x, y];
                    if (p != 0)
                    {
                        Put(left + x, top + y, p == GameFont.ShadowPixel ? Shadow : Face);
                    }
                }
            }
            for (int x = 0; x < Math.Min(g.Width, CellWidth); x++)
            {
                Put(left + x, top + font.Height, Mark);
            }
        }
        return Images.Png.Encode(width, height, rgba);
    }

    /// <exception cref="InvalidDataException">The picture isn't 16 × 16 cells of 8 × (height + 1).</exception>
    public static byte[] Import(ReadOnlySpan<byte> png)
    {
        var (width, height, rgba) = Images.Png.Decode(png);
        if (width != 16 * CellWidth || height % 16 != 0 || height / 16 < 2)
        {
            throw new InvalidDataException($"a font picture is 16 × 16 cells, {16 * CellWidth} pixels wide and a multiple of 16 tall; this is {width} × {height}");
        }
        int cellHeight = height / 16, glyphHeight = cellHeight - 1;
        bool Opaque(int x, int y) => rgba[(y * width + x) * 4 + 3] >= 128;
        var glyphs = new Glyph[256];
        for (int c = 0; c < 256; c++)
        {
            int left = c % 16 * CellWidth, top = c / 16 * cellHeight;
            int w = 0;
            while (w < CellWidth && Opaque(left + w, top + glyphHeight))
            {
                w++;
            }
            var pixels = new byte[w * glyphHeight];
            for (int y = 0; y < glyphHeight; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = ((top + y) * width + left + x) * 4;
                    if (rgba[i + 3] >= 128)
                    {
                        int light = (rgba[i] * 299 + rgba[i + 1] * 587 + rgba[i + 2] * 114) / 1000;
                        pixels[y * w + x] = light >= 128 ? (byte)GameFont.FacePixel : (byte)GameFont.ShadowPixel;
                    }
                }
            }
            glyphs[c] = new Glyph(w, pixels);
        }
        return GameFont.Build(glyphHeight, glyphs);
    }
}
