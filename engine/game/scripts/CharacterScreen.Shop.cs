using DarkSun.Game.World;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// The store (SHOP), as DSUN.EXE's items page shows it (<c>74F1:042B</c>, <c>8A89</c>): the
/// store's sign (<c>BMP</c> 12000 at (252, 11)) over the member's figure, and the merchant's
/// goods six at a time in <c>APFM</c> 11242–11247, the same thing shown once (<c>8A89:055A</c>),
/// each priced "%d$" under it (<c>8A89:0C3B</c>); MORE (13303) turns their pages. A click on
/// one buys it (<c>8A89:0879</c>: NOT ENOUGH MONEY, else SOLD!; <see cref="Shop"/>). SELL
/// (13304) offers the thing held (as does putting it on the goods): "I'll give you %u$",
/// TAKE IT or LEAVE IT (<c>8A89:0D50</c>, <c>5F6D:0191</c> at (92, 71)); I COULDN'T SELL THAT
/// for one worth nothing. (The original moves what is bought, and what is to be sold, through
/// a deal box first; here a purchase goes straight to the pack.)
/// </summary>
public partial class CharacterScreen
{
    private const int StoreSign = 12000, GoodsShown = 6;

    private static readonly Vector2 StoreSignAt = new(252, 11), SellQuestionAt = new(92, 71);

    private int _goodsPage;
    private TextureRect _storeSign = null!;
    private readonly Label[] _prices = new Label[GoodsShown];

    private void ResetStore() => _goodsPage = 0;

    /// <summary>The merchant's goods, each kind once, in its order.</summary>
    private List<Item> Goods => _store?.Items.DistinctBy(i => i.Rdff).ToList() ?? [];

    private void BuildStore(UiWindowView items)
    {
        _storeSign = new TextureRect { Texture = Art("BMP ", StoreSign, 0), Position = StoreSignAt, MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        items.AddChild(_storeSign);
        for (int n = 0; n < GoodsShown; n++)
        {
            Rect2 at = items.RectOf("APFM", FirstSellSlot + n);
            // 8A89:0C3B: centred on the slot's middle (x + 9), under it, orange (45) with its shadow (17).
            _prices[n] = Text(items, at.Position + new Vector2(-11, at.Size.Y), 40, "", HorizontalAlignment.Center);
            _prices[n].LabelSettings = GameFonts.Settings(PaletteColour(45), shadow: PaletteColour(17));
        }
    }

    /// <summary>Turn the goods' pages; past the last, back to the first (<c>8A89:055A</c>).</summary>
    private void TurnGoods(int by)
    {
        int pages = Math.Max(1, (Goods.Count + GoodsShown - 1) / GoodsShown);
        _goodsPage = ((_goodsPage + by) % pages + pages) % pages;
        RefreshItems();
    }

    private void RefreshStore(UiWindowView items)
    {
        bool store = _store is not null;
        _storeSign.Visible = store;
        List<Item> goods = Goods;
        for (int n = 0; n < GoodsShown; n++)
        {
            _prices[n].Text = store && goods.ElementAtOrDefault(_goodsPage * GoodsShown + n) is { } item ? $"{Shop.Price(item)}$" : "";
        }
        if (store)
        {
            items.Button(MoreButton)!.Visible = goods.Count > GoodsShown;
            items.Button(SellButton)!.Visible = true;
            Enable(items.Button(SellButton)!, _picked is { } held && held.Holder == Member);
        }
    }

    private string StoreText() => "STORE"; // DS:2ED2

    /// <summary>A thing of the member's put on the goods: the merchant's offer (<c>8A89:0D50</c>).</summary>
    private string? SellPicked((Item Item, WorldObject Holder) picked)
    {
        if (_store is not { } store || Member is not { } member || _world is null || picked.Holder != member)
        {
            return null; // the store's own goods: nothing to sell
        }
        int offer = Shop.Offer(picked.Item);
        if (offer <= 0)
        {
            return "I COULDN'T SELL THAT";
        }
        _ask.Ask($"I'll give you {offer}$", SellQuestionAt, ("TAKE IT", () =>
        {
            _picked = null;
            int paid = Shop.Sell(_world, store, member, [picked.Item]);
            SetMoney?.Invoke(Money() + paid);
            Did?.Invoke($"{MemberName(_member)} sells {ItemText.Name(_world, picked.Item)} for {paid}$");
            RefreshItems();
            ShowInfo(null, null, "SOLD!");
        }), ("LEAVE IT", null));
        return null;
    }

    private string? Buy(Item item)
    {
        if (_store is not { } store || Member is not { } member || _world is null)
        {
            return null;
        }
        if (Shop.Kept(item))
        {
            return "NOT FOR SALE";
        }
        int price = Shop.Price(item);
        if (Shop.Buy(_world, store, member, item, Money()) is not int left)
        {
            return Money() < price ? "NOT ENOUGH MONEY" : $"{MemberName(_member)} can't carry any more.";
        }
        SetMoney?.Invoke(left);
        Did?.Invoke($"{MemberName(_member)} buys {ItemText.Name(_world, item)} for {price}$");
        return "SOLD!";
    }

    /// <summary>The store's question, for the checks.</summary>
    public QuestionWindow Question => _ask;
}
