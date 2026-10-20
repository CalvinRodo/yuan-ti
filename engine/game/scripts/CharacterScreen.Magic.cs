using DarkSun.Game.Characters;
using DarkSun.Game.Magic;
using DarkSun.Game.World;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// The spell and psionic pages: View Character's panel holds 35 slots
/// (<c>APFM</c> 11213–11247), the 21 above (three rows of seven) for what can
/// be chosen, the 14 below for what is ready; the two text buttons (11319,
/// 11320) pick the level or discipline and, for a preserver, LEARN. Each
/// spell is its <c>ICON</c> (20999 + n; a power 3000 + n − 139), its name on
/// the status line while the pointer is on it and its description the
/// tooltip (<c>SPIN</c> n). The status line says what is left, in DSUN.EXE's words.
/// <list type="bullet">
/// <item>Spells: the level's spells above, click to memorize a copy, right-click
/// to forget one; LEARN shows the wizard spells not in the spellbook, click to
/// learn. Below, the memorized spells ready (with how many): click one, then a
/// member's box, to cast it on them (double-click: on the caster).</item>
/// <item>Psionics: the discipline's powers above, those not known dimmed:
/// click one to pick it (while the character may know more), or a known
/// one, then a box, to use it. Below, every power known.</item>
/// </list>
/// Outside combat only the healing spells and powers do anything (<see cref="Spellcasting.CastOutside"/>).
/// </summary>
public partial class CharacterScreen
{
    private const int FirstSpellSlot = 11213, LastSpellSlot = 11247, ChoiceSlots = 21;

    private sealed class SpellSlot
    {
        public required bool Ready { get; init; }
        public required int Index { get; init; }
        public required Control Control { get; init; }
        public required TextureRect Icon { get; init; }
        public required TextureRect Mark { get; init; }
        public required Label Count { get; init; }
        public int? Spell { get; set; }
    }

    private readonly List<SpellSlot> _spellSlots = [];
    private readonly Dictionary<int, Texture2D?> _spellIcons = [];
    private readonly Random _random = new();
    private Spellcasting _magic = null!;
    private TextureButton _choiceButton = null!, _learnButton = null!;
    private int _choice;
    private bool _learning;
    private int? _pickedSpell;
    private string? _hoverSaved;

