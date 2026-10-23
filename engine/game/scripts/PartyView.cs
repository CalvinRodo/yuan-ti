using DarkSun.Data.Gff;
using DarkSun.Data.Images;
using DarkSun.Data.Regions;
using DarkSun.Game.Characters;
using DarkSun.Game.World;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// The View Character screen that Create Characters opens, drawn as DSUN.EXE
/// draws it (<c>9106:0622</c> builds it, <c>8F23:1E30</c> fills it) and as a
/// recording of the original shows: the menu's fire (<c>BMP</c> 20028 at
/// (47, 0)), window 11500 over its backdrop (<c>BMP</c> 11000) at (0, 9), and
/// the VIEW•CHARACTER banner (<c>BMP</c> 20079, centred at y 11) over both.
/// Four character boxes (<c>BUTN</c> 11300–11303, <c>ICON</c> 11100: the
/// selected one's yellow border is frame 3, an empty one's stone disc frame
/// 2), the text under them and the selected character's details on the right
/// (<see cref="ViewCharacterText"/>), with the three readied-weapon slots
/// (<c>APFM</c> 11215–11217, <c>BMP</c> 13007's stone). The bottom bar's
/// text buttons (11319, 11320) are hidden, as the original hides them; the
/// status line names what the pointer is on. Right-clicking a box opens the
/// original's small window (<c>WIND</c> 14000 over <c>BMP</c> 14000, its
/// title INACTIVE CHARACTER or the name, the choices in dark letters, red
/// under the pointer): an empty box NEW (the Character Generation screen) or
/// ADD (a character made before, listed three at a time, then PRE-MADE
/// PARTY, which fills the empty boxes from <see cref="Session.Premade"/>, not
/// in the original); a full one EDIT (EDIT NAME once the character has
/// adventured), DROP (back to the roster) and, for a human, DUAL (a new
/// class, the old one kept at its level: <c>WIND</c> 17502, the eight
/// classes, over <c>BMP</c> 17002). The return button or Esc closes it. The
/// party is kept for the next new game; Return (or Esc) goes back to the
/// menu. The leader and computer-control buttons work (<see cref="PartyFrames"/>).
/// </summary>
public partial class PartyView : RetroScreen
{
    public const string ScenePath = "res://scenes/party.tscn";

    private const int PaletteNumber = 1001;
    // WIND 11500's controls.
    private const int WindowNumber = 11500, FirstBox = 11300, FirstLeader = 11309, FirstComputerControl = 11313,
        FirstText = 11319, SecondText = 11320, ReturnButton = 10308, FirstReadied = 11215;
    private const int WindowTop = 9, Fire = 20028, Banner = 20079, BoxArt = 11100, SelectedFrame = 3, EmptyFrame = 2, SlotArt = 13007;
    // The question window: WIND 14000 over BMP 14000, its title strip and three choices.
    private const int AskWindow = 14000, AskTitle = 14004, FirstChoice = 14001;

    /// <summary>The status line's grey (colour 27) and the small window's: its title the same, its choices dark (17) on a light shadow (29), red (77) under the pointer.</summary>
    private static readonly Color StatusGrey = Color.Color8(136, 136, 160), ChoiceInk = Color.Color8(32, 32, 56),
        ChoiceShadow = Color.Color8(164, 164, 188), ChoiceHover = Color.Color8(212, 64, 40);

    private Palette _palette = null!;
    private GffFile _resources = null!;
    private readonly List<TextureRect> _slotPictures = [], _selectedFrames = [];

    /// <summary>The equipment slots the readied boxes show, as the recording does: the missile weapon, the weapon hand, the other hand.</summary>
    private static readonly int[] ReadiedSlots = [2, 3, 10];

