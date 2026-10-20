using DarkSun.Data.Items;
using DarkSun.Game.Characters;
using DarkSun.Game.World;

namespace DarkSun.Game.Tests;

public class CharacterSheetTests
{
    private static ItemRecord Record(int type, sbyte bonus = 0) => new(-1, 1, 0, 10, 0, (short)type, 0, 0, 0, 6, 255, 0, bonus);

    [Fact]
    public void ArmourClassCountsWornPiecesTheirBonusesAndDexterity()
    {
        // Type 0: chest armour, AC 2. Type 1: a shield, AC 1. Type 2: a sword (no AC).
        byte[] it1r = [.. new byte[9], 1, .. new byte[8], 2, 0, .. new byte[9], 5, .. new byte[8], 1, 0, .. new byte[20]];
        var catalog = new ItemCatalog(ItemType.ParseAll(it1r), Data.Text.NameTable.Parse(new byte[25]));
        Item armour = new(1, Record(0, bonus: 1), slot: 9), shield = new(2, Record(1), slot: 3), sword = new(3, Record(2), slot: 10);
        Item carried = new(4, Record(0));

        Assert.Equal(10, CharacterSheet.ArmorClass(10, [], catalog));
        Assert.Equal(10 - 3 - 1, CharacterSheet.ArmorClass(10, [armour, shield, sword, carried], catalog)); // only what is worn
        Assert.Equal(6 - 4, CharacterSheet.ArmorClass(19, [armour, shield], catalog));
        Assert.Equal(14, CharacterSheet.ArmorClass(3, [], catalog));
    }

    [Fact]
    public void Thac0IsTheBestOfTheClassesTables()
    {
        Assert.Equal(20, CharacterSheet.Thac0(CharacterClass.Fighter, 1));
        Assert.Equal(16, CharacterSheet.Thac0(CharacterClass.Gladiator, 5));
        Assert.Equal(18, CharacterSheet.Thac0(CharacterClass.Cleric, 4));
        Assert.Equal(19, CharacterSheet.Thac0(CharacterClass.Preserver, 4));
        Assert.Equal(19, CharacterSheet.Thac0(CharacterClass.Thief, 3));

        var c = new Character { Classes = [CharacterClass.Preserver, CharacterClass.Fighter] };
        Assert.Equal(18, CharacterSheet.Thac0(c, [2, 3]));
        Assert.Equal(20, CharacterSheet.Thac0(new Character(), []));
        Assert.Equal(19, CharacterSheet.Thac0(CharacterClass.Cleric, 3)); // 8E77:0416: 20 − 2 × 8 ÷ 12
    }

    [Fact]
    public void TheSheetFollowsTheRules()
    {
        var rules = CharacterRules.Default with
        {
            Classes = [.. CharacterRules.Default.Classes.Select(k => k.Class == CharacterClass.Thief ? k with { Thac0Factor = 12 } : k)],
            AttackSteps = [new(1, 4)],
        };
        Assert.Equal(17, CharacterSheet.Thac0(CharacterClass.Thief, 4, rules));
        Assert.Equal(4, CharacterSheet.AttackHalves([(CharacterClass.Fighter, 1)], rules));
        Assert.Equal(2, CharacterSheet.AttackHalves([(CharacterClass.Thief, 9)], rules));
        Assert.Equal(3, CharacterSheet.AttackHalves([(CharacterClass.Ranger, 6)]));

        // 40B1:006C: a 1st-level fighter's saves; a dwarf's Con 18 takes 5 off them, not off a thief's.
        Assert.Equal([14, 16, 15, 17, 17], CharacterSheet.Saves([(CharacterClass.Fighter, 1)], Race.Human, 18));
        Assert.Equal([9, 11, 10, 12, 12], CharacterSheet.Saves([(CharacterClass.Fighter, 1)], Race.Dwarf, 18));
        Assert.Equal([13, 14, 12, 16, 15], CharacterSheet.Saves([(CharacterClass.Thief, 1)], Race.Dwarf, 18));
        var allClasses = CharacterRules.Default with { ConstitutionSavesWarriorsOnly = false };
        Assert.Equal([8, 9, 7, 11, 10], CharacterSheet.Saves([(CharacterClass.Thief, 1)], Race.Dwarf, 18, allClasses));
    }
}
