using DarkSun.Data.Items;
using DarkSun.Data.Regions;
using DarkSun.Game.Combat;
using DarkSun.Game.Movement;
using DarkSun.Game.Rules;
using DarkSun.Game.World;

namespace DarkSun.Game.Tests;

/// <summary>Rolls from a list, then from a rule (the lowest by default).</summary>
internal sealed class ScriptedDice(Func<int, int, int>? then = null, params int[] first) : ICombatDice
{
    private readonly Queue<int> _queue = new(first);

    public int Roll(int low, int high) =>
        _queue.TryDequeue(out int v) ? Math.Clamp(v, low, high) : Math.Clamp((then ?? ((l, _) => l))(low, high), low, high);
}

public class CombatTests
{
    internal static Combatant Fighter(string name, int side, int x, int y, int hp = 30, int type = 0, int dex = 10, int ac = 5, int str = 10, int mind = 2, int race = 0) => new()
    {
        Name = name,
        Side = side,
        Cell = new Cell(x, y),
        Vitals = new Vitals { Hp = hp, MaxHp = hp },
        Stats = [str, dex, 10, 10, 10, 10],
        ArmorClass = ac,
        Mind = mind,
        Thac0 = 20,
        Type = type,
        Level = 5,
        Race = race,
        Facing = side == 1 ? 2 : 6,
        ComputerControlled = side != 1,
    };

    internal static Weapon Sword(int bonus = 0) => new(new ItemType(
        0, WeaponKind.Melee, 0, (ushort)DamageElement.Slashing, 10, 0, 0, (byte)ItemMaterial.Metal, ItemPlacement.Hand,
        1, 1, 8, 1, 0, 0, 0xFFFF, 0, 0), bonus);

    private static Battle Fight(ICombatDice dice, params Combatant[] all) => new(all, new OpenField(30, 30), dice);

    private static Battle Started(ICombatDice dice, params Combatant[] all)
    {
        Battle b = Fight(dice, all);
        b.StartRound();
        return b;
    }

    private static readonly Func<int, int, int> Highest = (_, h) => h;

    /// <summary>Ends the others' turns until it is <paramref name="c"/>'s.</summary>
    private static void TurnOf(Battle b, Combatant c)
    {
        while (b.NextTurn() is { } next && next != c)
        {
            b.EndTurn(next);
        }
        Assert.Same(c, b.Current);
    }

    // -- rounds, turns, movement ------------------------------------------------------------------------

    [Fact]
    public void HighInitiativeActsFirstAndTurnsEndTheRound()
    {
        Combatant slow = Fighter("slow", 1, 0, 0), quick = Fighter("quick", 2, 5, 0, dex: 18);
        Battle b = Started(new ScriptedDice(), slow, quick);

        Assert.Equal((20, 22), (slow.Initiative, quick.Initiative)); // 20 + 0 + Dexterity's reaction
        Assert.Same(quick, b.NextTurn());
        b.EndTurn(quick);
        Assert.Same(slow, b.NextTurn());
        b.EndTurn(slow);
        Assert.Null(b.NextTurn());
    }

    [Fact]
    public void MovementPointsFollowTheSpeedConditions()
    {
        Combatant normal = Fighter("a", 1, 0, 0), hasted = Fighter("b", 1, 2, 0), slowed = Fighter("c", 1, 4, 0), held = Fighter("d", 1, 6, 0);
        hasted.Effects.Add(new ActiveEffect(30, Condition.Hasted, 5, null) { Charges = 0, EndsAt = 9999 });
        slowed.Effects.Add(new ActiveEffect(38, Condition.Slowed, 5, null) { EndsAt = 9999 });
        held.Effects.Add(new ActiveEffect(31, Condition.Paralyzed, 5, null) { EndsAt = 9999 });
        Battle b = Started(new ScriptedDice(), normal, hasted, slowed, held, Fighter("foe", 2, 20, 20));

        Assert.Equal((120, 240, 60, 0), (normal.MovementPoints, hasted.MovementPoints, slowed.MovementPoints, held.MovementPoints));
        Assert.Equal(TurnFlags.None, held.Flags & TurnFlags.MayMove);
        Assert.True(b.Step(normal, new Cell(1, 1)));
        Assert.Equal(106, normal.MovementPoints); // a diagonal step costs 14
    }

    // -- attacks ------------------------------------------------------------------------------------------

