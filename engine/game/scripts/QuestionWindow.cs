using DarkSun.Data.Images;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// DSUN.EXE's small question window: <c>WIND</c> 14000 over <c>BMP</c> 14000,
/// a title strip (14004) and up to three choices (14001–14003), as View
/// Character's INACTIVE CHARACTER and the game menu's EXIT: SAVE GAME? show it.
/// The title is the status grey (colour 27); the choices dark (17) on a light
/// shadow (29), red (77) under the pointer. The return button closes it.
/// </summary>
public sealed class QuestionWindow
{
    public static readonly Color TitleGrey = Color.Color8(136, 136, 160), ChoiceInk = Color.Color8(32, 32, 56),
        ChoiceShadow = Color.Color8(164, 164, 188), ChoiceHover = Color.Color8(212, 64, 40);

    private const int Window = 14000, Title = 14004, FirstChoice = 14001, ReturnButton = 10308;

    private readonly UiWindowView _window;
    private readonly Label _title;
    private readonly Label[] _choices = new Label[3];
    private readonly Action?[] _actions = new Action?[3];

    /// <summary>The window, hidden, in <paramref name="parent"/> (a 320 × 200 canvas).</summary>
    public QuestionWindow(Control parent, Palette palette)
    {
        _window = UiWindowView.Build(Window, palette, backdrop: Window)!;
        _window.Visible = false;
        parent.AddChild(_window);
        Rect2 title = _window.RectOf("BUTN", Title);
        _title = new Label
        {
            Position = title.Position + new Vector2(2, 0),
            Size = new Vector2(title.Size.X - 4, GameFonts.Size),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            LabelSettings = GameFonts.Settings(TitleGrey),
        };
        _window.AddChild(_title);
        for (int i = 0; i < 3; i++)
        {
            int choice = i;
            TextureButton button = _window.Button(FirstChoice + i)!;
            _choices[i] = new Label
            {
                Position = new Vector2(2, 0),
                Size = new Vector2(button.Size.X - 4, GameFonts.Size),
                MouseFilter = Control.MouseFilterEnum.Ignore,
                LabelSettings = GameFonts.Settings(ChoiceInk, shadow: ChoiceShadow),
            };
            button.AddChild(_choices[i]);
            button.MouseEntered += () => _choices[choice].LabelSettings = GameFonts.Settings(ChoiceHover, shadow: ChoiceShadow);
            button.MouseExited += () => _choices[choice].LabelSettings = GameFonts.Settings(ChoiceInk, shadow: ChoiceShadow);
            button.Pressed += () =>
            {
                Close();
                _actions[choice]?.Invoke();
            };
        }
        _window.Button(ReturnButton)!.Pressed += Close;
    }

    public bool Visible => _window.Visible;

    public Vector2 Size => _window.Size;

    public Vector2 Position => _window.Position;

    public void Close() => _window.Visible = false;

    /// <summary>The title asked, for the checks.</summary>
    public string Asked => _title.Text;

    /// <summary>Press choice <paramref name="choice"/> (0–2), as a click would (for the checks).</summary>
    public void Choose(int choice) => _window.Button(FirstChoice + choice)?.EmitSignal(BaseButton.SignalName.Pressed);

    /// <summary>
    /// Ask <paramref name="title"/> at <paramref name="at"/> (kept on the screen;
    /// null: centred) with up to three choices; a choice without an action just closes it.
    /// </summary>
    public void Ask(string title, Vector2? at, params (string Text, Action? Then)[] choices)
    {
        _title.Text = title;
        for (int i = 0; i < 3; i++)
        {
            bool there = i < choices.Length;
            _choices[i].Text = there ? choices[i].Text : "";
            _choices[i].LabelSettings = GameFonts.Settings(ChoiceInk, shadow: ChoiceShadow);
            _actions[i] = there ? choices[i].Then : null;
            _window.Button(FirstChoice + i)!.Visible = there;
        }
        Vector2 place = at ?? ((new Vector2(RetroScreen.Width, RetroScreen.Height) - _window.Size) / 2).Floor();
        _window.Position = new Vector2(
            Math.Clamp(place.X, 0, RetroScreen.Width - _window.Size.X),
            Math.Clamp(place.Y, 0, RetroScreen.Height - _window.Size.Y));
        _window.Visible = true;
        // Drawn over whatever the screen added since.
        _window.GetParent()?.MoveChild(_window, -1);
    }
}
