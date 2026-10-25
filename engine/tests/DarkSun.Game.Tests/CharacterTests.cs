using DarkSun.Data.Items;
using DarkSun.Game.Characters;

namespace DarkSun.Game.Tests;

public class CharacterTests
{
    private static CharacterCreation Creation(int seed = 1) =>
        new(CharacterRules.Default, new CharacterNames(["Garn"], ["Shandra"], ["T'kir'taap"]), new Random(seed));

    [Fact]
    public void ACharactersClassesAreTheItemClassesItCanUse()
    {
        var c = new Character { Classes = [CharacterClass.Cleric, CharacterClass.Ranger], Sphere = Sphere.Fire };
        Assert.Equal(ItemClasses.FireCleric | ItemClasses.Ranger, c.ItemClasses);
        c.Sphere = null;
        Assert.Equal(ItemClasses.Clerics | ItemClasses.Ranger, c.ItemClasses);
        Assert.Equal(ItemClasses.Psionicist | ItemClasses.Thief, new Character { Classes = [CharacterClass.Psionicist, CharacterClass.Thief] }.ItemClasses);
        Assert.Equal(ItemClasses.None, new Character().ItemClasses);
    }

    [Theory]
    [InlineData(2000, new[] { 8, 9 })]
    [InlineData(3000, new[] { 8, 9, 10, 12 })] // the original's starting experience: level 2
    [InlineData(5000, new[] { 8, 9, 10, 12, 16, 23 })]
    public void ANewPreserverKnowsTheOriginalsStartingSpells(int experience, int[] spells)
    {
        // 6ECD:1977: Grease and Magic Missile; at level 2 Shield and Wall of Fog; at 3 Fog Cloud and Stinking Cloud.
        var cc = new CharacterCreation(CharacterRules.Default with { StartingExperience = experience }, new CharacterNames(["Garn"], ["Shandra"], ["T'kir'taap"]), new Random(1));
        Character c = cc.New();
        Assert.Empty(c.Spellbook);

        Assert.True(cc.ToggleClass(c, CharacterClass.Fighter));
        Assert.True(cc.ToggleClass(c, CharacterClass.Preserver));

        Assert.Equal(spells, c.Spellbook.Order());
    }

    [Fact]
    public void APreserversStartingSpellsFollowItsLevelAsClassesChange()
    {
        var cc = Creation();
        Character c = cc.New();
        cc.SetLooks(c, Race.Elf, Gender.Female);
        cc.ToggleClass(c, CharacterClass.Fighter);
        cc.ToggleClass(c, CharacterClass.Preserver);
        c.Spellbook.Add(1); // a wizard spell from elsewhere goes: the book is set again
        c.Spellbook.Add(70); // a priest spell stays

        Assert.True(cc.ToggleClass(c, CharacterClass.Fighter)); // fighter/preserver: 3000 each, still level 2
        Assert.Equal([8, 9, 10, 12, 70], c.Spellbook.Order());

        Assert.True(cc.ToggleClass(c, CharacterClass.Preserver)); // no longer a preserver: the book is left as it is
        Assert.Equal([8, 9, 10, 12, 70], c.Spellbook.Order());
    }

    [Fact]
    public void ANewCharacterIsGivenTheOriginalsPowersNotAChoice()
    {
        // 6ECD:1871: four of the discipline's powers and 169; a psionicist those of its level, whatever it knew.
        var cc = Creation();
        Character c = cc.New();
        Assert.Empty(c.Powers); // no discipline chosen yet: the player picks it (the guide: unchecked by default)
        cc.ChooseDiscipline(c, Discipline.Psychokinesis);
        Assert.Equal([139, 142, 143, 144, 169], c.Powers); // a fighter, psychokinesis
        cc.ChooseDiscipline(c, Discipline.Telepathy);
        Assert.Equal([162, 164, 170, 166, 169], c.Powers);

        c.Powers.Add(152);
        c.PowerRanks[162] = 4;
        Assert.True(cc.ToggleClass(c, CharacterClass.Fighter)); // a human has one class: off, then the psionicist's
        Assert.True(cc.ToggleClass(c, CharacterClass.Psionicist)); // 3000: psionicist level 2
        Assert.Equal(2, cc.LevelIn(c, CharacterClass.Psionicist));
        Assert.Equal([139, 147, 162, 144, 170], c.Powers);
        Assert.Empty(c.PowerRanks);

        cc = new CharacterCreation(CharacterRules.Default with { StartingExperience = 1000 }, new CharacterNames(["Garn"], ["Shandra"], ["T'kir'taap"]), new Random(1));
        c = cc.New();
        cc.ToggleClass(c, CharacterClass.Fighter);
        cc.ToggleClass(c, CharacterClass.Psionicist);
        Assert.Equal([139, 147, 162, 144, 169], c.Powers);
    }

