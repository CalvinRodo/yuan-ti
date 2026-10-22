using DarkSun.Data.Gff;
using DarkSun.Data.Images;
using DarkSun.Game.World;
using Godot;

namespace DarkSun.Engine;

/// <summary>What the right-click menu shows of its object (<see cref="VerbMenu.Open"/>).</summary>
/// <param name="Lines">A creature's weapon lines (<c>78A9:28DC</c>: each weapon's name, then its damage).</param>
/// <param name="Picture">A thing's picture.</param>
public sealed record VerbTarget(string Name, bool Creature, int Level, int Hp, int MaxHp, IReadOnlyList<string> Lines, Texture2D? Picture);

/// <summary>
/// The right-click menu of an object, DSUN.EXE's verb window (<c>68BD:0040</c>):
/// <list type="bullet">
/// <item>a creature's <c>WIND</c> 3020 (its stone <c>BMP</c> 3020): its hit
/// points as a fire bar (<c>78A9:2796</c>: <c>BMP</c> 20106 at (5, 5), the fire
/// <c>BMP</c> 20107 cut to the hit points left), its name at (6, 16) and
/// "LEVEL: n" under it in colour 31 (shadow 17, <c>68BD:0F5C</c>), and its
/// weapons from (6, 30), a line every 7 (<c>78A9:28DC</c>);</item>
/// <item>a thing's <c>WIND</c> 15500 over the parchment <c>BMP</c> 15000 (its
/// size; the window's verb icons are drawn with the parchment's edge): the
/// thing's picture unless it is bigger than 85 × 43 (<c>91F7:0C29</c>), and its
/// name (laid out more simply here);</item>
/// <item>the three verb diamonds, Talk, Get and Use, always there and disabled
/// when the object doesn't offer them (<c>2A1D:071A</c> with the flags of
/// <c>68BD:0C6D</c>), and a fourth button (INFO; a creature's is blank) for
/// Look, or Open for a container.</item>
/// </list>
/// Only these, as in the original: Attack is the attack pointer's, and an item
/// is used with something by holding it on the pointer from the inventory. Click
/// a verb, or press its letter (T, G, U, L, O); Esc or a click elsewhere closes it.
/// An object with only Talk, only Use or only Open skips the menu (<c>RegionView</c>
/// does it at once, as <c>68BD:0040</c>).
/// </summary>
public partial class VerbMenu : Control
{
    private const int CreatureWindow = 3020, ThingWindow = 15500, ThingBackdrop = 15000;
    private const int BarFrame = 20106, BarFire = 20107;
    private const int TextColour = 31, TextShadow = 17;
    private const int LineHeight = 7;

    /// <summary>Each window's verb buttons (<c>BUTN</c>), Look (or Open) on the fourth.</summary>
    private static readonly Dictionary<Verb, int> CreatureButtons = new() { [Verb.Talk] = 15306, [Verb.Get] = 15308, [Verb.Use] = 15307, [Verb.Look] = 15309 };

    private static readonly Dictionary<Verb, int> ThingButtons = new() { [Verb.Talk] = 15301, [Verb.Get] = 15303, [Verb.Use] = 15302, [Verb.Look] = 15304 };

    private static readonly Dictionary<Verb, (string Label, Key Letter)> Labels = new()
    {
        [Verb.Talk] = ("Talk", Key.T),
        [Verb.Get] = ("Get", Key.G),
        [Verb.Use] = ("Use", Key.U),
        [Verb.UseWith] = ("Use with", Key.None),
        [Verb.Attack] = ("Attack", Key.None),
        [Verb.Look] = ("Look", Key.L),
        [Verb.Open] = ("Open", Key.O),
    };

    private IReadOnlyList<Verb> _verbs = [];

    /// <summary>The verb the player chose (the menu has closed).</summary>
    public Action<Verb>? Chosen { get; set; }

    public override void _Ready()
    {
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
    }

