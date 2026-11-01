using DarkSun.Game.Characters;
using DarkSun.Game.Magic;
using DarkSun.Game.World;

namespace DarkSun.Game.Tests;

public class SpellcastingTests
{
    private static readonly Spellcasting Magic = new(CharacterRules.Default);

    /// <summary>The tabletop rules mod that comes with the game (<c>engine/game/mods/Tabletop</c>).</summary>
    internal static CharacterRules Tabletop => CharacterRules.Read(TabletopFile("characters.json"));

    internal static string TabletopFile(string name) => BundledModFile("Tabletop", Path.Combine("rules", name));

    /// <summary>A file of a mod that comes with the game (<c>engine/game/mods/<paramref name="mod"/></c>).</summary>
    internal static string BundledModFile(string mod, string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, "engine", "game", "mods", mod, name);
            if (File.Exists(path))
            {
                return path;
            }
        }
        throw new FileNotFoundException(name);
    }

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
        Assert.Equal([(SpellKind.Priest, 1), (SpellKind.Priest, 2)], Magic.Levels(c));

        // 6798:0B32: the Wisdom profile adds even where the level gives none.
        Character novice = Cleric(Sphere.Water, wis: 16, xp: 0);
        Assert.Equal(2, Magic.Slots(novice, SpellKind.Priest, 2));
        Assert.Equal(1, Magic.Slots(Cleric(Sphere.Water, wis: 17), SpellKind.Priest, 3)); // level 3: none of its own, +1 at 17

        // The tabletop rules: a bonus only where the level gives spells.
        var tabletop = new Spellcasting(SpellcastingTests.Tabletop);
        Assert.Equal(0, tabletop.Slots(novice, SpellKind.Priest, 2));
        Assert.Equal(4, tabletop.Slots(wise, SpellKind.Priest, 1));
    }

    [Fact]
    public void SlotsFollowTheOriginalsFormula()
    {
        // 6798:0B32: a preserver's maximum 5, a priest's 9, a ranger's 3 with offset 3 and parity 1.
        var preserver = new Character { Classes = [CharacterClass.Preserver], Rolled = [12, 12, 12, 17, 12, 12] };
        int[] Row(Character c, SpellKind kind, int xp)
        {
            c.Experience = xp;
            return [.. Enumerable.Range(1, 5).Select(l => Magic.Slots(c, kind, l))];
        }
        Assert.Equal([2, 2, 0, 0, 0], Row(preserver, SpellKind.Wizard, 10000)); // level 4
        Assert.Equal([3, 2, 1, 0, 0], Row(preserver, SpellKind.Wizard, 20000)); // level 5
        var ranger = new Character { Classes = [CharacterClass.Ranger], Rolled = [12, 12, 12, 12, 12, 12] };
        Assert.Equal([1, 0, 0, 0, 0], Row(ranger, SpellKind.Priest, 150000)); // level 8
        // Cleric and ranger: both classes' slots.
        var both = new Character { Classes = [CharacterClass.Cleric, CharacterClass.Ranger], Sphere = Sphere.Water, Rolled = [12, 12, 12, 12, 12, 12] };
        Assert.Equal(Row(new Character { Classes = [CharacterClass.Cleric], Sphere = Sphere.Water, Rolled = [12, 12, 12, 12, 12, 12] }, SpellKind.Priest, 150000)[0]
            + 1, Row(both, SpellKind.Priest, 300000)[0]);
        Assert.Equal([3, 2, 0, 0, 0], new[] { 1, 2, 3, 4, 5 }.Select(l => new Spellcasting(Tabletop).Slots(new Character
            { Classes = [CharacterClass.Preserver], Rolled = [12, 12, 12, 17, 12, 12], Experience = 10000 }, SpellKind.Wizard, l)));
    }

    [Fact]
    public void AnyKnownSpellIsCastWhileItsLevelHasCastsAndARestFillsThem()
    {
        Character c = Cleric(Sphere.Water);
        Assert.Equal(2, Magic.CastsLeft(c, SpellKind.Priest, 1)); // two 1st-level casts, nothing memorized
        Assert.True(Magic.CanCast(c, 72) && Magic.CanCast(c, 70) && Magic.CanCast(c, 87)); // Cure Light Wounds, Bless, Hold Person
        Assert.False(Magic.CanCast(c, 9)); // a wizard spell

        var target = new Vitals { Hp = 2, MaxHp = 20, Status = Vitals.Down };
        string result = Magic.CastOutside(c, 72, target, new Random(1));
        Assert.StartsWith("HEALED", result);
        Assert.True(target.Hp > 2);
        Assert.Equal(Vitals.Well, target.Status);
        Assert.Equal(1, Magic.CastsLeft(c, SpellKind.Priest, 1));
        int second = Magic.CastsLeft(c, SpellKind.Priest, 2);
        Assert.Equal("ONLY IN COMBAT", Magic.CastOutside(c, 87, target, new Random(1)));
        Assert.Equal(second, Magic.CastsLeft(c, SpellKind.Priest, 2)); // nothing happened: no cast used
        Magic.CastOutside(c, 72, new Vitals { Hp = 1, MaxHp = 20 }, new Random(1));
        Assert.Equal(0, Magic.CastsLeft(c, SpellKind.Priest, 1));
        Assert.False(Magic.CanCast(c, 70)); // the level's casts are shared
        Assert.Equal("YOU NEED TO REST", Magic.CastOutside(c, 72, target, new Random(1)));
        Assert.Equal("NO SUCH SPELL TO CAST", Magic.CastOutside(c, 9, target, new Random(1)));
        Assert.DoesNotContain(Magic.Castable(c), s => s.Level == 1);

        Spellcasting.Rest(c);
        Assert.Equal(2, Magic.CastsLeft(c, SpellKind.Priest, 1));
    }

    [Fact]
    public void APreserverLearnsIntoItsBook()
    {
        var p = new Character { Classes = [CharacterClass.Preserver], Rolled = [12, 12, 12, 9, 12, 12], Experience = 3000 };
        Assert.Empty(Magic.Known(p, SpellKind.Wizard, 1));
        Assert.Contains(Spellcasting.Get(9)!, Magic.Learnable(p, 1));
        Assert.Equal("IS LEARNED!", Magic.Learn(p, 9));
        Assert.Equal("YOU ALREADY KNOW THIS SPELL", Magic.Learn(p, 9));
        Assert.Equal("CANNOT LEARN THIS SPELL", Magic.Learn(p, 28)); // Fireball: 3rd level
        Assert.Equal(Spellcasting.All.Count, Magic.SpellbookSize(p)); // DSUN.EXE: no limit
        Assert.True(Magic.CanCast(p, 9));

        // The tabletop rules: six a level at Intelligence 9.
        var tabletop = new Spellcasting(Tabletop);
        Assert.Equal(6, tabletop.SpellbookSize(p));
        foreach (int n in new[] { 2, 3, 4, 5, 6 })
        {
            tabletop.Learn(p, n);
        }
        Assert.Equal("THE SPELLBOOK IS FULL AT THIS LEVEL", tabletop.Learn(p, 7));
    }

    [Fact]
    public void PsionicistsUseTheirPowersAndSpendStrength()
    {
        // Str, Dex, Con 16, Int 17, Wis 18, Cha (a human: no adjustments).
        var psi = new Character { Race = Race.Human, Classes = [CharacterClass.Psionicist], Disciplines = [Discipline.Psychometabolism], Experience = 3000, Powers = [152], // Cell Adjustment
            Rolled = [10, 10, 16, 17, 18, 10] };
        Assert.Equal(2, Magic.Level(psi, CharacterClass.Psionicist));
        Assert.Equal(1 + 2 + 26 + (10 + 3) * 1, Magic.MaxPsp(psi)); // 8E77:0162: Con, Int, Wis, then a level past the first
        var hurt = new Vitals { Hp = 3, MaxHp = 10 };
        Assert.StartsWith("HEALED", Magic.CastOutside(psi, 152, hurt, new Random(2)));
        Assert.Equal(42 - CharacterRules.Default.PowerCost, Magic.Psp(psi));

        // Anyone else: the abilities' part and 4 a level; scores under 15 add nothing.
        var fighter = new Character { Race = Race.Human, Classes = [CharacterClass.Fighter], Disciplines = [Discipline.Telepathy], Experience = 3000, Rolled = [17, 10, 15, 10, 14, 10] };
        Assert.Equal(0 + 4 * new CharacterCreation(CharacterRules.Default, CharacterNames.Empty, new Random(0)).Levels(fighter).Max(), Magic.MaxPsp(fighter));
        // Past 22 a score reads on into the next table, as DSUN.EXE's do: Intelligence 23 gives 20.
        var genius = new Character { Race = Race.Human, Classes = [CharacterClass.Fighter], Disciplines = [Discipline.Telepathy], Rolled = [17, 10, 10, 23, 10, 10] };
        Assert.Equal(20 + 4 * new CharacterCreation(CharacterRules.Default, CharacterNames.Empty, new Random(0)).Levels(genius).Max(), Magic.MaxPsp(genius));
    }
}