    [Fact]
    public void TheNumberToHitIsThac0LessBonusesAndArmourClass()
    {
        Combatant a = Fighter("a", 1, 5, 5, str: 18), t = Fighter("t", 2, 6, 5, ac: 5);
        Battle b = Started(new ScriptedDice(), a, t);
        t.Facing = 6; // faces the attacker
        Assert.Equal(13, b.ToHitNeeded(a, t, Sword())); // 20 − 2 (Str 18) − 5
        t.Facing = 2; // faces away: −2 more, and no Dexterity
        Assert.Equal(11, b.ToHitNeeded(a, t, Sword()));
        t.Facing = 6;
        Assert.Equal(12, b.ToHitNeeded(a, t, Sword(bonus: 1)));
    }

    [Fact]
    public void AHitDoesItsDiceAndTheTargetFalls()
    {
        Combatant a = Fighter("a", 1, 5, 5), t = Fighter("t", 2, 6, 5, hp: 5);
        a.Weapon = Sword();
        Battle b = Started(new ScriptedDice(Highest), a, t);
        var fell = new List<Combatant>();
        b.Defeated += fell.Add;

        Assert.True(b.Attack(a, t));
        // 1d8 at its highest: 8 is under 5 + 10, so down (3), not dead.
        Assert.Equal((Vitals.Down, 0), (t.Vitals.Status, t.Vitals.Hp));
        Assert.Equal([t], fell);
        Assert.Contains(b.Log, e => e is AttackRolled { Roll: 20, Hit: true });
        Assert.False(b.Attack(a, t)); // once a turn
    }

    [Fact]
    public void ResistancesStopOrdinaryWeaponsButNotMagicOnes()
    {
        Combatant a = Fighter("a", 1, 5, 5), shadow = Fighter("shadow", 2, 6, 5, hp: 50, type: 13);
        Battle b = Started(new ScriptedDice(Highest), a, shadow);

        b.Hit(a, shadow, 8, Sword().Elements);
        Assert.Equal(50, shadow.Vitals.Hp);
        Assert.Contains(b.Log, e => e is Avoided { Why: "immune" });
        b.Hit(a, shadow, 8, Sword(bonus: 1).Elements);
        Assert.Equal(42, shadow.Vitals.Hp);
    }

    // -- charges, conditions, guard and wait ----------------------------------------------------------------

    [Fact]
    public void ArmorLastsAsManyHitsAsItHasCharges()
    {
        Combatant a = Fighter("a", 1, 5, 5), t = Fighter("t", 2, 6, 5, hp: 500, ac: 10);
        Battle b = Started(new ScriptedDice(), a, t);
        Assert.Equal(10, b.ArmorClassAgainst(t, a));
        Assert.True(b.AddCondition(t, t, SpellRules.Default.Effect(1)!, level: 2)); // Armor: 8d1 + 1 a level
        Assert.Equal(10, t.Effects.Single().Charges);
        Assert.Equal(6, b.ArmorClassAgainst(t, a)); // its body armour counts as 6

        for (int i = 0; i < 5; i++)
        {
            b.Hit(a, t, 1, Battle.PhysicalElements); // a hit spends one, the wound another
        }
        Assert.False(t.Has(Condition.ImprovedAc));
        Assert.Contains(b.Log, e => e is ConditionEnded { Condition: Condition.ImprovedAc });
    }

    /// <summary>A missile weapon: a chatkcha (thrown, range 9, 1d6, IT1R type 48) or a bow (shoots ammunition, range 17).</summary>
    private static Weapon Missile(bool bow) => new(new ItemType(
        bow ? 1 : 48, bow ? WeaponKind.Missile | WeaponKind.UsesAmmunition : WeaponKind.Missile | WeaponKind.Thrown, 0,
        (ushort)DamageElement.Slashing, 10, 0, 0, (byte)ItemMaterial.Metal, ItemPlacement.MissileWeapon,
        bow ? (byte)17 : (byte)9, 2, bow ? (byte)8 : (byte)6, 1, 0, 0, 0xFFFF, 0, 0));

