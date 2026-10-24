using DarkSun.Data.Gff;
using DarkSun.Data.Images;
using DarkSun.Game.Characters;
using DarkSun.Game.Magic;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// DSUN.EXE's study window (<c>8CCC:0211</c>), where a preserver who has gained a
/// level chooses a new spell (<see cref="Character.SpellsToChoose"/>; <c>8CCC:0000</c>):
/// <c>WIND</c> 17500 over <c>BMP</c> 17000 at (65, 41). Its 21 slots (<c>APFM</c>
/// 11213–11233) hold the wizard spells of the level shown that the preserver doesn't
/// know (<c>6798:03E1</c>), each on frame 2 of <c>BMP</c> 13007; under them two lines,
/// centred, say "CHOOSE A SPELL," and whose choice it is (<c>8CCC:0769</c>). The level
/// button (17300, "LEVEL %d") goes round the spell levels the preserver can learn, from
/// the highest, (preserver level + 1) ÷ 2, back to 1 (<c>8CCC:05E5</c>); a click on a
/// spell learns it ("%s IS LEARNED!"). EXIT (17301) before choosing asks
/// "YOU HAVEN'T CHOSEN" (EXIT / CANCEL): leaving gives the choice up.
/// </summary>
public partial class SpellStudyWindow : RetroScreen
{
    private const int PaletteNumber = 1001, Window = 17500, LevelButton = 17300, ExitButton = 17301,
        FirstSlot = 11213, Slots = 21, SlotArt = 13007, ListedFrame = 2, TitleY = 81;
    private static readonly Vector2 Place = new(65, 41);
    private static readonly Color Ink = new(0.95f, 0.9f, 0.75f);

    private GffFile _resources = null!;
    private Palette _palette = null!;
    private UiWindowView _window = null!;
    private QuestionWindow _question = null!;
    private Label _line1 = null!, _line2 = null!;
    private readonly List<(Control Slot, TextureRect Back, TextureRect Icon)> _slots = [];
    private readonly List<int> _shown = [];
    private Texture2D? _listed;
    private Character? _character;
    private string _name = "";
    private Spellcasting _magic = null!;
    private int _level, _highest;
    private bool _chosen;

    /// <summary>The window closed.</summary>
    public Action? Closed { get; set; }

    public bool IsOpen => Visible;

    /// <summary>The spells offered now (for the checks).</summary>
    public IReadOnlyList<int> Offered => _shown;

    /// <summary>The two lines under the slots (for the checks).</summary>
    public string Lines => $"{_line1.Text} {_line2.Text}";

