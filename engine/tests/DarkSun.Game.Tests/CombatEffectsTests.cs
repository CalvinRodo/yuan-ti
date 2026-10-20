using DarkSun.Data.Items;
using DarkSun.Game.Combat;
using DarkSun.Game.Movement;
using DarkSun.Game.World;
using static DarkSun.Game.Tests.CombatTests;

namespace DarkSun.Game.Tests;

/// <summary>A field with one sight-blocking wall cell.</summary>
internal sealed class WalledField(Cell wall) : ICombatMap
{
    public bool Walkable(Cell c) => c.X >= 0 && c.Y >= 0 && c.X < 30 && c.Y < 30 && c != wall;

    public bool BlocksSight(Cell c) => c == wall;
}

/// <summary>Areas, fields, casts, sides, psionics, saves and the effects' own handling.</summary>
public class CombatEffectsTests
{
    private static readonly Func<int, int, int> Highest = (_, h) => h;

    private static Battle Started(ICombatDice dice, ICombatMap? map, params Combatant[] all)
    {
        var b = new Battle(all, map ?? new OpenField(30, 30), dice);
        b.StartRound();
        return b;
    }

    private static SpellEffect Spell(int n) => SpellRules.Default.Effect(n)!;

    private static HashSet<string> Names(IEnumerable<Combatant> cs) => [.. cs.Select(c => c.Name)];

    // -- the original's shapes -------------------------------------------------------------------------------

    [Fact]
    public void AFireballIsACircleOfItsAreaAroundTheCell()
    {
        Combatant mage = Fighter("mage", 1, 9, 10);
        Combatant a = Fighter("a", 2, 15, 10), b = Fighter("b", 2, 20, 10), c = Fighter("c", 2, 13, 13), d = Fighter("d", 2, 21, 10);
        Battle battle = Started(new ScriptedDice(), null, mage, a, b, c, d);

        // Radius 80 sixteenths (area 80, doubled for a circle, halved): 5 cells.
        Assert.Equal(["a", "b", "c"], Names(battle.AffectedBy(mage, Spell(28), 5, new Cell(15, 10))));
    }

    [Fact]
    public void ALightningBoltRunsAlongTheLineOfFireCentredOnTheCell()
    {
        Combatant mage = Fighter("mage", 1, 10, 10);
        Combatant near = Fighter("near", 2, 11, 10), side = Fighter("side", 2, 16, 11), wide = Fighter("wide", 2, 16, 12),
            far = Fighter("far", 2, 21, 10), past = Fighter("past", 2, 22, 10);
        Battle b = Started(new ScriptedDice(), null, mage, near, side, wide, far, past);

        // Template 13: 4 × 40 long (± 5 cells), 40 wide (± 1.25 cells).
        Assert.Equal(["near", "side", "far"], Names(b.AffectedBy(mage, Spell(33), 5, new Cell(16, 10))));
    }

    [Fact]
    public void BurningHandsReachesOnlyTheCellInFront()
    {
        Combatant mage = Fighter("mage", 1, 10, 10);
        Combatant front = Fighter("front", 2, 11, 10), diagonal = Fighter("diagonal", 2, 11, 11), behind = Fighter("behind", 2, 9, 10);
        Battle b = Started(new ScriptedDice(), null, mage, front, diagonal, behind);

        Assert.Equal(["front"], Names(b.AffectedBy(mage, Spell(2), 5, new Cell(11, 10))));
    }

    [Fact]
    public void AnAreaDoesntReachWhatItsCentreCantSee()
    {
        Combatant mage = Fighter("mage", 1, 5, 10), open = Fighter("open", 2, 11, 12), hidden = Fighter("hidden", 2, 13, 10);
        Battle b = Started(new ScriptedDice(), new WalledField(new Cell(12, 10)), mage, open, hidden);

        Assert.Equal(["open"], Names(b.AffectedBy(mage, Spell(28), 5, new Cell(11, 10))));
    }