    [Fact]
    public void LevelsFollowTheOriginalsExperienceTable()
    {
        // 38F0:027C: a gladiator levels as a fighter; druids, rangers and thieves have their own rows.
        var cc = Creation();
        int LevelAt(CharacterClass k, int xp) => cc.Levels(new Character { Classes = [k], Experience = xp })[0];
        Assert.Equal((2, 3), (LevelAt(CharacterClass.Gladiator, 3999), LevelAt(CharacterClass.Gladiator, 4000)));
        Assert.Equal((2, 3), (LevelAt(CharacterClass.Fighter, 3000), LevelAt(CharacterClass.Fighter, 4000)));
        Assert.Equal((2, 3), (LevelAt(CharacterClass.Druid, 3000), LevelAt(CharacterClass.Druid, 4000)));
        Assert.Equal((1, 2), (LevelAt(CharacterClass.Ranger, 2199), LevelAt(CharacterClass.Ranger, 2200)));
        Assert.Equal((1, 2), (LevelAt(CharacterClass.Thief, 1199), LevelAt(CharacterClass.Thief, 1200)));
        Assert.Equal(9, LevelAt(CharacterClass.Cleric, 10_000_000)); // up to 9

        // The tabletop rules mod: a gladiator needs 4500 for level 3.
        var tabletop = new CharacterCreation(SpellcastingTests.Tabletop, new CharacterNames(["Garn"], ["Shandra"], ["T'kir'taap"]), new Random(1));
        Assert.Equal(2, tabletop.Levels(new Character { Classes = [CharacterClass.Gladiator], Experience = 4000 })[0]);
        Assert.Equal(CharacterRules.Default.Races.Count, SpellcastingTests.Tabletop.Races.Count); // races left out: the original's
        Assert.Equal(CharacterRules.Default.Class(CharacterClass.Thief).Saves, SpellcastingTests.Tabletop.Class(CharacterClass.Thief).Saves);
    }

    [Fact]
    public void AHumanDualClassesFromThirdLevel()
    {
        var cc = Creation();
        // Fighter with Strength 17 (prime 15+) and Intelligence 17 (a preserver's 17).
        var c = new Character { Race = Race.Human, Classes = [CharacterClass.Fighter], Rolled = [17, 12, 12, 17, 12, 12], Experience = 1000 };
        Assert.Contains("LEVEL 3", cc.DualChoices(c).Why);
        c.Experience = 10000;
        var (classes, why) = cc.DualChoices(c);
        Assert.Null(why);
        Assert.Equal([CharacterClass.Preserver], classes);

        int level = cc.Levels(c)[0];
        Assert.True(cc.Dual(c, CharacterClass.Preserver));
        Assert.Equal([CharacterClass.Preserver], c.Classes);
        Assert.Equal([new FormerClass(CharacterClass.Fighter, level)], c.Former);
        Assert.Equal(0, c.Experience);
        Assert.Equal([(CharacterClass.Preserver, 1)], cc.ActiveLevels(c)); // the fighter's level unused for now
        Assert.Equal(ItemClasses.Preserver | ItemClasses.Fighter, c.ItemClasses);

        c.Experience = 1_000_000; // far past the fighter's level
        Assert.Contains((CharacterClass.Fighter, level), cc.ActiveLevels(c));
        Assert.Equal(CharacterSheet.Thac0(CharacterClass.Fighter, level), CharacterSheet.Thac0(cc.ActiveLevels(c)));

        Assert.False(cc.Dual(c, CharacterClass.Fighter)); // never back to a class it had
        var elf = new Character { Race = Race.Elf, Classes = [CharacterClass.Fighter], Rolled = [17, 12, 12, 17, 12, 12], Experience = 10000 };
        Assert.Equal("ONLY HUMANS CAN DUAL-CLASS", cc.DualChoices(elf).Why);
    }

    [Fact]
    public void ANewCharacterIsAHumanFighterWithRolledAbilities()
    {
        var cc = Creation();

        Character c = cc.New();

        Assert.Equal((Race.Human, Gender.Male, "Garn"), (c.Race, c.Gender, c.Name));
        Assert.Equal([CharacterClass.Fighter], c.Classes);
        foreach (Ability a in Enum.GetValues<Ability>())
        {
            var (low, high) = cc.Range(c, a);
            Assert.InRange(cc.Adjusted(c, a), low, high);
        }
        Assert.InRange(c.Hp, cc.HpRange(c).Low, cc.HpRange(c).High);
        Assert.Equal(["Choose a psionic discipline."], cc.Problems(c)); // unchecked, as in the original
        cc.ChooseDiscipline(c, Discipline.Telepathy);
        Assert.Empty(cc.Problems(c));
    }

