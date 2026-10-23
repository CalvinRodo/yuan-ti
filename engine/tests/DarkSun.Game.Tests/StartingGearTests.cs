using DarkSun.Data.Gff;
using DarkSun.Data.Items;
using DarkSun.Game.World;

namespace DarkSun.Game.Tests;

/// <summary>A new character's gear by class (<c>6ECD:1D48</c>, the table at <c>38CE:0000</c>).</summary>
public class StartingGearTests
{
    private static int[] Ojffs(ItemClasses classes, bool thriKreen = false) => [.. StartingGear.For(classes, thriKreen).Select(g => g.Ojff)];

    [Fact]
    public void EachClassHasItsOwnGear()
    {
        Assert.Equal([1022, 1023, 1026, 1012, 1020], Ojffs(ItemClasses.Fighter)); // leather chest, arm, leg; long sword; shield
        Assert.Equal([1012, 1185, 1023], Ojffs(ItemClasses.Gladiator));
        Assert.Equal([1012, 1022, 1023, 1017, 1070], Ojffs(ItemClasses.Ranger));
        Assert.Equal([1185, 1020, 1022, 1023], Ojffs(ItemClasses.FireCleric));
        Assert.Equal([1012, 1022, 1015], Ojffs(ItemClasses.Thief));
        Assert.Equal([1185, 1022, 1017, 1070], Ojffs(ItemClasses.Psionicist));
        Assert.Equal([1015, 1019], Ojffs(ItemClasses.Preserver));
        Assert.Equal([1185, 1015], Ojffs(ItemClasses.Druid));
    }

    [Fact]
    public void SeveralClassesGetTheFirstTheCodeTestsAndAThriKreenItsChatkcha()
    {
        Assert.Equal(Ojffs(ItemClasses.Fighter), Ojffs(ItemClasses.Fighter | ItemClasses.Thief));
        Assert.Equal(Ojffs(ItemClasses.EarthCleric), Ojffs(ItemClasses.EarthCleric | ItemClasses.Preserver));
        Assert.Equal(Ojffs(ItemClasses.Thief), Ojffs(ItemClasses.Psionicist | ItemClasses.Thief));
        Assert.Equal([1010], Ojffs(ItemClasses.Fighter, thriKreen: true));
        Assert.Equal([3, 10, 0], StartingGear.For(ItemClasses.Gladiator, false).Select(g => g.Slot)); // right hand, left hand, arm
    }

    private sealed class Resources : IResourceSource
    {
        public Dictionary<(string, int), byte[]> Data { get; } = [];

        public bool Contains(string tag, int number) => Data.ContainsKey((tag, number));

        public bool TryRead(string tag, int number, out ReadOnlyMemory<byte> data)
        {
            bool found = Data.TryGetValue((tag, number), out byte[]? bytes);
            data = bytes ?? [];
            return found;
        }
    }

    /// <summary>An item <c>RDFF</c>: a record of <c>IT1R</c> type <paramref name="type"/>, <paramref name="quantity"/> of it.</summary>
    private static byte[] ItemRdff(int ojff, int type, int quantity = 0)
    {
        var item = new byte[10 + 21 + 1];
        (item[0], item[2], item[8]) = (1, 1, 21);
        BitConverter.GetBytes((short)-ojff).CopyTo(item, 10);
        BitConverter.GetBytes((ushort)quantity).CopyTo(item, 12);
        item[20] = (byte)type;
        item[^1] = 0xFF;
        return item;
    }

    [Fact]
    public void TheGearIsWornWhereItGoesIfTheClassMayElsePacked()
    {
        // IT1R: 0 a hand weapon, 1 chest and 2 arm armour, any class; 3 a bow fighters only; 4 arrows that stack.
        var types = new byte[5 * ItemType.Size];
        foreach (var (type, placement, classes) in new[] { (0, ItemPlacement.Hand, ItemClasses.All), (1, ItemPlacement.Chest, ItemClasses.All),
            (2, ItemPlacement.Arms, ItemClasses.All), (3, ItemPlacement.MissileWeapon, ItemClasses.Fighter), (4, ItemPlacement.Ammunition, ItemClasses.All) })
        {
            types[type * ItemType.Size + 9] = (byte)placement;
            BitConverter.GetBytes((ushort)classes).CopyTo(types, type * ItemType.Size + 16);
        }
        types[4 * ItemType.Size + 15] = 0x02;
        var data = new Resources();
        foreach (var (ojff, type, quantity) in new[] { (1012, 0, 0), (1022, 1, 0), (1023, 2, 0), (1017, 3, 0), (1070, 4, 20) })
        {
            data.Data[("RDFF", ojff)] = ItemRdff(ojff, type, quantity);
        }
        var catalog = new ItemCatalog(ItemType.ParseAll(types), Data.Text.NameTable.Parse(new byte[25]));

        List<ItemState> items = StartingGear.Items(ItemClasses.Ranger, false, data, catalog);

        Assert.Equal([(1012, 1, 3), (1022, 1, 9), (1023, 1, 0), (1017, 1, Equipment.Pack), (1070, 20, 1)],
            items.Select(i => (i.Rdff, i.Quantity, i.Slot)));
    }
}
