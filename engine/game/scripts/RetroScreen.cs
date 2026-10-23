using Godot;

namespace DarkSun.Engine;

/// <summary>
/// A black full-window backdrop holding a 320 × 200 <see cref="Canvas"/>, the
/// original screen, scaled up by the largest whole number that fits and
/// centred, so its pixels stay square and sharp. Children of the canvas use
/// the game's own pixel coordinates.
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

    public override void _Ready()
    {
        Fit();
        if (GetTree().CurrentScene == this && OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--screenshot=")) is { } shot)
        {
            // Development: the screen as a PNG after a second (as the map's --screenshot), then quit.
            // Only a screen that is the scene: one over the map (the game menu, the combat log) leaves it to the map's.
            GetTree().CreateTimer(1).Timeout += () =>
            {
                GetViewport().GetTexture().GetImage().SavePng(shot["--screenshot=".Length..]);
                GetTree().Quit();
            };
        }
    }

    private void Fit()
    {
        float scale = Math.Max(1f, MathF.Floor(Math.Min(Size.X / Width, Size.Y / Height)));
        Canvas.Scale = new Vector2(scale, scale);
        Canvas.Position = ((Size - new Vector2(Width, Height) * scale) / 2).Floor();
    }
}
