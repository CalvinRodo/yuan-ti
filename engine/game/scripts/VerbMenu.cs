using DarkSun.Data.Gff;
using DarkSun.Data.Images;
using DarkSun.Game.World;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// The right-click menu of an object: its name and the verbs it offers
/// (<see cref="ObjectVerbs"/>), one button each. Talk, Get and Use show
/// DSUN.EXE's verb icons (<c>ICON</c> 15101–15103 for things, 15105–15107
/// for creatures, from the buttons of windows 15500 and 3020). Click a verb,
/// or press its number or letter (T, G, U, L); Esc or a click elsewhere closes it.
/// An object with a single verb skips the menu (<c>RegionView</c> does it at once).
/// </summary>
public partial class VerbMenu : PanelContainer
{
    private const int FontSize = 10;

    private static readonly Dictionary<Verb, (string Label, Key Letter)> Labels = new()
    {
        [Verb.Talk] = ("Talk", Key.T),
        [Verb.Get] = ("Get", Key.G),
        [Verb.Use] = ("Use", Key.U),
        [Verb.UseWith] = ("Use with…", Key.None),
        [Verb.Attack] = ("Attack", Key.None),
        [Verb.Look] = ("Look", Key.L),
        [Verb.Open] = ("Open", Key.O),
    };

    private readonly Dictionary<(Verb, bool), Texture2D?> _icons = [];
    private VBoxContainer _list = null!;
    private Label _title = null!;
    private IReadOnlyList<Verb> _verbs = [];

    /// <summary>The verb the player chose (the menu has closed).</summary>
    public Action<Verb>? Chosen { get; set; }

    public override void _Ready()
    {
        Visible = false;
        AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.12f, 0.08f, 0.05f, 0.95f),
            BorderColor = new Color(0.6f, 0.42f, 0.25f),
            BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
            ContentMarginLeft = 4, ContentMarginTop = 3, ContentMarginRight = 4, ContentMarginBottom = 4,
        });
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 2);
        AddChild(column);
        _title = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _title.AddThemeFontSizeOverride("font_size", FontSize);
        _title.AddThemeColorOverride("font_color", new Color(0.95f, 0.8f, 0.55f));
        column.AddChild(_title);
        _list = new VBoxContainer();
        _list.AddThemeConstantOverride("separation", 1);
        column.AddChild(_list);
    }

    /// <summary>Show <paramref name="verbs"/> for <paramref name="title"/> at <paramref name="at"/> (screen position).</summary>
    public void Open(string title, IReadOnlyList<Verb> verbs, bool creature, Vector2 at)
    {
        _verbs = verbs;
        _title.Text = title;
        foreach (Node child in _list.GetChildren())
        {
            _list.RemoveChild(child);
            child.QueueFree();
        }
        for (int i = 0; i < verbs.Count; i++)
        {
            Verb verb = verbs[i];
            var (label, letter) = Labels[verb];
            var button = new Button
            {
                Text = $"{i + 1}  {label}",
                Icon = Icon(verb, creature),
                Alignment = HorizontalAlignment.Left,
                TooltipText = letter == Key.None ? $"{i + 1}" : $"{i + 1} or {letter}",
                FocusMode = FocusModeEnum.All,
            };
            button.AddThemeFontSizeOverride("font_size", FontSize);
            button.Pressed += () => Choose(verb);
            _list.AddChild(button);
        }
        Size = Vector2.Zero;
        Visible = true;
        // Keep it on screen, just right of and below the pointer.
        Vector2 size = GetCombinedMinimumSize();
        Vector2 screen = GetViewportRect().Size;
        Position = new Vector2(Math.Clamp(at.X + 6, 0, Math.Max(0, screen.X - size.X)), Math.Clamp(at.Y + 6, 0, Math.Max(0, screen.Y - size.Y)));
        if (_list.GetChildCount() > 0)
        {
            ((Control)_list.GetChild(0)).CallDeferred(Control.MethodName.GrabFocus);
        }
    }

    public void Close() => Visible = false;

    public override void _Input(InputEvent e)
    {
        if (!Visible)
        {
            return;
        }
        if (e is InputEventMouseButton { Pressed: true } click && !GetGlobalRect().HasPoint(click.Position))
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
        else if (e.IsActionPressed(Controls.Back))
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
        else if (e is InputEventKey { Pressed: true, Echo: false } key)
        {
            int number = (int)(key.Keycode - Key.Key1);
            Verb? verb = number >= 0 && number < _verbs.Count ? _verbs[number]
                : _verbs.Where(v => Labels[v].Letter == key.Keycode).Select(v => (Verb?)v).FirstOrDefault();
            if (verb is { } v)
            {
                GetViewport().SetInputAsHandled();
                Choose(v);
            }
        }
    }

    private void Choose(Verb verb)
    {
        Close();
        Chosen?.Invoke(verb);
    }

    /// <summary>The original's icon for Talk, Get and Use (normal frame, in the menu palette), or null.</summary>
    private Texture2D? Icon(Verb verb, bool creature)
    {
        if (_icons.TryGetValue((verb, creature), out Texture2D? icon))
        {
            return icon;
        }
        int? number = verb switch
        {
            Verb.Talk => creature ? 15105 : 15101,
            Verb.Use => creature ? 15106 : 15102,
            Verb.Get => creature ? 15107 : 15103,
            _ => null,
        };
        GffFile resources = Session.Open("RESOURCE.GFF");
        if (number is int n && resources.TryRead("ICON", n, out var data) && Session.Assets.Frames(data) is [var frame, ..])
        {
            icon = Textures.ToTexture(frame, Session.Assets.Palette(resources.Read("PAL ", 1001)));
        }
        _icons[(verb, creature)] = icon;
        return icon;
    }
}
