using DarkSun.Data.Gff;
using DarkSun.Data.Images;
using DarkSun.Data.Items;
using DarkSun.Game.World;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// An item right-clicked: DSUN.EXE's item window (<c>WIND</c> 15502,
/// "VIEWITEM", over the parchment <c>BMP</c> 15001; <c>91F7:0000</c>), beside
/// the pointer (to its right, or its left where that runs off the screen) and
/// centred on it up and down (<c>6BB8:0716</c>, <c>6BB8:0737</c>).
/// <list type="bullet">
/// <item>The item (<c>91F7:0C29</c>): its picture, a small one (up to 16 × 16)
/// in the frame <c>ICON</c> 15104 at (11, 10), a bigger one (up to 85 × 43)
/// as it is; its value ("%u$", times the count for a stack) and HEAVY (a
/// weapon over 30 in weight) beside a picture; then from (13, under the
/// picture) a line every 7: the count of a stack ("%d "), "2 handed" for a
/// two-handed weapon, the name ("%Fs%+d" with a bonus), a weapon's damage
/// (<c>78A9:2A9F</c>, "%d*%dD%d%+d"), and AC BONUS for armour. A magic
/// item's spell shows as its icon at (76, 12) (<c>91F7:14D8</c>: <c>ICON</c>
/// spell + 20999, a power's + 2861).</item>
/// <item>INFO (<c>BUTN</c> 15304, <c>91F7:03D0</c>) turns to who may use it:
/// "USEABLE BY:" at (13, 11) and each party member whose class may, a line
/// every 8, or "NO ONE" (<c>91F7:12D8</c>). It is there for things worn or
/// wielded (<c>IT1R</c> +9) and magic items (item +0x0F); INFO again turns back.</item>
/// </list>
/// USE (<c>BUTN</c> 15305, under the spell icon) is there only in the
/// inventory, for what <see cref="ItemMagic.Offered"/> allows; it casts the
/// item's spell (<see cref="Use"/>), and INFO's page hides it. Any click
/// outside, a right-click or Esc closes the window.
/// </summary>
public partial class ItemWindow : Control
{
    private const int Window = 15502, Backdrop = 15001, InfoButton = 15304, UseButton = 15305, SmallFrame = 15104;
    private const int TextColour = 31, TextShadow = 17;
    private const int Left = 13, LineHeight = 7, UsersLineHeight = 8, Small = 16;
    private const int SpellIcons = 20999, PowerIcons = 2861, FirstPower = 139, LastPower = 172;

    private Control _details = null!, _users = null!;
    private TextureButton? _use;

    /// <summary>USE was pressed (the window has closed).</summary>
    public Action<Item>? Use { get; set; }

    /// <summary>The item shown, or null.</summary>
    public Item? Item { get; private set; }

    /// <summary>Is it showing who may use the item (INFO pressed)?</summary>
    public bool ShowingUsers => _users?.Visible == true;

    /// <summary>The lines it shows (for the headless checks).</summary>
    public IReadOnlyList<string> Lines => [.. (ShowingUsers ? _users : _details).GetChildren().OfType<Label>().Select(l => l.Text)];

    public override void _Ready()
    {
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
    }