    private readonly TextureRect[] _readied = new TextureRect[ReadiedSlots.Length];
    private readonly Dictionary<int, Texture2D?> _itemPictures = [];
    private PartyFrames _frames = null!;
    private ViewCharacterText _text = null!;
    private UiWindowView _dual = null!;
    private Label _dualTitle = null!;
    private const int DualWindow = 17502, DualBackdrop = 17002, FirstDualClass = 17303, DualExit = 17302;
    private Label _status = null!;
    private UiWindowView _window = null!, _ask = null!;
    private Label _askTitle = null!;
    private readonly Label[] _choices = new Label[3];
    private readonly Action?[] _choiceActions = new Action?[3];
    private int _addPage;
    private int _selected;

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
        // 9106:0622: the fire behind, window 11500 at (0, 9) over BMP 11000, the banner over both (70E7:0000(20079, centred, 320, 11)).
        AddPicture(Fire, new Vector2(47, 0));
        _window = UiWindowView.Build(WindowNumber, _palette, backdrop: 11000)!;
        _window.Position = new Vector2(0, WindowTop);
        Canvas.AddChild(_window);
        if (Bmp(Banner) is { } banner)
        {
            AddPicture(Banner, new Vector2((RetroScreen.Width - banner.GetWidth()) / 2 + 1, 11));
        }

        for (int i = 0; i < GameWorld.PartySize; i++)
        {
            int slot = i;
            TextureButton box = _window.Button(FirstBox + i)!;
            // The figure at its own size, as the original draws it: centred across, from the top; the box cuts a tall one's feet.
            var picture = new TextureRect { MouseFilter = MouseFilterEnum.Ignore };
            box.ClipContents = true;
            box.AddChild(picture);
            var selected = new TextureRect { Texture = Icon(BoxArt, SelectedFrame), MouseFilter = MouseFilterEnum.Ignore };
            box.AddChild(selected);
            box.GuiInput += e => SlotInput(slot, e);
            _slotPictures.Add(picture);
            _selectedFrames.Add(selected);
            if (_window.Button(FirstLeader + i) is { } leader)
            {
                leader.Pressed += () => LeaderPressed(slot);
            }
            if (_window.Button(FirstComputerControl + i) is { } computer)
            {
                computer.Pressed += () => ComputerPressed(slot);
            }
        }
        // The three readied weapons' slots (9106:0B64), stone, the item's picture centred over it.
        for (int i = 0; i < ReadiedSlots.Length; i++)
        {
            Rect2 at = _window.RectOf("APFM", FirstReadied + i);
            _window.AddChild(new TextureRect { Texture = Bmp(SlotArt), Position = at.Position, MouseFilter = MouseFilterEnum.Ignore });
            _readied[i] = new TextureRect { Position = at.Position, Size = at.Size, StretchMode = TextureRect.StretchModeEnum.KeepCentered, MouseFilter = MouseFilterEnum.Ignore };
            _window.AddChild(_readied[i]);
        }
        _text = new ViewCharacterText(_window);

        // The bottom bar (9106:0622): the character screens' icons, drawn but not used here, this page's (10300)
        // pressed, the spells' (11305) and 11308 disabled out of the game; the text buttons hidden.
        foreach (int n in new[] { 10300, 11304, 11305, 11306, 11308 })
        {
            if (_window.Button(n) is { } b)
            {
                b.MouseFilter = MouseFilterEnum.Ignore;
                b.Disabled = n is 11305 or 11308;
            }
        }
        if (_window.Button(10300) is { } page)
        {
            page.TextureNormal = page.TexturePressed;
        }
        foreach (int n in new[] { FirstText, SecondText })
        {
            _window.Button(n)!.Visible = false;
        }
        TextureButton back = _window.Button(ReturnButton)!;
        back.Pressed += Back;
        _status = new Label
        {
            Position = new Vector2(72, 176),
            Size = new Vector2(176, GameFonts.Size),
            HorizontalAlignment = HorizontalAlignment.Center,
            ClipText = true,
            MouseFilter = MouseFilterEnum.Ignore,
            LabelSettings = GameFonts.Settings(StatusGrey),
        };
        _window.AddChild(_status);
        _frames = new PartyFrames(_window, _status, MemberView, () => Session.LeaderIndex, bars: false);
        _frames.Hoverable(back, () => "RETURN TO GAME");
        BuildAsk();
        BuildDual();

