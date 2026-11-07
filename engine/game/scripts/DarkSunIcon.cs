using DarkSun.Data.Images;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// The Dark Sun icon in the map's top left corner (<c>WIND</c> 3001's
/// <c>APFM</c> 1001; <c>BMP</c> 5012 at (10, 10), <c>191F:037E</c>): frame 0
/// the grey sun, frame 1 the burning one, shown while the pointer is on it
/// (<c>1587:0059</c>'s events 2 and 4). A left click opens the character
/// screens at the page last shown (event 0x40, <c>DS:4971</c>), a right click
/// the quick cast (event 0x100, <see cref="SpellBar"/>).
/// </summary>
public partial class DarkSunIcon : Control
{
    public const int Bmp = 5012, X = 10, Y = 10;

    private readonly TextureRect _picture = new() { MouseFilter = MouseFilterEnum.Ignore };
    private Texture2D? _grey, _burning;

    public Action? LeftClicked { get; set; }

    public Action? RightClicked { get; set; }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        AddChild(_picture);
        Palette palette = Session.Assets.Palette(Session.Open("RESOURCE.GFF").Read("PAL ", StoneUi.PaletteNumber));
        if (Session.Open("RESOURCE.GFF").TryRead("BMP ", Bmp, out var data) && Session.Assets.Frames(data) is { Count: >= 2 } frames)
        {
            (_grey, _burning) = (Textures.ToTexture(frames[0], palette), Textures.ToTexture(frames[1], palette));
            Size = _grey.GetSize();
        }
        _picture.Texture = _grey;
        MouseEntered += () => _picture.Texture = _burning;
        MouseExited += () => _picture.Texture = _grey;
        GetViewport().SizeChanged += Fit;
        Fit();
    }

    /// <summary>At (10, 10) of the 320 × 200 screen, scaled as it is and centred across.</summary>
    private void Fit()
    {
        Vector2 screen = GetViewportRect().Size;
        float scale = Math.Max(1f, MathF.Floor(Math.Min(screen.X / RetroScreen.Width, screen.Y / RetroScreen.Height)));
        Scale = new Vector2(scale, scale);
        Position = new Vector2(MathF.Floor((screen.X - RetroScreen.Width * scale) / 2), 0) + new Vector2(X, Y) * scale;
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is not InputEventMouseButton { Pressed: true } click)
        {
            return;
        }
        if (click.ButtonIndex == MouseButton.Left)
        {
            LeftClicked?.Invoke();
        }
        else if (click.ButtonIndex == MouseButton.Right)
        {
            RightClicked?.Invoke();
        }
        AcceptEvent();
    }
}
