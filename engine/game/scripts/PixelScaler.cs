using Godot;

namespace DarkSun.Engine;

/// <summary>
/// Whole-number scaling to any window size (an autoload). The game is drawn
/// for a viewport of at least 640 × 400 (twice the original's 320 × 200);
/// the window shows it at the largest whole-number scale that fits, and the
/// viewport grows to fill the rest of the window instead of leaving borders.
/// So every game pixel is a square block of screen pixels, never blurred or
/// uneven, whatever the window size.
/// </summary>
public partial class PixelScaler : Node
{
    public static readonly Vector2I MinimumSize = new(640, 400);

    /// <summary>Screen pixels per viewport pixel.</summary>
    public static int Scale { get; private set; } = 1;

    public override void _Ready()
    {
        Window window = GetWindow();
        window.SizeChanged += Fit;
        Fit();
    }

    private void Fit()
    {
        Window window = GetWindow();
        Vector2I size = window.Size;
        Scale = Math.Max(1, Math.Min(size.X / MinimumSize.X, size.Y / MinimumSize.Y));
        window.ContentScaleSize = new Vector2I(Math.Max(MinimumSize.X, size.X / Scale), Math.Max(MinimumSize.Y, size.Y / Scale));
    }
}
