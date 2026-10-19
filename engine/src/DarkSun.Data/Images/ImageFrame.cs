namespace DarkSun.Data.Images;

/// <summary>
/// One decoded frame: palette indices plus an opacity mask, row-major.
/// Transparency is the mask, not index 0: row-RLE images can draw index 0.
/// A frame from a mod's PNG (<see cref="FromRgba"/>) also has true colours,
/// which are drawn instead of the palette; its indices are 0 until
/// <see cref="Pixels"/> is matched to a palette by <see cref="IndexedFor"/>.
/// </summary>
public sealed class ImageFrame
{
    public ImageFrame(int width, int height, byte[] pixels, bool[] opaque)
    {
        if (pixels.Length != width * height || opaque.Length != width * height)
        {
            throw new ArgumentException("pixel and mask arrays must be width × height");
        }
        Width = width;
        Height = height;
        Pixels = pixels;
        Opaque = opaque;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Palette index per pixel; 0 where transparent.</summary>
    public byte[] Pixels { get; }

    public bool[] Opaque { get; }

    /// <summary>RGBA8 pixels of a true-colour frame (from a mod), else null.</summary>
    public byte[]? Rgba { get; private init; }

    /// <summary>A true-colour frame; pixels with alpha below 128 are transparent.</summary>
    public static ImageFrame FromRgba(int width, int height, byte[] rgba)
    {
        if (rgba.Length != width * height * 4)
        {
            throw new ArgumentException("RGBA array must be width × height × 4");
        }
        var opaque = new bool[width * height];
        for (int i = 0; i < opaque.Length; i++)
        {
            opaque[i] = rgba[i * 4 + 3] >= 128;
        }
        return new ImageFrame(width, height, new byte[width * height], opaque) { Rgba = rgba };
    }

    /// <summary>The colour of pixel <paramref name="index"/>: its true colour, or its palette entry.</summary>
    public Rgb ColorAt(int index, Palette palette) =>
        Rgba is { } c ? new Rgb(c[index * 4], c[index * 4 + 1], c[index * 4 + 2]) : palette[Pixels[index]];

    /// <summary>
    /// Palette indices for drawing into an indexed screen (cinematics): this
    /// frame's own, or, for a true-colour frame, each pixel's nearest colour
    /// in <paramref name="palette"/>.
    /// </summary>
    public byte[] IndexedFor(Palette palette)
    {
        if (Rgba is null)
        {
            return Pixels;
        }
        var indexes = new byte[Pixels.Length];
        var nearest = new Dictionary<int, byte>();
        for (int i = 0; i < indexes.Length; i++)
        {
            int key = Rgba[i * 4] << 16 | Rgba[i * 4 + 1] << 8 | Rgba[i * 4 + 2];
            if (!nearest.TryGetValue(key, out byte best))
            {
                int bestDistance = int.MaxValue;
                for (int p = 0; p < palette.Count; p++)
                {
                    Rgb c = palette[p];
                    int dr = c.R - Rgba[i * 4], dg = c.G - Rgba[i * 4 + 1], db = c.B - Rgba[i * 4 + 2];
                    int d = dr * dr + dg * dg + db * db;
                    if (d < bestDistance)
                    {
                        (bestDistance, best) = (d, (byte)p);
                    }
                }
                nearest[key] = best;
            }
            indexes[i] = best;
        }
        return indexes;
    }

    /// <summary>RGBA8 pixels (4 bytes each), transparent pixels all zero.</summary>
    public byte[] ToRgba(Palette palette)
    {
        var rgba = new byte[Width * Height * 4];
        for (int i = 0; i < Pixels.Length; i++)
        {
            if (Opaque[i])
            {
                Rgb c = ColorAt(i, palette);
                rgba[i * 4] = c.R;
                rgba[i * 4 + 1] = c.G;
                rgba[i * 4 + 2] = c.B;
                rgba[i * 4 + 3] = 255;
            }
        }
        return rgba;
    }
}
