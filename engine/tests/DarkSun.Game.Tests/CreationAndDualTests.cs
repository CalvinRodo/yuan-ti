using DarkSun.Data.Items;
using DarkSun.Game.Characters;
using DarkSun.Game.Combat;
using DarkSun.Game.Magic;
using DarkSun.Game.Movement;
using DarkSun.Game.World;

namespace DarkSun.Game.Tests;

/// <summary>Character creation's choices, dual-classing and Raise Dead, as the original has them (the guide).</summary>
public class CreationAndDualTests
{
    private const int RaiseDead = 136;

    private static CharacterCreation Creation() => new(CharacterRules.Default, new CharacterNames(["Garn"], ["Shandra"], ["T'kir'taap"]), new Random(1));

    [Fact]
    public void DruidsAreTrueNeutralOnly()
    {
        var cc = Creation();
        Character c = cc.New();
        cc.SetLooks(c, Race.HalfElf, Gender.Female);
        c.Alignment = Alignment.LawfulGood;
        Assert.False(cc.CanAdd(c, CharacterClass.Druid));

        c.Alignment = Alignment.TrueNeutral;
        Assert.True(cc.CanAdd(c, CharacterClass.Druid));
        Assert.True(cc.ToggleClass(c, CharacterClass.Druid));
        cc.CycleAlignment(c, +1); // away from True Neutral: the druid goes
        Assert.DoesNotContain(CharacterClass.Druid, c.Classes);
    }

    [Fact]
    public void AbilitiesAndHitPointsGoRoundPastTheirLimits()
    {
        var cc = Creation();
        Character c = cc.New();
        var (low, high) = cc.Range(c, Ability.Int);
        while (cc.Adjusted(c, Ability.Int) < high)
        {
            cc.Raise(c, Ability.Int);
        }
        Assert.True(cc.Raise(c, Ability.Int));
        Assert.Equal(low, cc.Adjusted(c, Ability.Int));
        Assert.True(cc.Lower(c, Ability.Int));
        Assert.Equal(high, cc.Adjusted(c, Ability.Int));

        var (hpLow, hpHigh) = cc.HpRange(c);
        c.Hp = hpHigh;
        Assert.True(cc.ChangeHp(c, +1));
        Assert.Equal(hpLow, c.Hp);
        Assert.True(cc.ChangeHp(c, -1));
        Assert.Equal(hpHigh, c.Hp);
    }

    [Fact]
    public void ASecondDualClassNeedsSecondLevelInEach()
    {
        var cc = Creation();
        var c = new Character { Name = "Dual", Race = Race.Human, Classes = [CharacterClass.Fighter], Rolled = [18, 18, 18, 18, 18, 18], Experience = 2000 };
        Assert.Equal("YOU MUST BE LEVEL 3 FIRST", cc.DualChoices(c).Why); // the first: third level (the manual)
        c.Experience = 4000;
        Assert.True(cc.Dual(c, CharacterClass.Thief));
        c.Experience = 1250; // thief level 2
        Assert.Contains(CharacterClass.Preserver, cc.DualChoices(c).Classes); // the second: second level in each
        c.Experience = 0;
        Assert.Equal("YOU MUST BE LEVEL 2 FIRST", cc.DualChoices(c).Why);
    }

    [Fact]
    public void AHaltedClassesGearIsUnusableUntilItIsInUseAgain()
    {
        var cc = Creation();
        var c = new Character { Name = "Dual", Race = Race.Human, Classes = [CharacterClass.Thief], Experience = 0 };
        c.Former.Add(new FormerClass(CharacterClass.Fighter, 3));
        Assert.Equal(ItemClasses.Thief, cc.UsableItemClasses(c)); // thief level 1: the fighter's halted
        c.Experience = 5000; // thief level 4, past the fighter's 3
        Assert.Equal(ItemClasses.Thief | ItemClasses.Fighter, cc.UsableItemClasses(c));
    }

    [Fact]
    public void RaiseDeadDoesntTakeElvesOrHalfElves()
    {
        Assert.Equal(Spellcasting.NothingHappens, Spellcasting.Heal(RaiseDead, new Vitals { Status = Vitals.Dead }, new Random(1), Race.Elf));
        Assert.Equal(Spellcasting.NothingHappens, Spellcasting.Heal(RaiseDead, new Vitals { Status = Vitals.Dead }, new Random(1), Race.HalfElf));
        Assert.Equal("RAISED FROM THE DEAD", Spellcasting.Heal(RaiseDead, new Vitals { Status = Vitals.Dead }, new Random(1), Race.Human));

        Combatant priest = CombatTests.Fighter("priest", 1, 5, 5);
        Combatant elf = CombatTests.Fighter("elf", 1, 6, 5, race: 3), human = CombatTests.Fighter("human", 1, 4, 5, race: 1);
        foreach (Combatant dead in new[] { elf, human })
        {
            (dead.Vitals.Status, dead.Vitals.Hp) = (Vitals.Dead, 0);
        }
        var b = new Battle([priest, elf, human, CombatTests.Fighter("foe", 2, 20, 20)], new OpenField(30, 30), new ScriptedDice());
        b.Cast(priest, RaiseDead, 9, elf.Cell, ownSpell: false); // a cast finds the dead at the cell
        b.Cast(priest, RaiseDead, 9, human.Cell, ownSpell: false);
        Assert.Equal(Vitals.Dead, elf.Vitals.Status);
        Assert.Equal(Vitals.Well, human.Vitals.Status);
    }
}
