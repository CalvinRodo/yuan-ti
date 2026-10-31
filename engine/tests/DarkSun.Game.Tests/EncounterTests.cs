using DarkSun.Game.Characters;
using DarkSun.Game.Combat;
using DarkSun.Game.Rules;
using DarkSun.Game.World;

namespace DarkSun.Game.Tests;

/// <summary>Starting a fight in a region, party members from their sheets, and the end.</summary>
public class EncounterTests
{
    private static readonly CharacterRules Rules = CharacterRules.Default;

    private static Character Fighter(string name) => new()
    {
        Name = name, Race = Race.Human, Classes = [CharacterClass.Fighter], Rolled = [17, 14, 15, 10, 10, 10], Experience = 0,
        ComputerControl = true,
    };

    private static (GameWorld World, WorldObject Templar) Hostile(int allegiance)
    {
        GameWorld world = GameWorldTests.World();
        WorldObject templar = world.Select(-100).Single();
        templar.Vitals.Status = Vitals.Well;
        templar.Vitals.Hp = templar.Vitals.MaxHp = 6;
        templar.Allegiance = allegiance;
        foreach (WorldObject p in world.Party)
        {
            p.Vitals.Hp = p.Vitals.MaxHp = 20;
            p.Vitals.Status = Vitals.Well;
        }
        return (world, templar);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheArenaBoutIsFoughtWithoutTheParty(bool celgorFalls)
    {
        GameWorld world = GameWorldTests.Arena();
        foreach (WorldObject o in world.Objects.Where(o => o.Kind != ObjectKind.Thing))
        {
            o.Vitals.Hp = o.Vitals.MaxHp = 20;
            o.Vitals.Status = Vitals.Well;
        }
        world.First(-100)!.Allegiance = 0; // the templar stays out of it
        WorldObject celgor = world.StageArenaBout()!;
        celgor.Vitals.Hp = celgor.Vitals.MaxHp = 20;
        foreach (WorldObject o in world.Objects.Where(o => o.Allegiance == 2))
        {
            o.Vitals.Hp = o.Vitals.MaxHp = 20;
            o.Vitals.Status = Vitals.Well;
        }
        celgor.Vitals.Status = Vitals.Well;

        // 612E:281F: the range is from the mage, 12 cells and more from the party.
        Battle b = Encounter.Start(world, i => Fighter($"P{i}"), new ScriptedDice(), Rules)!;

        Assert.NotNull(b);
        Assert.DoesNotContain(b.Combatants, c => c.PartyMember);
        Assert.Equal(["Defiler", "Rampager", "Slig", "Slig"], b.Combatants.Select(c => c.Name).Order());
        Assert.True(b.Combatants.Single(c => c.Name == "Defiler").IsEnemyOf(b.Combatants.Single(c => c.Name == "Rampager")));

        if (celgorFalls)
        {
            celgor.Vitals.Hp = 0;
            celgor.Vitals.Status = Vitals.Dead;
            world.Fell(celgor);
        }
        Assert.False(Encounter.End(b, world)); // watched, not lost
        Assert.All(world.Party, p => Assert.Equal(0, p.Allegiance));
        Assert.DoesNotContain(world.Objects, o => o.Id is -296 or -171 or -269);
        Assert.NotNull(world.First(-100)); // spectators are not part of the cleanup
    }

    [Fact]
    public void OrdinaryFightsDoNotRunArenaCleanup()
    {
        var (world, creature) = Hostile(2);
        creature.Id = -171;
        Battle battle = Encounter.Start(world, _ => null, new ScriptedDice(), Rules)!;

        Encounter.End(battle, world);

        Assert.Same(creature, world[creature.Index]);
    }

    [Fact]
    public void AWoundInAFightKeepsANamedCreaturesStatusForGetStatus()
    {
        // 612E:18DA: each wound puts the creature's status in DS:0333 under its name (1–50).
        var (world, creature) = Hostile(2);
        creature.Id = -14; // Balkazar
        Battle battle = Encounter.Start(world, _ => null, new ScriptedDice(), Rules)!;
        Combatant hurt = battle.Combatants.Single(c => c.Source == creature);
        Assert.Equal(0, world.Status(-14));

        battle.Hurt(null, hurt, 2);
        Assert.Equal(Vitals.Well, world.Status(-14));
        battle.Hurt(null, hurt, 20);
        Assert.Equal(Vitals.Dead, world.Status(-14));
    }

    [Fact]
    public void NoEnemyNoFight()
    {
        var (world, _) = Hostile(1);
        Assert.Null(Encounter.Start(world, _ => null, new ScriptedDice(), Rules));
    }

    [Fact]
    public void AttackingANeutralCreatureMakesItAnEnemy()
    {
        var (world, templar) = Hostile(0);
        Battle? b = Encounter.Start(world, _ => null, new ScriptedDice(), Rules, attacked: templar);
        Assert.NotNull(b);
        Assert.Equal(2, templar.Allegiance);
        Assert.Equal(5, b.Combatants.Count);
    }

    [Fact]
    public void PartyMembersComeFromTheirSheets()
    {
        var (world, _) = Hostile(2);
        Character[] sheets = [Fighter("A"), Fighter("B"), Fighter("C"), Fighter("D")];
        Battle b = Encounter.Start(world, i => sheets[i], new ScriptedDice(), Rules)!;

        Combatant a = b.Combatants.Single(c => c.Name == "A");
        Assert.True(a.PartyMember);
        Assert.Same(world[0]!.Vitals, a.Vitals);
        Assert.Equal(20, a.Thac0);
        Assert.Equal([14, 16, 15, 17, 17], a.Saves);
        Assert.Equal(3, a.AttackHalves); // a warrior from level 1
        Assert.Equal([(9, 1)], a.Classes);
    }

    [Fact]
    public void AFightRunsToItsEndAndTheFallenLeaveTheMap()
    {
        var (world, templar) = Hostile(2);
        Character[] sheets = [Fighter("A"), Fighter("B"), Fighter("C"), Fighter("D")];
        Battle b = Encounter.Start(world, i => sheets[i], new BorlandDice(new BorlandRandom(7)), Rules)!;

        b.Fight(30);
        Assert.True(b.Over);
        Assert.Null(world[templar.Index]); // fell and left the region
        Encounter.End(b);
    }

    [Fact]
    public void SpellsCastGoBackToTheSheet()
    {
        var (world, _) = Hostile(2);
        var mage = new Character { Name = "M", Race = Race.Human, Classes = [CharacterClass.Preserver], Rolled = [10, 14, 12, 16, 10, 10], Spellbook = [9, 1], Experience = 2500 };
        Battle b = Encounter.Start(world, i => i == 0 ? mage : null, new ScriptedDice(), Rules)!;
        Combatant m = b.Combatants.Single(c => c.Name == "M");
        // Level 2: two 1st-level casts, shared by every 1st-level spell in the book.
        Assert.Equal([1, 9], m.Spells);
        Assert.Equal(2, m.CastsOf(9));

        b.StartRound();
        b.Cast(m, 9, 1, b.Combatants.Single(c => !c.PartyMember).Cell);
        Assert.Equal((1, 1), (m.CastsOf(9), m.CastsOf(1)));
        b.Cast(m, 1, 1, b.Combatants.Single(c => !c.PartyMember).Cell);
        Assert.Empty(m.Spells);
        b.SyncSheets();
        Assert.Equal([9, 1], mage.Cast);
    }
}
