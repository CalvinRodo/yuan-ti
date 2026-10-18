using DarkSun.Data.Images;
using Godot;

namespace DarkSun.Engine;

/// <summary>Turns decoded game images into Godot textures.</summary>
public static class Textures
{
    public static Image ToImage(ImageFrame frame, Palette palette) =>
        Image.CreateFromData(frame.Width, frame.Height, false, Image.Format.Rgba8, frame.ToRgba(palette));

    public static ImageTexture ToTexture(ImageFrame frame, Palette palette) =>
        ImageTexture.CreateFromImage(ToImage(frame, palette));
}
