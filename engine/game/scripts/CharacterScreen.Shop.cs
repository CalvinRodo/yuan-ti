using DarkSun.Game.World;

namespace DarkSun.Engine;

/// <summary>
/// The store (SHOP): the merchant's goods where a container's contents go,
/// priced (<see cref="Shop"/>); taking one buys it. To sell, the member's
/// things go in the six slots by SELL (<c>APFM</c> 11242–11247); SELL asks
/// the merchant ("I'll give you %u$"), and pressed again (TAKE IT) sells them.
/// Taking a thing back out, or leaving the store, keeps them (LEAVE IT).
/// </summary>
public partial class CharacterScreen
{
    private readonly List<Item> _offered = [];
    private int? _offer;

    private void ResetStore()
    {
        _offered.Clear();
        _offer = null;
    }

    private string StoreText()
    {
        string offered = _offer is int n ? (n > 0 ? $"I'll give you {n}$" : "I COULDN'T SELL THAT")
            : _offered.Count > 0 ? "SELL: what for?" : "Click goods, then the pack";
        return $"STORE\nMONEY {Money()}$\n{offered}";
    }

    /// <summary>The picked thing goes in the sell slots (taken off first).</summary>
    private string? OfferPicked((Item Item, WorldObject Holder) picked)
    {
        if (_store is null || Member is not { } member)
        {
            return null;
        }
        _picked = null;
        if (picked.Holder != member)
        {
            return null; // the store's own goods: nothing to sell
        }
        if (_offered.Count >= SellSlots)
        {
            return "The six slots are full: SELL those first.";
        }
        if (!_offered.Contains(picked.Item))
        {
            picked.Item.Slot = Equipment.Pack;
            _offered.Add(picked.Item);
            _offer = null;
        }
        return null;
    }

    private void SellPressed()
    {
        if (_store is not { } store || Member is not { } member || _world is null || _offered.Count == 0)
        {
            return;
        }
        if (_offer is null)
        {
            _offer = _offered.Sum(Shop.Offer);
            RefreshItems();
            return;
        }
        if (_offer == 0)
        {
            _offer = null;
            RefreshItems();
            ShowInfo(null, null, "NO DEAL!");
            return;
        }
        var names = _offered.Select(i => ItemText.Name(_world, i)).ToList();
        int paid = Shop.Sell(_world, store, member, _offered.Where(i => Shop.Offer(i) > 0));
        SetMoney?.Invoke(Money() + paid);
        Did?.Invoke($"{MemberName(_member)} sells {string.Join(", ", names)} for {paid}$");
        _offered.RemoveAll(i => !member.Items.Contains(i) || Shop.Offer(i) > 0);
        _offer = null;
        RefreshItems();
        ShowInfo(null, null, $"SOLD! {paid}$");
    }

    private string? Buy(Item item)
    {
        if (_store is not { } store || Member is not { } member || _world is null)
        {
            return null;
        }
        if (Shop.Price(item) >= Shop.NotForSale)
        {
            return "NOT FOR SALE";
        }
        if (Shop.Buy(_world, store, member, item, Money()) is not int left)
        {
            return Money() < Shop.Price(item) ? "NOT ENOUGH MONEY" : $"{MemberName(_member)} can't carry any more.";
        }
        SetMoney?.Invoke(left);
        Did?.Invoke($"{MemberName(_member)} buys {ItemText.Name(_world, item)} for {Shop.Price(item)}$");
        return null;
    }
}
