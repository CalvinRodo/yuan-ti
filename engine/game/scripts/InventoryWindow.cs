using DarkSun.Data.Items;
using DarkSun.Game.World;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// The party's items (I on the map): one member at a time, what they wear
/// (by equipment slot) and what is in their pack, and the party's money.
/// Wear / Take off moves an item between the pack and a free slot of its
/// placement (<see cref="Equipment"/>); Give passes it to the next member;
/// Drop puts it on the map. A modern stand-in for DSUN.EXE's inventory
/// screen, which isn't ported.
/// </summary>
public partial class InventoryWindow : PanelContainer
{
    private const int FontSize = 10;

    private static readonly Color Heading = new(0.95f, 0.8f, 0.55f);
    private static readonly Color Dim = new(0.7f, 0.65f, 0.6f);

    private GameWorld? _world;
    private int _member;
    private readonly List<Item> _rows = [];
    private HBoxContainer _members = null!;
    private Label _money = null!;
    private ItemList _list = null!;
    private Label _details = null!;
    private Button _wear = null!, _give = null!, _drop = null!;

    /// <summary>The party members' names, by slot.</summary>
    public Func<int, string> MemberName { get; set; } = slot => $"Character {slot + 1}";

    /// <summary>The party's money (<c>gname[10]</c>).</summary>
    public Func<int> Money { get; set; } = () => 0;

    /// <summary>Something happened worth logging (an item dropped, given, …).</summary>
    public Action<string>? Did { get; set; }

