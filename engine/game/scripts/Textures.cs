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
            texture = ImageTexture.CreateFromImage(ToImage(frame, palette));
            byPalette[key] = texture;
        }
        return texture;
    }

    private static readonly Palette NoPalette = new([]);
}
