using DarkSun.Data.Gff;
using DarkSun.Data.Images;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// DSUN.EXE's message box (<c>5F6D:05DE</c>): <c>WIND</c> 10501, a 192 × 28 bar
/// at (66, 90) over the map drawn with <c>BMP</c> 10001 (<c>78A9:31D3</c>(14): the
/// 15th of the pictures <c>78A9:2FF7</c> caches, <c>DS:1A5B</c>), its invisible
/// button (10309) the whole bar, the message centred in its text strip
/// (<c>APFM</c> 11270) in the status grey. The game waits until a click or a
/// key puts it away: "NO PATH FROM HERE" (<c>DS:0BD9</c>, a walk with nowhere
/// to step, <c>1587:0427</c>), "NO LINE OF SIGHT" (<c>DS:0BF8</c>).
/// </summary>
public partial class MessageBox : RetroScreen
{
    private const int PaletteNumber = 1001, Window = 10501, Bar = 10001, TextStrip = 11270;
    private static readonly Vector2 Place = new(66, 90);

    private Label _text = null!;

    public bool IsOpen => Visible;

    /// <summary>The message shown, or null (for the checks).</summary>
    public string? Shown => Visible ? _text.Text : null;

    public override void _Ready()
    {
        base._Ready();
        Visible = false;
        ((ColorRect)GetChild(0)).Color = Colors.Transparent; // the map shows as it is
        MouseFilter = MouseFilterEnum.Stop;
        GffFile resources = Session.Open("RESOURCE.GFF");
        Palette palette = Session.Assets.Palette(resources.Read("PAL ", PaletteNumber));
        UiWindowView window = UiWindowView.Build(Window, palette, Bar)!;
        window.Position = Place;
        window.MouseFilter = MouseFilterEnum.Ignore;
        Canvas.AddChild(window);
        Rect2 strip = window.RectOf("APFM", TextStrip);
        _text = new Label
        {
            Position = new Vector2(strip.Position.X, strip.Position.Y - 2),
            Size = new Vector2(window.Size.X - 2 * strip.Position.X, GameFonts.Size),
            HorizontalAlignment = HorizontalAlignment.Center,
            ClipText = true,
            MouseFilter = MouseFilterEnum.Ignore,
            LabelSettings = GameFonts.Settings(QuestionWindow.TitleGrey),
        };
        window.AddChild(_text);
    }

    /// <summary>Show <paramref name="message"/> until a click or a key.</summary>
    public void Show(string message)
    {
        _text.Text = message;
        Visible = true;
        GetParent()?.MoveChild(this, -1);
    }

    public void Close() => Visible = false;

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true })
        {
            Close();
            AcceptEvent();
        }
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (Visible && e is InputEventKey { Pressed: true, Echo: false })
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }
}
