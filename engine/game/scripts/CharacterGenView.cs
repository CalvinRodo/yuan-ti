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
/// rolls new abilities and hit points), the class list (<c>ICON</c> 2002–2009,
/// whose frames are black: can be chosen, orange: under the pointer,
/// blue-grey: can't; a dark diamond in the picture's blank left margin marks
/// the chosen ones), the six abilities, alignment and hit points (a square
/// marks them: left-click raises, right-click lowers), the name
/// (<c>EBOX</c> 4003), the psionic disciplines or clerical spheres
/// (<c>WIND</c> 3012 / 3013 on their parchment, <c>ICON</c> 2038–2047, coloured
/// as the classes), EXIT and DONE. The rules are <see cref="CharacterCreation"/>'s.
/// It looks as DSUN.EXE's does in a recording of the original: every control
/// where the window's data puts it (<see cref="UiWindowView"/>; the engine
/// moves the die, <c>res://ui/WIND/3011.json</c>), on the stone (<c>BMP</c>
/// 13001) and DSUN.EXE's parchment panels (<c>BMP</c> 20083–20087, placed as
/// <c>6ECD:08D3</c> does), the text straight on the stone in colour 52 with
/// the font's shadow, in rows 7 pixels apart: under the name the abilities
/// on the left; on the right gender and race, alignment, the classes,
/// level and experience (with the next level's), armour class and damage,
/// hit points and psionic strength.
/// </summary>
public partial class CharacterGenView : RetroScreen
{
    public const string ScenePath = "res://scenes/character_gen.tscn";

    private const int PaletteNumber = 1001;
    private const int DieFirst = 20048, DieLast = 20058;

    // WIND 3011's controls.
    private const int WindowNumber = 3011, PortraitButton = 2001, IconButtonNumber = 2027, DieButton = 2010,
        FirstClassButton = 2002, FirstAbilityButton = 2012, AlignmentButton = 2011, HpButton = 2018,
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
    private readonly List<(TextureButton Label, TextureRect Mark, CharacterClass Class)> _classes = [];
    private readonly List<Label> _abilities = [];
    private readonly List<ColorRect> _squares = [];
    private Label _hp = null!, _psp = null!, _looks = null!, _classNames = null!, _levels = null!, _experience = null!,
        _fighting = null!, _alignment = null!, _status = null!;

    /// <summary>The text's colour, palette colour 52 (as the original's screen shows).</summary>
    private static readonly Color Face = Color.Color8(212, 212, 224);

    /// <summary>The right-hand column: its text's left edge, and its first row (gender and race; the label's top, the letters 2 pixels lower).</summary>
    private const int Column = 86, FirstRow = 135, RowStep = 7;

    /// <summary>The chosen mark, frame 1 of <c>BMP</c> 20047 (a 6 × 5 diamond in colour 129; frame 0 is the parchment that rubs it out).</summary>
    private const int DiamondBmp = 20047;
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
            _classes.Add((label, Diamond(label), cls));
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
        GameFonts.Use(_name, Face);
        var bare = new StyleBoxEmpty();
        _name.AddThemeStyleboxOverride("normal", bare);
        _name.AddThemeStyleboxOverride("focus", bare);
        _name.TextChanged += t => { _cc.Rename(_c, t); };
        _name.TextSubmitted += _ => _name.ReleaseFocus();
        Canvas.AddChild(_name);
        // It grows to its minimum height with the text centred: once laid out, keep the text on the box.
        Callable.From(() => _name.Position = nameBox.Position - new Vector2(0, MathF.Floor((_name.Size.Y - GameFonts.Size) / 2))).CallDeferred();
        Text(4, (int)nameBox.Position.Y, (int)nameBox.Position.X - 4, "NAME:");

