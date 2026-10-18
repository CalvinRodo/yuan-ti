using Godot;

namespace DarkSun.Engine;

/// <summary>
/// A black full-window backdrop holding a 320 × 200 <see cref="Canvas"/>, the
/// original screen, scaled up as far as it fits and centred. Children of the
/// canvas use the game's own pixel coordinates.
/// </summary>
public partial class RetroScreen : Control
{
    public const int Width = 320;
    public const int Height = 200;

    public RetroScreen()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(new ColorRect { Color = Colors.Black, MouseFilter = MouseFilterEnum.Ignore });
        ((ColorRect)GetChild(0)).SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Canvas = new Control { Size = new Vector2(Width, Height), MouseFilter = MouseFilterEnum.Pass };
        AddChild(Canvas);
        Resized += Fit;
    }

    public Control Canvas { get; }

    public override void _Ready() => Fit();

    private void Fit()
    {
        float scale = Math.Max(0.25f, Math.Min(Size.X / Width, Size.Y / Height));
        Canvas.Scale = new Vector2(scale, scale);
        Canvas.Position = ((Size - new Vector2(Width, Height) * scale) / 2).Floor();
    }
}
