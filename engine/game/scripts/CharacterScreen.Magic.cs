using DarkSun.Game.Characters;
using DarkSun.Game.Magic;
using DarkSun.Game.World;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// The spell and psionic pages (<c>8F23:19B6</c>): View Character's panel holds 35 slots
/// (<c>APFM</c> 11213–11247), filled in their numbers' order (11213 is the second of
/// the top row's seven), the first 21 above for what can be chosen, the 14 below
/// for what is ready. A listed spell sits on frame 2 of <c>BMP</c> 13007 (an empty
/// slot shows nothing) under its <c>ICON</c> (20999 + n; a power 3000 + n − 139),
/// its description the tooltip (<c>SPIN</c> n). The two text buttons are
/// DSUN.EXE's TOGGLE SPELLS &amp; PSIONICS (11319: MAGE, CLERIC or PSIONIC, the
/// generic class names) and SELECT SPELL LEVEL / SELECT PSIONIC DISCIPLINE (11320:
/// "LEVEL %d", <c>DS:2FCE</c>, or the discipline). The status line says what is
/// left, in DSUN.EXE's words.
/// <list type="bullet">
/// <item>Spells: the spells the member knows at the level, as DSUN.EXE
/// has them: nothing is memorized, every one of them can be cast while the
/// level has casts left ("2 LEVEL 1 SPELLS TO CAST"; a rest fills them,
/// <see cref="Spellcasting.CastsLeft"/>), crossed out (frame 6) when it has none or the
/// member isn't Okay (<c>8F23:1DED</c>); the pointer on one shows the level's line
/// (<c>8F23:1111</c>: "YOU NEED TO REST" when none are left). Click one,
/// then a member's box, to cast it on them (double-click: on the caster).
/// A preserver learns new spells on gaining a level (<see cref="SpellStudyWindow"/>).</item>
/// <item>A right-click on a spell above, or on a power, opens the spell
/// information window (<see cref="ShowSpellInfo"/>).</item>
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
        public required TextureRect Back { get; init; }
        public required TextureRect Icon { get; init; }
        public required TextureRect Mark { get; init; }
        public required Label Count { get; init; }
        public int? Spell { get; set; }
    }

    private readonly List<SpellSlot> _spellSlots = [];
    private readonly Dictionary<int, Texture2D?> _spellIcons = [];
    private readonly Random _random = new();
    private Spellcasting _magic = null!;
    private TextureButton _choiceButton = null!, _levelButton = null!;
    private int _choice;
    private int? _pickedSpell;
    private string _levelStatus = "";
    private string? _hoverSaved;

    private void BuildMagic(UiWindowView sheet)
    {
        _magic = new Spellcasting(Session.Rules);
        var places = Enumerable.Range(FirstSpellSlot, LastSpellSlot - FirstSpellSlot + 1)
            .Select(n => sheet.Slot(n)).OfType<Control>().ToList();
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
                Back = Layer(),
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
            slot.Icon.Position = new Vector2(1, 1);
            slot.Icon.Size = control.Size - new Vector2(2, 2);
            foreach (Control c in new Control[] { slot.Back, slot.Icon, slot.Mark, slot.Count })
            {
                control.AddChild(c);
            }
            control.GuiInput += e => SpellSlotInput(slot, e);
            control.MouseEntered += () =>
            {
                if (slot.Spell is int n)
                {
                    _hoverSaved ??= _status.Text;
                    // 8F23:1111: on a spell, the level's line; on a power, its name.
                    _status.Text = _page == Page.Spells && !slot.Ready ? _levelStatus : Session.SpellName(n)?.ToUpperInvariant() ?? "";
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
        _choiceButton.Pressed += NextKind;
        _levelButton = sheet.Button(SecondText)!;
        _levelButton.Pressed += NextLevel;
        foreach (TextureButton b in new[] { _choiceButton, _levelButton })
        {
            b.Visible = false;
            if (b.GetNodeOrNull<Label>("Text") is { } t)
            {
                t.LabelSettings = GameFonts.Settings(Ink);
            }
        }
    }

    private void ResetMagic() => (_choice, _pickedSpell) = (0, null);

    private void ShowMagic(Page page)
    {
        bool magic = page is Page.Spells or Page.Psionics;
        foreach (SpellSlot slot in _spellSlots)
        {
            slot.Control.Visible = magic;
        }
        _choiceButton.Visible = _levelButton.Visible = magic;
        _pickedSpell = null; // a spell picked on one page isn't cast from another
    }

    /// <summary>DSUN.EXE's spell information window (<c>91F7:1716</c>), <c>WIND</c> 15503 and its text box 15400.</summary>
    private const int SpellInfoWindow = 15503, SpellInfoText = 15400;

    private Control? _spellInfo;

    /// <summary>The spell information window's text while it shows (for the checks), else null.</summary>
    public string? SpellInfo { get; private set; }

    /// <summary>
    /// The spell information window (<c>91F7:1716</c>): <c>WIND</c> 15503 at
    /// (86, 56) over its <c>BMP</c> 15002, the spell's <c>ICON</c> 9 right and 8
    /// down inside it, and its <c>SPIN</c> text ("NAME: what it does") in text
    /// box 15400 after five spaces, UNKNOWN for none. DSUN.EXE opens it when an item that carries a spell is used; here
    /// a right-click on a spell or power opens it too. A click or Esc closes it.
    /// </summary>
    private void ShowSpellInfo(int number)
    {
        CloseSpellInfo();
        // Behind it, the rest of the screen takes no clicks: a click anywhere closes it.
        var shade = new Control { Size = new Vector2(Width, Height), MouseFilter = MouseFilterEnum.Stop };
        shade.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true })
            {
                CloseSpellInfo();
                GetViewport().SetInputAsHandled();
            }
        };
        string text = Session.SpellName(number) is { } name ? $"{name.ToUpperInvariant()}: {Session.SpellDescription(number)}" : "UNKNOWN";
        if (UiWindowView.Build(SpellInfoWindow, _palette) is { } window)
        {
            window.Position = new Vector2(86, 56);
            window.MouseFilter = MouseFilterEnum.Ignore;
            if (SpellIcon(number) is { } icon)
            {
                window.AddChild(new TextureRect { Texture = icon, Position = new Vector2(9, 8), MouseFilter = MouseFilterEnum.Ignore });
            }
            if (window.TextBox(SpellInfoText) is { } box)
            {
                // DSUN.EXE reads the text 5 characters into its buffer, after spaces: the first line clears the icon.
                box.Text = "     " + text;
                box.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            }
            shade.AddChild(window);
        }
        else
        {
            _status.Text = text;
            return;
        }
        Canvas.AddChild(shade);
        (_spellInfo, SpellInfo) = (shade, text);
    }

    private void CloseSpellInfo()
    {
        _spellInfo?.QueueFree();
        (_spellInfo, SpellInfo) = (null, null);
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

    /// <summary>The spell levels the member can cast (a preserver-cleric has both kinds).</summary>
    private List<(SpellKind Kind, int Level)> SpellChoices(Character c) => [.. _magic.Levels(c)];

    /// <summary>The pages the first button goes round (TOGGLE SPELLS &amp; PSIONICS): each kind of spell the member casts, then psionics.</summary>
    private List<(Page Page, SpellKind? Kind)> Kinds(Character c) =>
        [.. SpellChoices(c).Select(x => x.Kind).Distinct().Select(k => (Page.Spells, (SpellKind?)k)),
            .. c.Disciplines.Count > 0 ? [(Page.Psionics, (SpellKind?)null)] : Array.Empty<(Page, SpellKind?)>()];

    /// <summary>The first button: the next kind of spell, or psionics, or back.</summary>
    private void NextKind()
    {
        _pickedSpell = null;
        if (CharacterOf(_member) is not { } c || Kinds(c) is not { Count: > 0 } kinds)
        {
            return;
        }
        List<(SpellKind Kind, int Level)> choices = SpellChoices(c);
        SpellKind? now = _page == Page.Spells && choices.Count > 0 ? choices[_choice % choices.Count].Kind : null;
        var (page, kind) = kinds[(kinds.IndexOf((_page, now)) + 1) % kinds.Count];
        _choice = kind is { } k ? choices.FindIndex(x => x.Kind == k) : 0;
        if (page != _page)
        {
            Show(page);
        }
        else
        {
            RefreshMagic();
        }
    }

    /// <summary>The second button: the next level of the kind shown (or the next discipline).</summary>
    private void NextLevel()
    {
        _pickedSpell = null;
        if (_page == Page.Spells && CharacterOf(_member) is { } c && SpellChoices(c) is { Count: > 0 } choices)
        {
            SpellKind kind = choices[_choice % choices.Count].Kind;
            List<int> same = [.. Enumerable.Range(0, choices.Count).Where(i => choices[i].Kind == kind)];
            _choice = same[(same.IndexOf(_choice % choices.Count) + 1) % same.Count];
        }
        else
        {
            _choice++;
        }
        RefreshMagic();
    }

    /// <summary>The member can cast now (<c>8F23:1DED</c>): standing, and Okay.</summary>
    private bool FitToCast => _world?[_member] is { Vitals: { Hp: > 0, Status: Vitals.Well } };

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
        if (choices.Count == 0)
        {
            Fill([], []);
            _choiceButton.Visible = c.Disciplines.Count > 0;
            SetTextButtons("PSIONIC", null);
            _status.Text = "YOU CANNOT CAST SPELLS";
            return;
        }
        _choice %= choices.Count;
        var (kind, level) = choices[_choice];
        _choiceButton.Visible = true;
        SetTextButtons(kind == SpellKind.Wizard ? "MAGE" : "CLERIC", $"LEVEL {level}");
        var shown = _magic.Known(c, kind, level).Select(s => s.Number).ToList();
        Fill(shown, []);
        int left = _magic.CastsLeft(c, kind, level);
        bool cant = left == 0 || !FitToCast;
        foreach (SpellSlot slot in _spellSlots.Where(s => s.Spell is not null && !s.Ready && s.Spell != _pickedSpell))
        {
            slot.Mark.Texture = cant ? SlotArtFrame(CantFrame) : null;
        }
        _levelStatus = left == 0 ? "YOU NEED TO REST" : $"{left} LEVEL {level} SPELL{(left == 1 ? "" : "S")} TO CAST";
        _status.Text = _pickedSpell is int picked ? $"CAST {Session.SpellName(picked)?.ToUpperInvariant()} ON?" : _levelStatus;
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
        SetTextButtons("PSIONIC", d switch
        {
            Discipline.Psychokinesis => "KINET",
            Discipline.Psychometabolism => "METAB",
            _ => "TELEP",
        });
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

    /// <summary>Set the two text buttons: the kind, and the level or discipline (none: hidden).</summary>
    private void SetTextButtons(string kind, string? level)
    {
        _sheetPage.Window.ButtonText(FirstText)!.Text = kind;
        _levelButton.Visible = level is not null;
        if (level is not null)
        {
            _sheetPage.Window.ButtonText(SecondText)!.Text = level;
        }
    }

    private void Fill(List<int> choices, List<int> ready)
    {
        foreach (SpellSlot slot in _spellSlots)
        {
            List<int> from = slot.Ready ? ready : choices;
            slot.Spell = slot.Index < from.Count ? from[slot.Index] : null;
            slot.Icon.Texture = slot.Spell is int n ? SpellIcon(n) : null;
            slot.Back.Texture = slot.Spell is not null ? SlotArtFrame(ListedFrame) : null;
            slot.Icon.Modulate = Colors.White;
            slot.Count.Text = "";
            slot.Control.TooltipText = slot.Spell is int t ? Tooltip(t) : "";
            slot.Mark.Texture = slot.Spell is not null && slot.Spell == _pickedSpell && !slot.Ready ? SlotArtFrame(SelectedFrame) : null;
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
            ShowSpellInfo(n);
            return;
        }
        if (click.ButtonIndex != MouseButton.Left)
        {
            return;
        }
        string? message = null;
        if (_page == Page.Spells)
        {
            if (!_magic.CanCast(c, n) || !FitToCast)
            {
                _pickedSpell = null;
                message = "YOU NEED TO REST";
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

    /// <summary>Is the slot's spell crossed out (frame 6: it can't be cast now)?</summary>
    public bool CrossedOut(bool ready, int index) =>
        _spellSlots.FirstOrDefault(s => s.Ready == ready && s.Index == index) is { Mark.Texture: { } t } && t == SlotArtFrame(CantFrame);

    /// <summary>The spell in a slot, as <see cref="ClickSpell"/> numbers them.</summary>
    public int? SpellAt(bool ready, int index) => _spellSlots.FirstOrDefault(s => s.Ready == ready && s.Index == index)?.Spell;
}