    [Fact]
    public void AThrownWeaponNeedsNoAmmunitionAndABowUsesItsArrows()
    {
        // 612E:1FCF: only a weapon whose type shoots ammunition counts the ammunition slot; a chatkcha throws as it is.
        Combatant kreen = Fighter("kreen", 1, 5, 5), t = Fighter("t", 2, 10, 5, hp: 500);
        kreen.MissileWeapon = Missile(bow: false);
        Battle b = Started(new ScriptedDice(Highest), kreen, t);
        Assert.False(kreen.ShootsAmmunition);
        Assert.True(kreen.CanShoot);
        Assert.True(b.CanAttack(kreen, t, missile: true));
        Assert.True(b.Attack(kreen, t, missile: true));
        Assert.True(t.Vitals.Hp < 500);
        Assert.Equal(0, kreen.Ammunition);

        Combatant archer = Fighter("archer", 1, 5, 6), u = Fighter("u", 2, 10, 6, hp: 500);
        archer.MissileWeapon = Missile(bow: true);
        Battle c = Started(new ScriptedDice(Highest), archer, u);
        Assert.True(archer.ShootsAmmunition);
        Assert.False(c.CanAttack(archer, u, missile: true)); // no arrows
        archer.Ammunition = 1;
        Assert.True(c.Attack(archer, u, missile: true));
        Assert.Equal(0, archer.Ammunition); // two shots a round, but only one arrow
        Assert.False(archer.CanShoot);
    }

    [Fact]
    public void AttackingEndsInvisibilityAndPoisonKillsWhenItsRoundsRunOut()
    {
        Combatant a = Fighter("a", 1, 5, 5), t = Fighter("t", 2, 6, 5, hp: 500);
        Battle b = Started(new ScriptedDice(), a, t);
        b.AddCondition(a, a, SpellRules.Default.Effect(18)!, 3); // Invisibility: one charge
        Assert.True(a.Has(Condition.Invisible));
        Assert.Equal(-4 + 5, b.ArmorClassAgainst(a, t)); // unseen: 4 better
        b.Attack(a, t);
        Assert.False(a.Has(Condition.Invisible));

        b.AddCondition(a, t, SpellRules.Default.Effect(120)!, 3); // Poison: 10 charges, one a round
        for (int r = 0; r < 9; r++)
        {
            b.StartRound();
        }
        Assert.True(t.Alive);
        b.StartRound();
        Assert.Equal(Vitals.Dead, t.Vitals.Status);
    }

    [Fact]
    public void AGuardAttacksTheFirstEnemyThatStepsNextToIt()
    {
        Combatant guard = Fighter("guard", 1, 5, 5, dex: 18), mover = Fighter("mover", 2, 8, 5, hp: 500);
        Battle b = Started(new ScriptedDice(Highest), guard, mover);
        Assert.Same(guard, b.NextTurn());
        b.Guard(guard);
        Assert.True(guard.Guarding);

        Assert.Same(mover, b.NextTurn());
        Assert.True(b.Step(mover, new Cell(7, 5)));
        Assert.Equal(500, mover.Vitals.Hp); // two away: nothing yet
        Assert.True(b.Step(mover, new Cell(6, 5)));
        Assert.True(mover.Vitals.Hp < 500);
        Assert.False(guard.Guarding);
    }

    [Fact]
    public void WaitingLetsTheOthersGoFirst()
    {
        Combatant quick = Fighter("quick", 1, 0, 0, dex: 18), slow = Fighter("slow", 2, 9, 9);
        Battle b = Started(new ScriptedDice(), quick, slow);
        Assert.Same(quick, b.NextTurn());
        b.Wait(quick);
        Assert.Equal(10, quick.Initiative);
        Assert.Same(slow, b.NextTurn());
        b.EndTurn(slow);
        Assert.Same(quick, b.NextTurn());
    }

    // -- on-hit abilities and immunities --------------------------------------------------------------------

    [Fact]
    public void ASpidersLastBitePoisonsAndABabauCorrodesArmour()
    {
        Combatant spider = Fighter("spider", 2, 5, 5, type: 5), t = Fighter("t", 1, 6, 5, hp: 500);
        Battle b = Started(new ScriptedDice(), spider, t); // lowest rolls: saves fail
        b.Hit(spider, t, 1, Battle.PhysicalElements, first: false, lastNatural: true);
        Assert.Equal(500 - 1 - 10, t.Vitals.Hp); // the bite, then effect 177's 10 poison

        var plate = new Item(1, new ItemRecord(-1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 9, 0, 0), slot: 9);
        t.Items.Add(plate);
        Combatant babau = Fighter("babau", 2, 4, 5, type: 3);
        b.Add(babau);
        b.Hit(babau, t, 1, Battle.PhysicalElements, first: true);
        Assert.DoesNotContain(plate, t.Items);
        Assert.Contains(b.Log, e => e is ItemDestroyed { Message: "Armor is corroded" });
    }

