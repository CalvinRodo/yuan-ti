using DarkSun.Game.Combat;
using DarkSun.Game.Magic;
using DarkSun.Game.World;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// The Dark Sun icon, its quick cast and CAST's choice in a fight, on the spell bar
/// (<see cref="DarkSunIcon"/>, <see cref="SpellBar"/>): the member it is for is the
/// one whose turn it is in a fight, else the selected one (<c>DS:4979</c>). A spell
/// or power chosen from it is cast as one chosen with CAST in a fight; out of one,
/// the Cast Spells page opens with it picked (one at foes goes to the map to aim). An
/// item's spell is used as USE in its item window does.
/// </summary>
public partial class RegionView
{
    private DarkSunIcon _darkSun = null!;
    private SpellBar _spellBar = null!;

    /// <summary>The member the spell bar is open for.</summary>
    private int _spellBarMember;

    /// <summary>The character screen's page last shown for each member (<c>DS:4971</c>), where the icon opens it.</summary>
    private readonly Dictionary<int, CharacterScreen.Page> _lastPage = [];

    private void BuildDarkSun(CanvasLayer layer)
    {
        _darkSun = new DarkSunIcon { LeftClicked = OpenFromIcon, RightClicked = OpenQuickCast };
        _spellBar = new SpellBar { Choose = ChooseFromBar, OpenSpells = () => OpenMagic(_spellBarMember) };
        layer.AddChild(_darkSun);
        layer.AddChild(_spellBar);
    }

    /// <summary>Can the icon be used now (no script waiting, no screen or window over the map)?</summary>
    private bool IconFree => !_character.IsOpen && !_dialogueWindow.Visible && !_verbMenu.Visible && !Session.Scripts.IsSuspended;

    /// <summary>Whose spells the icon's window shows: whose turn it is in a fight, else the selected member.</summary>
    private int QuickCaster => _battle is { Current: { PartyMember: true, Source: { } o } } && o.Index < Session.Party.Count ? o.Index : _selected;

    private void OpenFromIcon()
    {
        if (IconFree)
        {
            OpenInventory(_selected, _lastPage.GetValueOrDefault(_selected, CharacterScreen.Page.Character));
        }
    }

    /// <summary>A right-click on the icon: the spell bar, or with nothing to cast the Cast Spells page (<c>5F6D:0CB4</c>).</summary>
    private void OpenQuickCast()
    {
        int member = QuickCaster;
        if (IconFree && member < Session.Party.Count && !OpenSpellBar(member, "quick cast"))
        {
            OpenMagic(member);
        }
    }

    /// <summary>The spell bar for <paramref name="member"/> (at the top left, or just over <paramref name="above"/>); false if it has nothing to show.</summary>
    private bool OpenSpellBar(int member, string what, Vector2? above = null)
    {
        List<SpellBarRow> rows = SpellBarRows(member);
        if (rows.All(r => r.Entries.Count == 0))
        {
            return false;
        }
        _spellBarMember = member;
        _spellBar.Open($"{Session.Party[member].Name}: {what}", rows, above);
        return true;
    }