    public override void _Ready()
    {
        Visible = false;
        SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
        GrowHorizontal = GrowDirection.Both;
        GrowVertical = GrowDirection.Both;
        CustomMinimumSize = new Vector2(380, 250);
        AddThemeStyleboxOverride("panel", Panel());
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 3);
        AddChild(column);
        var top = new HBoxContainer();
        column.AddChild(top);
        top.AddChild(Text("Items", Heading));
        top.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        _money = Text("", Dim);
        top.AddChild(_money);
        _members = new HBoxContainer();
        column.AddChild(_members);
        _list = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 140) };
        _list.AddThemeFontSizeOverride("font_size", FontSize);
        _list.ItemSelected += _ => ShowDetails();
        _list.ItemActivated += _ => WearOrTakeOff();
        column.AddChild(_list);
        _details = Text("", Dim);
        _details.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(_details);
        var buttons = new HBoxContainer();
        column.AddChild(buttons);
        _wear = AddButton(buttons, "Wear", WearOrTakeOff);
        _give = AddButton(buttons, "Give to next", GiveToNext);
        _drop = AddButton(buttons, "Drop", DropSelected);
        buttons.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        AddButton(buttons, "Close", Close);
    }

    /// <summary>The world to show items of (the view sets it with each region).</summary>
    public GameWorld? World
    {
        get => _world;
        set => _world = value;
    }

    /// <summary>Open on <paramref name="member"/>'s items in <see cref="World"/>.</summary>
    public void Open(int member)
    {
        if (_world is not null)
        {
            Open(_world, member);
        }
    }

    public void Open(GameWorld world, int member)
    {
        _world = world;
        _member = member;
        Visible = true;
        Refresh();
    }

    public void Close() => Visible = false;

    public override void _UnhandledInput(InputEvent e)
    {
        if (Visible && (e.IsActionPressed(Controls.Back) || e.IsActionPressed(Controls.ToggleInventory)))
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }

    private WorldObject? Holder => _world?[_member];

    private void Refresh()
    {
        if (_world is null)
        {
            return;
        }
        _money.Text = $"Money: {Money()}";
        foreach (Node b in _members.GetChildren())
        {
            b.QueueFree();
        }
        foreach (WorldObject m in _world.Party)
        {
            int slot = m.Index;
            var b = Small(new Button { Text = MemberName(slot), ToggleMode = true, ButtonPressed = slot == _member, FocusMode = FocusModeEnum.None });
            b.Pressed += () =>
            {
                _member = slot;
                Refresh();
            };
            _members.AddChild(b);
        }
        _rows.Clear();
        _list.Clear();
        if (Holder is { } holder)
        {
            foreach (Item item in holder.Items.OrderBy(i => i.Equipped ? i.Slot : Equipment.Slots + 1))
            {
                string name = ItemName(item);
                string where = item.Equipped ? $"{Equipment.Names[item.Slot]}: " : "";
                _list.AddItem(where + name + (item.Quantity > 1 ? $" ×{item.Quantity}" : ""));
                _rows.Add(item);
            }
            if (_rows.Count == 0)
            {
                _list.AddItem("(carrying nothing)");
                _list.SetItemDisabled(0, true);
            }
        }
        ShowDetails();
    }

    private Item? Selected => _list.GetSelectedItems() is [int i, ..] && i < _rows.Count ? _rows[i] : null;

    private string ItemName(Item item)
    {
        string name = _world!.Items.NameOf(item.Record);
        return name.Length > 0 ? name : $"item {item.Rdff}";
    }

    private void ShowDetails()
    {
        Item? item = Selected;
        ItemType? type = item is null ? null : _world!.Items.TypeOf(item.Record);
        _details.Text = item is null ? "Double-click an item to wear it or take it off."
            : $"{ItemName(item)}: worth {item.Record.Value}"
              + (type is { IsWeapon: true } ? $", damage {type.Damage}" : "")
              + (type is { Placement: not ItemPlacement.None } ? $", worn: {type.Placement}" : "")
              + (item.Equipped ? $" (worn: {Equipment.Names[item.Slot]})" : "");
        _wear.Disabled = item is null || (!item.Equipped && type is null or { Placement: ItemPlacement.None });
        _wear.Text = item is { Equipped: true } ? "Take off" : "Wear";
        _give.Disabled = item is null || _world!.Party.Count() < 2;
        _drop.Disabled = item is null;
    }

    private void WearOrTakeOff()
    {
        if (Selected is not { } item || Holder is not { } holder)
        {
            return;
        }
        if (item.Equipped)
        {
            item.Slot = Equipment.Pack;
        }
        else if (!_world!.Equip(holder, item))
        {
            _details.Text = $"No free place to wear {ItemName(item)}.";
            return;
        }
        Refresh();
    }

    private void GiveToNext()
    {
        if (Selected is not { } item || Holder is not { } holder)
        {
            return;
        }
        var others = _world!.Party.Where(p => p.Index != _member).ToList();
        WorldObject to = others.FirstOrDefault(p => p.Index > _member) ?? others[0];
        holder.Items.Remove(item);
        item.Slot = Equipment.Pack;
        to.Items.Add(item);
        Did?.Invoke($"{MemberName(_member)} gives {ItemName(item)} to {MemberName(to.Index)}");
        Refresh();
    }

    private void DropSelected()
    {
        if (Selected is not { } item || Holder is not { } holder)
        {
            return;
        }
        item.Slot = Equipment.Pack;
        if (_world!.DropItem(holder, item, Math.Max(1, item.Quantity)) is null)
        {
            _details.Text = $"{ItemName(item)} can't be put down here.";
            return;
        }
        Did?.Invoke($"{MemberName(_member)} drops {ItemName(item)}");
        Refresh();
    }

    // -- building -------------------------------------------------------------------------

    internal static StyleBoxFlat Panel() => new()
    {
        BgColor = new Color(0.12f, 0.08f, 0.05f, 0.96f),
        BorderColor = new Color(0.6f, 0.42f, 0.25f),
        BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
        ContentMarginLeft = 6, ContentMarginTop = 4, ContentMarginRight = 6, ContentMarginBottom = 5,
    };

    internal static Label Text(string text, Color? color = null)
    {
        var label = Small(new Label { Text = text });
        if (color is { } c)
        {
            label.AddThemeColorOverride("font_color", c);
        }
        return label;
    }

    internal static Button AddButton(Container parent, string text, Action pressed)
    {
        var b = Small(new Button { Text = text, FocusMode = FocusModeEnum.None });
        b.Pressed += pressed;
        parent.AddChild(b);
        return b;
    }

    internal static T Small<T>(T control) where T : Control
    {
        control.AddThemeFontSizeOverride("font_size", FontSize);
        return control;
    }
}
