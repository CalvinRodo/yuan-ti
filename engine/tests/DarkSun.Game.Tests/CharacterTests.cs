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
        while (cc.Raise(c, Ability.Str))
        {
        }
        Assert.Equal(24, cc.Adjusted(c, Ability.Str));
        Assert.False(cc.Raise(c, Ability.Str));
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
        while (cc.Lower(c, Ability.Con))
        {
        }
        Assert.Equal((2, 20), cc.HpRange(c)); // a d10 for each level
        while (cc.Raise(c, Ability.Con))
        {
        }
        Assert.Equal(20, cc.Adjusted(c, Ability.Con));
        Assert.Equal((7, 25), cc.HpRange(c)); // +5 at 19 and above, once
        Assert.Equal(4, CharacterRules.ConstitutionBonus(18, warrior: true));
        Assert.Equal(2, CharacterRules.ConstitutionBonus(18, warrior: false));

        cc.ToggleClass(c, CharacterClass.Fighter);
        cc.ToggleClass(c, CharacterClass.Preserver);
        Assert.Equal((7, 13), cc.HpRange(c)); // level 2 at 2500: two d4, + 5

        Character giant = cc.New();
        cc.SetLooks(giant, Race.HalfGiant, Gender.Male);
        while (cc.Lower(giant, Ability.Con))
        {
        }
        var (low, high) = cc.HpRange(giant);
        int bonus = CharacterRules.ConstitutionBonus(cc.Adjusted(giant, Ability.Con), true);
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