    /// <summary>
    /// What the spell bar shows for <paramref name="member"/>: RECENT (its quick casts by kind and
    /// level), its spells by kind and level, its powers by discipline, and its items that cast a spell.
    /// In a fight what can be used is its combatant's (casts left this fight, points, its turn).
    /// </summary>
    private List<SpellBarRow> SpellBarRows(int member)
    {
        var c = Session.Party[member];
        var magic = new Spellcasting(Session.Rules);
        WorldObject? self = _world[member];
        Combatant? fighter = _battle?.Combatants.FirstOrDefault(x => x.Source == self && self is not null);
        bool fit = fighter is not null ? PlayerTurn == fighter : self is { Vitals: { Hp: > 0, Status: Vitals.Well } };
        string unfit = fighter is not null ? "not its turn" : "not fit to cast";
        GffArt art = SpellBarArt();

        SpellBarEntry Entry(int n)
        {
            Spell? s = Spellcasting.Get(n);
            string name = Session.SpellName(n) ?? EffectName(n);
            Texture2D? icon = s is null ? null : art.Icon(s.Icon);
            if (s is { Kind: SpellKind.Psionic })
            {
                int cost = (fighter is not null ? _battle!.Spells : Session.SpellRules).Power(n)?.Cost ?? Session.Rules.PowerCost;
                int psp = fighter?.Psp ?? magic.Psp(c);
                bool enough = cost <= psp && c.Powers.Contains(n);
                return new(n, icon, name, !fit ? unfit : $"{cost} PSP ({psp} left)", fit && enough);
            }
            int left = s is null ? 0 : fighter is not null ? fighter.CastsOf(n) : magic.CanCast(c, n) ? magic.CastsLeft(c, s.Kind, s.Level) : 0;
            string casts = left switch { 0 => "no casts left, rest first", 1 => "1 cast left", _ => $"{left} casts left" };
            return new(n, icon, name, !fit ? unfit : s is null ? "" : $"level {s.Level}, {casts}", fit && left > 0);
        }

        var rows = new List<SpellBarRow>();
        static int Order(int n) => Spellcasting.Get(n) is { } s ? (int)s.Kind * 100 + s.Level : 1000;
        rows.Add(new(SpellBar.Recent, "", [.. c.QuickCasts.Where(n => Spellcasting.Get(n) is not null).OrderBy(Order).Select(Entry)]));
        foreach (SpellKind kind in new[] { SpellKind.Wizard, SpellKind.Priest })
        {
            foreach (var (_, level) in magic.Levels(c).Where(l => l.Kind == kind))
            {
                rows.Add(new(kind.ToString().ToUpperInvariant(), $"L{level}", [.. magic.Known(c, kind, level).Select(s => Entry(s.Number))]));
            }
        }
        rows.Add(new("PSIONIC", "", [.. c.Powers.Distinct().OrderBy(n => Spellcasting.Get(n)?.Discipline).ThenBy(n => n).Select(Entry)]));
        if (self is not null)
        {
            rows.Add(new("ITEMS", "", [.. self.Items.Where(CastsASpell).Select(item =>
            {
                ItemUse use = ItemMagic.Plan(item, Session.SpellRules);
                string left = _world.Stacks(item.Record) ? $"{item.Quantity} left" : item.Charges == 1 ? "1 charge" : $"{item.Charges} charges";
                return new SpellBarEntry(use.Spell, ItemIcon(item.Rdff), ItemText.Name(_world, item),
                    !fit ? unfit : $"{Session.SpellName(use.Spell) ?? EffectName(use.Spell)}, {left}", fit, item, self);
            })]));
        }
        return rows;
    }

    /// <summary>An item USE casts a spell from (a wand's, a potion's): not a quest item, fruit, or a scroll that teaches.</summary>
    private bool CastsASpell(Item item) =>
        !ItemMagic.IsScripted(item.Rdff) && !ItemMagic.IsFruit(item.Rdff)
        && ItemMagic.Offered(item, _world.Items.TypeOf(item.Record), Session.SpellRules)
        && ItemMagic.Plan(item, Session.SpellRules).Kind is ItemUseKind.Aim or ItemUseKind.OnSelf or ItemUseKind.FullHeal;

    private GffArt? _spellBarArt;

    private GffArt SpellBarArt() => _spellBarArt ??= new GffArt(StoneUi.PaletteNumber);

    private void OpenMagic(int member)
    {
        if (member < Session.Party.Count)
        {
            _character.Open(_world, member, CharacterScreen.Page.Spells);
        }
    }

    /// <summary>One chosen on the spell bar: a spell or power cast (<see cref="QuickCast"/>), an item used.</summary>
    private void ChooseFromBar(SpellBarEntry e)
    {
        if (e is { Item: { } item, Holder: { } holder })
        {
            UseItemFromBar(_spellBarMember, item, holder);
        }
        else
        {
            QuickCast(e.Spell);
        }
    }

    /// <summary>
    /// An item chosen on the spell bar, as USE in its item window: in a fight on the member's turn
    /// (<see cref="CastItemInFight"/>); out of one a spell to aim goes to the map
    /// (<see cref="AimItemOnMap"/>), one on the user is used on the member's Items page.
    /// </summary>
    private void UseItemFromBar(int member, Item item, WorldObject holder)
    {
        ItemUse use = ItemMagic.Plan(item, Session.SpellRules);
        string? refused;
        if (InCombat)
        {
            refused = CastItemInFight(member, item, holder, use);
        }
        else if (use.Kind == ItemUseKind.Aim)
        {
            refused = AimItemOnMap(member, item, holder, use);
        }
        else
        {
            _character.Open(_world, member, CharacterScreen.Page.Items);
            refused = _character.UseOwn(item) ? null : Spellcasting.NothingHappens;
        }
        if (refused is not null)
        {
            Notice(refused);
        }
    }