    [Fact]
    public void RacesAdjustAbilitiesAndLimitThem()
    {
        var cc = Creation();
        Character c = cc.New();
        int str = c.Rolled[(int)Ability.Str];

        cc.SetLooks(c, Race.HalfGiant, Gender.Female);

        Assert.Equal(4, cc.Adjustment(c, Ability.Str));
        Assert.Equal(-5, cc.Adjustment(c, Ability.Dex)); // DSUN.EXE's table, not the manual's
        Assert.Equal(24, cc.Range(c, Ability.Str).High);
        Assert.Equal(15, cc.Range(c, Ability.Dex).High); // 20 − 5
        Assert.Equal(Math.Max(str + 4, 17), cc.Adjusted(c, Ability.Str)); // a fighter's Strength is at least 17
        Assert.Equal(9, cc.Range(c, Ability.Wis).Low); // the fighter's floor for the other abilities
        while (cc.Adjusted(c, Ability.Str) < cc.Range(c, Ability.Str).High)
        {
            cc.Raise(c, Ability.Str);
        }
        Assert.Equal(24, cc.Adjusted(c, Ability.Str));
        Assert.True(cc.Raise(c, Ability.Str)); // past the top, round to the bottom (the guide)
        Assert.Equal(cc.Range(c, Ability.Str).Low, cc.Adjusted(c, Ability.Str));
        Assert.True(cc.Lower(c, Ability.Str)); // and back
        Assert.Equal(24, cc.Adjusted(c, Ability.Str));
    }

    [Fact]
    public void LooksCycleThroughTheFourteenPortraits()
    {
        var cc = Creation();
        Character c = cc.New();

        Assert.Equal(14, cc.Looks.Count);
        Assert.Equal((Race.ThriKreen, Gender.Female), cc.Looks[^1]);
        cc.CycleLooks(c, -1);
        Assert.Equal((Race.ThriKreen, Gender.Female), (c.Race, c.Gender));
        Assert.Equal("T'kir'taap", c.Name);
        cc.CycleLooks(c, +1);
        Assert.Equal((Race.Human, Gender.Male), (c.Race, c.Gender));
        Assert.Equal(12, Enumerable.Range(0, 14).Count(i => cc.Looks[i].Race is not (Race.Mul or Race.ThriKreen)));
    }

    [Fact]
    public void PortraitsAreNumberedInLookOrder()
    {
        var cc = Creation();
        Character c = cc.New();
        Assert.Equal(20000, cc.Portrait(c));
        cc.SetLooks(c, Race.Halfling, Gender.Female);
        Assert.Equal(20011, cc.Portrait(c));
        cc.SetLooks(c, Race.Mul, Gender.Male);
        Assert.Equal(20012, cc.Portrait(c));
        cc.SetLooks(c, Race.ThriKreen, Gender.Female);
        Assert.Equal(20013, cc.Portrait(c));
    }

    [Fact]
    public void HumansTakeOneClassOthersUpToThreeAndNeverClericAndDruid()
    {
        var cc = Creation();
        Character human = cc.New();
        Assert.False(cc.CanAdd(human, CharacterClass.Thief));
        Assert.True(cc.ToggleClass(human, CharacterClass.Fighter)); // deselect: "all the class options are now available"
        Assert.All(Enum.GetValues<CharacterClass>(), k => Assert.True(cc.CanAdd(human, k)));

        Character halfElf = cc.New();
        cc.SetLooks(halfElf, Race.HalfElf, Gender.Female);
        Assert.True(cc.ToggleClass(halfElf, CharacterClass.Cleric));
        Assert.False(cc.CanAdd(halfElf, CharacterClass.Druid));
        Assert.True(cc.ToggleClass(halfElf, CharacterClass.Thief));
        Assert.False(cc.CanAdd(halfElf, CharacterClass.Preserver)); // three already
        Assert.Equal([CharacterClass.Cleric, CharacterClass.Fighter, CharacterClass.Thief], halfElf.Classes);
        Assert.Null(halfElf.Sphere); // the cleric's sphere is the player's to pick
        Assert.Contains("Choose a sphere.", cc.Problems(halfElf));
        cc.ChooseSphere(halfElf, Sphere.Earth);
        Assert.Equal(Sphere.Earth, halfElf.Sphere);

        Character dwarf = cc.New();
        cc.SetLooks(dwarf, Race.Dwarf, Gender.Male);
        Assert.False(cc.CanAdd(dwarf, CharacterClass.Preserver));
        Character giant = cc.New();
        cc.SetLooks(giant, Race.HalfGiant, Gender.Male);
        Assert.False(cc.CanAdd(giant, CharacterClass.Ranger));
    }

