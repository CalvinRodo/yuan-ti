using System.Text;
using DarkSun.Data.Gff;
using DarkSun.Data.Items;
using DarkSun.Data.Regions;
using DarkSun.Data.Text;

namespace DarkSun.Data.Tests;

public class ItemTests
{
    /// <summary>An item RDFF as the game stores one: the record, references to its type and name, the end byte.</summary>
    internal static byte[] ItemRdff(int number, int type, int name, sbyte bonus)
    {
        var b = new Bytes();
        b.Byte(1, 0).Short(1).Short(number).Short(0).Short(ItemRecord.Size);          // inline item record
        b.Short(-number).Short(1).Short(0).Short(1).Short(0).Short(type).Short(0)
            .Byte(3, 0x08, 6, 0xFF).Short(name).Byte((byte)bonus);
        b.Byte(3, 0).Short(5).Short(type).Short(4).Short(0);                           // IT1R reference
        b.Byte(3, 0).Short(3).Short(name).Short(2).Short(0);                           // NAME reference
        return b.Byte(0xFF).ToArray();
    }

    [Fact]
    public void ReadsTheEntriesOfAnItemRdff()
    {
        var entries = Rdff.Entries(ItemRdff(1233, 67, 44, 0));

        Assert.Equal([(1, 1, 21), (3, 5, 0), (3, 3, 0)], entries.Select(e => (e.LoadAction, e.Type, e.Body.Length)));
        Assert.Equal(67, entries[1].Index);
        Assert.Equal(44, entries[2].Index);
    }

    [Fact]
    public void ParsesTheItemRecord()
    {
        ItemRecord item = ItemRecord.FromRdff(ItemRdff(30002, 41, 80, 1))!;

        Assert.Equal((-30002, 41, 80, 1, 0x08, 255), (item.Id, (int)item.Type, (int)item.NameIndex, (int)item.Bonus, (int)item.Flags, (int)item.Slot));
        Assert.Null(ItemRecord.FromRdff(new Bytes().Byte(1, 0).Short(2).Short(5).Short(0).Short(0).ToArray()));
    }

    [Fact]
    public void ParsesItemTypes()
    {
        // A long sword: melee, 40, 1d8+1, hand, range 1.
        byte[] it1r = [0x01, 0, 0, 0, 40, 0, 0, 0, 3, 5, 1, 1, 8, 1, 1, 0, 0xFF, 0x1F, 0, 0];

        ItemType t = Assert.Single(ItemType.ParseAll(it1r));

        Assert.Equal((WeaponKind.Melee, ItemPlacement.Hand, 1, 1), (t.Weapon, t.Placement, (int)t.Range, (int)t.Attacks));
        Assert.Equal("1d8+1", t.Damage);
        Assert.Equal(0x1FFF, t.LegalClasses);
        Assert.True(t.IsWeapon);
    }

    [Fact]
    public void NamesItemsAsTheGameDoes()
    {
        var names = new List<byte>();
        foreach (string n in new[] { "Sling", "Long Sword" })
        {
            var r = new byte[NameTable.RecordSize];
            Encoding.Latin1.GetBytes(n).CopyTo(r, 0);
            names.AddRange(r);
        }
        var catalog = new ItemCatalog([], NameTable.Parse(names.ToArray()));
        ItemRecord Item(int name, sbyte bonus) => ItemRecord.FromRdff(ItemRdff(1, 0, name, bonus))!;

        Assert.Equal("Long Sword", catalog.NameOf(Item(1, 0)));
        Assert.Equal("+2 Long Sword", catalog.NameOf(Item(1, 2)));
        Assert.Equal("-1 Sling", catalog.NameOf(Item(0, -1)));
        Assert.Equal("", catalog.NameOf(Item(ItemRecord.None, 0)));
    }

    [Fact]
    public void ReadsTheCharacterRecordAfterACombatRecord()
    {
        var b = new Bytes();
        b.Byte(1, 0).Short(2).Short(249).Short(0).Short(58).Zero(58);   // combat record
        b.Byte(3, 0).Short(4).Short(5).Short(71).Short(71);             // character record, 71 bytes
        byte[] character = new byte[71];
        (character[8], character[0x3D]) = (18, 17);
        b.Byte(character).Byte(0xFF);

        CharacterRecord record = CharacterRecord.FromRdff(b.ToArray())!;

        Assert.Equal(17, record.Size);
        Assert.Equal(18, record.DifficultyScaled);
        Assert.Null(CharacterRecord.FromRdff(ItemRdff(1, 0, 0, 0)));
    }
}
