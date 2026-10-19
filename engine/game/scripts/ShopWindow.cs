using DarkSun.Game.World;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// A merchant's shop (SHOP in a script; the script waits until it closes):
/// the merchant's goods on the left, the active member's pack on the right.
/// Buying costs an item's value (item record +6) from the party's money
/// (<c>gname[10]</c>) and moves one into the member's pack; selling gives
/// half its value. Items worth 9999 (quest items in the data) aren't sold.
/// DSUN.EXE's shop screen (<c>84C9:11B4</c>) isn't traced: these prices are
/// this engine's.
/// </summary>
public partial class ShopWindow : PanelContainer
{
    /// <summary>The value of items that aren't for sale.</summary>
    public const int NotForSale = 9999;

    private GameWorld? _world;
    private WorldObject? _merchant;
    private WorldObject? _buyer;
    private readonly List<Item> _goods = [], _pack = [];
    private Label _title = null!, _money = null!, _status = null!;
    private ItemList _left = null!, _right = null!;

    public Func<int> Money { get; set; } = () => 0;

    public Action<int>? SetMoney { get; set; }

    /// <summary>The shop closed: the script goes on.</summary>
    public Action? Closed { get; set; }

    public Action<string>? Did { get; set; }

    public bool IsOpen => Visible;

    public override void _Ready()
    {
        Visible = false;
        SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
        GrowHorizontal = GrowDirection.Both;
        GrowVertical = GrowDirection.Both;
        CustomMinimumSize = new Vector2(440, 240);
        AddThemeStyleboxOverride("panel", InventoryWindow.Panel());
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 3);
        AddChild(column);
        var top = new HBoxContainer();
        column.AddChild(top);
        _title = InventoryWindow.Text("", new Color(0.95f, 0.8f, 0.55f));
        top.AddChild(_title);
        top.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        _money = InventoryWindow.Text("", new Color(0.7f, 0.65f, 0.6f));
        top.AddChild(_money);
        var lists = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        column.AddChild(lists);
        _left = List(lists, "For sale");
        _left.ItemActivated += _ => Buy();
        _right = List(lists, "Yours");
        _right.ItemActivated += _ => Sell();
        _status = InventoryWindow.Text("Double-click to buy or sell.", new Color(0.7f, 0.65f, 0.6f));
        column.AddChild(_status);
        var buttons = new HBoxContainer();
        column.AddChild(buttons);
        InventoryWindow.AddButton(buttons, "Buy", Buy);
        InventoryWindow.AddButton(buttons, "Sell", Sell);
        buttons.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        InventoryWindow.AddButton(buttons, "Done", Close);
    }

    private static ItemList List(Container parent, string title)
    {
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        parent.AddChild(box);
        box.AddChild(InventoryWindow.Text(title));
        var list = InventoryWindow.Small(new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 150) });
        box.AddChild(list);
        return list;
    }

    public void Open(GameWorld world, WorldObject merchant, WorldObject buyer)
    {
        (_world, _merchant, _buyer) = (world, merchant, buyer);
        _title.Text = $"{(merchant.Name.Length > 0 ? merchant.Name : "Merchant")}'s goods ({buyer.Name} buying)";
        Visible = true;
        Refresh();
    }

    public void Close()
    {
        if (!Visible)
        {
            return;
        }
        Visible = false;
        Closed?.Invoke();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (Visible && e.IsActionPressed(Controls.Back))
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }

    private static int Price(Item item) => item.Record.Value;

    private static int SellPrice(Item item) => item.Record.Value / 2;

    private void Refresh()
    {
        _money.Text = $"Money: {Money()}";
        Fill(_left, _goods, _merchant!, i => $"{Price(i)}");
        Fill(_right, _pack, _buyer!, i => Price(i) >= NotForSale ? "not for sale" : $"{SellPrice(i)}");
    }

    private void Fill(ItemList list, List<Item> rows, WorldObject holder, Func<Item, string> price)
    {
        list.Clear();
        rows.Clear();
        foreach (Item item in holder.Items.Where(i => !i.Equipped))
        {
            list.AddItem($"{ItemName(item)}{(item.Quantity > 1 ? $" ×{item.Quantity}" : "")}  {price(item)}");
            rows.Add(item);
        }
    }

    private string ItemName(Item item) => _world!.Items.NameOf(item.Record) is { Length: > 0 } n ? n : $"item {item.Rdff}";

    private void Buy()
    {
        if (_left.GetSelectedItems() is not [int i, ..] || i >= _goods.Count)
        {
            return;
        }
        Item item = _goods[i];
        if (Money() < Price(item))
        {
            _status.Text = $"{ItemName(item)} costs {Price(item)}; you have {Money()}.";
            return;
        }
        if (_world!.Give(1, item.Id, _buyer!.Index, _merchant!.Index) == 0)
        {
            _status.Text = $"{_buyer.Name} can't carry any more.";
            return;
        }
        SetMoney?.Invoke(Money() - Price(item));
        _status.Text = $"Bought {ItemName(item)} for {Price(item)}.";
        Did?.Invoke(_status.Text);
        Refresh();
    }

    private void Sell()
    {
        if (_right.GetSelectedItems() is not [int i, ..] || i >= _pack.Count)
        {
            return;
        }
        Item item = _pack[i];
        if (Price(item) >= NotForSale)
        {
            _status.Text = $"{ItemName(item)} isn't for sale.";
            return;
        }
        if (_world!.Give(1, item.Id, _merchant!.Index, _buyer!.Index) == 0)
        {
            return;
        }
        SetMoney?.Invoke(Money() + SellPrice(item));
        _status.Text = $"Sold {ItemName(item)} for {SellPrice(item)}.";
        Did?.Invoke(_status.Text);
        Refresh();
    }
}
