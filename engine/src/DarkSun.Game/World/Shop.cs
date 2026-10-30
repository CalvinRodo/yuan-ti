namespace DarkSun.Game.World;

/// <summary>
/// Buying and selling (SHOP in a script): DSUN.EXE's store is the items page
/// (<c>74F1:042B(actor, merchant)</c>) with the merchant's goods in six slots
/// (<c>APFM</c> 11242–11247), each priced "%d$" under it (<c>8A89:0C3B</c>).
/// A thing costs its value (item record +6), times its quantity for one that
/// stacks (<c>8A89:0879</c>: else NOT ENOUGH MONEY, or NO DEAL! while one is
/// offered for sale); the merchant pays the same for one sold, nothing for one
/// worth 0 ("I'll give you %u$", TAKE IT / LEAVE IT, SOLD!; I COULDN'T SELL
/// THAT, <c>8A89:0D50</c>). What is bought leaves the merchant whole, and if
/// the merchant has another of it a new one takes its place (<c>8A89:0A82</c>
/// → <c>7372:0FEE</c>), so what it has two of never runs out. The engine's
/// own: things worth 9999 (the quest items) are neither bought nor sold.
/// </summary>
public static class Shop
{
    public const int NotForSale = 9999;

    /// <summary>Is <paramref name="item"/> one the engine keeps out of trade (a quest item)?</summary>
    public static bool Kept(Item item) => item.Record.Value >= NotForSale;

    /// <summary>What <paramref name="item"/> costs (all of a stack).</summary>
    public static int Price(Item item) => item.Record.Value * Math.Max(1, item.Quantity);

    /// <summary>What the merchant gives for <paramref name="item"/> (all of a stack), or 0 if it won't buy it.</summary>
    public static int Offer(Item item) => Kept(item) ? 0 : Price(item);

    /// <summary>
    /// <paramref name="buyer"/> buys <paramref name="item"/> from
    /// <paramref name="merchant"/> with <paramref name="money"/>: the money
    /// left, or null if it isn't enough or the pack is full.
    /// </summary>
    public static int? Buy(GameWorld world, WorldObject merchant, WorldObject buyer, Item item, int money)
    {
        int price = Price(item);
        bool another = merchant.Items.Any(i => i != item && i.Rdff == item.Rdff);
        if (money < price || Kept(item) || !world.MoveItem(merchant, buyer, item))
        {
            return null;
        }
        if (another)
        {
            world.Give(Math.Max(1, item.Quantity), -item.Rdff, merchant.Index, GameWorld.None);
        }
        return money - price;
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