        // The abilities: "STR:" 6 pixels into the strip, past its square, the number right-aligned under the 45th pixel.
        for (int i = 0; i < 6; i++)
        {
            var ability = (Ability)i;
            Label name = ButtonLabel(FirstAbilityButton + i, 6, "Left-click raises, right-click lowers",
                by => { if (by > 0) { _cc.Raise(_c, ability); } else { _cc.Lower(_c, ability); } });
            name.Text = $"{(ability == Ability.Cha ? "CHR" : ability.ToString().ToUpperInvariant())}:";
            Label value = Text(0, (int)name.Position.Y, 45 - (int)_window.RectOf("BUTN", FirstAbilityButton + i).Position.X, "", parent: false);
            value.HorizontalAlignment = HorizontalAlignment.Right;
            name.GetParent().AddChild(value);
            _abilities.Add(value);
        }
        // The right-hand column: the alignment and hit points on their strips (2011, 2018), the rest in the rows between.
        int alignmentX = Column - (int)_window.RectOf("BUTN", AlignmentButton).Position.X;
        _alignment = ButtonLabel(AlignmentButton, alignmentX, "Click to change", by => _cc.CycleAlignment(_c, by));
        _hp = ButtonLabel(HpButton, Column - (int)_window.RectOf("BUTN", HpButton).Position.X, "Left-click raises, right-click lowers", by => _cc.ChangeHp(_c, by));
        _looks = Text(Column, FirstRow, 120, "");
        _classNames = Text(Column, FirstRow + 2 * RowStep, 120, "");
        _levels = Text(Column, FirstRow + 3 * RowStep, 36, "");
        _experience = Text(Column + 36, FirstRow + 3 * RowStep, 120, "");
        _fighting = Text(Column, FirstRow + 4 * RowStep, 130, "");
        _psp = Text(Column, FirstRow + 6 * RowStep, 120, "");

        // WIND 3012 / 3013 on the lower parchment (20087, their own backdrop, drawn with the others).
        _powers = new Control { Position = new Vector2(210, 88), Size = new Vector2(110, 64) };
        Canvas.AddChild(_powers);

