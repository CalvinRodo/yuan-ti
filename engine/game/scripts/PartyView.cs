using DarkSun.Data.Gff;
using DarkSun.Data.Images;
using DarkSun.Game.Characters;
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
    private static readonly Vector2I[] Slots = [new(53, 30), new(104, 30), new(53, 90), new(104, 90)];

    private Palette _palette = null!;
    private GffFile _resources = null!;
    private readonly List<TextureRect> _slotPictures = [];
    private readonly List<Label> _slotNames = [];
    private Label _name = null!, _details = null!, _status = null!;
    private Button _first = null!, _second = null!;
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
        Picture("BMP ", 11000, 0, 0);

        for (int i = 0; i < Slots.Length; i++)
        {
            int slot = i;
            Picture("ICON", 11100, Slots[i].X, Slots[i].Y);
            var picture = new TextureRect
            {
                Position = Slots[i] + new Vector2I(1, 1),
                Size = new Vector2(32, 32),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                ClipContents = true,
                MouseFilter = MouseFilterEnum.Stop,
            };
            picture.GuiInput += e => SlotInput(slot, e);
            Canvas.AddChild(picture);
            _slotPictures.Add(picture);
            Label name = Text(new Vector2(Slots[i].X - 10, Slots[i].Y + 35), 54, "", HorizontalAlignment.Center);
            _slotNames.Add(name);
            Picture("ICON", 11106, Slots[i].X - 10, Slots[i].Y);     // leader (not used yet)
            Picture("ICON", 11111, Slots[i].X - 10, Slots[i].Y + 10); // computer control (not used yet)
        }
        Picture("ICON", 11108, 151, 26);
        _name = Text(new Vector2(153, 27), 119, "", HorizontalAlignment.Center);
        _details = Text(new Vector2(150, 41), 128, "");
        _details.Size = new Vector2(128, 110);
        _details.AutowrapMode = TextServer.AutowrapMode.WordSmart;

        // The bottom bar: the character screens' icons (not ported yet), the two text buttons, Return.
        foreach (var (icon, x) in new[] { (10100, 43), (11102, 67), (11103, 91), (11104, 114) })
        {
            Picture("ICON", icon, x, 155).Modulate = new Color(1, 1, 1, 0.45f);
        }
        _first = TextButton(new Vector2(132, 157), new Vector2(42, 12), () => Do(first: true));
        _second = TextButton(new Vector2(173, 157), new Vector2(48, 12), () => Do(first: false));
        Picture("ICON", 11101, 223, 155).Modulate = new Color(1, 1, 1, 0.45f);
        TextureButton back = IconButton(10108, 253, 155, Back);
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
        for (int i = 0; i < Slots.Length; i++)
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
        _first.Text = full ? (Selected!.Adventured ? "NAME" : "EDIT") : "NEW";
        _second.Text = full ? "DROP" : "ADD";
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
            case >= 100 when Available() is var list && id - 100 < list.Count && Session.Party.Count < Slots.Length:
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

    private TextureRect Picture(string tag, int number, int x, int y)
    {
        var rect = new TextureRect { Position = new Vector2(x, y), MouseFilter = MouseFilterEnum.Ignore };
        if (_resources.TryRead(tag, number, out var data) && Session.Assets.Frames(data) is [var frame, ..])
        {
            rect.Texture = Textures.ToTexture(frame, _palette);
        }
        Canvas.AddChild(rect);
        return rect;
    }

    private TextureButton IconButton(int icon, int x, int y, Action pressed)
    {
        var frames = _resources.TryRead("ICON", icon, out var data) ? Session.Assets.Frames(data) : [];
        Texture2D? Frame(int n) => n < frames.Count && frames[n].Width > 1 ? Textures.ToTexture(frames[n], _palette) : null;
        var button = new TextureButton
        {
            Position = new Vector2(x, y),
            TextureNormal = Frame(0),
            TextureHover = Frame(1),
            TexturePressed = Frame(3) ?? Frame(1),
            TextureDisabled = Frame(2),
        };
        button.Pressed += pressed;
        Canvas.AddChild(button);
        return button;
    }

    private Button TextButton(Vector2 at, Vector2 size, Action pressed)
    {
        var button = new Button { Position = at, Size = size, Flat = false, FocusMode = FocusModeEnum.None, ClipText = true };
        button.AddThemeFontSizeOverride("font_size", 7);
        var normal = new StyleBoxFlat { BgColor = new Color(0.28f, 0.29f, 0.38f), BorderColor = new Color(0.12f, 0.12f, 0.18f) };
        normal.SetBorderWidthAll(1);
        var hover = (StyleBoxFlat)normal.Duplicate();
        hover.BgColor = new Color(0.38f, 0.39f, 0.5f);
        button.AddThemeStyleboxOverride("normal", normal);
        button.AddThemeStyleboxOverride("hover", hover);
        button.AddThemeStyleboxOverride("pressed", hover);
        button.AddThemeStyleboxOverride("disabled", normal);
        button.AddThemeColorOverride("font_color", new Color(0.95f, 0.9f, 0.75f));
        button.AddThemeColorOverride("font_disabled_color", new Color(0.55f, 0.55f, 0.6f));
        button.Pressed += pressed;
        Canvas.AddChild(button);
        return button;
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
            LabelSettings = new LabelSettings { FontSize = 7, LineSpacing = -2, FontColor = new Color(0.95f, 0.9f, 0.75f), OutlineSize = 2, OutlineColor = new Color(0.1f, 0.1f, 0.15f) },
        };
        Canvas.AddChild(label);
        return label;
    }
}
