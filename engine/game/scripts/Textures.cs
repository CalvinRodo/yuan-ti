using System.Runtime.CompilerServices;
using DarkSun.Data.Images;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// Turns decoded game images into Godot textures, keeping one texture per
/// frame and palette for as long as the frame is alive (the frames themselves
/// come from <see cref="Session.Assets"/>, so they last the session).
/// </summary>
public static class Textures
{
    private static readonly ConditionalWeakTable<ImageFrame, Dictionary<Palette, ImageTexture>> Cache = new();

    public static Image ToImage(ImageFrame frame, Palette palette) =>
        Image.CreateFromData(frame.Width, frame.Height, false, Image.Format.Rgba8, frame.ToRgba(palette));

    /// <summary>The texture of <paramref name="frame"/> in <paramref name="palette"/>, made once.</summary>
    public static ImageTexture ToTexture(ImageFrame frame, Palette palette)
    {
        Dictionary<Palette, ImageTexture> byPalette = Cache.GetOrCreateValue(frame);
        // A true-colour frame (from a mod) looks the same in every palette.
        Palette key = frame.Rgba is null ? palette : NoPalette;
        if (!byPalette.TryGetValue(key, out ImageTexture? texture))
        {
            Image image = ToImage(frame, palette);
            texture = ImageTexture.CreateFromImage(image);
            byPalette[key] = texture;
            Images.AddOrUpdate(texture, image);
        }
        return texture;
    }

    private static readonly ConditionalWeakTable<Texture2D, Image> Images = new();

    /// <summary>
    /// Is pixel (<paramref name="x"/>, <paramref name="y"/>) of
    /// <paramref name="texture"/> drawn (not transparent)? Outside it, no.
    /// </summary>
    public static bool Opaque(Texture2D texture, int x, int y)
    {
        if (x < 0 || y < 0 || x >= texture.GetWidth() || y >= texture.GetHeight())
        {
            return false;
        }
        if (!Images.TryGetValue(texture, out Image? image))
        {
            image = texture.GetImage();
            Images.AddOrUpdate(texture, image);
        }
        return image.GetPixel(x, y).A > 0;
    }

    private static readonly Palette NoPalette = new([]);
}
