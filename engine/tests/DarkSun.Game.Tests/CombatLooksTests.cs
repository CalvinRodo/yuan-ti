using DarkSun.Data.Animation;
using DarkSun.Data.Items;
using DarkSun.Data.Regions;
using DarkSun.Game.Characters;
using DarkSun.Game.Combat;
using DarkSun.Game.Movement;
using DarkSun.Game.Rules;
using DarkSun.Game.World;
using static DarkSun.Game.Tests.CombatTests;

namespace DarkSun.Game.Tests;

/// <summary>The fight's end, attack animations, what spells look like, and the natural attacks.</summary>
public class CombatLooksTests
{
    [Fact]
    public void AtTheEndTheDownGetUpAndAFallenPartyIsSaidSo()
    {
        Combatant a = Fighter("a", 1, 5, 5) is var f ? new Combatant { Name = "a", Side = 1, PartyMember = true, Cell = f.Cell, Vitals = new Vitals { Hp = 0, MaxHp = 10, Status = Vitals.Down } } : null!;
        Combatant b = new() { Name = "b", Side = 1, PartyMember = true, Vitals = new Vitals { Hp = 7, MaxHp = 10 } };
        var battle = new Battle([a, b, Fighter("orc", 2, 9, 9)], new OpenField(20, 20), new ScriptedDice());
        a.Effects.Add(new ActiveEffect(31, Condition.Paralyzed, 5, a));

        Assert.False(battle.Finish());
        Assert.Equal((Vitals.Well, 1), (a.Vitals.Status, a.Vitals.Hp));
        Assert.False(a.Has(Condition.Paralyzed));

        var down = new Combatant { Name = "c", Side = 1, PartyMember = true, Vitals = new Vitals { Hp = 0, MaxHp = 10, Status = Vitals.Down } };
        Assert.True(new Battle([down], new OpenField(5, 5), new ScriptedDice()).Finish()); // got up, but the party had fallen
        Assert.Equal(1, down.Vitals.Hp);
    }

    [Fact]
    public void AWalkerPlaysItsAttackAndStandsAgain()
    {
        // Slot facing × 8 + 4 (the test script's action slots all point at the walk).
        var walker = new Walker(1, WalkerTests.Script(), [(10, 20), (12, 22), (14, 24)], new Cell(0, 0), Footprint.OneCell, new Occupancy());
        static bool Open(int x, int y) => false;
        walker.Update(0, Open);
        Assert.True(walker.PlayAction(Scmd.AttackAction, Direction.East));
        Assert.True(walker.IsActing);
        for (long t = 1; t < Walker.MaxActionTicks + 10; t++)
        {
            walker.Update(t, Open);
        }
        Assert.False(walker.IsActing);
        Assert.Equal(new Cell(0, 0), walker.Cell);
    }

    [Fact]
    public void SpellsHaveTheirLooksAndWeaponsTheirMissiles()
    {
        SpellRules s = SpellRules.Default;
        SpellLook missile = s.LookOf(9)!;
        Assert.Equal((8007, 77, 4, 71, 8211, 97), (missile.CastGraphic, missile.CastSound, missile.Projectile, missile.FlightSound, missile.HitGraphic, missile.HitSound));
        Assert.Equal(8109, s.ProjectileOf(missile));
        Assert.Null(s.ProjectileOf(s.LookOf(2)!));
        Assert.Equal(8117, s.MissileOf(1010));
        Assert.Equal(8101, s.MissileOf(1234));
        Assert.Equal(8415, s.Templates[0].Graphic);
    }

    [Fact]
    public void BodyWeaponryAndBlowsWithoutAWeapon()
    {
        Combatant a = Fighter("a", 1, 5, 5), t = Fighter("t", 2, 6, 5, hp: 100, ac: 10);
        a.Effects.Add(new ActiveEffect(151, Condition.BodyWeaponry, 5, a));
        var b = new Battle([a, t], new OpenField(20, 20), new ScriptedDice((_, h) => h));
        b.StartRound();
        b.Attack(a, t);
        Assert.Equal(100 - 8, t.Vitals.Hp); // 2d4 at its highest, Str 10
    }

    [Fact]
    public void ThriKreenFightWithClawsAndABite()
    {
        GameWorld world = GameWorldTests.World();
        var kreen = new Character { Name = "K", Race = Race.ThriKreen, Classes = [CharacterClass.Fighter], Rolled = [17, 14, 15, 10, 10, 10] };
        var man = new Character { Name = "M", Race = Race.Human, Classes = [CharacterClass.Fighter], Rolled = [17, 14, 15, 10, 10, 10] };
        Combatant k = PartyCombatant.From(kreen, world[0]!, world.Items, CharacterRules.Default);
        Combatant m = PartyCombatant.From(man, world[1]!, world.Items, CharacterRules.Default);
        Assert.Equal([new NaturalAttack(8, 1, 4, 0), new NaturalAttack(2, 1, 4, 1)], k.NaturalAttacks);
        Assert.Equal(15, k.Move);
        Assert.Equal([new NaturalAttack(3, 1, 1, 0)], m.NaturalAttacks);
        Assert.Equal(12, m.Move);
    }
}