    /// <summary>
    /// Show <paramref name="verbs"/> for <paramref name="target"/> beside
    /// <paramref name="at"/> (screen position), in <paramref name="palette"/>
    /// (the region's), at the 320 × 200 screen's scale.
    /// </summary>
    public void Open(VerbTarget target, IReadOnlyList<Verb> verbs, Palette palette, Vector2 at)
    {
        _verbs = verbs;
        foreach (Node child in GetChildren())
        {
            RemoveChild(child);
            child.QueueFree();
        }
        Color Of(int index) => palette[index] is var c ? Color.Color8(c.R, c.G, c.B) : Colors.White;
        LabelSettings text = GameFonts.Settings(Of(TextColour), shadow: Of(TextShadow));
        if (UiWindowView.Build(target.Creature ? CreatureWindow : ThingWindow, palette, target.Creature ? null : ThingBackdrop) is not { } window)
        {
            return;
        }
        AddChild(window);
        Label Line(string s, Vector2 where)
        {
            var label = new Label { Text = s, Position = where, Size = new Vector2(window.Size.X - where.X - 4, GameFonts.Size), ClipText = true, LabelSettings = text, MouseFilter = MouseFilterEnum.Ignore };
            window.AddChild(label);
            return label;
        }

        if (target.Creature)
        {
            if (Art("BMP ", BarFrame, palette) is { } frame)
            {
                window.AddChild(new TextureRect { Texture = frame, Position = new Vector2(5, 5), MouseFilter = MouseFilterEnum.Ignore });
            }
            if (Art("BMP ", BarFire, palette) is { } fire && target.MaxHp > 0)
            {
                int width = Math.Clamp(fire.GetWidth() * Math.Max(0, target.Hp) / target.MaxHp, 0, fire.GetWidth());
                if (width > 0)
                {
                    var cut = new AtlasTexture { Atlas = fire, Region = new Rect2(0, 0, width, fire.GetHeight()) };
                    window.AddChild(new TextureRect { Texture = cut, Position = new Vector2(6, 6), MouseFilter = MouseFilterEnum.Ignore });
                }
            }
            Line(target.Name, new Vector2(6, 16));
            if (target.Level > 0)
            {
                Line($"LEVEL: {target.Level}", new Vector2(6, 16 + LineHeight));
            }
            for (int i = 0; i < target.Lines.Count && i < 4; i++)
            {
                Line(target.Lines[i], new Vector2(6, 30 + LineHeight * i));
            }
        }
        else
        {
            // 91F7:0C29: a picture over 85 × 43 isn't shown; the text goes under it (at 28 under a small one).
            float y = 12;
            if (target.Picture is { } picture && picture.GetWidth() <= 85 && picture.GetHeight() <= 43)
            {
                window.AddChild(new TextureRect { Texture = picture, Position = new Vector2(13, 8), MouseFilter = MouseFilterEnum.Ignore });
                y = Math.Max(28, 8 + picture.GetHeight() + 4);
            }
            Line(target.Name, new Vector2(13, y));
        }

        Dictionary<Verb, int> buttons = target.Creature ? CreatureButtons : ThingButtons;
        foreach (var (verb, number) in buttons)
        {
            if (window.Button(number) is not { } button)
            {
                continue;
            }
            // The fourth button: a container opens, anything else is looked at.
            Verb v = verb == Verb.Look && verbs.Contains(Verb.Open) ? Verb.Open : verb;
            button.Disabled = !verbs.Contains(v);
            button.TooltipText = Labels[v].Label;
            button.Pressed += () => Choose(v);
        }
        _verbs = [.. buttons.Keys.Select(v => v == Verb.Look && verbs.Contains(Verb.Open) ? Verb.Open : v).Where(verbs.Contains)];
        Vector2 size = window.Size;
        Size = size;

        // The screen's scale; beside the pointer, kept on screen (68BD:0DA1 keeps the window on it too).
        Vector2 screen = GetViewportRect().Size;
        float scale = Math.Max(1f, MathF.Floor(Math.Min(screen.X / RetroScreen.Width, screen.Y / RetroScreen.Height)));
        Scale = new Vector2(scale, scale);
        Vector2 shown = size * scale;
        Position = new Vector2(Math.Clamp(at.X + 6, 0, Math.Max(0, screen.X - shown.X)), Math.Clamp(at.Y + 6, 0, Math.Max(0, screen.Y - shown.Y)));
        Visible = true;
    }

    public void Close() => Visible = false;

    public override void _Input(InputEvent e)
    {
        if (!Visible)
        {
            return;
        }
        if (e is InputEventMouseButton { Pressed: true } click && !new Rect2(GlobalPosition, Size * Scale).HasPoint(click.Position))
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

    /// <summary>Choose <paramref name="verb"/> as a click on it would (for the headless checks).</summary>
    public void Pick(Verb verb)
    {
        if (Visible && _verbs.Contains(verb))
        {
            Choose(verb);
        }
    }

    private void Choose(Verb verb)
    {
        Close();
        Chosen?.Invoke(verb);
    }

    private static Texture2D? Art(string tag, int number, Palette palette)
    {
        GffFile resources = Session.Open("RESOURCE.GFF");
        return resources.TryRead(tag, number, out var data) && Session.Assets.Frames(data) is [var frame, ..] && frame.Width > 0
            ? Textures.ToTexture(frame, palette)
            : null;
    }
}
