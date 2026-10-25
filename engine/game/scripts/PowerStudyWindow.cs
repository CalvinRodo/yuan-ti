using DarkSun.Data.Gff;
using DarkSun.Data.Images;
using DarkSun.Game.Characters;
using DarkSun.Game.Magic;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// DSUN.EXE's psionic study window (<c>8CCC:0A0E</c>), where a psionicist who has gained a
/// level picks a power (<see cref="Character.PowersToChoose"/>; the level raise calls it,
/// <c>8E77:0765</c> → <c>430A:0057</c>): <c>WIND</c> 17501 over <c>BMP</c> 17001 at (80, 43).
/// Its 21 slots (<c>APFM</c> 11213–11233) hold the powers of the discipline shown not yet at
/// rank 30 (<c>8CCC:0BA7</c>), each on frame 2 of <c>BMP</c> 13007; under them "PICK A PSIONIC
/// POWER," and whose choice it is. The first text button (11319, disabled) shows the choices
/// left ("  %d"), the second (11320) the discipline, going round them. The pointer on a known
/// power says "ENHANCE TO LEVEL %d"; a click learns a new power ("IS LEARNED") or enhances a
/// known one a rank ("IS ENHANCED", <see cref="Spellcasting.StudyPower"/>). EXIT (17302) with
/// choices left asks "YOU HAVE MORE" (EXIT / CANCEL): leaving gives them up.
/// </summary>
public partial class PowerStudyWindow : RetroScreen
{
    private const int PaletteNumber = 1001, Window = 17501, LeftButton = 11319, DisciplineButton = 11320, ExitButton = 17302,
        FirstSlot = 11213, Slots = 21, SlotArt = 13007, ListedFrame = 2, TitleY = 70;
    private static readonly Vector2 Place = new(80, 43);
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
    private Discipline _discipline;

    /// <summary>The window closed.</summary>
    public Action? Closed { get; set; }

    public bool IsOpen => Visible;

    /// <summary>The powers offered now (for the checks).</summary>
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
                if (index < _shown.Count && _character is { } c && c.PowersToChoose > 0)
                {
                    int n = _shown[index];
                    (_line1.Text, _line2.Text) = c.Powers.Contains(n)
                        ? (Session.SpellName(n)?.ToUpperInvariant() ?? "", $"ENHANCE TO LEVEL {c.PowerRank(n) + 1}")
                        : ("LEARN", Session.SpellName(n)?.ToUpperInvariant() ?? "");
                }
            };
            slot.MouseExited += () =>
            {
                if (_character is { PowersToChoose: > 0 })
                {
                    Title();
                }
            };
            _slots.Add((slot, back, icon));
        }
        _window.Button(DisciplineButton)!.Pressed += NextDiscipline;
        _window.Button(ExitButton)!.Pressed += Exit;
        if (_window.Button(LeftButton) is { } left)
        {
            left.Disabled = true;
        }
        foreach (int n in new[] { LeftButton, DisciplineButton, ExitButton })
        {
            if (_window.ButtonText(n) is { } t)
            {
                t.LabelSettings = GameFonts.Settings(Ink);
            }
        }
        if (_window.ButtonText(ExitButton) is { } exit)
        {
            exit.Text = "EXIT";
        }
        _question = new QuestionWindow(Canvas, _palette);
    }

    /// <summary>Let psionicist <paramref name="c"/> (called <paramref name="name"/>) pick its powers; false if there is nothing to pick.</summary>
    public bool Open(Character c, string name)
    {
        if (c.PowersToChoose <= 0 || !Enum.GetValues<Discipline>().Any(d => Spellcasting.PowersToStudy(c, d).Count > 0))
        {
            return false;
        }
        (_character, _name) = (c, name);
        _discipline = Enum.GetValues<Discipline>().First(d => Spellcasting.PowersToStudy(c, d).Count > 0);
        Title();
        Fill();
        Visible = true;
        GetParent()?.MoveChild(this, -1);
        return true;
    }

    /// <summary>Click power <paramref name="index"/> (the checks use it too).</summary>
    public void Choose(int index)
    {
        if (_character is not { PowersToChoose: > 0 } c || index >= _shown.Count)
        {
            return;
        }
        int power = _shown[index];
        string said = Spellcasting.StudyPower(c, power);
        (_line1.Text, _line2.Text) = (Session.SpellName(power)?.ToUpperInvariant() ?? "", said);
        Fill();
    }

    /// <summary>The discipline button (the checks press it too).</summary>
    public void NextDiscipline()
    {
        Discipline[] all = Enum.GetValues<Discipline>();
        _discipline = all[(Array.IndexOf(all, _discipline) + 1) % all.Length];
        Fill();
    }

    /// <summary>EXIT: with choices left, asked again (the checks press it too).</summary>
    public void Exit()
    {
        if (_character is not { PowersToChoose: > 0 })
        {
            Close();
            return;
        }
        _question.Ask("YOU HAVE MORE", new Vector2(92, 71), ("EXIT", () =>
        {
            if (_character is { } c)
            {
                c.PowersToChoose = 0; // as DSUN.EXE: the window comes with the level, and doesn't come back
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

    private void Title() => (_line1.Text, _line2.Text) = ("PICK A PSIONIC POWER,", _name.ToUpperInvariant());

    private void Fill()
    {
        _shown.Clear();
        if (_character is { } c)
        {
            _shown.AddRange(Spellcasting.PowersToStudy(c, _discipline).Take(Slots));
        }
        if (_window.ButtonText(LeftButton) is { } left)
        {
            left.Text = $"  {_character?.PowersToChoose ?? 0}";
        }
        if (_window.ButtonText(DisciplineButton) is { } label)
        {
            label.Text = _discipline.ToString().ToUpperInvariant();
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