    [Fact]
    public void ChoosingAClassRaisesTheAbilitiesItNeeds()
    {
        var cc = Creation();
        Character c = cc.New();
        cc.ToggleClass(c, CharacterClass.Fighter);

        cc.ToggleClass(c, CharacterClass.Psionicist);

        // DSUN.EXE (6ECD:1F19): 17 in the class's key ability (Wisdom), its floor (12) in every other.
        Assert.True(cc.Adjusted(c, Ability.Wis) >= 17);
        Assert.All(new[] { Ability.Str, Ability.Dex, Ability.Con, Ability.Int, Ability.Cha }, a => Assert.True(cc.Adjusted(c, a) >= 12));
        Assert.Equal(17, cc.Range(c, Ability.Wis).Low);
        Assert.Equal(Enum.GetValues<Discipline>(), c.Disciplines);
        cc.ChooseDiscipline(c, Discipline.Telepathy);
        Assert.Equal(3, c.Disciplines.Count); // psionicists keep all three
        Assert.Empty(cc.Problems(c));
    }

    [Fact]
    public void HitPointsFollowTheHitDiceLevelsAndConstitution()
    {
        var cc = Creation();
        Character c = cc.New(); // a fighter with 3000 experience: level 2
        Assert.Equal([2], cc.Levels(c));
        while (cc.Adjusted(c, Ability.Con) > cc.Range(c, Ability.Con).Low)
        {
            cc.Lower(c, Ability.Con);
        }
        Assert.Equal((2, 20), cc.HpRange(c)); // a d10 for each level
        while (cc.Adjusted(c, Ability.Con) < cc.Range(c, Ability.Con).High)
        {
            cc.Raise(c, Ability.Con);
        }
        Assert.Equal(20, cc.Adjusted(c, Ability.Con));
        Assert.Equal((7, 25), cc.HpRange(c)); // +5 at 19 and above, once
        Assert.Equal(4, CharacterRules.Default.ConstitutionBonus(18, warrior: true));
        Assert.Equal(2, CharacterRules.Default.ConstitutionBonus(18, warrior: false));

        cc.ToggleClass(c, CharacterClass.Fighter);
        cc.ToggleClass(c, CharacterClass.Preserver);
        Assert.Equal((4, 10), cc.HpRange(c)); // level 2 at 2500: two d4, + 2 (40B1:0052's 5, held to 2: not a warrior)
        Assert.Equal(-3, CharacterRules.Default.ConstitutionBonus(1, warrior: false));
        Assert.Equal(7, CharacterRules.Default.ConstitutionBonus(25, warrior: true));
        CharacterRules tabletop = SpellcastingTests.Tabletop;
        Assert.Equal((5, 5, -2), (tabletop.ConstitutionBonus(25, true), tabletop.ConstitutionBonus(19, false), tabletop.ConstitutionBonus(3, false)));

        Character giant = cc.New();
        cc.SetLooks(giant, Race.HalfGiant, Gender.Male);
        while (cc.Adjusted(giant, Ability.Con) > cc.Range(giant, Ability.Con).Low)
        {
            cc.Lower(giant, Ability.Con);
        }
        var (low, high) = cc.HpRange(giant);
        int bonus = CharacterRules.Default.ConstitutionBonus(cc.Adjusted(giant, Ability.Con), true);
        Assert.Equal((4 + bonus, 40 + bonus), (low, high)); // half-giants' hit points are doubled (6ECD:0156)
    }

    [Fact]
    public void NewCharactersStartWithExperienceInEachClass()
    {
        var cc = Creation();
        Character c = cc.New();
        Assert.Equal(3000, c.Experience);

        cc.SetLooks(c, Race.HalfElf, Gender.Female);
        cc.ToggleClass(c, CharacterClass.Thief);
        cc.ToggleClass(c, CharacterClass.Cleric);

        Assert.Equal(9000, c.Experience); // 3000 in each of three classes
        Assert.Equal([3, 2, 3], cc.Levels(c)); // cleric 3000, fighter 2000, thief 2500
        c.Adventured = true;
        cc.ToggleClass(c, CharacterClass.Thief);
        Assert.Equal(9000, c.Experience); // an adventured character keeps what it earned
    }