    private void BuildMagic(UiWindowView sheet)
    {
        _magic = new Spellcasting(Session.Rules);
        var places = Enumerable.Range(FirstSpellSlot, LastSpellSlot - FirstSpellSlot + 1)
            .Select(n => sheet.Slot(n)).OfType<Control>()
            .OrderBy(c => c.Position.Y).ThenBy(c => c.Position.X).ToList();
        for (int i = 0; i < places.Count; i++)
        {
            Control control = places[i];
            control.MouseFilter = MouseFilterEnum.Stop;
            TextureRect Layer() => new()
            {
                Size = control.Size,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            var slot = new SpellSlot
            {
                Ready = i >= ChoiceSlots,
                Index = i < ChoiceSlots ? i : i - ChoiceSlots,
                Control = control,
                Icon = Layer(),
                Mark = Layer(),
                Count = new Label
                {
                    Size = control.Size,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    LabelSettings = GameFonts.Settings(Ink),
                    MouseFilter = MouseFilterEnum.Ignore,
                },
            };
            var back = Layer();
            back.Texture = SlotArtFrame(EmptyFrame);
            slot.Icon.Position = new Vector2(1, 1);
            slot.Icon.Size = control.Size - new Vector2(2, 2);
            foreach (Control c in new Control[] { back, slot.Icon, slot.Mark, slot.Count })
            {
                control.AddChild(c);
            }
            control.GuiInput += e => SpellSlotInput(slot, e);
            control.MouseEntered += () =>
            {
                if (slot.Spell is int n)
                {
                    _hoverSaved ??= _status.Text;
                    _status.Text = Session.SpellName(n)?.ToUpperInvariant() ?? "";
                }
            };
            control.MouseExited += () =>
            {
                if (_hoverSaved is { } saved)
                {
                    _status.Text = saved;
                    _hoverSaved = null;
                }
            };
            control.Visible = false;
            _spellSlots.Add(slot);
        }
        _choiceButton = sheet.Button(FirstText)!;
        _choiceButton.Pressed += NextChoice;
        _learnButton = sheet.Button(SecondText)!;
        _learnButton.Pressed += () =>
        {
            _learning = !_learning;
            _pickedSpell = null;
            RefreshMagic();
        };
        foreach (TextureButton b in new[] { _choiceButton, _learnButton })
        {
            b.Visible = false;
            if (b.GetNodeOrNull<Label>("Text") is { } t)
            {
                t.LabelSettings = GameFonts.Settings(Ink);
            }
        }
    }

    private void ResetMagic() => (_choice, _learning, _pickedSpell) = (0, false, null);

    private void ShowMagic(Page page)
    {
        bool magic = page is Page.Spells or Page.Psionics;
        foreach (SpellSlot slot in _spellSlots)
        {
            slot.Control.Visible = magic;
        }
        _choiceButton.Visible = magic;
        _learnButton.Visible = false;
        if (!magic)
        {
            _pickedSpell = null;
            _learning = false;
        }
    }

    private Texture2D? SpellIcon(int number)
    {
        if (!_spellIcons.TryGetValue(number, out Texture2D? t))
        {
            _spellIcons[number] = t = Spellcasting.Get(number) is { } spell ? Art("ICON", spell.Icon, 0) : null;
        }
        return t;
    }

    private string Tooltip(int number) =>
        Session.SpellName(number) is { } name ? $"{name}\n{Session.SpellDescription(number)}" : $"Spell {number}";

    /// <summary>The spell levels the member can memorize (a preserver-cleric has both kinds).</summary>
    private List<(SpellKind Kind, int Level)> SpellChoices(Character c) => [.. _magic.Levels(c)];

    private void NextChoice()
    {
        _choice++;
        _pickedSpell = null;
        RefreshMagic();
    }

    private void RefreshMagic()
    {
        _hoverSaved = null;
        foreach (SpellSlot slot in _spellSlots)
        {
            slot.Spell = null;
        }
        if (CharacterOf(_member) is not { } c)
        {
            Fill([], []);
            _choiceButton.Visible = false;
            _status.Text = "INACTIVE CHARACTER";
            return;
        }
        if (_page == Page.Spells)
        {
            RefreshSpells(c);
        }
        else
        {
            RefreshPowers(c);
        }
    }

    private void RefreshSpells(Character c)
    {
        List<(SpellKind Kind, int Level)> choices = SpellChoices(c);
        var ready = c.Memorized.Distinct().OrderBy(n => n).ToList();
        if (choices.Count == 0)
        {
            Fill([], ready);
            _choiceButton.Visible = _learnButton.Visible = false;
            _status.Text = "YOU CANNOT CAST SPELLS";
            return;
        }
        _choice %= choices.Count;
        var (kind, level) = choices[_choice];
        bool preserver = kind == SpellKind.Wizard;
        _learning &= preserver;
        _choiceButton.Visible = true;
        SetTextButtons($"{(kind == SpellKind.Wizard ? "WIZ" : "PRI")} {level}", preserver ? (_learning ? "DONE" : "LEARN") : null);
        var shown = (_learning ? _magic.Learnable(c, level) : _magic.Memorizable(c, kind, level)).Select(s => s.Number).ToList();
        Fill(shown, ready);
        foreach (SpellSlot slot in _spellSlots.Where(s => s.Spell is not null))
        {
            int n = slot.Spell!.Value;
            int copies = slot.Ready ? Spellcasting.Ready(c, n) : c.Memorized.Count(m => m == n);
            slot.Count.Text = slot.Ready || (copies > 0 && !_learning) ? copies.ToString() : "";
            slot.Icon.Modulate = slot.Ready && copies == 0 ? new Color(1, 1, 1, 0.4f) : Colors.White;
        }
        int left = _magic.Slots(c, kind, level) - _magic.MemorizedAt(c, kind, level);
        _status.Text = _pickedSpell is int picked ? $"CAST {Session.SpellName(picked)?.ToUpperInvariant()} ON?"
            : _learning ? $"BOOK: {c.Spellbook.Count(n => Spellcasting.Get(n)?.Level == level)} OF {_magic.SpellbookSize(c)} LEVEL {level}"
            : shown.Count == 0 && preserver ? "NO SPELLS: LEARN SOME"
            : $"{left} LEVEL {level} SPELL{(left == 1 ? "" : "S")} TO MEMORIZE";
    }

    private void RefreshPowers(Character c)
    {
        var disciplines = c.Disciplines.Distinct().ToList();
        if (disciplines.Count == 0)
        {
            Fill([], []);
            _choiceButton.Visible = false;
            _status.Text = "NO PSIONIC DISCIPLINE";
            return;
        }
        _choice %= disciplines.Count;
        Discipline d = disciplines[_choice];
        SetTextButtons(d switch
        {
            Discipline.Psychokinesis => "KINET",
            Discipline.Psychometabolism => "METAB",
            _ => "TELEP",
        }, null);
        Fill([.. Spellcasting.PowersOf(c, d).Select(s => s.Number)], [.. c.Powers.OrderBy(n => n)]);
        foreach (SpellSlot slot in _spellSlots.Where(s => s.Spell is not null))
        {
            slot.Icon.Modulate = c.Powers.Contains(slot.Spell!.Value) ? Colors.White : new Color(1, 1, 1, 0.4f);
            slot.Count.Text = "";
        }
        int picks = _magic.PowersAllowed(c) - c.Powers.Count;
        _status.Text = _pickedSpell is int picked ? $"USE {Session.SpellName(picked)?.ToUpperInvariant()} ON?"
            : $"PSI {_magic.Psp(c)}/{_magic.MaxPsp(c)}" + (picks > 0 ? $"  {picks} TO PICK" : "");
    }

    /// <summary>Set the two text buttons: the choice's label, and LEARN (or none).</summary>
    private void SetTextButtons(string choice, string? learn)
    {
        _sheetPage.Window.ButtonText(FirstText)!.Text = choice;
        _learnButton.Visible = learn is not null;
        if (learn is not null)
        {
            _sheetPage.Window.ButtonText(SecondText)!.Text = learn;
        }
    }

    private void Fill(List<int> choices, List<int> ready)
    {
        foreach (SpellSlot slot in _spellSlots)
        {
            List<int> from = slot.Ready ? ready : choices;
            slot.Spell = slot.Index < from.Count ? from[slot.Index] : null;
            slot.Icon.Texture = slot.Spell is int n ? SpellIcon(n) : null;
            slot.Icon.Modulate = Colors.White;
            slot.Count.Text = "";
            slot.Control.TooltipText = slot.Spell is int t ? Tooltip(t) : "";
            slot.Mark.Texture = slot.Spell is not null && slot.Spell == _pickedSpell && slot.Ready == (_page == Page.Spells) ? SlotArtFrame(SelectedFrame) : null;
        }
    }

    private void SpellSlotInput(SpellSlot slot, InputEvent e)
    {
        if (e is not InputEventMouseButton { Pressed: true } click || CharacterOf(_member) is not { } c)
        {
            return;
        }
        GetViewport().SetInputAsHandled();
        if (slot.Spell is not int n)
        {
            return;
        }
        string name = Session.SpellName(n)?.ToUpperInvariant() ?? $"SPELL {n}";
        if (click.ButtonIndex == MouseButton.Right)
        {
            bool forgot = _page == Page.Spells && !_learning && _magic.Forget(c, n);
            RefreshMagic();
            _status.Text = forgot ? $"{name} FORGOTTEN" : name;
            return;
        }
        if (click.ButtonIndex != MouseButton.Left)
        {
            return;
        }
        string? message = null;
        if (_page == Page.Spells)
        {
            if (_learning)
            {
                message = $"{name} {_magic.Learn(c, n)}";
            }
            else if (!slot.Ready)
            {
                message = _magic.Memorize(c, n) ? $"{name} MEMORIZED" : "NO MORE SPELLS OF THAT LEVEL";
            }
            else if (click.DoubleClick)
            {
                CastAt(_member, n);
                return;
            }
            else
            {
                _pickedSpell = Spellcasting.Ready(c, n) > 0 ? (_pickedSpell == n ? null : n) : null;
                message = _pickedSpell is null && Spellcasting.Ready(c, n) == 0 ? "YOU NEED TO REST" : null;
            }
        }
        else if (!c.Powers.Contains(n))
        {
            message = $"{name} {_magic.Pick(c, n)}";
        }
        else if (click.DoubleClick)
        {
            CastAt(_member, n);
            return;
        }
        else
        {
            _pickedSpell = _pickedSpell == n ? null : n;
        }
        RefreshMagic();
        if (message is not null)
        {
            _status.Text = message;
        }
    }

    /// <summary>Cast the picked spell (or use the power) on member <paramref name="slot"/>.</summary>
    private void CastAt(int slot, int number)
    {
        _pickedSpell = null;
        if (CharacterOf(_member) is not { } caster || _world?[slot] is not { Kind: ObjectKind.Party } target)
        {
            return;
        }
        string result = _magic.CastOutside(caster, number, target.Vitals, _random);
        string name = Session.SpellName(number)?.ToUpperInvariant() ?? $"SPELL {number}";
        if (result.StartsWith("HEALED") || result.StartsWith("RAISED"))
        {
            Did?.Invoke($"{MemberName(_member)} casts {name} on {MemberName(slot)}: {result.ToLowerInvariant()}");
        }
        RefreshMagic();
        _status.Text = $"{name}: {result}";
    }

    /// <summary>The spell or power picked to cast (for the checks).</summary>
    public int? PickedSpell => _pickedSpell;

    /// <summary>Click spell slot <paramref name="index"/>: above (<paramref name="ready"/> false) or below.</summary>
    public void ClickSpell(bool ready, int index, bool doubleClick = false, MouseButton button = MouseButton.Left)
    {
        if (_spellSlots.FirstOrDefault(s => s.Ready == ready && s.Index == index) is { } slot)
        {
            slot.Control.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = button, Pressed = true, DoubleClick = doubleClick });
        }
    }

    /// <summary>The spell in a slot, as <see cref="ClickSpell"/> numbers them.</summary>
    public int? SpellAt(bool ready, int index) => _spellSlots.FirstOrDefault(s => s.Ready == ready && s.Index == index)?.Spell;
}
