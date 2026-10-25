using DarkSun.Game.Characters;
using DarkSun.Game.Combat;
using DarkSun.Game.Magic;
using DarkSun.Game.Movement;
using DarkSun.Game.World;

namespace DarkSun.Game.Tests;

/// <summary>Level-ups, casting on the map and effects that outlast a fight, being hit while casting, the gladiator's armour.</summary>
public class LevellingAndMapMagicTests
{
    private const int Bless = 70;

    private static readonly CharacterRules Rules = CharacterRules.Default;

    private static Character Hero(string name, params CharacterClass[] classes) => new()
    {
        Name = name, Race = Race.Human, Classes = [.. classes], Rolled = [15, 14, 15, 15, 15, 10], Hp = 8,
    };

    // -- YOU WERE HIT ---------------------------------------------------------------------------------

    [Fact]
    public void ACasterHitThisRoundCantCastUntilTheNext()
    {
        Combatant mage = CombatTests.Fighter("mage", 1, 5, 5), foe = CombatTests.Fighter("foe", 2, 10, 5, hp: 50);
        var b = new Battle([mage, foe], new OpenField(30, 30), new ScriptedDice());
        b.StartRound();
        b.Hurt(foe, mage, 1);

        Assert.True(mage.WasHit);
        Assert.False(b.Cast(mage, Bless, 5, mage.Cell));
        Assert.True(b.Cast(mage, Bless, 5, mage.Cell, ownSpell: false)); // a wand's spell isn't spoiled
        b.StartRound();
        Assert.False(mage.WasHit);
        Assert.True(b.Cast(mage, Bless, 5, mage.Cell));
    }

    // -- lasting effects ---------------------------------------------------------------------------

    [Fact]
    public void SpellsOutlastAFightOnTheGameClockAndPowersEndWithIt()
    {
        Character sheet = Hero("Hero", CharacterClass.Fighter);
        Combatant c = CombatTests.Fighter("hero", 1, 5, 5);
        c.Effects.Add(new ActiveEffect(Bless, Condition.Blessed, 5, null) { EndsAt = 360 });
        c.Effects.Add(new ActiveEffect(150, Condition.Biofeedback, 5, null)); // a power

        LastingEffects.Keep(sheet, c, clock: 1000, battleClock: 120);
        Assert.Equal([new LastingEffect(Bless, Condition.Blessed, 5, EndsAt: 1240)], sheet.Lasting);

        Combatant next = CombatTests.Fighter("hero", 1, 5, 5);
        LastingEffects.Apply(sheet, next, clock: 1100);
        Assert.Equal(140, next.Effects.Single().EndsAt); // 140 game seconds left

        LastingEffects.Apply(sheet, CombatTests.Fighter("hero", 1, 5, 5), clock: 1300);
        Assert.Empty(sheet.Lasting); // over
    }

    [Fact]
    public void BlessCastOnTheMapLastsIntoTheNextFight()
    {
        GameWorld world = GameWorldTests.World();
        foreach (WorldObject p in world.Party)
        {
            (p.Vitals.Hp, p.Vitals.MaxHp, p.Vitals.Status) = (20, 20, Vitals.Well);
        }
        var sheets = world.Party.ToDictionary(p => p.Index, p => Hero($"P{p.Index}", CharacterClass.Cleric));
        Character? SheetOf(int slot) => sheets.GetValueOrDefault(slot);
        int damaging = Enumerable.Range(1, Spellcasting.FirstPower - 1).First(n => SpellRules.Default.Effect(n) is { Damaging: true });

        Assert.Null(Encounter.CastOnMap(world, SheetOf, 0, 0, damaging, new ScriptedDice(), Rules)); // only in a fight
        Assert.Equal("IT TAKES EFFECT", Encounter.CastOnMap(world, SheetOf, 0, 0, Bless, new ScriptedDice(), Rules));
        Assert.Contains(Bless, sheets[0].Cast);
        Assert.Contains(sheets[0].Lasting, e => e.Condition == Condition.Blessed && e.EndsAt > world.Clock);

        WorldObject templar = world.Select(-100).Single();
        (templar.Vitals.Hp, templar.Vitals.MaxHp, templar.Vitals.Status, templar.Allegiance) = (6, 6, Vitals.Well, 2);
        Battle fight = Encounter.Start(world, SheetOf, new ScriptedDice(), Rules)!;
        Assert.True(fight.Combatants.First(c => c.Sheet == sheets[0]).Has(Condition.Blessed));
    }

