using DarkSun.Data.Images;
using DarkSun.Game.Characters;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// DUAL, the box menu's class choice (<c>WIND</c> 17502 over <c>BMP</c> 17002), centred on the
/// screen: the eight classes (<c>BUTN</c> 17303–17310 in the class order cleric, druid, fighter,
/// gladiator, preserver, psionicist, ranger, thief), those the character can't take dimmed and
/// disabled (<see cref="CharacterCreation.DualChoices"/>), EXIT (17302), and to the left whose
/// choice it is or why there is none (NO OTHER CLASSES AVAILABLE, <c>DS:3075</c>). Create
/// Characters and the game's character screens both open it.
/// </summary>
public sealed class DualWindow
{
    private const int Window = 17502, Backdrop = 17002, FirstClass = 17303, ExitButton = 17302;
    private static readonly Color Ink = new(0.95f, 0.9f, 0.75f);

    private readonly UiWindowView _window;
    private readonly Label _title;
    private Character? _character;

    /// <summary>A class was taken: the character (dual-classed now) and its new class.</summary>
    public Action<Character, CharacterClass>? Chosen { get; set; }

    public DualWindow(Control parent, Palette palette)
    {
        _window = UiWindowView.Build(Window, palette, Backdrop)!;
        _window.Position = ((new Vector2(RetroScreen.Width, RetroScreen.Height) - _window.Size) / 2).Floor();
        _window.Visible = false;
        parent.AddChild(_window);
        // BMP 17002 has its own DUAL title; the message goes left of the class list.
        _title = new Label
        {
            Position = new Vector2(10, 30),
            Size = new Vector2(_window.RectOf("BUTN", FirstClass).Position.X - 16, 60),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            LabelSettings = GameFonts.Settings(Ink),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _window.AddChild(_title);
        foreach (CharacterClass k in Enum.GetValues<CharacterClass>())
        {
            CharacterClass choice = k;
            Button(k).Pressed += () => Choose(choice);
        }
        _window.Button(ExitButton)!.Pressed += Close;
        if (_window.ButtonText(ExitButton) is { } t)
        {
            t.LabelSettings = GameFonts.Settings(Ink);
        }
    }

    public bool Visible => _window.Visible;

    /// <summary>What the window says (for the checks).</summary>
    public string Title => _title.Text;

    /// <summary>Class <paramref name="k"/>'s button.</summary>
    public TextureButton Button(CharacterClass k) => _window.Button(FirstClass + (int)k)!;

    /// <summary>Offer <paramref name="c"/> the classes it can dual-class into.</summary>
    public void Open(Character c)
    {
        _character = c;
        var (classes, why) = new CharacterCreation(Session.Rules, Session.Names, new Random()).DualChoices(c);
        foreach (CharacterClass k in Enum.GetValues<CharacterClass>())
        {
            TextureButton b = Button(k);
            b.Disabled = !classes.Contains(k);
            b.Modulate = b.Disabled ? new Color(1, 1, 1, 0.35f) : Colors.White;
        }
        _title.Text = why ?? $"CHOOSE {c.Name.ToUpperInvariant()}'S NEW CLASS";
        _window.Visible = true;
        _window.GetParent()?.MoveChild(_window, -1);
    }

    public void Close() => _window.Visible = false;

    private void Choose(CharacterClass k)
    {
        if (_character is { } c && new CharacterCreation(Session.Rules, Session.Names, new Random()).Dual(c, k))
        {
            Close();
            Chosen?.Invoke(c, k);
        }
    }
}
