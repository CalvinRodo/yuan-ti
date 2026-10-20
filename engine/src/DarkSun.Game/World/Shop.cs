namespace DarkSun.Game.World;

/// <summary>
/// Buying and selling (SHOP in a script). DSUN.EXE's store screen
/// (<c>74F1:042B</c>, <c>84C9:11B4</c>) isn't traced past its messages
/// (STORE, NOT ENOUGH MONEY, "I'll give you %u$", TAKE IT / LEAVE IT,
/// SOLD!, I COULDN'T SELL THAT); the prices are the engine's: an item
/// costs its value (item record +6) and sells for half; items worth 9999
/// (quest items in the data) aren't bought.
/// </summary>
public static class Shop
{
    public const int NotForSale = 9999;

    public static int Price(Item item) => item.Record.Value;

    /// <summary>What the merchant gives for <paramref name="item"/> (all of a stack), or 0 if it won't buy it.</summary>
    public static int Offer(Item item) =>
        item.Record.Value >= NotForSale || item.Record.Value < 2 ? 0 : item.Record.Value / 2 * Math.Max(1, item.Quantity);

    /// <summary>
    /// <paramref name="buyer"/> buys one <paramref name="item"/> from
    /// <paramref name="merchant"/> with <paramref name="money"/>: the money
    /// left, or null if it isn't enough or the pack is full.
    /// </summary>
    public static int? Buy(GameWorld world, WorldObject merchant, WorldObject buyer, Item item, int money)
    {
        if (money < Price(item) || world.Give(1, item.Id, buyer.Index, merchant.Index) == 0)
        {
            return null;
        }
        return money - Price(item);
    }

    /// <summary>Sell <paramref name="items"/> to <paramref name="merchant"/>: what was paid (they go to the merchant).</summary>
    public static int Sell(GameWorld world, WorldObject merchant, WorldObject seller, IEnumerable<Item> items)
    {
        int paid = 0;
        foreach (Item item in items.ToList())
        {
            int offer = Offer(item);
            item.Slot = Equipment.Pack;
            if (offer > 0 && world.MoveItem(seller, merchant, item))
            {
                paid += offer;
            }
        }
        return paid;
    }
}
