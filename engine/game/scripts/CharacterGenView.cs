using DarkSun.Data.Animation;
using DarkSun.Data.Gff;
using DarkSun.Data.Images;
using DarkSun.Game.Animation;
using DarkSun.Game.Characters;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// The Character Generation screen (<c>WIND</c> 3011; the manual, pages 9–11),
/// with its parts where the window puts them and the original's art: the body
/// portrait (<c>BMP</c> 20000–20013, <see cref="CharacterCreation.Portrait"/>; left-click for the next race
/// and gender, right-click for the previous), the map icon (DSUN.EXE's for the
/// look, <see cref="CharacterCreation.Icon"/>; clicking it picks another, which the
/// original may not offer), the die (<c>BMP</c> 20048–20058;
/// rolls new abilities and hit points), the class list (<c>ICON</c> 2002–2009:
/// normal, chosen, unavailable; a diamond marks the chosen ones), the six
/// abilities, hit points and alignment (left-click raises, right-click
/// lowers), the name (<c>EBOX</c> 4003), the psionic disciplines or clerical
/// spheres (<c>WIND</c> 3012 / 3013, <c>ICON</c> 2038–2047), EXIT and DONE. The
/// rules are <see cref="CharacterCreation"/>'s. Every control is where the
/// window's data puts it (<see cref="UiWindowView"/>; the engine moves the die,
/// <c>res://ui/WIND/3011.json</c>); the panels behind them are drawn here, as
/// the window has no <c>APFM</c> frames.
/// </summary>
public partial class CharacterGenView : RetroScreen
{
    public const string ScenePath = "res://scenes/character_gen.tscn";

    private const int PaletteNumber = 1001;
    private const int DieFirst = 20048, DieLast = 20058;

    // WIND 3011's controls.
    private const int WindowNumber = 3011, PortraitButton = 2001, IconButtonNumber = 2027, DieButton = 2010,
        FirstClassButton = 2002, FirstAbilityButton = 2012, HpButton = 2011, AlignmentButton = 2018,
        ExitButton = 2058, DoneButton = 2000, NameBox = 4003;

    private static Character? _editing;
    private static int _slot;
    private static bool _isNew;

    private Character _c = null!;
    private CharacterCreation _cc = null!;
    private Palette _palette = null!;
    private GffFile _resources = null!;
    private UiWindowView _window = null!;
    private TextureRect _portrait = null!, _icon = null!, _die = null!;
    private readonly List<(TextureButton Label, Label Mark, CharacterClass Class)> _classes = [];
    private readonly List<Label> _abilities = [];
    private Label _hp = null!, _summary = null!, _alignment = null!, _status = null!;
    private LineEdit _name = null!;
    private Control _powers = null!;
    private bool _showSpheres;
    private int _dieFrames;
    private double _dieClock;

    /// <summary>Open the screen for <paramref name="character"/> (null: a new one) in party slot <paramref name="slot"/>.</summary>
    public static void Open(Character? character, int slot)
    {
        _editing = character;
        _slot = slot;
        _isNew = character is null;
        ((SceneTree)Godot.Engine.GetMainLoop()).CallDeferred(SceneTree.MethodName.ChangeSceneToFile, ScenePath);
    }

    public override void _Ready()
    {
        base._Ready();
        if (Session.Install is null)
        {
            GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, "res://scenes/main.tscn");
            return;
        }
        _resources = Session.Open("RESOURCE.GFF");
        _palette = Session.Assets.Palette(_resources.Read("PAL ", PaletteNumber));
        _cc = new CharacterCreation(Session.Rules, Session.Names, new Random());
        // Work on a copy: EXIT leaves the character as it was.
        _c = _editing is { } e ? CharacterRoster.FromJson(CharacterRoster.ToJson(e)) : _cc.New();
        if (_isNew)
        {
            _c.Sprite = _cc.Icon(_c);
        }
        BuildBackdrop();
        // The controls are window 3011's, where its data (and res://ui/WIND/3011.json) puts them.
        _window = UiWindowView.Build(WindowNumber, _palette, backdrop: 0)!;
        Canvas.AddChild(_window);

        _portrait = Picture(PortraitButton, by => _cc.CycleLooks(_c, by)); // the look's own map icon comes with it
        _icon = Picture(IconButtonNumber, CycleSprite);
        _die = Picture(DieButton, _ => Roll());
        _window.Button(DieButton)!.TooltipText = "Roll new abilities";

        for (int k = 0; k < 8; k++)
        {
            var cls = (CharacterClass)k;
            TextureButton label = _window.Button(FirstClassButton + k)!;
            label.GuiInput += e => Click(e, _ => _cc.ToggleClass(_c, cls));
            var mark = Text((int)label.Position.X - 8, (int)label.Position.Y - 2, 8, "");
            _classes.Add((label, mark, cls));
        }