    /// <summary>
    /// Show <paramref name="item"/> (named and described from
    /// <paramref name="world"/>; <paramref name="icon"/> its picture) for a
    /// pointer at <paramref name="pointer"/> in game pixels on a screen
    /// <paramref name="area"/> big, drawn <paramref name="scale"/> times as
    /// big. <paramref name="users"/>: the party members who may use it.
    /// <paramref name="useOffered"/>: USE is there (the inventory's, for an item that offers it).
    /// </summary>
    public void Open(GameWorld world, Item item, Texture2D? icon, IReadOnlyList<string> users, Palette palette, Vector2 pointer, Vector2 area, float scale = 1,
        bool useOffered = false)
    {
        Item = item;
        foreach (Node child in GetChildren())
        {
            RemoveChild(child);
            child.QueueFree();
        }
        if (UiWindowView.Build(Window, palette, Backdrop) is not { } window)
        {
            return;
        }
        AddChild(window);
        Color Of(int index) => palette[index] is var c ? Color.Color8(c.R, c.G, c.B) : Colors.White;
        LabelSettings text = GameFonts.Settings(Of(TextColour), shadow: Of(TextShadow));
        _details = new Control { MouseFilter = MouseFilterEnum.Ignore };
        _users = new Control { MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        window.AddChild(_details);
        window.AddChild(_users);
        void Line(Control layer, string s, float x, float y) =>
            layer.AddChild(new Label { Text = s, Position = new Vector2(x, y), Size = new Vector2(window.Size.X - x - 4, GameFonts.Size), ClipText = true, LabelSettings = text, MouseFilter = MouseFilterEnum.Ignore });

        ItemType? type = world.Items.TypeOf(item.Record);
        bool stacks = type is { Stacks: true };
        bool weapon = type is not null && ((int)type.Weapon & 0x13) != 0;

        // The picture: a small one in its frame, the lines under it; a bigger one as it is.
        float y = 12, besideX = Left;
        bool picture = false;
        if (icon is not null && icon.GetWidth() <= Small && icon.GetHeight() <= Small)
        {
            if (Art("ICON", SmallFrame, palette) is { } frame)
            {
                _details.AddChild(new TextureRect { Texture = frame, Position = new Vector2(11, 10), MouseFilter = MouseFilterEnum.Ignore });
            }
            _details.AddChild(new TextureRect { Texture = icon, Position = new Vector2(11 + (18 - icon.GetWidth()) / 2, 10 + (18 - icon.GetHeight()) / 2), MouseFilter = MouseFilterEnum.Ignore });
            (y, besideX, picture) = (28, 11 + 18 + 4, true);
        }
        else if (icon is not null && icon.GetWidth() <= 85 && icon.GetHeight() <= 43)
        {
            _details.AddChild(new TextureRect { Texture = icon, Position = new Vector2(Left, 12), MouseFilter = MouseFilterEnum.Ignore });
            (y, besideX, picture) = (icon.GetHeight() + 12, Left + icon.GetWidth() + 4, true);
        }

        // Beside the picture: the value, and HEAVY.
        int value = item.Record.Value * (stacks ? Math.Max(1, item.Quantity) : 1);
        if (item.Record.Value is not (0 or ItemRecord.None) && (stacks || item.Record.Value != 1))
        {
            Line(_details, $"{value}$", picture ? besideX : Left, picture ? 12 : y);
            y += picture ? 0 : LineHeight;
        }
        if (type is { Weight: > 30 } && (type.Weapon & WeaponKind.Melee) != 0 && (type.Kind & ItemTypeFlags.TwoHanded) == 0)
        {
            Line(_details, "HEAVY", picture ? besideX : Left, picture ? 19 : y);
            y += picture ? 0 : LineHeight;
        }

        string name = world.Items.NameOf(item.Record);
        string count = stacks && item.Quantity > 1 ? $"{item.Quantity} " : "";
        if (weapon && (type!.Kind & ItemTypeFlags.TwoHanded) != 0)
        {
            Line(_details, $"{count}2 handed", Left, y);
            (y, count) = (y + LineHeight, "");
        }
        if (name.Length > 0)
        {
            Line(_details, count + name, Left, y);
            y += LineHeight;
        }
        if (weapon && type!.DamageDice > 0)
        {
            Line(_details, $"{Math.Max(1, (int)type.Attacks)}*{type.DamageDice}D{type.DamageSides}{type.DamageBonus + item.Record.Bonus:+0;-0;+0}", Left, y);
            y += LineHeight;
        }
        if (type is not null && (type.Kind & ItemTypeFlags.Armor) != 0)
        {
            Line(_details, $"AC BONUS:{type.ArmorClass + item.Record.Bonus}", Left, y);
        }

        // A magic item's spell (or psionic power) as its icon, where the USE button is.
        if (item.Record.PowerFor(type) is > 0 and int power
            && Art("ICON", power < FirstPower ? power + SpellIcons : power <= LastPower ? power + PowerIcons : 0, palette) is { } spell)
        {
            _details.AddChild(new TextureRect { Texture = spell, Position = new Vector2(76, 12), MouseFilter = MouseFilterEnum.Ignore });
        }
        _use = window.Button(UseButton);
        if (_use is not null)
        {
            _use.Visible = useOffered;
            _use.TooltipText = "Use it";
            _use.Pressed += PressUse;
        }

        // INFO: who may use it.
        Line(_users, "USEABLE BY:", Left, 11);
        for (int i = 0; i < users.Count; i++)
        {
            Line(_users, users[i], Left, 19 + UsersLineHeight * i);
        }
        if (users.Count == 0)
        {
            Line(_users, "NO ONE", Left, 19);
        }
        if (window.Button(InfoButton) is { } info)
        {
            // 91F7:0000: INFO for things worn or wielded and for magic items; not for fixtures (IT1R +8 & 0x30, 91F7:14D8).
            bool offered = type is not null && (type.Placement != ItemPlacement.None || item.Record.Flags != 0) && (type.Material & 0x30) == 0;
            info.Disabled = !offered;
            info.Modulate = offered ? Colors.White : new Color(1, 1, 1, 0.5f);
            info.TooltipText = "Who can use it";
            info.Pressed += ToggleUsers;
        }

        Size = window.Size;
        Scale = new Vector2(scale, scale);
        // 6BB8:0716: to the pointer's right, or its left if the window would run off the screen;
        // 6BB8:0737: centred on it up and down, kept on the screen, no lower than 97.
        float x = pointer.X + 18 + Size.X > area.X ? pointer.X - Size.X : pointer.X + 18;
        float top = pointer.Y - Size.Y / 2 + 9 < 0 ? 0 : pointer.Y + Size.Y / 2 + 9 < area.Y - 1 ? pointer.Y + 9 - Size.Y / 2 : area.Y - 1 - Size.Y;
        Position = new Vector2(Math.Max(0, x), top > 116 ? 97 : top) * scale;
        Visible = true;
    }

    /// <summary>INFO: the item, or who may use it.</summary>
    public void ToggleUsers()
    {
        if (_users is null || _details is null)
        {
            return;
        }
        _users.Visible = !_users.Visible;
        _details.Visible = !_users.Visible;
        if (_use is not null && Use is not null)
        {
            _use.Disabled = _users.Visible; // 91F7:12D8
        }
    }

    /// <summary>Is USE there (for the headless checks)?</summary>
    public bool UseOffered => _use is { Visible: true, Disabled: false };

    /// <summary>USE: the window closes and the item is used.</summary>
    public void PressUse()
    {
        if (Item is not { } item || !UseOffered)
        {
            return;
        }
        Close();
        Use?.Invoke(item);
    }

    public void Close()
    {
        Visible = false;
        Item = null;
    }

    public override void _Input(InputEvent e)
    {
        if (!Visible)
        {
            return;
        }
        if (e is InputEventMouseButton { Pressed: true } click
            && (click.ButtonIndex == MouseButton.Right || !new Rect2(GlobalPosition, Size * GetGlobalTransform().Scale).HasPoint(click.Position)))
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

    private static Texture2D? Art(string tag, int number, Palette palette)
    {
        GffFile resources = Session.Open("RESOURCE.GFF");
        return number > 0 && resources.TryRead(tag, number, out var data) && Session.Assets.Frames(data) is [var frame, ..] && frame.Width > 1
            ? Textures.ToTexture(frame, palette)
            : null;
    }
}