        _window.Button(ExitButton)!.Pressed += Exit;
        _window.Button(ExitButton)!.TooltipText = "Leave without keeping the changes (Esc)";
        _window.Button(DoneButton)!.Pressed += Done;
        _status = Text(4, 188, 236, "");
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
            Paint(label, FirstClassButton + (int)cls, chosen || _cc.CanAdd(_c, cls));
            mark.Visible = chosen;
        }
        if (!_name.HasFocus())
        {
            _name.Text = _c.Name;
        }
        _name.Editable = true;
        for (int i = 0; i < 6; i++)
        {
            _abilities[i].Text = $"{_cc.Adjusted(_c, (Ability)i)}";
        }
        foreach (ColorRect square in _squares)
        {
            square.Visible = Changeable;
        }
        CharacterDetailsView d = ViewCharacterText.Of(_c);
        _looks.Text = d.Looks;
        _alignment.Text = d.Alignment;
        _classNames.Text = d.Classes.ToUpperInvariant();
        _levels.Text = d.Levels;
        _experience.Text = $"EXP:{d.Experience}" + (d.NextLevel is int next ? $" ({next})" : "");
        _fighting.Text = $"AC:{d.ArmorClass,3} DAM: {d.Damage}";
        _hp.Text = $"{d.Hp,3}/{d.MaxHp}";
        _psp.Text = $"{d.Psp,3}/{d.MaxPsp}";
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
        // The heading, black with a rose shadow (colours 254 and 60), centred at the parchment's top.
        Label heading = Text(0, 6, 110, spheres ? "CLERICAL SPHERE" : "PSI DISCIPLINES", parent: false);
        heading.LabelSettings = GameFonts.Settings(Colors.Black, shadow: Color.Color8(164, 112, 112));
        heading.HorizontalAlignment = HorizontalAlignment.Center;
        _powers.AddChild(heading);
        int[] icons = spheres ? [2042, 2043, 2044, 2045] : [2038, 2039, 2040];
        // Until one is chosen every one can be; then the others can't (a psionicist has them all).
        bool any = spheres ? _c.Sphere is not null : _c.Disciplines.Count > 0;
        for (int i = 0; i < icons.Length; i++)
        {
            bool chosen = spheres ? _c.Sphere == (Sphere)i : _c.Disciplines.Contains((Discipline)i);
            int index = i;
            if (list.Button(icons[i]) is not { } label)
            {
                continue;
            }
            Paint(label, icons[i], chosen || !any);
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
            Diamond(label).Visible = chosen;
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
        if (!_c.Adventured)
        {
            // DONE gives the class's gear (6ECD:1D48), kept with the character.
            _c.Items = Session.StartingItems(_c);
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

    private ImageTexture? SpriteTexture(int ojff) => StandingSprite(ojff, _palette);

    /// <summary>A character's map sprite (<c>OJFF</c> <paramref name="ojff"/>) standing, facing south.</summary>
    public static ImageTexture? StandingSprite(int ojff, Palette palette)
    {
        if (Session.CharacterSprites.FirstOrDefault(s => s.Ojff == ojff) is not { } sprite)
        {
            return null;
        }
        var animator = ScmdAnimator.ForCreature(sprite.Script, sprite.Sizes, sprite.Script.StandStart(Direction.South));
        return sprite.Frames.Count > animator.Frame ? Textures.ToTexture(sprite.Frames[animator.Frame], palette) : null;
    }

    // -- drawing --------------------------------------------------------------------------

    private void BuildBackdrop()
    {
        if (Bmp(13001) is { } stone)
        {
            Canvas.AddChild(new TextureRect { Texture = stone, MouseFilter = MouseFilterEnum.Ignore });
        }
        // The parchment panels DSUN.EXE draws (6ECD:08D3, 4239:0039(bmp, x, y)): the portrait's (20084),
        // the map icon's (20083), the die's (20085), and two on the right (20086, 20087).
        foreach (var (number, x, y) in new[] { (20084, 0, 0), (20083, 135, 19), (20085, 129, 70), (20086, 208, 0), (20087, 210, 88) })
        {
            if (Bmp(number) is { } parchment)
            {
                Canvas.AddChild(new TextureRect { Texture = parchment, Position = new Vector2(x, y), MouseFilter = MouseFilterEnum.Ignore });
            }
        }
    }

    private ImageTexture? Bmp(int number, int frame = 0) =>
        _resources.TryRead("BMP ", number, out var data) && Session.Assets.Frames(data) is { } frames && frame < frames.Count
            ? Textures.ToTexture(frames[frame], _palette)
            : null;

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

    /// <summary>
    /// A line of text on button <paramref name="number"/> (the window's thin
    /// click strips), <paramref name="x"/> pixels in, after the square that
    /// marks it as one to click (4 × 4, a pixel below the text's top; on the
    /// right-hand column's a pixel in); clicked as <paramref name="act"/> says.
    /// </summary>
    private Label ButtonLabel(int number, int x, string tip, Action<int> act)
    {
        TextureButton button = _window.Button(number)!;
        button.TooltipText = tip;
        button.GuiInput += e => Click(e, act);
        // The strip is 5–7 pixels high, the font 9: the text sits on it.
        // The letters start 2 pixels into the font's 9 rows; they sit on the strip as the original's do.
        int top = (int)(button.Size.Y - GameFonts.Size) / 2 - 2;
        var square = new ColorRect
        {
            Color = Face,
            Position = new Vector2(button.Position.X > 40 ? 1 : 0, top + 3),
            Size = new Vector2(4, 4),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        button.AddChild(square);
        _squares.Add(square);
        var label = Text(x, top, 140, "", parent: false);
        button.AddChild(label);
        return label;
    }

    /// <summary>A class or discipline picture's frames: black (0) if it can be chosen, else blue-grey (2); orange (1) under the pointer.</summary>
    private void Paint(TextureButton label, int icon, bool open)
    {
        Texture2D? look = Icon(icon, open ? 0 : 2);
        (label.TextureNormal, label.TextureDisabled) = (look, look);
        (label.TextureHover, label.TexturePressed, label.TextureFocused) = open ? (Icon(icon, 1), Icon(icon, 1), look) : (look, look, look);
    }

    /// <summary>The chosen mark: the diamond (<see cref="DiamondBmp"/>) in the blank margin left of a class or discipline picture.</summary>
    private TextureRect Diamond(TextureButton label)
    {
        var mark = new TextureRect
        {
            Texture = Bmp(DiamondBmp, 1),
            Position = new Vector2(1, 1),
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false,
        };
        label.AddChild(mark);
        return mark;
    }

    private Label Text(int x, int y, int width, string text, bool parent = true)
    {
        var label = new Label
        {
            Position = new Vector2(x, y),
            Size = new Vector2(width, 9),
            Text = text,
            MouseFilter = MouseFilterEnum.Ignore,
            LabelSettings = GameFonts.Settings(Face),
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
            // Every class alone, and every combination the race allows (DSUN.EXE's table, 38F0), picked one by one.
            RaceRules r = Session.Rules.Race(race);
            IEnumerable<IReadOnlyList<CharacterClass>> sets = [.. r.Classes.Select(k => (IReadOnlyList<CharacterClass>)[k]), .. r.Combinations ?? []];
            foreach (IReadOnlyList<CharacterClass> set in sets)
            {
                Character c = _cc.New();
                _cc.SetLooks(c, race, gender);
                c.Classes.Clear();
                bool picked = set.All(k => _cc.ToggleClass(c, k));
                // Nothing is chosen for the player: the discipline (and a priest's sphere) are picked.
                _cc.ChooseDiscipline(c, Discipline.Telepathy);
                _cc.ChooseSphere(c, Sphere.Fire);
                if (!picked || _cc.Problems(c) is { Count: > 0 })
                {
                    failed++;
                    GD.Print($"  {race} {gender} {string.Join("/", set)}: {(picked ? string.Join("; ", _cc.Problems(c)) : "couldn't be picked")}");
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