        Rect2 nameBox = _window.RectOf("EBOX", NameBox);
        _name = new LineEdit
        {
            Position = nameBox.Position,
            Size = nameBox.Size,
            MaxLength = 15,
            Editable = true,
            Flat = true,
            CaretBlink = true,
        };
        GameFonts.Use(_name, new Color(1f, 0.92f, 0.7f));
        var bare = new StyleBoxEmpty();
        _name.AddThemeStyleboxOverride("normal", bare);
        _name.AddThemeStyleboxOverride("focus", bare);
        _name.TextChanged += t => { _cc.Rename(_c, t); };
        _name.TextSubmitted += _ => _name.ReleaseFocus();
        Canvas.AddChild(_name);
        // It grows to its minimum height with the text centred: once laid out, keep the text on the box.
        Callable.From(() => _name.Position = nameBox.Position - new Vector2(0, MathF.Floor((_name.Size.Y - GameFonts.Size) / 2))).CallDeferred();
        Text(4, (int)nameBox.Position.Y, (int)nameBox.Position.X - 4, "NAME:");

        for (int i = 0; i < 6; i++)
        {
            var ability = (Ability)i;
            _abilities.Add(ButtonLabel(FirstAbilityButton + i, "Left-click raises, right-click lowers",
                by => { if (by > 0) { _cc.Raise(_c, ability); } else { _cc.Lower(_c, ability); } }));
        }
        _hp = ButtonLabel(HpButton, "Left-click raises, right-click lowers", by => _cc.ChangeHp(_c, by));
        _alignment = ButtonLabel(AlignmentButton, "Click to change", by => _cc.CycleAlignment(_c, by));
        // Race, gender and level: no control of their own in the window; between hit points and alignment.
        Rect2 hp = _window.RectOf("BUTN", HpButton);
        _summary = Text((int)hp.Position.X, (int)hp.End.Y + 1, 130, "");
        _summary.Size = new Vector2(130, 20);

        _powers = new Control { Position = new Vector2(210, 80), Size = new Vector2(110, 64) };
        Canvas.AddChild(_powers);

        _window.Button(ExitButton)!.Pressed += Exit;
        _window.Button(ExitButton)!.TooltipText = "Leave without keeping the changes (Esc)";
        _window.Button(DoneButton)!.Pressed += Done;
        _status = Text(4, 186, 236, "");
        _status.LabelSettings.FontColor = new Color(1f, 0.7f, 0.45f);

