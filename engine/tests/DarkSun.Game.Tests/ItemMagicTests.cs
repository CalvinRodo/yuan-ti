using DarkSun.Data.Items;
using DarkSun.Game.Combat;
using DarkSun.Game.World;

namespace DarkSun.Game.Tests;

/// <summary>USE on items (<see cref="ItemMagic"/>), with DSUN.EXE's spell effects (<see cref="SpellRules.Default"/>).</summary>
public class ItemMagicTests
{
    private const int MagicMissile = 9, CureSeriousWounds = 113;
    private static readonly SpellRules Spells = SpellRules.Default;

    /// <summary>An item of <c>RDFF</c> <paramref name="rdff"/> casting <paramref name="spell"/> (+0x0F and its copy +2).</summary>
    private static Item Magic(int rdff, int spell, int charges = 0, sbyte bonus = 0) =>
        new(rdff, new ItemRecord(-rdff, (ushort)spell, 0, 0, 0, 0, 0, (byte)charges, (byte)spell, 6, 255, 0, bonus));

    /// <summary>An item type worn at <paramref name="placement"/> (0: not worn).</summary>
    private static ItemType WornAt(int placement)
    {
        var bytes = new byte[ItemType.Size];
        bytes[9] = (byte)placement;
        return ItemType.Parse(0, bytes);
    }

    [Fact]
    public void AWandAimsItsSpell()
    {
        Item wand = Magic(31007, MagicMissile, charges: 50);

        Assert.True(ItemMagic.Offered(wand, WornAt(0), Spells));
        Assert.Equal(new ItemUse(ItemUseKind.Aim, MagicMissile), ItemMagic.Plan(wand, Spells));
    }

    [Fact]
    public void WornThingsWhoseSpellHelpsWorkByBeingWorn()
    {
        Assert.Equal(SpellEffect.Helps, Spells.Effect(89)!.Purpose); // Drake Armor's
        Assert.False(ItemMagic.Offered(Magic(30013, 89), WornAt(1), Spells));
        Assert.True(ItemMagic.Offered(Magic(31011, 140, charges: 7), WornAt(7), Spells)); // the necklace's power, against a creature
        Assert.False(ItemMagic.Offered(Magic(31011, 140, bonus: -1), WornAt(7), Spells)); // cursed
        Assert.False(ItemMagic.Offered(Magic(30006, 11), WornAt(5), Spells)); // a weapon in the hand
        Assert.False(ItemMagic.Offered(Magic(1, 0), WornAt(0), Spells)); // no spell
    }

    [Fact]
    public void FruitActsOnWhoeverEatsItAndALemonMakesThemWhole()
    {
        Item pear = Magic(31021, CureSeriousWounds, charges: 1), lemon = Magic(31028, ItemMagic.FullHeal, charges: 1);

        Assert.True(ItemMagic.Offered(lemon, WornAt(0), Spells));
        Assert.Equal(ItemUseKind.OnSelf, ItemMagic.Plan(pear, Spells).Kind); // a touch spell, but eaten
        Assert.Equal(ItemUseKind.FullHeal, ItemMagic.Plan(lemon, Spells).Kind);
    }

    [Fact]
    public void ScrollsTeachAndQuestItemsDoNothingHere()
    {
        Assert.Equal(new ItemUse(ItemUseKind.Learn, 66), ItemMagic.Plan(Magic(1410, 66), Spells));
        Assert.Equal(ItemUseKind.Nothing, ItemMagic.Plan(Magic(30029, 0), Spells).Kind);
        Assert.True(ItemMagic.Offered(Magic(30029, 0), WornAt(0), Spells));
    }
}