    // -- fields ------------------------------------------------------------------------------------------------

    [Fact]
    public void ACloudActsOnWhoeverEntersItAndEndsForThoseWhoLeave()
    {
        Combatant mage = Fighter("mage", 1, 5, 10), orc = Fighter("orc", 2, 15, 10), goblin = Fighter("goblin", 2, 20, 11);
        Battle b = Started(new ScriptedDice(), null, mage, orc, goblin);

        Assert.True(b.Cast(mage, 23, 5, new Cell(15, 10)));
        AreaField cloud = Assert.Single(b.Fields);
        Assert.True(orc.Has(Condition.CantAttack)); // Stinking Cloud, no save on a 1
        Assert.False(goblin.Has(Condition.CantAttack));

        for (int i = 0; i < 3; i++)
        {
            Assert.True(b.Step(orc, orc.Cell + new Cell(1, 0)));
        }
        Assert.False(orc.Has(Condition.CantAttack)); // 3 cells out of a radius of 2.5
        for (int i = 0; i < 3; i++)
        {
            b.Step(goblin, goblin.Cell + new Cell(-1, 0));
        }
        Assert.True(goblin.Has(Condition.CantAttack));
        Assert.Contains(goblin, cloud.Occupants);
    }

    [Fact]
    public void GreaseHoldsWhileItLastsAndThenGoes()
    {
        Combatant mage = Fighter("mage", 1, 5, 10), orc = Fighter("orc", 2, 15, 10);
        Battle b = Started(new ScriptedDice(), null, mage, orc);

        b.Cast(mage, 8, 5, new Cell(15, 10));
        ActiveEffect stuck = Assert.Single(orc.Effects, e => e.Condition == Condition.Stuck);
        Assert.True(stuck.Permanent); // no time of its own: it lasts while the orc is in the grease
        Assert.False(b.Step(orc, new Cell(16, 10)));

        // 3d1 + 5 rounds (template 1).
        for (int round = 0; round < 8; round++)
        {
            b.StartRound();
        }
        Assert.Empty(b.Fields);
        Assert.False(orc.Has(Condition.Stuck));
    }

    [Fact]
    public void AWallOfStoneBlocksMovementAndSight()
    {
        Combatant mage = Fighter("mage", 1, 10, 10), orc = Fighter("orc", 2, 20, 10);
        Battle b = Started(new ScriptedDice(), null, mage, orc);

        b.Cast(mage, 69, 5, new Cell(15, 4), new Cell(15, 16));
        Assert.False(b.Walkable(new Cell(15, 10)));
        Assert.False(b.Walkable(new Cell(15, 15))); // the far end itself falls just short: 1A0A:2DE6 scales long distances down
        Assert.True(b.Walkable(new Cell(14, 10)));
        Assert.False(b.InSight(mage, orc));
    }

    [Fact]
    public void AWallCastAtOneCellIsAPointUnlessFixed()
    {
        Combatant mage = Fighter("mage", 1, 10, 10), orc = Fighter("orc", 2, 20, 10);
        Battle b = Started(new ScriptedDice(), null, mage, orc);
        b.Cast(mage, 69, 5, new Cell(15, 10));
        Assert.Equal(1, b.Fields[0].Cells.Count);

        var rules = new CombatRules { Fixes = new CombatFixes(WallsFromOneCell: true) };
        var fixedBattle = new Battle([mage, orc], new OpenField(30, 30), new ScriptedDice(), rules);
        fixedBattle.StartRound();
        fixedBattle.Cast(mage, 69, 5, new Cell(15, 10));
        Assert.False(fixedBattle.Walkable(new Cell(15, 14)));
    }

    // -- the rest of the traced details ------------------------------------------------------------------------