        if (OS.GetCmdlineUserArgs().Contains("--check-chargen"))
        {
            Check();
            return;
        }
        Refresh();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e.IsActionPressed(Controls.Back) && !_name.HasFocus())
        {
            GetViewport().SetInputAsHandled();
            Exit();
        }
    }

    public override void _Process(double delta)
    {
        // The die tumbles through its faces for a moment after a roll.
        _dieClock += delta;
        if (_dieFrames > 0 && _dieClock >= 0.06)
        {
            _dieClock = 0;
            _dieFrames--;
            int face = _dieFrames == 0 ? DieFirst + new Random().Next(DieLast - DieFirst + 1) : DieFirst + _dieFrames % (DieLast - DieFirst + 1);
            _die.Texture = Bmp(face);
        }
    }

    /// <summary>Things an adventured character can't change any more (EDIT NAME only).</summary>
    private bool Changeable => !_c.Adventured;

    private void Click(InputEvent e, Action<int> act)
    {
        if (e is InputEventMouseButton { Pressed: true } click && click.ButtonIndex is MouseButton.Left or MouseButton.Right && Changeable)
        {
            act(click.ButtonIndex == MouseButton.Left ? +1 : -1);
            GetViewport().SetInputAsHandled();
            Refresh();
        }
    }

    private void Roll()
    {
        _cc.Roll(_c);
        _dieFrames = 8;
    }

    private void Refresh()
    {
        _portrait.Texture = Bmp(_cc.Portrait(_c));
        _icon.Texture = SpriteTexture(_c.Sprite);
        _die.Texture ??= Bmp(DieFirst);
        foreach (var (label, mark, cls) in _classes)
        {
            bool chosen = _c.Classes.Contains(cls);
            bool open = chosen || _cc.CanAdd(_c, cls);
            // The label's frames are its states, not hover looks: normal, chosen, unavailable.
            Texture2D? look = Icon(FirstClassButton + (int)cls, chosen ? 1 : open ? 0 : 2);
            (label.TextureNormal, label.TextureHover, label.TexturePressed, label.TextureDisabled) = (look, look, look, look);
            mark.Text = chosen ? "◆" : "";
        }
        if (!_name.HasFocus())
        {
            _name.Text = _c.Name;
        }
        _name.Editable = true;
        for (int i = 0; i < 6; i++)
        {
            var a = (Ability)i;
            int adjust = _cc.Adjustment(_c, a);
            _abilities[i].Text = $"{a.ToString().ToUpperInvariant()}: {_cc.Adjusted(_c, a),2}{(adjust != 0 ? $" ({adjust:+0;-0})" : "")}";
        }
        var levels = _cc.Levels(_c);
        _hp.Text = $"HP: {_c.Hp}  (MAX {_cc.HpRange(_c).High})";
        _summary.Text = $"{Session.Rules.Race(_c.Race).Name.ToUpperInvariant()} {_c.Gender.ToString().ToUpperInvariant()}\n"
            + $"LEVEL {string.Join("/", levels)}   EXP: {_c.Experience}";
        _alignment.Text = string.Concat(_c.Alignment.ToString().Select((ch, i) => i > 0 && char.IsUpper(ch) ? " " + ch : ch.ToString())).ToUpperInvariant();
        BuildPowers();
        _status.Text = _c.Adventured ? "Only the name can change now: this character has adventured." : "";
    }

    /// <summary>The discipline or sphere list (<c>WIND</c> 3012 / 3013, from their data), with the button that switches between them.</summary>
    private void BuildPowers()
    {
        foreach (Node child in _powers.GetChildren())
        {
            child.QueueFree();
        }
        bool spheres = _showSpheres && _cc.NeedsSphere(_c);
        if (UiWindowView.Build(spheres ? 3013 : 3012, _palette, backdrop: 0) is not { } list)
        {
            return;
        }
        _powers.AddChild(list);
        _powers.AddChild(Text(0, 3, 110, spheres ? "CLERICAL SPHERE" : "PSI DISCIPLINES", parent: false));
        int[] icons = spheres ? [2042, 2043, 2044, 2045] : [2038, 2039, 2040];
        for (int i = 0; i < icons.Length; i++)
        {
            bool chosen = spheres ? _c.Sphere == (Sphere)i : _c.Disciplines.Contains((Discipline)i);
            int index = i;
            if (list.Button(icons[i]) is not { } label)
            {
                continue;
            }
            Texture2D? look = Icon(icons[i], chosen ? 1 : 0);
            (label.TextureNormal, label.TextureHover, label.TexturePressed) = (look, look, look);
            label.GuiInput += e => Click(e, _ =>
            {
                if (spheres)
                {
                    _cc.ChooseSphere(_c, (Sphere)index);
                }
                else
                {
                    _cc.ChooseDiscipline(_c, (Discipline)index);
                }
            });
            if (chosen)
            {
                _powers.AddChild(Text((int)label.Position.X - 7, (int)label.Position.Y - 2, 8, "◆", parent: false));
            }
        }
        // The fourth discipline (2041, psychoportation) has no picture: not offered.
        if (list.Button(2041) is { } unused)
        {
            unused.Visible = false;
        }
        // VIEW SPHERES / VIEW PSIONICS: only for characters that have a sphere.
        bool can = _cc.NeedsSphere(_c);
        if (list.Button(spheres ? 2047 : 2046) is { } toggle)
        {
            toggle.Disabled = !can;
            toggle.Pressed += () =>
            {
                _showSpheres = !_showSpheres;
                Refresh();
            };
        }
    }

    private void Done()
    {
        if (_c.Name.Trim().Length == 0)
        {
            _c.Name = Session.Names.Pick(_c.Race, _c.Gender, new Random());
        }
        if (_cc.Problems(_c) is { Count: > 0 } problems)
        {
            _status.Text = problems[0];
            return;
        }
        var party = Session.Party;
        if (_isNew)
        {
            if (party.Count >= DarkSun.Game.World.GameWorld.PartySize)
            {
                _status.Text = "The party is full.";
                return;
            }
            party.Insert(Math.Min(_slot, party.Count), _c);
        }
        else if (party.FindIndex(p => p.Id == _c.Id) is int at and >= 0)
        {
            party[at] = _c;
        }
        Session.Roster.Save(_c);
        Session.SaveParty();
        GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, PartyView.ScenePath);
    }

    private void Exit() => GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, PartyView.ScenePath);

    // -- sprites ------------------------------------------------------------------------


    private void CycleSprite(int by)
    {
        var sprites = Session.CharacterSprites;
        if (sprites.Count == 0)
        {
            return;
        }
        int at = Math.Max(0, sprites.FindIndex(s => s.Ojff == _c.Sprite));
        _c.Sprite = sprites[((at + by) % sprites.Count + sprites.Count) % sprites.Count].Ojff;
    }

    /// <summary>The sprite standing, facing south.</summary>
    private ImageTexture? SpriteTexture(int ojff)
    {
        if (Session.CharacterSprites.FirstOrDefault(s => s.Ojff == ojff) is not { } sprite)
        {
            return null;
        }
        var animator = ScmdAnimator.ForCreature(sprite.Script, sprite.Sizes, sprite.Script.StandStart(Direction.South));
        return sprite.Frames.Count > animator.Frame ? Textures.ToTexture(sprite.Frames[animator.Frame], _palette) : null;
    }

    // -- drawing --------------------------------------------------------------------------

    private void BuildBackdrop()
    {
        if (Bmp(13001) is { } stone)
        {
            Canvas.AddChild(new TextureRect { Texture = stone, MouseFilter = MouseFilterEnum.Ignore });
        }
        foreach (var (x, y, w, h) in new[] { (0, 0, 100, 116), (132, 17, 51, 41), (132, 59, 56, 57), (205, 6, 113, 72), (205, 80, 113, 66), (0, 119, 164, 64), (72, 140, 132, 42) })
        {
            var panel = new Panel { Position = new Vector2(x, y), Size = new Vector2(w, h), MouseFilter = MouseFilterEnum.Ignore };
            var box = new StyleBoxFlat { BgColor = new Color(0.12f, 0.12f, 0.17f, 0.55f), BorderColor = new Color(0.62f, 0.62f, 0.72f) };
            box.SetBorderWidthAll(1);
            panel.AddThemeStyleboxOverride("panel", box);
            Canvas.AddChild(panel);
        }
    }

    private ImageTexture? Bmp(int number) =>
        _resources.TryRead("BMP ", number, out var data) && Session.Assets.Frames(data) is [var frame, ..] ? Textures.ToTexture(frame, _palette) : null;

    private ImageTexture? Icon(int number, int frame)
    {
        if (!_resources.TryRead("ICON", number, out var data))
        {
            return null;
        }
        var frames = Session.Assets.Frames(data);
        ImageFrame f = frame < frames.Count && frames[frame].Width > 1 ? frames[frame] : frames[0];
        return Textures.ToTexture(f, _palette);
    }

    /// <summary>A picture filling button <paramref name="number"/> (centred); left- and right-clicks on it go to <paramref name="act"/>.</summary>
    private TextureRect Picture(int number, Action<int> act)
    {
        TextureButton button = _window.Button(number)!;
        var rect = new TextureRect
        {
            Size = button.Size,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepCentered,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        button.AddChild(rect);
        button.GuiInput += e => Click(e, act);
        return rect;
    }

    /// <summary>A line of text on button <paramref name="number"/> (the window's thin click strips), clicked as <paramref name="act"/> says.</summary>
    private Label ButtonLabel(int number, string tip, Action<int> act)
    {
        TextureButton button = _window.Button(number)!;
        button.TooltipText = tip;
        button.GuiInput += e => Click(e, act);
        // The strip is 5–7 pixels high, the font 9: the text sits on it.
        var label = Text(0, (int)(button.Size.Y - GameFonts.Size) / 2, 140, "", parent: false);
        button.AddChild(label);
        return label;
    }

    private Label Text(int x, int y, int width, string text, bool parent = true)
    {
        var label = new Label
        {
            Position = new Vector2(x, y),
            Size = new Vector2(width, 9),
            Text = text,
            MouseFilter = MouseFilterEnum.Ignore,
            LabelSettings = GameFonts.Settings(new Color(0.95f, 0.9f, 0.75f)),
        };
        if (parent)
        {
            Canvas.AddChild(label);
        }
        return label;
    }

    /// <summary><c>-- --check-chargen</c>: make a character of every race and gender, every class each allows, and print what came of it.</summary>
    private void Check()
    {
        int made = 0, failed = 0;
        foreach (var (race, gender) in _cc.Looks)
        {
            foreach (CharacterClass k in Session.Rules.Race(race).Classes)
            {
                Character c = _cc.New();
                _cc.SetLooks(c, race, gender);
                c.Classes.Clear();
                _cc.ToggleClass(c, k);
                if (_cc.Problems(c) is { Count: > 0 } p)
                {
                    failed++;
                    GD.Print($"  {race} {gender} {k}: {string.Join("; ", p)}");
                }
                else
                {
                    made++;
                }
            }
        }
        bool art = _cc.Looks.All(l => Bmp(CharacterCreation.PortraitBase + _cc.Looks.ToList().IndexOf(l)) is not null) && Icon(2002, 0) is not null && Bmp(DieFirst) is not null;
        GD.Print($"chargen: {made} characters made, {failed} refused; {_cc.Looks.Count} portraits; art {(art ? "found" : "missing")}; {Session.CharacterSprites.Count} sprites");
        GetTree().Quit(failed > 0 || !art ? 1 : 0);
    }
}
