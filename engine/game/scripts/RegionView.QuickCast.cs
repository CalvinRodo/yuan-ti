using DarkSun.Game.Combat;
using DarkSun.Game.Magic;
using DarkSun.Game.World;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// The Dark Sun icon and its quick cast (<see cref="DarkSunIcon"/>,
/// <see cref="QuickCastWindow"/>): the member it is for is the one whose turn
/// it is in a fight, else the selected one (<c>DS:4979</c>). A spell or power
/// chosen from the window is cast as one chosen with CAST in a fight; out of
/// one, the Cast Spells page opens with it picked, to click on whom to cast it.
/// </summary>
public partial class RegionView
{
    private DarkSunIcon _darkSun = null!;
    private QuickCastWindow _quickCast = null!;

    /// <summary>The character screen's page last shown for each member (<c>DS:4971</c>), where the icon opens it.</summary>
    private readonly Dictionary<int, CharacterScreen.Page> _lastPage = [];

    private void BuildDarkSun(CanvasLayer layer)
    {
        _darkSun = new DarkSunIcon { LeftClicked = OpenFromIcon, RightClicked = OpenQuickCast };
        _quickCast = new QuickCastWindow { Choose = QuickCast, OpenSpells = () => OpenMagic(QuickCaster) };
        layer.AddChild(_darkSun);
        layer.AddChild(_quickCast);
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

    /// <summary>A right-click on the icon: the member's quick casts, or with none the Cast Spells page (<c>5F6D:0CB4</c>).</summary>
    private void OpenQuickCast()
    {
        int member = QuickCaster;
        if (!IconFree || member >= Session.Party.Count)
        {
            return;
        }
        var c = Session.Party[member];
        if (c.QuickCasts.Count == 0)
        {
            OpenMagic(member);
            return;
        }
        var magic = new Spellcasting(Session.Rules);
        bool fit = _world[member] is { Vitals: { Hp: > 0, Status: Vitals.Well } };
        _quickCast.Open(c.QuickCasts, n => fit && magic.CanUse(c, n));
    }

    private void OpenMagic(int member)
    {
        if (member < Session.Party.Count)
        {
            _character.Open(_world, member, CharacterScreen.Page.Spells);
        }
    }

    /// <summary>A spell or power chosen from the quick-cast window.</summary>
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
        bool power = number is >= Spellcasting.FirstPower and <= Spellcasting.LastPower;
        _character.Open(_world, QuickCaster, power ? CharacterScreen.Page.Psionics : CharacterScreen.Page.Spells);
        _character.PickToCast(number);
    }

    /// <summary>
    /// <c>--check-quick-cast</c>: a new game with a cleric and a psionicist. With nothing
    /// cast yet the icon's right-click opens Cast Spells; a spell picked there and a power
    /// chosen in a fight go into the quick-cast list, the latest first; the window shows them,
    /// a spell whose casts are used crossed out; a slot picks its spell on Cast Spells out of
    /// a fight and chooses it to cast in one; the left click opens the page last shown.
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
        const int CureLight = 72, Detonate = 139;
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

        OpenQuickCast();
        Expect(screen.IsOpen && screen.Showing == CharacterScreen.Page.Spells && !_quickCast.Visible,
            $"with nothing cast yet, a right-click on the icon opens Cast Spells ({screen.Showing})");
        int slot = Enumerable.Range(0, 21).FirstOrDefault(i => screen.SpellAt(false, i) == CureLight, -1);
        screen.ClickSpell(false, slot);
        Expect(cleric.QuickCasts is [CureLight], $"picking Cure Light Wounds puts it in the cleric's list ({string.Join(", ", cleric.QuickCasts)})");
        screen.Close();

        OpenQuickCast();
        Expect(_quickCast.Shown is [CureLight] && !_quickCast.CrossedOut(0), $"the window shows it, castable ({string.Join(", ", _quickCast.Shown)})");
        _quickCast.Click(0);
        Expect(screen.IsOpen && screen.Showing == CharacterScreen.Page.Spells && screen.PickedSpell == CureLight,
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
        Expect(_quickCast.CrossedOut(0), "with the level's casts used it is crossed out");
        _quickCast.Close();

        for (int n = 1; n <= 7; n++)
        {
            cleric.RememberCast(n);
        }
        cleric.RememberCast(5);
        Expect(cleric.QuickCasts is [7, 6, 5, 4, 3, 2], $"six at most, the latest first, one already there left in place ({string.Join(", ", cleric.QuickCasts)})");

        // In a fight: the psionicist's power chosen with CAST goes into the list, and its slot chooses it again.
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
                ChooseCast(Detonate);
                Expect(psionicist.QuickCasts is [Detonate], $"a power chosen with CAST goes into the list ({string.Join(", ", psionicist.QuickCasts)})");
                _castChoice = null;
                OpenQuickCast();
                Expect(_quickCast.Shown is [Detonate], $"in a fight the window is the psionicist's ({string.Join(", ", _quickCast.Shown)})");
                _quickCast.Click(0);
                Expect(_castChoice is { Spell: Detonate }, $"and its slot chooses it to cast ({_castChoice})");
            }
        }
        GetTree().Quit(problems.Count == 0 ? 0 : 1);
    }
}