    [Fact]
    public void ABlinkerIsOutOfReachOnceItHasActed()
    {
        Combatant a = Fighter("a", 1, 5, 10), t = Fighter("t", 2, 6, 10);
        Battle b = Started(new ScriptedDice(), null, a, t);
        t.Effects.Add(new ActiveEffect(26, Condition.Blink, 5, t));

        b.Hit(a, t, 5, DamageElement.Slashing);
        Assert.Equal(25, t.Vitals.Hp); // still to act this round
        b.EndTurn(t);
        b.Hit(a, t, 5, DamageElement.Slashing);
        Assert.Equal(25, t.Vitals.Hp);
        Assert.True(b.Immune(t, Spell(9)));
        Assert.False(b.Immune(t, Spell(28))); // a Fireball's area is large
    }

    [Fact]
    public void SomeSpellsChooseWhomTheyTake()
    {
        Combatant priest = Fighter("priest", 1, 5, 10), friend = Fighter("friend", 1, 6, 10), orc = Fighter("orc", 2, 9, 10),
            human = Fighter("human", 2, 7, 10, race: 1), ghoul = new() { Name = "ghoul", Side = 2, Race = 9, Cell = new Cell(8, 10) };
        Battle b = Started(new ScriptedDice(), null, priest, friend, orc, human, ghoul);

        Assert.True(b.Reaches(priest, Spell(31), human));
        Assert.False(b.Reaches(priest, Spell(31), orc));     // Hold Person: races 1–7
        Assert.True(b.Reaches(priest, Spell(32), ghoul));    // Hold Undead
        Assert.False(b.Reaches(priest, Spell(30), orc));     // Haste: its own side
        Assert.False(b.Reaches(priest, Spell(38), friend));  // Slow: the other side
        Assert.Empty(b.AffectedBy(priest, Spell(31), 5, orc.Cell));
    }

    [Fact]
    public void AConjuredWeaponNeedsAFreeHandAndGoesWithItsCondition()
    {
        Combatant druid = Fighter("druid", 1, 5, 10);
        Battle b = Started(new ScriptedDice(), null, druid);

        druid.Weapon = Sword();
        b.Apply(druid, druid, Spell(86), 5);
        Assert.Contains(b.Log, e => e is Avoided { Why: "Failed: weapon in hand" });
        Assert.False(druid.Has(Condition.FlameBlade));

        druid.Weapon = null;
        b.Apply(druid, druid, Spell(86), 5);
        Assert.True(druid.Has(Condition.FlameBlade));
        Assert.Equal(30025, Battle.ConjuredItem(86));
    }

    [Fact]
    public void BlowsWithoutAWeaponBludgeonAndACreaturesCountAsMagicByLevel()
    {
        Assert.Equal(DamageElement.Bludgeoning | DamageElement.Plus1, Battle.NaturalHit(Fighter("orc", 2, 0, 0)));      // level 5, race 0
        Assert.Equal(DamageElement.Bludgeoning, Battle.NaturalHit(Fighter("man", 1, 0, 0, race: 1)));
    }

    [Fact]
    public void APowersRankAddsToItsCheck()
    {
        var psion = new Combatant { Name = "psion", Side = 1, PartyMember = true, Stats = [10, 10, 10, 10, 10, 10], PowerRanks = { [161] = 4 } };
        var b = new Battle([psion], new OpenField(10, 10), new ScriptedDice(null, 9));
        PsionicPower crush = SpellRules.Default.Power(161)!;

        Assert.Equal(9, b.PowerCheck(psion, 161, crush)); // 9 ≤ Wis 10 − 4 + 4 − 1
    }

    [Fact]
    public void TheFixesAreOffUnlessAsked()
    {
        Combatant a = Fighter("a", 1, 5, 10), t = Fighter("t", 2, 6, 10, hp: 50);
        t.Effects.Add(new ActiveEffect(20, Condition.MirrorImages, 5, t) { Charges = 2 });
        var b = new Battle([a, t], new OpenField(10, 20), new ScriptedDice(), new CombatRules { Fixes = CombatFixes.All });
        b.StartRound();

        b.Hit(a, t, 5, DamageElement.Slashing);
        Assert.Equal(50, t.Vitals.Hp); // an image took it
        Assert.False(CombatRules.Default.Fixes.MirrorImagesTakeBlows);

        t.Effects.Add(new ActiveEffect(3, Condition.Berserk, 5, t));
        t.Side = 1;
        b.EndCondition(t, Condition.Berserk);
        Assert.Equal(2, t.Side);
    }

