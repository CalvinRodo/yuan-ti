using Godot;

namespace DarkSun.Engine;

/// <summary>
/// Whole-number scaling to any window size (an autoload). The game is drawn
/// for a viewport of at least 640 × 400 (twice the original's 320 × 200);
/// the window shows it at the largest whole-number scale that fits, so every
/// game pixel is a square block of screen pixels, never blurred or uneven,
/// whatever the window size. With <see cref="Settings.LockView"/> (the default)
/// the viewport stays 640 × 400, bordered in black, so no more of a map shows
/// than the original showed; off, it grows to fill the rest of the window.
/// </summary>
public partial class PixelScaler : Node
{
    public static readonly Vector2I MinimumSize = new(640, 400);

    public static PixelScaler? Instance { get; private set; }

    /// <summary>Screen pixels per viewport pixel.</summary>
    public static int Scale { get; private set; } = 1;

    public override void _Ready()
    {
        Instance = this;
        Window window = GetWindow();
        window.SizeChanged += Fit;
        Fit();
    }

    public override void _ExitTree()
    {
        if (Instance == this)
        {
            Instance = null;
        }
    }

    /// <summary>The viewport size and scale for a <paramref name="window"/> of that size, the view locked to the original's or not.</summary>
    public static (Vector2I Viewport, int Scale) Fitting(Vector2I window, bool locked)
    {
        int scale = Math.Max(1, Math.Min(window.X / MinimumSize.X, window.Y / MinimumSize.Y));
        return (locked ? MinimumSize : new Vector2I(Math.Max(MinimumSize.X, window.X / scale), Math.Max(MinimumSize.Y, window.Y / scale)), scale);
    }

    /// <summary>Size the viewport to the window (again after the view lock changes).</summary>
    public void Fit()
    {
        Window window = GetWindow();
        bool locked = Settings.LockView;
        (window.ContentScaleSize, Scale) = Fitting(window.Size, locked);
        window.ContentScaleAspect = locked ? Window.ContentScaleAspectEnum.Keep : Window.ContentScaleAspectEnum.Expand;
    }
}
