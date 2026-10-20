using DarkSun.Game.World;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// An open container (Open on a chest, a bag on the ground, a dead body;
/// <see cref="GameWorld.IsContainer"/>): what is in it beside a party member's
/// pack. Take / Take all move items to the member, Put in moves the member's
/// selected item (taken off first) into the container; double-click does the
/// same. DSUN.EXE's VIEW INVENTORY screen isn't ported; this stands in for it.
/// </summary>
public partial class ContainerWindow : PanelContainer
{
    private static readonly Color Heading = new(0.95f, 0.8f, 0.55f);
    private static readonly Color Dim = new(0.7f, 0.65f, 0.6f);

    private GameWorld? _world;
    private WorldObject? _container;
    private int _member;
    private readonly List<Item> _inside = [], _carried = [];
    private Label _title = null!, _note = null!;
    private HBoxContainer _members = null!;
    private ItemList _insideList = null!, _carriedList = null!;

    /// <summary>The party members' names, by slot.</summary>
    public Func<int, string> MemberName { get; set; } = slot => $"Character {slot + 1}";

    /// <summary>Something happened worth logging (an item taken or put in).</summary>
    public Action<string>? Did { get; set; }

    public bool IsOpen => Visible;

    public override void _Ready()
    {
        Visible = false;
        SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
        GrowHorizontal = GrowDirection.Both;
        GrowVertical = GrowDirection.Both;
        CustomMinimumSize = new Vector2(440, 250);
        AddThemeStyleboxOverride("panel", InventoryWindow.Panel());
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 3);
        AddChild(column);
        var top = new HBoxContainer();
        column.AddChild(top);
        _title = InventoryWindow.Text("", Heading);
        top.AddChild(_title);
        top.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        top.AddChild(InventoryWindow.Text("Esc closes", Dim));
        _members = new HBoxContainer();
        column.AddChild(_members);
        var lists = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        column.AddChild(lists);
        _insideList = List(lists, Take);
        _carriedList = List(lists, PutIn);
        _note = InventoryWindow.Text("", Dim);
        _note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(_note);
        var buttons = new HBoxContainer();
        column.AddChild(buttons);
        InventoryWindow.AddButton(buttons, "Take", Take);
        InventoryWindow.AddButton(buttons, "Take all", TakeAll);
        InventoryWindow.AddButton(buttons, "Put in", PutIn);
        buttons.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        InventoryWindow.AddButton(buttons, "Close", Close);
    }

    private static ItemList List(Container parent, Action activated)
    {
        var list = InventoryWindow.Small(new ItemList { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 140) });
        list.ItemActivated += _ => activated();
        parent.AddChild(list);
        return list;
    }

    /// <summary>Open <paramref name="container"/> for party member <paramref name="member"/>.</summary>
    public void Open(GameWorld world, WorldObject container, int member)
    {
        (_world, _container, _member) = (world, container, member);
        _note.Text = "";
        Visible = true;
        Refresh();
    }

    public void Close() => Visible = false;

    public override void _UnhandledInput(InputEvent e)
    {
        if (Visible && e.IsActionPressed(Controls.Back))
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }

    private WorldObject? Member => _world?[_member];

    private string ItemName(Item item)
    {
        string name = _world!.Items.NameOf(item.Record);
        return (name.Length > 0 ? name : $"item {item.Rdff}") + (item.Quantity > 1 ? $" ×{item.Quantity}" : "");
    }

    private void Refresh()
    {
        if (_world is null || _container is null)
        {
            return;
        }
        _title.Text = _container.Name.Length > 0 ? _container.Name : "Container";
        foreach (Node b in _members.GetChildren())
        {
            b.QueueFree();
        }
        foreach (WorldObject m in _world.Party)
        {
            int slot = m.Index;
            var b = InventoryWindow.Small(new Button { Text = MemberName(slot), ToggleMode = true, ButtonPressed = slot == _member, FocusMode = FocusModeEnum.None });
            b.Pressed += () =>
            {
                _member = slot;
                Refresh();
            };
            _members.AddChild(b);
        }
        Fill(_insideList, _inside, _container.Items, "(empty)");
        Fill(_carriedList, _carried, Member?.Items ?? [], "(carrying nothing)");
    }

    private void Fill(ItemList list, List<Item> rows, IEnumerable<Item> items, string empty)
    {
        rows.Clear();
        list.Clear();
        foreach (Item item in items)
        {
            list.AddItem((item.Equipped ? "(worn) " : "") + ItemName(item));
            rows.Add(item);
        }
        if (rows.Count == 0)
        {
            list.AddItem(empty);
            list.SetItemDisabled(0, true);
        }
    }

    private static Item? Selected(ItemList list, List<Item> rows) => list.GetSelectedItems() is [int i, ..] && i < rows.Count ? rows[i] : null;

    private void Take()
    {
        if (Selected(_insideList, _inside) is { } item)
        {
            Move(_container!, Member, item, "takes");
        }
    }

    private void TakeAll()
    {
        foreach (Item item in _inside.ToList())
        {
            if (!Move(_container!, Member, item, "takes"))
            {
                break;
            }
        }
    }

    private void PutIn()
    {
        if (Selected(_carriedList, _carried) is { } item && Member is { } member)
        {
            Move(member, _container, item, "puts in");
        }
    }

    private bool Move(WorldObject from, WorldObject? to, Item item, string verb)
    {
        if (_world is null || to is null)
        {
            return false;
        }
        string name = ItemName(item);
        bool moved = _world.MoveItem(from, to, item);
        _note.Text = moved ? "" : $"{(to == _container ? _title.Text : MemberName(_member))} can't hold any more.";
        if (moved)
        {
            Did?.Invoke($"{MemberName(_member)} {verb} {name}");
        }
        Refresh();
        return moved;
    }
}