    [Fact]
    public void FireBurnsAWebAway()
    {
        Combatant mage = Fighter("mage", 1, 5, 10), orc = Fighter("orc", 2, 15, 10);
        Battle b = Started(new ScriptedDice(), null, mage, orc);

        b.Cast(mage, 25, 5, new Cell(15, 10));
        Assert.True(orc.Has(Condition.Stuck));
        b.StartRound();
        b.Cast(mage, 28, 5, new Cell(15, 10));
        Assert.Empty(b.Fields);
        Assert.False(orc.Has(Condition.Stuck));
    }

    // -- casts, sides, footprints ------------------------------------------------------------------------------

    [Fact]
    public void ACreaturesCastsComeFromItsClassAndItsHealth()
    {
        Combatant Mage(int hp) => new()
        {
            Name = "mage", Side = 2, Vitals = new Vitals { Hp = hp, MaxHp = 30 }, SpellSet = 1, Classes = [(11, 5)],
        };
        var b = new Battle([], new OpenField(10, 10), new ScriptedDice());

        Combatant well = Mage(30);
        b.CastsFor(well);
        // A level 5 preserver: 3 first-level (Magic Missile), 2 second (Acid Arrow), 1 third (Fireball).
        Assert.Equal(6, well.Spells.Count);
        Assert.Equal(3, well.Spells.Count(n => n == 9));
        Assert.Equal(2, well.Spells.Count(n => n == 19));
        Assert.Equal(1, well.Spells.Count(n => n == 28));

        Combatant hurt = Mage(15);
        b.CastsFor(hurt);
        Assert.Equal([9, 19], hurt.Spells.Order());
    }

    [Fact]
    public void ACharmTakesItToTheCastersSideUntilItEnds()
    {
        Combatant mage = Fighter("mage", 1, 5, 10), orc = Fighter("orc", 2, 6, 10, race: 7);
        Battle b = Started(new ScriptedDice(), null, mage, orc);

        b.Cast(mage, 3, 5, orc.Cell);
        Assert.Equal(1, orc.Side);
        Assert.True(orc.UnderAi);
        Assert.True(b.Over);
        b.EndCondition(orc, Condition.Charmed);
        Assert.Equal(2, orc.Side);
    }

    [Fact]
    public void ALargeCreatureStandsOnItsFootprint()
    {
        var giant = new Combatant { Name = "giant", Side = 2, Cell = new Cell(10, 10), Vitals = new Vitals { Hp = 30, MaxHp = 30 }, Size = 1 };
        Combatant fighter = Fighter("fighter", 1, 13, 10);
        Battle b = Started(new ScriptedDice(), null, giant, fighter);

        Assert.Same(giant, b.At(new Cell(9, 10)));
        Assert.Same(giant, b.At(new Cell(11, 10)));
        Assert.Equal(2, Battle.Distance(giant, fighter));
        Assert.False(b.Fits(fighter, new Cell(11, 10)));
    }

    // -- defences ------------------------------------------------------------------------------------------------

    [Fact]
    public void MirrorImagesTakeSpellsButNotBlows()
    {
        Combatant mage = Fighter("mage", 1, 5, 10), orc = Fighter("orc", 2, 6, 10, hp: 50);
        Battle b = Started(new ScriptedDice(), null, mage, orc);
        orc.Effects.Add(new ActiveEffect(20, Condition.MirrorImages, 5, orc) { Charges = 3 });

        b.Hit(mage, orc, 5, DamageElement.Slashing);
        Assert.Equal(45, orc.Vitals.Hp);
        b.Apply(mage, orc, Spell(9), 5);
        Assert.Equal(45, orc.Vitals.Hp);
        Assert.Equal(2, orc.Effects.Single(e => e.Condition == Condition.MirrorImages).Charges);
    }

