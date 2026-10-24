using DarkSun.Data.Images;
using DarkSun.Game.World;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// A container on the map opened (a chest, a bag, a body; the verb window's
/// Open, DSUN.EXE's VIEW INVENTORY): its own small window on the map,
/// <c>WIND</c> 13501 over the wooden frame <c>BMP</c> 13008, with six slots
/// (<c>APFM</c> 11213–11218, three by two) (<c>6527:002C</c>). The original
/// lays the contents out by each item record's +0x11 (its slot, 1–6; a
/// misplaced item takes the first free one, <c>6527:0909</c>), so a container
/// shows at most six things; here they are shown in order, six at a time, and
/// the rest show as those are taken. Each item's icon is centred in its slot
/// when it is 16 × 16 or less (<c>6527:075C</c>).
/// <para>
/// A click on an item takes it: coins become the party's money
/// (<c>6527:0FD4</c>), anything else goes into the pack of whoever opened it
/// (the original puts it on the pointer, to drop on a party member). A
/// right-click on an item shows the item window (<see cref="ItemWindow"/>).
/// Esc, or a click outside, closes it.
/// </para>
/// </summary>
public partial class ContainerWindow : Control
{
    public const int Slots = 6;

    private const int Window = 13501, Backdrop = 13008, FirstSlot = 11213, IconSize = 16;

    private readonly List<(Control Control, TextureRect Icon, Label Count)> _slots = [];

    /// <summary>The container open, or null.</summary>
    public WorldObject? Container { get; private set; }

    /// <summary>Who opened it: what is taken goes to them.</summary>
    public WorldObject? Opener { get; private set; }

    /// <summary>An item's icon, by <c>RDFF</c>.</summary>
    public Func<int, Texture2D?> ItemIcon { get; set; } = _ => null;

    /// <summary>Take this item (a slot clicked).</summary>
    public Action<Item>? Take { get; set; }

    /// <summary>Describe this item (a slot right-clicked).</summary>
    public Action<Item>? Describe { get; set; }

    /// <summary>Is a window over it (the item window): its clicks aren't this window's to close it.</summary>
    public Func<bool> Covered { get; set; } = () => false;

    public override void _Ready()
    {
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
    }

    /// <summary>
    /// Show <paramref name="container"/>'s contents, opened by
    /// <paramref name="opener"/>, beside <paramref name="at"/> (screen
    /// position), in <paramref name="palette"/> (the region's).
    /// </summary>
    public void Open(WorldObject container, WorldObject opener, Palette palette, Vector2 at)
    {
        (Container, Opener) = (container, opener);
        foreach (Node child in GetChildren())
        {
            RemoveChild(child);
            child.QueueFree();
        }
        _slots.Clear();
        if (UiWindowView.Build(Window, palette, Backdrop) is not { } window)
        {
            return;
        }
        AddChild(window);
        for (int n = 0; n < Slots; n++)
        {
            if (window.Slot(FirstSlot + n) is not { } control)
            {
                continue;
            }
            control.MouseFilter = MouseFilterEnum.Stop;
            var icon = new TextureRect { Size = control.Size, StretchMode = TextureRect.StretchModeEnum.KeepCentered, MouseFilter = MouseFilterEnum.Ignore };
            var count = new Label
            {
                Size = control.Size,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                LabelSettings = GameFonts.Settings(Colors.White),
                MouseFilter = MouseFilterEnum.Ignore,
            };
            control.AddChild(icon);
            control.AddChild(count);
            int slot = n;
            control.GuiInput += e => SlotInput(slot, e);
            _slots.Add((control, icon, count));
        }
        Size = window.Size;

        // The screen's scale; beside the pointer, kept on screen (as the verb window).
        Vector2 screen = GetViewportRect().Size;
        float scale = Math.Max(1f, MathF.Floor(Math.Min(screen.X / RetroScreen.Width, screen.Y / RetroScreen.Height)));
        Scale = new Vector2(scale, scale);
        Vector2 shown = Size * scale;
        Position = new Vector2(Math.Clamp(at.X + 6, 0, Math.Max(0, screen.X - shown.X)), Math.Clamp(at.Y + 6, 0, Math.Max(0, screen.Y - shown.Y)));
        Visible = true;
        Refresh();
    }

    public void Close()
    {
        Visible = false;
        (Container, Opener) = (null, null);
    }

    /// <summary>
    /// An item's icon in a slot of <paramref name="box"/>'s size, at its own size, as DSUN.EXE draws it
    /// (<c>6527:075C</c>, and the items page's <c>74F1:20EF</c>): up to 16 × 16 it is centred in the
    /// 16 × 16 inside the slot's border; a bigger one is drawn from that corner, cut to the slot.
    /// </summary>
    public static void PlaceIcon(TextureRect icon, Vector2 box)
    {
        Vector2 size = icon.Texture?.GetSize() ?? Vector2.Zero;
        bool small = size.X <= IconSize && size.Y <= IconSize;
        icon.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
        icon.StretchMode = TextureRect.StretchModeEnum.Keep;
        icon.ClipContents = !small;
        icon.Position = small ? new Vector2((int)(IconSize - size.X) / 2 + 1, (int)(IconSize - size.Y) / 2 + 1) : Vector2.One;
        icon.Size = small ? size : box - new Vector2(2, 2);
    }

    /// <summary>The item shown in slot <paramref name="slot"/> (0–5), or null.</summary>
    public Item? ItemAt(int slot) => Container?.Items.ElementAtOrDefault(slot) is { } item && slot < Slots ? item : null;

    /// <summary>Show the contents again (after something was taken).</summary>
    public void Refresh()
    {
        if (Container is null)
        {
            return;
        }
        for (int n = 0; n < _slots.Count; n++)
        {
            var (_, icon, count) = _slots[n];
            Item? item = ItemAt(n);
            Texture2D? texture = item is null ? null : ItemIcon(item.Rdff);
            icon.Texture = texture;
            PlaceIcon(icon, _slots[n].Control.Size);
            count.Text = item is { Quantity: > 1 } many ? many.Quantity.ToString() : "";
        }
    }

    /// <summary>Click slot <paramref name="slot"/> as the mouse would (for the headless checks).</summary>
    public void Click(int slot, MouseButton button = MouseButton.Left) =>
        SlotInput(slot, new InputEventMouseButton { ButtonIndex = button, Pressed = true });

    private void SlotInput(int slot, InputEvent e)
    {
        if (e is not InputEventMouseButton { Pressed: true } click || ItemAt(slot) is not { } item)
        {
            return;
        }
        if (click.ButtonIndex == MouseButton.Left)
        {
            Take?.Invoke(item);
            Refresh();
        }
        else if (click.ButtonIndex == MouseButton.Right)
        {
            Describe?.Invoke(item);
        }
        GetViewport()?.SetInputAsHandled();
    }

    public override void _Input(InputEvent e)
    {
        if (!Visible || Covered())
        {
            return;
        }
        if (e is InputEventMouseButton { Pressed: true } click && !new Rect2(GlobalPosition, Size * Scale).HasPoint(click.Position))
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
        else if (e.IsActionPressed(Controls.Back))
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }
}