    [Fact]
    public void AbilitiesAreTheBestOfFourRolls()
    {
        var rolls = new List<int>();
        for (int seed = 0; seed < 200; seed++)
        {
            var cc = Creation(seed);
            Character c = cc.New();
            cc.ToggleClass(c, CharacterClass.Fighter); // no class: no minimums
            cc.Roll(c);
            rolls.AddRange(c.Rolled);
        }
        Assert.All(rolls, r => Assert.InRange(r, 8, 20));
        // One 4d4+4 averages 14; the best of four, about 16.6.
        Assert.InRange(rolls.Average(), 16, 17.3);
    }

    [Fact]
    public void EachLookHasItsOwnMapIcon()
    {
        var cc = Creation();
        Character c = cc.New();
        Assert.Equal(300, c.Sprite);
        cc.SetLooks(c, Race.HalfElf, Gender.Female);
        Assert.Equal(307, c.Sprite);
        cc.SetLooks(c, Race.ThriKreen, Gender.Female);
        Assert.Equal(313, cc.Icon(c));
        Assert.Equal(313, c.Sprite);
    }

    [Fact]
    public void AlignmentsAreGoodOrNeutralOnly()
    {
        var cc = Creation();
        Character c = cc.New();
        var seen = new HashSet<Alignment>();
        for (int i = 0; i < 12; i++)
        {
            cc.CycleAlignment(c, +1);
            seen.Add(c.Alignment);
        }
        Assert.Equal(6, seen.Count);
        Assert.DoesNotContain(seen, a => a.ToString().EndsWith("Evil"));
    }

    [Fact]
    public void RulesAndCharactersRoundTripAsJson()
    {
        string path = Path.Combine(Path.GetTempPath(), $"darksun-rules-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, CharacterRules.Default.ToJson());
            CharacterRules rules = CharacterRules.Read(path);
            Assert.Equal(CharacterRules.Default.Races.Count, rules.Races.Count);
            Assert.Equal(4, rules.Race(Race.HalfGiant).Adjustments[Ability.Str]);
            Assert.Equal(17, rules.Class(CharacterClass.Psionicist).Requirements[Ability.Wis]);
            Assert.Equal(2, rules.Race(Race.HalfGiant).HitPointFactor);
            Assert.Equal((3000, 300, 4), (rules.StartingExperience, rules.IconBase, rules.RollTries));
            Assert.Equal(CharacterRules.Default.ToJson(), rules.ToJson());
            Assert.Equal(15, rules.Race(Race.ThriKreen).Move);
            Assert.Equal(2, rules.Race(Race.ThriKreen).NaturalAttacks!.Count);
            Assert.Equal(-6, rules.Abilities.DexDefenceOf(25));
            Assert.Equal(new SaveRow(10, 45, 2), rules.Class(CharacterClass.Cleric).Saves![0]);

            // A file from before the combat tables: each race and class gets the original's.
            string old = System.Text.RegularExpressions.Regex.Replace(CharacterRules.Default.ToJson(),
                @",\s*""(Move|NaturalAttacks|Thac0Factor|Saves)"": (\d+|\[[^\]]*\](\s*\])?)", "");
            Assert.DoesNotContain("Thac0Factor", old);
            File.WriteAllText(path, old);
            rules = CharacterRules.Read(path);
            Assert.Equal(6, rules.Class(CharacterClass.Thief).Thac0Factor);
            Assert.Equal(15, rules.Race(Race.ThriKreen).Move);
            Assert.Equal(CharacterRules.Default.ToJson(), rules.ToJson());
        }
        finally
        {
            File.Delete(path);
        }

        Character c = Creation().New();
        Character back = CharacterRoster.FromJson(CharacterRoster.ToJson(c));
        Assert.Equal((c.Id, c.Name, c.Hp), (back.Id, back.Name, back.Hp));
        Assert.Equal(c.Rolled, back.Rolled);
    }

    [Fact]
    public void TheRosterKeepsCharactersOnDisk()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"darksun-roster-{Guid.NewGuid():N}");
        try
        {
            var roster = new CharacterRoster(folder);
            Character c = Creation().New();
            roster.Save(c);
            File.WriteAllText(Path.Combine(folder, "broken.json"), "{");

            Assert.Equal(c.Id, Assert.Single(roster.All()).Id);
            roster.Delete(c);
            Assert.Empty(roster.All());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