    /// <summary>A spell or power chosen from the spell bar.</summary>
    private void QuickCast(int number)
    {
        if (InCombat)
        {
            if (PlayerTurn is null)
            {
                Notice("Not now.");
                return;
            }
            ChooseCast(number);
            return;
        }
        bool power = Spellcasting.IsPower(number);
        _character.Open(_world, _spellBarMember, power ? CharacterScreen.Page.Psionics : CharacterScreen.Page.Spells);
        _character.PickToCast(number);
    }

    /// <summary>Frames of <c>RESOURCE.GFF</c>'s pictures in one palette, each decoded once.</summary>
    private sealed class GffArt(int paletteNumber)
    {
        private readonly Dictionary<(string, int, int), Texture2D?> _made = [];

        public DarkSun.Data.Images.Palette Palette { get; } = Session.Assets.Palette(Session.Open("RESOURCE.GFF").Read("PAL ", paletteNumber));

        public Texture2D? Icon(int number) => Frame("ICON", number, 0);

        public Texture2D? Frame(string tag, int number, int frame)
        {
            if (!_made.TryGetValue((tag, number, frame), out Texture2D? t))
            {
                _made[(tag, number, frame)] = t = Session.Open("RESOURCE.GFF").TryRead(tag, number, out var data)
                    && Session.Assets.Frames(data) is var frames && frame < frames.Count
                    ? Textures.ToTexture(frames[frame], Palette) : null;
            }
            return t;
        }
    }

