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
    }
}
