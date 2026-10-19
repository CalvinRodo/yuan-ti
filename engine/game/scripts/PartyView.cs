using DarkSun.Data.Gff;
using DarkSun.Data.Images;
using DarkSun.Game.Characters;
using DarkSun.Game.World;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// The View Character screen that Create Characters opens (<c>WIND</c> 11500
/// over <c>BMP</c> 11000, as DSUN.EXE draws it; <c>9106:0622</c>): four
/// character boxes (<c>BUTN</c> 11300–11303), the selected character's
/// details on the right, and along the bottom the two text buttons, which
/// here do what the manual's right-click menus do. An empty box: NEW (the
/// Character Generation screen) or ADD (a character made before); a full
/// one: EDIT (EDIT NAME once the character has adventured) or DROP (back to
/// the roster). Right-clicking a box opens the same choices as a menu. The
/// party is kept for the next new game; Return (or Esc) goes back to the menu.
/// </summary>
public partial class PartyView : RetroScreen
{
    public const string ScenePath = "res://scenes/party.tscn";

    private const int PaletteNumber = 1001;
    // WIND 11500's controls.
    private const int WindowNumber = 11500, FirstBox = 11300, FirstLeader = 11309, FirstComputerControl = 11313,
        NameBar = 11318, FirstText = 11319, SecondText = 11320, ReturnButton = 10308, ItemArea = 11269;

    private Palette _palette = null!;
    private GffFile _resources = null!;
    private readonly List<TextureRect> _slotPictures = [];
    private readonly List<Label> _slotNames = [];
    private Label _name = null!, _details = null!, _status = null!;
    private UiWindowView _window = null!;
    private TextureButton _first = null!, _second = null!;
    private PopupMenu _menu = null!;
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
        // Window 11500 over its backdrop, BMP 11000: every control where the window's data puts it.
        _window = UiWindowView.Build(WindowNumber, _palette, backdrop: 11000)!;
        Canvas.AddChild(_window);

        for (int i = 0; i < GameWorld.PartySize; i++)
        {
            int slot = i;
            TextureButton box = _window.Button(FirstBox + i)!;
            var picture = new TextureRect
            {
                Position = new Vector2(1, 1),
                Size = box.Size - new Vector2(2, 2),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                ClipContents = true,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            box.AddChild(picture);
            box.GuiInput += e => SlotInput(slot, e);
            _slotPictures.Add(picture);
            _slotNames.Add(Text(new Vector2(box.Position.X - 10, box.Position.Y + box.Size.Y + 1), box.Size.X + 20, "", HorizontalAlignment.Center));
            // Leader and computer control: shown, not working yet.
            foreach (int n in new[] { FirstLeader + i, FirstComputerControl + i })
            {
                if (_window.Button(n) is { } b)
                {
                    b.MouseFilter = MouseFilterEnum.Ignore;
                }
            }
        }
        Rect2 bar = _window.RectOf("BUTN", NameBar);
        _name = Text(bar.Position + new Vector2(0, 1), bar.Size.X, "", HorizontalAlignment.Center);
        // The original shows the items here (its slots, APFM 11213–11247); this shows the character's details.
        Rect2 area = _window.RectOf("APFM", ItemArea);
        _details = Text(area.Position + new Vector2(3, 0), area.Size.X - 4, "");
        _details.Size = area.Size;
        _details.AutowrapMode = TextServer.AutowrapMode.WordSmart;

        // The bottom bar: the character screens' icons and the game menu (not ported yet), the two text buttons, Return.
        foreach (int n in new[] { 10300, 11304, 11305, 11306, 11308 })
        {
            if (_window.Button(n) is { } b)
            {
                b.Modulate = new Color(1, 1, 1, 0.45f);
                b.MouseFilter = MouseFilterEnum.Ignore;
            }
        }
        _first = _window.Button(FirstText)!;
        _first.Pressed += () => Do(first: true);
        _second = _window.Button(SecondText)!;
        _second.Pressed += () => Do(first: false);
        foreach (TextureButton b in new[] { _first, _second })
        {
            if (b.GetNodeOrNull<Label>("Text") is { } t)
            {
                t.LabelSettings = GameFonts.Settings(new Color(0.95f, 0.9f, 0.75f));
            }
        }
        TextureButton back = _window.Button(ReturnButton)!;
        back.Pressed += Back;
        back.TooltipText = "Return (Esc)";
        _status = Text(new Vector2(72, 175), 176, "", HorizontalAlignment.Center);

        _menu = new PopupMenu();
        _menu.AddThemeFontSizeOverride("font_size", 8);
        _menu.IdPressed += id => Command((int)id);
        AddChild(_menu);

        if (OS.GetCmdlineUserArgs().Contains("--check-party"))
        {
            GD.Print($"party: {Session.Party.Count} characters ({string.Join(", ", Session.Party.Select(c => c.Name))}); roster {Session.Roster.All().Count}");
            GetTree().Quit();
            return;
        }
        Refresh();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e.IsActionPressed(Controls.Back))
        {
            GetViewport().SetInputAsHandled();
            Back();
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
        var cc = new CharacterCreation(Session.Rules, Session.Names, new Random());
        for (int i = 0; i < GameWorld.PartySize; i++)
        {
            Character? c = i < Session.Party.Count ? Session.Party[i] : null;
            _slotPictures[i].Texture = c is null ? null : Portrait(c);
            _slotNames[i].Text = c?.Name ?? "";
            _slotNames[i].Modulate = i == _selected ? new Color(1f, 0.9f, 0.5f) : Colors.White;
        }
        if (Selected is { } s)
        {
            _name.Text = s.Name;
            string Abil(Ability a) => $"{a.ToString().ToUpperInvariant()} {cc.Adjusted(s, a),2}";
            var levels = cc.Levels(s);
            _details.Text = $"{Session.Rules.Race(s.Race).Name.ToUpperInvariant()} {s.Gender.ToString().ToUpperInvariant()}\n"
                + string.Join(" / ", s.Classes.Select((k, n) => $"{Session.Rules.Class(k).Name.ToUpperInvariant()} {levels[n]}")) + "\n"
                + $"{Abil(Ability.Str)}  {Abil(Ability.Int)}\n{Abil(Ability.Dex)}  {Abil(Ability.Wis)}\n{Abil(Ability.Con)}  {Abil(Ability.Cha)}\n"
                + $"HP {s.Hp}   EXP {s.Experience}\n{Spaced(s.Alignment.ToString())}\n"
                + $"PSI: {string.Join(", ", s.Disciplines.Select(d => d.ToString().ToUpperInvariant()))}"
                + (s.Sphere is { } sphere ? $"\nSPHERE: {sphere.ToString().ToUpperInvariant()}" : "");
        }
        else
        {
            _name.Text = "";
            _details.Text = "An empty character box.\n\nNEW creates a character; ADD brings back one made before.";
        }
        bool full = Selected is not null;
        _window.ButtonText(FirstText)!.Text = full ? (Selected!.Adventured ? "NAME" : "EDIT") : "NEW";
        _window.ButtonText(SecondText)!.Text = full ? "DROP" : "ADD";
        _second.Modulate = _second.Disabled ? new Color(1, 1, 1, 0.5f) : Colors.White;
        _second.Disabled = !full && Available().Count == 0;
    }

