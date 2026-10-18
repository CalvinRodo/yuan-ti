namespace DarkSun.Data.Images;

/// <summary>
/// One decoded frame: palette indices plus an opacity mask, row-major.
/// Transparency is the mask, not index 0: row-RLE images can draw index 0.
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

    /// <summary>RGBA8 pixels (4 bytes each), transparent pixels all zero.</summary>
    public byte[] ToRgba(Palette palette)
    {
        var rgba = new byte[Width * Height * 4];
        for (int i = 0; i < Pixels.Length; i++)
        {
            if (Opaque[i])
            {
                Rgb c = palette[Pixels[i]];
                rgba[i * 4] = c.R;
                rgba[i * 4 + 1] = c.G;
                rgba[i * 4 + 2] = c.B;
                rgba[i * 4 + 3] = 255;
            }
        }
        return rgba;
    }
}