    [Fact]
    public void NegativePlaneProtectionOnlySpendsACharge()
    {
        // An undead's negative-energy blow (or large area) meets it; it saves "as
        // against Vampiric Touch" (80F5:1999), whose record allows no save.
        var ghoul = new Combatant { Name = "ghoul", Side = 2, Cell = new Cell(5, 10), Vitals = new Vitals { Hp = 30, MaxHp = 30 }, Race = 9, Level = 3 };
        Combatant priest = Fighter("priest", 1, 6, 10);
        priest.Effects.Add(new ActiveEffect(101, Condition.NegativePlaneProtection, 5, priest) { Charges = 1 });
        Battle b = Started(new ScriptedDice(), null, ghoul, priest);

        b.Hit(ghoul, priest, 5, DamageElement.Negative);
        Assert.False(priest.Has(Condition.NegativePlaneProtection));
        Assert.Equal(25, priest.Vitals.Hp);
    }

    [Fact]
    public void SynapticStaticBlocksPowersAndAMindDefendsItself()
    {
        Combatant psion = Fighter("psion", 2, 5, 10), holder = Fighter("holder", 2, 20, 20), target = Fighter("target", 1, 6, 10);
        psion.Psp = 50;
        holder.Effects.Add(new ActiveEffect(171, Condition.SynapticStatic, 5, holder));
        Battle b = Started(new ScriptedDice(), null, psion, holder, target);
        PsionicPower crush = SpellRules.Default.Power(161)!;

        Assert.Equal(-1, b.PowerCheck(psion, 161, crush)); // the holder's 1 isn't over Int 10 − 4
        holder.Effects.Clear();
        Assert.Equal(1, b.PowerCheck(psion, 161, crush));

        // A monster defends with its level (5): two rolls over the attacker's 1 hold.
        var high = new Battle([psion, target], new OpenField(30, 30), new ScriptedDice(Highest, 1));
        Assert.True(high.UsePower(psion, 161, target.Cell));
        Assert.Contains(high.Log, e => e is Avoided { Why: "mind defended" });
    }

    // -- saves -----------------------------------------------------------------------------------------------------

    [Fact]
    public void SomeSpellsCantBeSavedAgainstAndPrayerHelpsItsSide()
    {
        Combatant priest = Fighter("priest", 1, 5, 10), friend = Fighter("friend", 1, 6, 10), low = Fighter("low", 2, 7, 10);
        Battle b = Started(new ScriptedDice(), null, priest, friend, low);

        Assert.Equal(-100, b.SaveBonus(low, Spell(Battle.Scare), priest, 5, 5));
        Assert.Equal(100, b.SaveBonus(new Combatant { Name = "high", Level = 6 }, Spell(Battle.Scare), priest, 5, 5));
        Assert.Equal(-100, b.SaveBonus(low, Spell(Battle.Cloudkill), priest, 5, 1) - b.SaveBonus(low, Spell(9), priest, 5, 1));
        friend.Effects.Add(new ActiveEffect(102, Condition.Prayer, 5, priest));
        low.Effects.Add(new ActiveEffect(102, Condition.Prayer, 5, priest));
        Assert.Equal(1, b.SaveBonus(friend, Spell(9), null, 5, 5));
        Assert.Equal(-1, b.SaveBonus(low, Spell(9), null, 5, 5));
    }