    private static string Spaced(string pascal) =>
        string.Concat(pascal.Select((ch, i) => i > 0 && char.IsUpper(ch) ? " " + ch : ch.ToString())).ToUpperInvariant();

    private void SlotInput(int slot, InputEvent e)
    {
        if (e is not InputEventMouseButton { Pressed: true } click)
        {
            return;
        }
        _selected = Math.Min(slot, Session.Party.Count);
        Refresh();
        if (click.ButtonIndex == MouseButton.Right)
        {
            _menu.Clear();
            if (Selected is not null)
            {
                _menu.AddItem(Selected.Adventured ? "EDIT NAME" : "EDIT", 1);
                _menu.AddItem("DROP", 2);
            }
            else
            {
                _menu.AddItem("NEW", 3);
                _menu.AddItem("ADD", 4);
                _menu.SetItemDisabled(1, Available().Count == 0);
            }
            _menu.AddItem("CANCEL", 0);
            _menu.Position = (Vector2I)GetGlobalMousePosition();
            _menu.Popup();
        }
    }

    private void Do(bool first) => Command(Selected is not null ? (first ? 1 : 2) : (first ? 3 : 4));

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
                _status.Text = $"{c.Name} is on disk; ADD brings them back.";
                Refresh();
                break;
            case 3:
                CharacterGenView.Open(null, Session.Party.Count);
                break;
            case 4:
                var choices = Available();
                _menu.Clear();
                for (int i = 0; i < choices.Count; i++)
                {
                    _menu.AddItem(choices[i].Name, 100 + i);
                }
                _menu.AddItem("CANCEL", 0);
                _menu.Popup(new Rect2I((Vector2I)GetGlobalMousePosition(), Vector2I.Zero));
                break;
            case >= 100 when Available() is var list && id - 100 < list.Count && Session.Party.Count < GameWorld.PartySize:
                Session.Party.Add(list[id - 100]);
                Session.SaveParty();
                _selected = Session.Party.Count - 1;
                Refresh();
                break;
        }
    }

    /// <summary>Roster characters not in the party.</summary>
    private List<Character> Available() =>
        [.. Session.Roster.All().Where(c => Session.Party.All(p => p.Id != c.Id))];

    /// <summary>The head of the body portrait: its top middle, 32 × 32.</summary>
    private Texture2D? Portrait(Character c)
    {
        int number = new CharacterCreation(Session.Rules, Session.Names, new Random()).Portrait(c);
        if (!_resources.TryRead("BMP ", number, out var data) || Session.Assets.Frames(data) is not [var frame, ..])
        {
            return null;
        }
        return new AtlasTexture
        {
            Atlas = Textures.ToTexture(frame, _palette),
            Region = new Rect2(Math.Max(0, (frame.Width - 32) / 2), 0, Math.Min(32, frame.Width), Math.Min(32, frame.Height)),
        };
    }

    private Label Text(Vector2 at, float width, string text, HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var label = new Label
        {
            Position = at,
            Size = new Vector2(width, 10),
            Text = text,
            HorizontalAlignment = align,
            MouseFilter = MouseFilterEnum.Ignore,
            LabelSettings = GameFonts.Settings(new Color(0.95f, 0.9f, 0.75f), lineSpacing: -1),
        };
        Canvas.AddChild(label);
        return label;
    }
}