    [Fact]
    public void ImmunitiesByAbilityBitAndMagicResistance()
    {
        Combatant caster = Fighter("caster", 1, 5, 5), shadow = Fighter("shadow", 2, 6, 5, type: 13), xorn = Fighter("xorn", 2, 7, 7, type: 27);
        Combatant resistant = new()
        {
            Name = "vrock", Side = 2, Cell = new Cell(8, 8), Vitals = new Vitals { Hp = 50, MaxHp = 50 }, MagicResistance = 100,
        };
        Battle b = Started(new ScriptedDice(), caster, shadow, xorn, resistant);

        SpellEffect hold = SpellRules.Default.Effect(31)!;
        Assert.True(b.Immune(shadow, hold)); // ability bit 5: no charm or hold
        b.Apply(caster, shadow, hold, 5);
        Assert.False(shadow.Has(Condition.Paralyzed));

        Assert.False(b.AddCondition(caster, xorn, SpellRules.Default.Effect(176)!, 5)); // bit 9: never Stuck
        b.Apply(caster, resistant, hold, 5);
        Assert.Contains(b.Log, e => e is Avoided { Why: "resisted" });
    }

    [Fact]
    public void RegenerationHealsThreeARound()
    {
        Combatant mastyrial = Fighter("mastyrial", 2, 5, 5, hp: 40, type: 11);
        mastyrial.Vitals.Hp = 30;
        Battle b = Started(new ScriptedDice(), mastyrial, Fighter("p", 1, 9, 9));
        Assert.Equal(33, mastyrial.Vitals.Hp);
    }

    // -- the AI -----------------------------------------------------------------------------------------------

    [Fact]
    public void TheAiWalksUpAndThenAttacks()
    {
        Combatant monster = Fighter("monster", 2, 0, 0), member = Fighter("member", 1, 4, 0, hp: 500);
        Battle b = Started(new ScriptedDice(Highest), monster, member);
        TurnOf(b, monster);
        b.RunAi(monster);

        Assert.Equal(1, Battle.Distance(monster, member));
        Assert.True(member.Vitals.Hp < 500);
        Assert.Null(b.Current);
    }

    [Fact]
    public void FleeingAndIdleMindsDontAttack()
    {
        Combatant civilian = Fighter("civilian", 2, 5, 5, mind: CombatRecord.MindFlees), member = Fighter("member", 1, 6, 5);
        Combatant bystander = Fighter("bystander", 2, 10, 10, mind: CombatRecord.MindIdle);
        Battle b = Started(new ScriptedDice(), civilian, bystander, member);
        TurnOf(b, civilian);
        b.RunAi(civilian);
        Assert.True(Battle.Distance(civilian, member) > 1);

        Cell before = bystander.Cell;
        TurnOf(b, bystander);
        b.RunAi(bystander);
        Assert.Equal(before, bystander.Cell);
        Assert.Equal(30, member.Vitals.Hp);
    }

    [Fact]
    public void ACasterCastsItsHeaviestRollAtTheTarget()
    {
        var mage = new Combatant
        {
            Name = "mage", Side = 2, Cell = new Cell(0, 0), Vitals = new Vitals { Hp = 20, MaxHp = 20 },
            Stats = [10, 18, 10, 14, 10, 10], Level = 5, Spells = [9], Move = 0,
        };
        Combatant member = Fighter("member", 1, 8, 0, hp: 500);
        Battle b = Started(new ScriptedDice(Highest), mage, member);
        TurnOf(b, mage);
        b.RunAi(mage);

        Assert.Contains(b.Log, e => e is Cast { Effect: 9 });
        Assert.Empty(mage.Spells);
        Assert.Equal(500 - 3 * 5, member.Vitals.Hp); // Magic Missile at 5: 3 × (1d4 + 1)
    }

    [Fact]
    public void AnAbilityIsTriedOneTurnInFour()
    {
        Combatant eel = new()
        {
            Name = "fire eel", Side = 2, Cell = new Cell(0, 0), Vitals = new Vitals { Hp = 40, MaxHp = 40 }, Type = 9, Level = 6, Move = 0,
        };
        Combatant member = Fighter("member", 1, 1, 0, hp: 500);
        Battle b = Started(new ScriptedDice(), eel, member); // the lowest roll: 0, so it tries
        TurnOf(b, eel);
        b.RunAi(eel);
        Assert.Contains(b.Log, e => e is Cast { Effect: 182 }); // a 6d6 fire cone
    }