    /// <summary>
    /// <c>--check-quick-cast</c>: a new game with a cleric and a psionicist (#159's spell bar). The
    /// icon's right-click opens the bar: the cleric's priest spells by level, its wand under ITEMS,
    /// RECENT empty until a spell is picked on Cast Spells; then RECENT shows it, a slot picks its
    /// spell on Cast Spells, a spell whose casts are used is crossed out and a click on it says why;
    /// the wand goes to the map to aim; the left click opens the page last shown. In a fight CAST
    /// opens the bar for the member whose turn it is, a power chosen goes into RECENT and its slot
    /// chooses it to cast, the wand waits for its target, and the page's pick goes to the map (#156).
    /// </summary>
    private void CheckQuickCast()
    {
        _checking = true;
        var problems = new List<string>();
        void Expect(bool ok, string what)
        {
            GD.Print($"quick cast: {(ok ? "" : "PROBLEM ")}{what}");
            if (!ok)
            {
                problems.Add(what);
            }
        }
        const int CureLight = 72, Detonate = 139, WandOfMissiles = 31007;
        var cleric = new DarkSun.Game.Characters.Character { Name = "Cleric", Classes = [DarkSun.Game.Characters.CharacterClass.Cleric], Sphere = DarkSun.Game.Characters.Sphere.Water, Rolled = [12, 12, 12, 12, 16, 12], Experience = 3000 };
        var psionicist = new DarkSun.Game.Characters.Character { Name = "Psionicist", Classes = [DarkSun.Game.Characters.CharacterClass.Psionicist], Disciplines = [.. Enum.GetValues<DarkSun.Game.Characters.Discipline>()], Rolled = [12, 12, 12, 12, 12, 12], Experience = 3000 };
        new DarkSun.Game.Characters.CharacterCreation(Session.Rules, Session.Names, new Random()).GiveStartingPowers(psionicist);
        if (!psionicist.Powers.Contains(Detonate))
        {
            psionicist.Powers.Add(Detonate);
        }
        Session.Party.Clear();
        Session.Party.AddRange([cleric, psionicist]);
        while (_party.Count < 2 && AddCharacter(_world[0]!.Cell) is not null)
        {
        }
        _selected = 0;
        CharacterScreen screen = _character;
        _world.Give(1, -WandOfMissiles, 0, GameWorld.None);
        _world.Give(1, -WandOfMissiles, 1, GameWorld.None);
        static string Names(IEnumerable<int> n) => string.Join(", ", n);

        OpenQuickCast();
        Expect(_spellBar.Visible && _spellBar.Shown.Count == 0 && _spellBar.Row("PRIEST", "L1").Any(e => e.Spell == CureLight),
            $"the right-click opens the bar: nothing recent, Cure Light Wounds among the priest's first level ({Names(_spellBar.Row("PRIEST", "L1").Select(e => e.Spell))})");
        Expect(_spellBar.Row("ITEMS") is [{ Item.Rdff: WandOfMissiles, Usable: true }], $"and the wand under ITEMS ({string.Join(", ", _spellBar.Row("ITEMS").Select(e => e.Name))})");
        Expect(_spellBar.Rows.All(r => r.Heading != "WIZARD") && _spellBar.Row("PSIONIC").Count == 0, "a cleric has no wizard spells or powers to show");
        _spellBar.Close();

        OpenMagic(0);
        int slot = Enumerable.Range(0, 21).FirstOrDefault(i => screen.SpellAt(false, i) == CureLight, -1);
        screen.ClickSpell(false, slot);
        Expect(cleric.QuickCasts is [CureLight], $"picking Cure Light Wounds on Cast Spells puts it in the cleric's list ({Names(cleric.QuickCasts)})");
        screen.Close();

        OpenQuickCast();
        Expect(_spellBar.Shown is [CureLight] && !_spellBar.CrossedOut(0), $"the bar's RECENT shows it, castable ({Names(_spellBar.Shown)})");
        _spellBar.Click(0);
        Expect(!_spellBar.Visible && screen.IsOpen && screen.Showing == CharacterScreen.Page.Spells && screen.PickedSpell == CureLight,
            $"out of a fight its slot opens Cast Spells with it picked ({screen.Status})");
        screen.Close();
        Expect(_lastPage.GetValueOrDefault(0) == CharacterScreen.Page.Spells, "the page shown is remembered");
        OpenFromIcon();
        Expect(screen.IsOpen && screen.Showing == CharacterScreen.Page.Spells, $"and the left click opens it again ({screen.Showing})");
        screen.Close();

        var magic = new Spellcasting(Session.Rules);
        while (magic.CastsLeft(cleric, SpellKind.Priest, 1) > 0)
        {
            cleric.Cast.Add(CureLight);
        }
        OpenQuickCast();
        Expect(_spellBar.CrossedOut(0), "with the level's casts used it is crossed out");
        _spellBar.Click(0);
        Expect(_spellBar.Visible && _spellBar.Said?.Contains("no casts left") == true && !screen.IsOpen, $"and a click on it says why, the bar staying ({_spellBar.Said})");
        _spellBar.Click("ITEMS", 0);
        Expect(!_spellBar.Visible && _mapCast is { Member: 0, Cast: { Spell: 9, Item.Rdff: WandOfMissiles } },
            $"the wand's slot puts its spell on the map to aim ({_mapCast})");
        _mapCast = null;

        for (int n = 1; n <= 7; n++)
        {
            cleric.RememberCast(n);
        }
        cleric.RememberCast(5);
        Expect(cleric.QuickCasts is [7, 6, 5, 4, 3, 2], $"six at most, the latest first, one already there left in place ({Names(cleric.QuickCasts)})");
        cleric.QuickCasts = [Detonate, CureLight, 3];
        OpenQuickCast();
        Expect(_spellBar.Shown is [3, CureLight, Detonate], $"RECENT is by kind and level: wizard, priest, psionic ({Names(_spellBar.Shown)})");
        _spellBar.Close();

        // In a fight: CAST opens the bar for the psionicist; a power chosen goes into the list, and its slot chooses it again.
        WorldObject? foe = BringFoeNear();
        if (foe is null || !StartCombat(foe, "the quick-cast check"))
        {
            Expect(false, "a fight to cast in");
        }
        else
        {
            for (int step = 0; step < 2000 && !(PlayerTurn is { } t && t.Source == _world[1]); step++)
            {
                if (PlayerTurn is { } other)
                {
                    _battle!.EndTurn(other);
                }
                _Process(1.0 / 30);
            }
            if (PlayerTurn is not { } turn || turn.Source != _world[1])
            {
                Expect(false, "the psionicist's turn comes");
            }
            else
            {
                Expect(_combatPanel.TooltipOf("CAST") == $"CAST ({KeyHelp.Keys(Controls.CombatCast)})" && _combatPanel.TooltipOf("END TURN").StartsWith("END TURN"),
                    $"the quick-use bar's icons name themselves and their keys under the pointer ({_combatPanel.TooltipOf("CAST")})");
                Expect(_combatPanel.Press("CAST") && _spellBar.Visible && _spellBar.Bottom <= _combatPanel.TopLeft.Y,
                    $"its CAST pops the spell list up over the bar ({_spellBar.Bottom} over {_combatPanel.TopLeft.Y})");
                IReadOnlyList<SpellBarEntry> powers = _spellBar.Row("PSIONIC");
                int aimed = powers.FirstOrDefault(e => e.Usable && _battle!.Spells.Effect(e.Spell) is { Target: not (7 or 8) })?.Spell ?? -1;
                Expect(_spellBar.Visible && aimed > 0 && powers.Any(e => e.Spell == Detonate && !e.Usable && e.Note.Contains("PSP")),
                    $"CAST opens the bar with the psionicist's powers, those it hasn't the points for crossed out ({string.Join(", ", powers.Select(e => $"{e.Spell} {e.Note}"))})");
                _spellBar.Click("PSIONIC", powers.ToList().FindIndex(e => e.Spell == aimed));
                Expect(psionicist.QuickCasts is [var remembered] && remembered == aimed && _castChoice?.Spell == aimed,
                    $"a power chosen there waits for its target and goes into the list ({Names(psionicist.QuickCasts)}; {_castChoice})");
                _castChoice = null;
                OpenQuickCast();
                Expect(_spellBar.Shown.Count == 1 && _spellBar.Shown[0] == aimed, $"in a fight the icon's bar is the psionicist's ({Names(_spellBar.Shown)})");
                _spellBar.Click(0);
                Expect(_castChoice?.Spell == aimed, $"and its slot chooses it to cast ({_castChoice})");
                _castChoice = null;
                OpenCastMenu();
                _spellBar.Click("ITEMS", 0);
                Expect(_castChoice is { Spell: 9, Item.Rdff: WandOfMissiles }, $"the wand waits for its target as USE does ({_castChoice})");
                _castChoice = null;
                // #156: picked on the Cast Spells page in a fight, it goes back to the map to aim, as CAST does.
                OpenMagic(1);
                int power = Enumerable.Range(0, 21).FirstOrDefault(i => screen.SpellAt(false, i) == Detonate, -1);
                screen.ClickSpell(false, power);
                Expect(!screen.IsOpen && _castChoice is { Spell: Detonate },
                    $"in a fight, a power picked on the page closes it to aim on the map ({_castChoice}; {screen.Status})");
                _castChoice = null;
                cleric.Cast.Clear(); // its casts back (they were used above)
                OpenMagic(0);
                screen.ClickSpell(false, Enumerable.Range(0, 21).FirstOrDefault(i => screen.SpellAt(false, i) == CureLight, -1));
                Expect(screen.IsOpen && _castChoice is null && screen.Status == "NOT THIS CHARACTER'S TURN",
                    $"and another member's spell waits for its turn ({screen.Status})");
                screen.Close();
            }
        }
        GetTree().Quit(problems.Count == 0 ? 0 : 1);
    }
}

