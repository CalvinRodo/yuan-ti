namespace DarkSun.Data.Images;

public readonly record struct Rgb(byte R, byte G, byte B);

/// <summary>Palette bit depth: VGA DAC values (0–63) or full bytes.</summary>
public enum PaletteDepth { Auto, SixBit, EightBit }

/// <summary>
/// A <c>PAL </c> or <c>CPAL</c> resource (port of <c>Palette</c> in
/// <c>src/image.py</c>). Usually 256 × 3 bytes of 6-bit VGA values.
/// </summary>
public sealed class Palette
{
    private readonly Rgb[] _colors;

    public Palette(Rgb[] colors) => _colors = colors;

    public int Count => _colors.Length;

    public Rgb this[int index] => _colors[index];

    /// <summary>
    /// Read raw palette bytes. The stride is 3 (RGB) unless the size only
    /// divides by 4 or is exactly 1024 bytes (RGB plus padding). With
    /// <see cref="PaletteDepth.Auto"/>, any channel above 63 means 8-bit
    /// values; otherwise 6-bit values are scaled by 4 (0–252).
    /// </summary>
    public static Palette FromBytes(ReadOnlySpan<byte> data, PaletteDepth depth = PaletteDepth.Auto)
    {
        int size = data.Length;
        int stride = (size % 3 == 0, size % 4 == 0) switch
        {
            (true, false) => 3,
            (false, true) => 4,
            (true, true) => size == 1024 ? 4 : 3,
            _ => 4,
        };
        int count = size / stride;

        if (depth == PaletteDepth.Auto)
        {
            int max = 0;
            for (int i = 0; i < count; i++)
            {
                int at = i * stride;
                max = Math.Max(max, Math.Max(data[at], Math.Max(data[at + 1], data[at + 2])));
            }
            depth = max > 63 ? PaletteDepth.EightBit : PaletteDepth.SixBit;
        }
        int scale = depth == PaletteDepth.SixBit ? 4 : 1;

        var colors = new Rgb[count];
        for (int i = 0; i < count; i++)
        {
            int at = i * stride;
            colors[i] = new Rgb(Scale(data[at], scale), Scale(data[at + 1], scale), Scale(data[at + 2], scale));
        }
        return new Palette(colors);
    }

    private static byte Scale(byte value, int scale) => (byte)Math.Min(255, value * scale);
}