    // -- levels ------------------------------------------------------------------------------------

    [Fact]
    public void ALevelGainedRollsHitPointsAndAPsionicistPicksAPower()
    {
        var creation = new CharacterCreation(Rules, CharacterNames.Empty, new Random(1));
        Character psion = Hero("Psion", CharacterClass.Psionicist);
        psion.Experience = 2000;
        Assert.True(creation.NoteLevels(psion)); // level 1, noted
        psion.Experience = 2200; // level 2

        IReadOnlyList<LevelGain> gains = creation.CheckLevels(psion);

        Assert.Equal(CharacterClass.Psionicist, gains.Single().Class);
        Assert.Equal(2, gains.Single().Level);
        Assert.InRange(gains.Single().HitPoints, 1, 6); // a d6
        Assert.Equal(1, psion.PowersToChoose);
        Assert.True(psion.Hp > 8);
        Assert.Empty(creation.CheckLevels(psion)); // once

        Assert.Equal("Psion is 2nd level Psionic", CharacterCreation.LevelMessage("Psion", gains[0], "Psionic"));
        Assert.Equal("Bartholomew gains a level", CharacterCreation.LevelMessage("Bartholomew", gains[0], "Psionicist")); // over 29 letters
    }

    [Fact]
    public void AHumansNewClassGivesNoHitPointsUntilItPassesTheOldOne()
    {
        var creation = new CharacterCreation(Rules, CharacterNames.Empty, new Random(1));
        Character dual = Hero("Dual", CharacterClass.Thief);
        dual.Former.Add(new FormerClass(CharacterClass.Fighter, 3));
        Assert.Equal(0, creation.LevelHitPoints(dual, CharacterClass.Thief, 2));
        Assert.Equal(0, creation.LevelHitPoints(dual, CharacterClass.Thief, 3));
        Assert.InRange(creation.LevelHitPoints(dual, CharacterClass.Thief, 4), 1, 6);
    }

    [Fact]
    public void APsionicistLearnsANewPowerOrEnhancesOne()
    {
        Character psion = Hero("Psion", CharacterClass.Psionicist);
        psion.PowersToChoose = 2;
        int power = Spellcasting.PowersToStudy(psion, Discipline.Telepathy)[0];

        Assert.Equal("IS LEARNED", Spellcasting.StudyPower(psion, power));
        Assert.Equal(1, psion.PowerRank(power));
        Assert.Equal("IS ENHANCED", Spellcasting.StudyPower(psion, power));
        Assert.Equal(2, psion.PowerRank(power));
        Assert.Equal(0, psion.PowersToChoose);
    }

    // -- the gladiator's armour ----------------------------------------------------------------------

    [Fact]
    public void AGladiatorsArmourClassImprovesAtLevelFive()
    {
        Assert.Equal(10, CharacterSheet.BaseArmorClassOf(Race.Human, [(CharacterClass.Gladiator, 4)]));
        Assert.Equal(9, CharacterSheet.BaseArmorClassOf(Race.Human, [(CharacterClass.Gladiator, 5)]));
        Assert.Equal(10, CharacterSheet.BaseArmorClassOf(Race.Human, [(CharacterClass.Fighter, 9)]));
        Assert.Equal(4, CharacterSheet.BaseArmorClassOf(Race.ThriKreen, [(CharacterClass.Gladiator, 6)]));
    }
}