    [Fact]
    public void TheAiScoresWeapons()
    {
        Combatant c = Fighter("c", 2, 0, 0, str: 18);
        var b = new Battle([c], new OpenField(5, 5), new ScriptedDice());
        // 1d8, + 10 (metal counts as magic, as in the original), Str 18 +4, two halves.
        Assert.Equal((8 + 10 + 4) * 2, b.WeaponScore(c, Sword()));
        Assert.Equal((8 + 10 + 4 + 4) * 2, b.WeaponScore(c, Sword(bonus: 1))); // + 4 × the bonus
    }

    [Fact]
    public void TheRulesChooseTargetsAndSteps()
    {
        Combatant monster = Fighter("monster", 2, 0, 0), near = Fighter("near", 1, 1, 0), far = Fighter("far", 1, 0, 9);
        var nearest = new Battle([monster, near, far], new OpenField(20, 20), new ScriptedDice(Highest),
            new CombatRules { Targeting = TargetChoice.Nearest });
        Assert.Same(near, nearest.PickTarget(monster, 24, sight: false));

        var guardOnly = new Battle([monster, near], new OpenField(20, 20), new ScriptedDice(Highest), new CombatRules { AiSteps = [AiStep.Guard] });
        guardOnly.StartRound();
        TurnOf(guardOnly, monster);
        guardOnly.RunAi(monster);
        Assert.True(monster.Guarding);
        Assert.Equal(30, near.Vitals.Hp);
    }

    [Fact]
    public void AComputerFightEnds()
    {
        Combatant a = Fighter("a", 1, 0, 0, hp: 20), z = Fighter("z", 2, 6, 6, hp: 20);
        a.ComputerControlled = true;
        a.Weapon = Sword();
        var b = new Battle([a, z], new OpenField(20, 20), new BorlandDice(new BorlandRandom(7)));
        b.Fight(50);
        Assert.True(b.Over);
        Assert.True(a.Alive ^ z.Alive);
    }

    // -- the tables -------------------------------------------------------------------------------------------

    [Fact]
    public void SpellRecordsGiveDamageDurationAndRange()
    {
        SpellRules s = SpellRules.Default;
        Assert.Equal((196, 45, 34), (s.Effects.Count, s.Templates.Count, s.Powers.Count));
        Assert.Equal((5, 0), s.Effect(28)!.DamageAt(5)); // Fireball: 5d6
        Assert.Equal((10, 0), s.Effect(28)!.DamageAt(14)); // at most 10d6
        Assert.Equal((3, 3), s.Effect(9)!.DamageAt(5)); // Magic Missile: 3 × (1d4 + 1)
        Assert.Equal(-1, s.Effect(1)!.Unit); // Armor has charges
        Assert.Equal(1, s.Effect(11)!.RangeCells(1)); // Shocking Grasp: touch
        var b = new Battle([], new OpenField(1, 1), new ScriptedDice());
        Assert.Equal(60 * 2 * 5, b.Duration(s.Effect(31)!, 5)); // Hold Person: 2 rounds a level
    }

    [Fact]
    public void CombatAndSpellRulesRoundTripAsJsonAndBadOnesAreRefused()
    {
        string path = Path.Combine(Path.GetTempPath(), $"darksun-combat-{Guid.NewGuid():N}.json");
        try
        {
            string json = CombatRules.Default.ToJson();
            Assert.Contains("\"Targeting\": \"Random\"", json);
            File.WriteAllText(path, json.Replace("\"Random\"", "\"Weakest\""));
            Assert.Equal(TargetChoice.Weakest, CombatRules.Read(path).Targeting);
            File.WriteAllText(path, json);
            Assert.Equal(json, CombatRules.Read(path).ToJson());
            File.WriteAllText(path, json.Replace("\"SightRange\": 24", "\"SightRange\": 0"));
            Assert.Throws<InvalidDataException>(() => CombatRules.Read(path));

            string spells = SpellRules.Default.ToJson();
            File.WriteAllText(path, spells);
            SpellRules back = SpellRules.Read(path);
            Assert.Equal(SpellRules.Default.Effects, back.Effects);
            Assert.Equal(spells, back.ToJson());
            File.WriteAllText(path, spells.Replace("\"Number\": 2,", "\"Number\": 3,"));
            Assert.Throws<InvalidDataException>(() => SpellRules.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
