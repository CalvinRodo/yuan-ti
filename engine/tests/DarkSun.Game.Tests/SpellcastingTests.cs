using DarkSun.Game.Characters;
using DarkSun.Game.Magic;
using DarkSun.Game.World;

namespace DarkSun.Game.Tests;

public class SpellcastingTests
{
    private static readonly Spellcasting Magic = new(CharacterRules.Default);

    private static Character Cleric(Sphere sphere, int wis = 12, int xp = 3000) =>
        new() { Classes = [CharacterClass.Cleric], Sphere = sphere, Rolled = [12, 12, 12, 12, wis, 12], Experience = xp };

    [Fact]
    public void TheTableGivesLevelsAndSpheres()
    {
        Assert.Equal((1, SpellKind.Wizard), (Spellcasting.Get(9)!.Level, Spellcasting.Get(9)!.Kind)); // Magic Missile
        Assert.Equal((5, SpellKind.Priest), (Spellcasting.Get(136)!.Level, Spellcasting.Get(136)!.Kind)); // Raise Dead
        Assert.Equal(Discipline.Telepathy, Spellcasting.Get(164)!.Discipline); // Ego Whip
        Assert.Equal(21008, Spellcasting.Get(9)!.Icon);
        Assert.Equal(3000, Spellcasting.Get(139)!.Icon); // Detonate
        Assert.Equal(3033, Spellcasting.Get(172)!.Icon); // Thought Shield

        Spell magicalStone = Spellcasting.Get(76)!;
        Assert.True(Spellcasting.Allows(Cleric(Sphere.Earth), magicalStone));
        Assert.False(Spellcasting.Allows(Cleric(Sphere.Fire), magicalStone));
        Assert.True(Spellcasting.Allows(Cleric(Sphere.Fire), Spellcasting.Get(72)!)); // Cure Light Wounds: everyone's
        Assert.False(Spellcasting.Allows(Cleric(Sphere.Water), Spellcasting.Get(108)!)); // Abjure: the druids'
        Assert.True(Spellcasting.Allows(new Character { Classes = [CharacterClass.Druid], Sphere = Sphere.Water }, Spellcasting.Get(108)!));
        Assert.False(Spellcasting.Allows(Cleric(Sphere.Air), Spellcasting.Get(9)!));
    }

    [Fact]
    public void SlotsFollowLevelAndWisdom()
    {
        Character c = Cleric(Sphere.Water); // level 3 (3000 experience)
        Assert.Equal(2, Magic.Slots(c, SpellKind.Priest, 1));
        Assert.Equal(1, Magic.Slots(c, SpellKind.Priest, 2));
        Assert.Equal(0, Magic.Slots(c, SpellKind.Priest, 3));
        Assert.Equal(0, Magic.Slots(c, SpellKind.Wizard, 1));
        Character wise = Cleric(Sphere.Water, wis: 16);
        Assert.Equal(4, Magic.Slots(wise, SpellKind.Priest, 1)); // +1 at 13 and 14
        Assert.Equal(3, Magic.Slots(wise, SpellKind.Priest, 2)); // +1 at 15 and 16
        Character novice = Cleric(Sphere.Water, wis: 16, xp: 0);
        Assert.Equal(0, Magic.Slots(novice, SpellKind.Priest, 2)); // no bonus without a 2nd-level spell
        Assert.Equal([(SpellKind.Priest, 1), (SpellKind.Priest, 2)], Magic.Levels(c));
    }

    [Fact]
    public void MemorizeCastAndRest()
    {
        Character c = Cleric(Sphere.Water);
        Assert.True(Magic.Memorize(c, 72));
        Assert.True(Magic.Memorize(c, 72));
        Assert.False(Magic.Memorize(c, 72)); // two 1st-level slots
        Assert.False(Magic.Memorize(c, 9)); // a wizard spell
        Assert.True(Magic.Memorize(c, 87)); // Hold Person, 2nd level

        var target = new Vitals { Hp = 2, MaxHp = 20, Status = Vitals.Down };
        string result = Magic.CastOutside(c, 72, target, new Random(1));
        Assert.StartsWith("HEALED", result);
        Assert.True(target.Hp > 2);
        Assert.Equal(Vitals.Well, target.Status);
        Assert.Equal(1, Spellcasting.Ready(c, 72));
        Assert.Equal("NO SUCH SPELL TO CAST", Magic.CastOutside(c, 70, target, new Random(1))); // Bless: not memorized
        Assert.Equal("ONLY IN COMBAT", Magic.CastOutside(c, 87, target, new Random(1)));
        Magic.CastOutside(c, 72, new Vitals { Hp = 1, MaxHp = 20 }, new Random(1));
        Assert.Equal("NO SUCH SPELL TO CAST", Magic.CastOutside(c, 72, target, new Random(1)));

        Spellcasting.Rest(c);
        Assert.Equal(2, Spellcasting.Ready(c, 72));
        Assert.True(Magic.Forget(c, 72));
        Assert.Equal([72, 87], c.Memorized);
    }

    [Fact]
    public void APreserverLearnsIntoItsBook()
    {
        var p = new Character { Classes = [CharacterClass.Preserver], Rolled = [12, 12, 12, 9, 12, 12], Experience = 3000 };
        Assert.Empty(Magic.Memorizable(p, SpellKind.Wizard, 1));
        Assert.Contains(Spellcasting.Get(9)!, Magic.Learnable(p, 1));
        Assert.Equal("IS LEARNED!", Magic.Learn(p, 9));
        Assert.Equal("YOU ALREADY KNOW THIS SPELL", Magic.Learn(p, 9));
        Assert.Equal("CANNOT LEARN THIS SPELL", Magic.Learn(p, 28)); // Fireball: 3rd level
        Assert.Equal(6, Magic.SpellbookSize(p)); // Intelligence 9
        foreach (int n in new[] { 2, 3, 4, 5, 6 })
        {
            Magic.Learn(p, n);
        }
        Assert.Equal("THE SPELLBOOK IS FULL AT THIS LEVEL", Magic.Learn(p, 7));
        Assert.True(Magic.Memorize(p, 9));
    }

    [Fact]
    public void PsionicistsPickPowersAndSpendStrength()
    {
        var psi = new Character { Classes = [CharacterClass.Psionicist], Disciplines = [Discipline.Psychometabolism], Experience = 3000 };
        Assert.Equal(22, Magic.MaxPsp(psi)); // level 2
        Assert.Equal(4, Magic.PowersAllowed(psi));
        Assert.Equal("NOT IN THIS CHARACTER'S DISCIPLINES", Magic.Pick(psi, 139));
        Assert.Equal("IS LEARNED", Magic.Pick(psi, 152)); // Cell Adjustment
        var hurt = new Vitals { Hp = 3, MaxHp = 10 };
        Assert.StartsWith("HEALED", Magic.CastOutside(psi, 152, hurt, new Random(2)));
        Assert.Equal(22 - CharacterRules.Default.PowerCost, Magic.Psp(psi));

        var fighter = new Character { Classes = [CharacterClass.Fighter], Disciplines = [Discipline.Telepathy] };
        Assert.Equal((10, 1), (Magic.MaxPsp(fighter), Magic.PowersAllowed(fighter)));
    }
}