        if (OS.GetCmdlineUserArgs().Contains("--check-party"))
        {
            GD.Print($"party: {Session.Party.Count} characters ({string.Join(", ", Session.Party.Select(c => c.Name))}); roster {Session.Roster.All().Count}");
            GetTree().Quit(CheckParty() ? 0 : 1);
            return;
        }
        Refresh();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e.IsActionPressed(Controls.Back))
        {
            GetViewport().SetInputAsHandled();
            if (_dual.Visible)
            {
                _dual.Visible = false;
            }
            else if (_ask.Visible)
            {
                _ask.Visible = false;
            }
            else
            {
                Back();
            }
        }
    }

    private void Back()
    {
        Session.SaveParty();
        GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, MainMenu.ScenePath);
    }

    private Character? Selected => _selected < Session.Party.Count ? Session.Party[_selected] : null;

    private void Refresh()
    {
        for (int i = 0; i < GameWorld.PartySize; i++)
        {
            Character? c = i < Session.Party.Count ? Session.Party[i] : null;
            TextureRect picture = _slotPictures[i];
            picture.Texture = c is null ? null : Portrait(c);
            if (picture.Texture is { } figure)
            {
                picture.Size = figure.GetSize();
                picture.Position = new Vector2(MathF.Floor((_window.RectOf("BUTN", FirstBox + i).Size.X - figure.GetWidth()) / 2), 1);
            }
            // An empty box is the stone disc; a full one the frame round its picture.
            TextureButton box = _window.Button(FirstBox + i)!;
            box.TextureNormal = Icon(BoxArt, c is null ? EmptyFrame : 0);
            _selectedFrames[i].Visible = i == _selected;
            _text.ShowBox(i, MemberView(i), i == _selected);
        }
        _frames.Refresh();
        Character? selected = Selected;
        IReadOnlyList<ItemState> kit = selected is null ? [] : Session.Kit(selected);
        _text.Show(selected is null ? null : ViewCharacterText.Of(selected, kit));
        for (int i = 0; i < ReadiedSlots.Length; i++)
        {
            _readied[i].Texture = kit.FirstOrDefault(k => k.Slot == ReadiedSlots[i]) is { } item ? ItemPicture(item.Rdff) : null;
        }
        if (Selected is null && _frames.Resting.Length == 0)
        {
            _frames.Resting = "PRESS RIGHT MOUSE BUTTON";
        }
    }

    private void SlotInput(int slot, InputEvent e)
    {
        if (e is not InputEventMouseButton { Pressed: true } click || _ask.Visible)
        {
            return;
        }
        _selected = Math.Min(slot, Session.Party.Count);
        _frames.Resting = "";
        Refresh();
        if (click.ButtonIndex != MouseButton.Right)
        {
            return;
        }
        // The small window opens beside the box, its top 2 pixels above the box's (as the recording shows).
        Rect2 box = _window.RectOf("BUTN", FirstBox + slot);
        Vector2 at = _window.Position + new Vector2(box.End.X + 1, box.Position.Y - 2);
        if (Selected is { } c)
        {
            Ask(c.Name.ToUpperInvariant(), at,
                (c.Adventured ? "EDIT NAME" : "EDIT", () => Command(1)),
                ("DROP", () => Command(2)),
                c.Race == Race.Human ? ("DUAL", () => Command(5)) : ("CANCEL", null));
        }
        else
        {
            Ask("INACTIVE CHARACTER", at, ("NEW", () => Command(3)), ("ADD", () => Command(4)), ("CANCEL", null));
        }
    }

    // -- the small window: WIND 14000 over BMP 14000 --------------------------------------------

    private void BuildAsk()
    {
        _ask = UiWindowView.Build(AskWindow, _palette, backdrop: AskWindow)!;
        _ask.Visible = false;
        Canvas.AddChild(_ask);
        Rect2 title = _ask.RectOf("BUTN", AskTitle);
        _askTitle = new Label
        {
            Position = title.Position + new Vector2(2, 0),
            Size = new Vector2(title.Size.X - 4, GameFonts.Size),
            MouseFilter = MouseFilterEnum.Ignore,
            LabelSettings = GameFonts.Settings(StatusGrey),
        };
        _ask.AddChild(_askTitle);
        for (int i = 0; i < 3; i++)
        {
            int choice = i;
            TextureButton button = _ask.Button(FirstChoice + i)!;
            _choices[i] = new Label
            {
                Position = new Vector2(2, 0),
                Size = new Vector2(button.Size.X - 4, GameFonts.Size),
                MouseFilter = MouseFilterEnum.Ignore,
                LabelSettings = GameFonts.Settings(ChoiceInk, shadow: ChoiceShadow),
            };
            button.AddChild(_choices[i]);
            button.MouseEntered += () => _choices[choice].LabelSettings = GameFonts.Settings(ChoiceHover, shadow: ChoiceShadow);
            button.MouseExited += () => _choices[choice].LabelSettings = GameFonts.Settings(ChoiceInk, shadow: ChoiceShadow);
            button.Pressed += () =>
            {
                _ask.Visible = false;
                _choiceActions[choice]?.Invoke();
            };
        }
        _ask.Button(ReturnButton)!.Pressed += () => _ask.Visible = false;
    }

    /// <summary>The small window at <paramref name="at"/> (kept on the screen): a title and up to three choices; a choice without an action just closes it.</summary>
    private void Ask(string title, Vector2 at, params (string Text, Action? Then)[] choices)
    {
        _askTitle.Text = title;
        for (int i = 0; i < 3; i++)
        {
            bool there = i < choices.Length;
            _choices[i].Text = there ? choices[i].Text : "";
            _choices[i].LabelSettings = GameFonts.Settings(ChoiceInk, shadow: ChoiceShadow);
            _choiceActions[i] = there ? choices[i].Then : null;
            _ask.Button(FirstChoice + i)!.Visible = there;
        }
        _ask.Position = new Vector2(
            Math.Clamp(at.X, 0, RetroScreen.Width - _ask.Size.X),
            Math.Clamp(at.Y, 0, RetroScreen.Height - _ask.Size.Y));
        _ask.Visible = true;
    }

    /// <summary>ADD: the roster's characters not in the party, two a page with MORE between pages, then PRE-MADE PARTY.</summary>
    private void AskAdd(Vector2 at)
    {
        var names = Available().Select(c => (c.Name.ToUpperInvariant(), (Action?)(() => AddCharacter(c)))).ToList();
        names.Add(("PRE-MADE PARTY", () => Command(6)));
        int pages = (names.Count + 1) / 2;
        _addPage %= Math.Max(1, pages);
        var shown = names.Skip(_addPage * 2).Take(2).ToList();
        if (names.Count <= 3)
        {
            shown = names;
        }
        else
        {
            shown.Add(("MORE", () => { _addPage++; AskAdd(at); }));
        }
        Ask("ADD", at, [.. shown]);
    }

    private void AddCharacter(Character c)
    {
        if (Session.Party.Count >= GameWorld.PartySize)
        {
            _frames.Resting = "MAXIMUM CHARACTERS: 4";
            return;
        }
        Session.Party.Add(c);
        Session.SaveParty();
        _selected = Session.Party.Count - 1;
        _frames.Resting = "";
        Refresh();
    }

    /// <summary>1 edit, 2 drop, 3 new, 4 add, 0 cancel.</summary>
    private void Command(int id)
    {
        switch (id)
        {
            case 1 when Selected is { } c:
                CharacterGenView.Open(c, _selected);
                break;
            case 2 when Selected is { } c:
                Session.Roster.Save(c);
                Session.Party.RemoveAt(_selected);
                Session.SaveParty();
                _frames.Resting = $"{c.Name.ToUpperInvariant()} IS ON DISK";
                Refresh();
                break;
            case 3:
                CharacterGenView.Open(null, Session.Party.Count);
                break;
            case 4:
                _addPage = 0;
                AskAdd(_ask.Position);
                break;
            case 5 when Selected is { } c:
                OpenDual(c);
                break;
            case 6:
                AddPremade();
                break;
        }
    }

    private PartyMemberView? MemberView(int slot)
    {
        if (slot >= Session.Party.Count)
        {
            return null;
        }
        Character c = Session.Party[slot];
        int psp = new DarkSun.Game.Magic.Spellcasting(Session.Rules).MaxPsp(c);
        return new PartyMemberView(c.Name, c.Hp, c.Hp, psp - c.PspSpent, psp, c.Adventured ? 1 : 0, c.ComputerControl);
    }

    private void LeaderPressed(int slot)
    {
        if (slot < Session.Party.Count)
        {
            Session.SetLeader(slot);
            Session.SaveParty();
            _frames.Resting = $"{Session.Party[slot].Name.ToUpperInvariant()} IS LEADER";
        }
        Refresh();
    }

    private void ComputerPressed(int slot)
    {
        if (slot < Session.Party.Count)
        {
            Character c = Session.Party[slot];
            c.ComputerControl = !c.ComputerControl;
            Session.SaveParty();
            _frames.Resting = $"COMPUTER CONTROL IS {(c.ComputerControl ? "ON" : "OFF")}";
        }
        Refresh();
    }

    /// <summary>Fill the empty boxes from the pre-made party (those not in the party already, by name).</summary>
    private void AddPremade()
    {
        int added = 0;
        foreach (Character c in Session.Premade().Where(p => Session.Party.All(m => m.Name != p.Name)))
        {
            if (Session.Party.Count >= GameWorld.PartySize)
            {
                break;
            }
            c.Leader &= Session.Party.Count == 0;
            Session.Party.Add(c);
            added++;
        }
        Session.SaveParty();
        _frames.Resting = added > 0 ? "THE PRE-MADE PARTY JOINS" : "NO ROOM";
        Refresh();
    }

    // -- DUAL: WIND 17502, the eight classes --------------------------------------------------

    private void BuildDual()
    {
        _dual = UiWindowView.Build(DualWindow, _palette, DualBackdrop)!;
        _dual.Position = ((new Vector2(RetroScreen.Width, RetroScreen.Height) - _dual.Size) / 2).Floor();
        _dual.Visible = false;
        Canvas.AddChild(_dual);
        // BMP 17002 has its own DUAL title; the message goes left of the class list.
        _dualTitle = new Label
        {
            Position = new Vector2(10, 30),
            Size = new Vector2(_dual.RectOf("BUTN", FirstDualClass).Position.X - 16, 60),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            LabelSettings = GameFonts.Settings(new Color(0.95f, 0.9f, 0.75f)),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _dual.AddChild(_dualTitle);
        // The class buttons, in CharacterClass order: cleric, druid, fighter, gladiator, preserver, psionicist, ranger, thief.
        foreach (CharacterClass k in Enum.GetValues<CharacterClass>())
        {
            CharacterClass choice = k;
            _dual.Button(FirstDualClass + (int)k)!.Pressed += () => ChooseDual(choice);
        }
        _dual.Button(DualExit)!.Pressed += () => _dual.Visible = false;
        if (_dual.ButtonText(DualExit) is { } t)
        {
            t.LabelSettings = GameFonts.Settings(new Color(0.95f, 0.9f, 0.75f));
        }
    }

    private void OpenDual(Character c)
    {
        var cc = new CharacterCreation(Session.Rules, Session.Names, new Random());
        var (classes, why) = cc.DualChoices(c);
        foreach (CharacterClass k in Enum.GetValues<CharacterClass>())
        {
            TextureButton b = _dual.Button(FirstDualClass + (int)k)!;
            b.Disabled = !classes.Contains(k);
            b.Modulate = b.Disabled ? new Color(1, 1, 1, 0.35f) : Colors.White;
        }
        _dualTitle.Text = why ?? $"CHOOSE {c.Name.ToUpperInvariant()}'S NEW CLASS";
        _dual.Visible = true;
    }

    private void ChooseDual(CharacterClass k)
    {
        if (Selected is not { } c)
        {
            return;
        }
        var cc = new CharacterCreation(Session.Rules, Session.Names, new Random());
        if (cc.Dual(c, k))
        {
            Session.Roster.Save(c);
            Session.SaveParty();
            _dual.Visible = false;
            _frames.Resting = $"{c.Name.ToUpperInvariant()} IS NOW A {Session.Rules.Class(k).Name.ToUpperInvariant()}";
            Refresh();
        }
    }

    /// <summary>
    /// <c>--check-party</c>: the pre-made party is four finished characters
    /// with a leader; DUAL on a third-level human fighter offers preserver
    /// and makes it one; the leader button moves the leader. The party is put back.
    /// </summary>
    private bool CheckParty()
    {
        var problems = new List<string>();
        var cc = new CharacterCreation(Session.Rules, Session.Names, new Random());
        List<Character> premade = Session.Premade();
        if (premade.Count != GameWorld.PartySize || premade.Count(c => c.Leader) != 1)
        {
            problems.Add($"the pre-made party: {premade.Count} characters, {premade.Count(c => c.Leader)} leaders");
        }
        foreach (Character c in premade)
        {
            if (cc.Problems(c) is { Count: > 0 } p)
            {
                problems.Add($"{c.Name}: {string.Join(" ", p)}");
            }
            if (!c.Items.Any(i => i.Slot < Equipment.Slots))
            {
                problems.Add($"{c.Name} has no starting gear worn");
            }
        }
        var keep = Session.Party.ToList();
        var fighter = new Character { Name = "Dual check", Race = Race.Human, Classes = [CharacterClass.Fighter], Rolled = [17, 12, 12, 17, 12, 12], Experience = 10000, Hp = 20 };
        Session.Party.Clear();
        Session.Party.AddRange([fighter, premade[0]]);
        _selected = 0;
        Command(5);
        TextureButton preserver = _dual.Button(FirstDualClass + (int)CharacterClass.Preserver)!;
        if (!_dual.Visible || preserver.Disabled || !_dual.Button(FirstDualClass + (int)CharacterClass.Cleric)!.Disabled)
        {
            problems.Add($"DUAL shows the classes ({_dualTitle.Text})");
        }
        preserver.EmitSignal(BaseButton.SignalName.Pressed);
        if (fighter.Classes is not [CharacterClass.Preserver] || fighter.Former is not [{ Class: CharacterClass.Fighter }] || _dual.Visible)
        {
            problems.Add($"DUAL made a preserver ({string.Join("/", fighter.Classes)}, former {string.Join("/", fighter.Former)})");
        }
        _window.Button(FirstLeader + 1)!.EmitSignal(BaseButton.SignalName.Pressed);
        if (Session.LeaderIndex != 1 || fighter.Leader)
        {
            problems.Add("the leader button");
        }
        Session.Roster.Delete(fighter);
        Session.Party.Clear();
        Session.Party.AddRange(keep);
        Session.SaveParty();
        GD.Print(problems.Count == 0 ? "party: pre-made party, DUAL and the leader button: OK" : "party: " + string.Join("; ", problems));
        return problems.Count == 0;
    }

    /// <summary>Roster characters not in the party.</summary>
    private List<Character> Available() =>
        [.. Session.Roster.All().Where(c => Session.Party.All(p => p.Id != c.Id))];

    /// <summary>A box's picture: the character's map sprite standing, facing forwards.</summary>
    private Texture2D? Portrait(Character c) => CharacterGenView.StandingSprite(c.Sprite, _palette);

    private ImageTexture? Bmp(int number) =>
        _resources.TryRead("BMP ", number, out var data) && Session.Assets.Frames(data) is [var frame, ..] ? Textures.ToTexture(frame, _palette) : null;

    private ImageTexture? Icon(int number, int frame) =>
        _resources.TryRead("ICON", number, out var data) && Session.Assets.Frames(data) is { } frames && frame < frames.Count
            ? Textures.ToTexture(frames[frame], _palette)
            : null;

    /// <summary>An item's picture: its map sprite (<c>OJFF</c> = <c>RDFF</c>), as the inventory shows it.</summary>
    private Texture2D? ItemPicture(int rdff)
    {
        if (!_itemPictures.TryGetValue(rdff, out Texture2D? picture))
        {
            picture = Region.MakeObject(Session.ObjectData, rdff, 0, 0, d => Session.Assets.Frames(d)) is { Frames: [var frame, ..] }
                ? Textures.ToTexture(frame, _palette)
                : null;
            _itemPictures[rdff] = picture;
        }
        return picture;
    }

    private void AddPicture(int bmp, Vector2 at)
    {
        if (Bmp(bmp) is { } texture)
        {
            Canvas.AddChild(new TextureRect { Texture = texture, Position = at, MouseFilter = MouseFilterEnum.Ignore });
        }
    }
}