    [Fact]
    public void EffectsChangeArmourClassAndToHit()
    {
        Combatant a = Fighter("a", 1, 5, 10), t = Fighter("t", 2, 6, 10, ac: 10);
        Battle b = Started(new ScriptedDice(), null, a, t);
        int before = b.ToHitNeeded(a, t, null);

        t.Effects.Add(new ActiveEffect(13, Condition.Blur, 5, t));
        Assert.Equal(before + 2, b.ToHitNeeded(a, t, null));
        t.Effects.Add(new ActiveEffect(82, Condition.Barkskin, 8, t));
        Assert.Equal(4, b.ArmorClassAgainst(t, a)); // 6 − 8 ÷ 4
        a.Effects.Add(new ActiveEffect(70, Condition.Blessed, 5, a));
        Assert.Equal(before + 2 + (10 - 4) - 1, b.ToHitNeeded(a, t, null));
    }

    // -- the effects' own handling ----------------------------------------------------------------------------------

    [Fact]
    public void CuresStrengthAndDrainsDoWhatTheirHandlersSay()
    {
        Combatant priest = Fighter("priest", 1, 5, 10, str: 18), friend = Fighter("friend", 1, 6, 10, hp: 30), orc = Fighter("orc", 2, 7, 10);
        Battle b = Started(new ScriptedDice(), null, priest, friend, orc);
        friend.Vitals.Hp = 10;

        b.Apply(priest, friend, Spell(72), 5);
        Assert.Equal(11, friend.Vitals.Hp); // 1d8
        b.Apply(priest, friend, Spell(24), 5);
        Assert.Equal(11, friend.Strength); // +1d6
        b.Apply(priest, friend, Spell(158), 5);
        Assert.Equal(18, friend.Strength); // borrowed: min(24 − 11, (18 − 3) / 2) = 7
        Assert.Equal(4, priest.Strength);  // lent twice that
        b.EndCondition(priest, Condition.StrengthLent);
        Assert.Equal(11, friend.Strength);

        b.Apply(priest, orc, Spell(Battle.VampiricTouch), 6);
        Assert.Equal(27, orc.Vitals.Hp); // 3d6
        Assert.Equal(33, priest.Vitals.MaxHp);
    }

    [Fact]
    public void DispelRaiseDeadDisintegrateAndTurnUndead()
    {
        Combatant priest = Fighter("priest", 1, 5, 10), friend = Fighter("friend", 1, 6, 10), orc = Fighter("orc", 2, 7, 10);
        Battle b = Started(new ScriptedDice(), null, priest, friend, orc);
        orc.Effects.Add(new ActiveEffect(30, Condition.Hasted, 5, orc));
        orc.Effects.Add(new ActiveEffect(120, Condition.Poisoned, 5, orc) { Charges = 10 });

        b.Apply(priest, orc, Spell(27), 10);
        Assert.False(orc.Has(Condition.Hasted));  // 1 ≤ 50 + 5 × (10 − 5)
        Assert.True(orc.Has(Condition.Poisoned)); // never dispelled

        b.Hurt(null, friend, 45);
        Assert.Equal(Vitals.Dead, friend.Vitals.Status);
        b.Apply(priest, friend, Spell(Battle.RaiseDead), 9);
        Assert.Equal((Vitals.Well, 1, 9), (friend.Vitals.Status, friend.Vitals.Hp, friend.Stats[2]));

        b.Apply(priest, orc, Spell(Battle.Disintegrate), 5);
        Assert.Equal(Vitals.Destroyed, orc.Vitals.Status);

        Assert.Equal(-2, Battle.TurnNeeded(1, 5));
        Assert.Equal(34, Battle.TurnNeeded(10, 3));
    }

    [Fact]
    public void HasteDoublesThisRoundsMovementAtOnce()
    {
        Combatant mage = Fighter("mage", 1, 5, 10), friend = Fighter("friend", 1, 6, 10);
        Battle b = Started(new ScriptedDice(), null, mage, friend);
        int points = friend.MovementPoints;

        b.Cast(mage, 30, 5, friend.Cell);
        Assert.True(friend.Has(Condition.Hasted));
        Assert.Equal(points * 2, friend.MovementPoints);
    }
}