    public override void _Ready()
    {
        base._Ready();
        Visible = false;
        ((ColorRect)GetChild(0)).Color = new Color(0, 0, 0, 0.35f); // the map shows through
        _resources = Session.Open("RESOURCE.GFF");
        _palette = Session.Assets.Palette(_resources.Read("PAL ", PaletteNumber));
        _window = UiWindowView.Build(Window, _palette)!;
        _window.Position = Place;
        Canvas.AddChild(_window);
        _line1 = Line(TitleY);
        _line2 = Line(TitleY + 7);
        _listed = Art("BMP ", SlotArt, ListedFrame);
        for (int i = 0; i < Slots; i++)
        {
            if (_window.Slot(FirstSlot + i) is not { } slot)
            {
                continue;
            }
            int index = _slots.Count;
            slot.MouseFilter = MouseFilterEnum.Stop;
            var back = new TextureRect { Size = slot.Size, MouseFilter = MouseFilterEnum.Ignore };
            var icon = new TextureRect
            {
                Position = new Vector2(1, 1),
                Size = slot.Size - new Vector2(2, 2),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            slot.AddChild(back);
            slot.AddChild(icon);
            slot.GuiInput += e =>
            {
                if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
                {
                    Choose(index);
                }
            };
            slot.MouseEntered += () =>
            {
                if (index < _shown.Count && !_chosen)
                {
                    (_line1.Text, _line2.Text) = ("LEARN", Session.SpellName(_shown[index])?.ToUpperInvariant() ?? "");
                }
            };
            slot.MouseExited += () =>
            {
                if (!_chosen)
                {
                    Title();
                }
            };
            _slots.Add((slot, back, icon));
        }
        _window.Button(LevelButton)!.Pressed += NextLevel;
        _window.Button(ExitButton)!.Pressed += Exit;
        if (_window.ButtonText(ExitButton) is { } exit)
        {
            exit.Text = "EXIT";
        }
        _question = new QuestionWindow(Canvas, _palette);
    }

    /// <summary>Let <paramref name="c"/> (called <paramref name="name"/>) choose a spell; false if there is none to learn.</summary>
    public bool Open(Character c, string name, Spellcasting magic)
    {
        int level = new CharacterCreation(Session.Rules, Session.Names, new Random()).LevelIn(c, CharacterClass.Preserver);
        _highest = Math.Clamp((level + 1) / 2, 1, 5);
        // 8CCC:0000: only when there is something to learn.
        if (level == 0 || Enumerable.Range(1, _highest).All(l => !magic.Learnable(c, l).Any()))
        {
            return false;
        }
        (_character, _name, _magic, _level, _chosen) = (c, name, magic, _highest, false);
        _window.Button(LevelButton)!.Disabled = _highest == 1;
        Title();
        Fill();
        Visible = true;
        GetParent()?.MoveChild(this, -1);
        return true;
    }

    /// <summary>Click spell <paramref name="index"/> (the checks use it too).</summary>
    public void Choose(int index)
    {
        if (_character is not { } c || _chosen || index >= _shown.Count)
        {
            return;
        }
        int spell = _shown[index];
        string said = _magic.Learn(c, spell);
        if (said != "IS LEARNED!")
        {
            (_line1.Text, _line2.Text) = (Session.SpellName(spell)?.ToUpperInvariant() ?? "", said);
            return;
        }
        c.SpellsToChoose = Math.Max(0, c.SpellsToChoose - 1);
        _chosen = c.SpellsToChoose == 0;
        (_line1.Text, _line2.Text) = (Session.SpellName(spell)?.ToUpperInvariant() ?? "", "IS LEARNED!");
        Fill();
    }

    /// <summary>The level button (the checks press it too).</summary>
    public void NextLevel()
    {
        _level = _level >= _highest ? 1 : _level + 1;
        Fill();
    }

    /// <summary>EXIT: before a choice, asked again (the checks press it too).</summary>
    public void Exit()
    {
        if (_chosen)
        {
            Close();
            return;
        }
        _question.Ask("YOU HAVEN'T CHOSEN", new Vector2(92, 71), ("EXIT", () =>
        {
            if (_character is { } c)
            {
                c.SpellsToChoose = 0; // as DSUN.EXE: the window comes with the level, and doesn't come back
            }
            Close();
        }), ("CANCEL", null));
    }

    private void Close()
    {
        _question.Close();
        Visible = false;
        _character = null;
        Closed?.Invoke();
    }

    private void Title() => (_line1.Text, _line2.Text) = ("CHOOSE A SPELL,", _name.ToUpperInvariant());

    private void Fill()
    {
        _shown.Clear();
        if (_character is { } c)
        {
            _shown.AddRange(_magic.Learnable(c, _level).Select(s => s.Number).Take(Slots));
        }
        if (_window.ButtonText(LevelButton) is { } label)
        {
            label.Text = $"LEVEL {_level}";
        }
        for (int i = 0; i < _slots.Count; i++)
        {
            var (_, back, icon) = _slots[i];
            bool there = i < _shown.Count;
            back.Texture = there ? _listed : null;
            icon.Texture = there && Spellcasting.Get(_shown[i]) is { } s ? Art("ICON", s.Icon, 0) : null;
            _slots[i].Slot.TooltipText = there ? $"{Session.SpellName(_shown[i])}\n{Session.SpellDescription(_shown[i])}" : "";
        }
    }

    private Label Line(int y)
    {
        var label = new Label
        {
            Position = new Vector2(0, y),
            Size = new Vector2(_window.Size.X, GameFonts.Size),
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
            LabelSettings = GameFonts.Settings(Ink),
        };
        _window.AddChild(label);
        return label;
    }

    private Texture2D? Art(string tag, int number, int frame) =>
        _resources.TryRead(tag, number, out var data) && Session.Assets.Frames(data) is var frames && frame < frames.Count
            ? Textures.ToTexture(frames[frame], _palette)
            : null;

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (Visible && e.IsActionPressed(Controls.Back) && !_question.Visible)
        {
            Exit();
            GetViewport().SetInputAsHandled();
        }
    }
}