public partial class RegionView
{
    /// <summary>
    /// <c>--open-quick-cast</c> (development, for screenshots): the party's best-read caster
    /// selected and its spell bar opened (given a Wand of Missiles); with <c>--fight</c>, CAST's once it is that member's turn.
    /// </summary>
    private void OpenQuickCastForShow()
    {
        var magic = new Spellcasting(Session.Rules);
        int member = Enumerable.Range(0, Session.Party.Count)
            .OrderByDescending(i => magic.Castable(Session.Party[i]).Count() + Session.Party[i].Powers.Count).FirstOrDefault();
        if (member >= Session.Party.Count)
        {
            return;
        }
        _selected = member;
        var c = Session.Party[member];
        _world.Give(1, -31007, member, GameWorld.None); // a Wand of Missiles, for the ITEMS row
        foreach (int n in magic.Castable(c).Select(s => s.Number).Concat(c.Powers).Take(4).Reverse())
        {
            c.RememberCast(n);
        }
        if (!OS.GetCmdlineUserArgs().Contains("--fight"))
        {
            OpenQuickCast();
            return;
        }
        void Wait()
        {
            if (PlayerTurn is { } t)
            {
                if (t.Source == _world[member])
                {
                    OpenCastMenu();
                    return;
                }
                _battle!.EndTurn(t);
            }
            GetTree().CreateTimer(0.1).Timeout += Wait;
        }
        Wait();
    }
}
