using DarkSun.Data.Gff;
using DarkSun.Data.Images;
using DarkSun.Data.Items;
using DarkSun.Game.Characters;
using DarkSun.Game.World;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// The party's character screens on the map, built from DSUN.EXE's own
/// windows and art. The bottom bar's icons switch between them:
/// <list type="bullet">
/// <item><b>Character</b> (C): View Character, <c>WIND</c> 11500 over
/// <c>BMP</c> 11000. The four boxes, and the selected member's sheet in the
/// panel (<c>APFM</c> 11269): race, classes and levels, abilities, hit
/// points, armour class and THAC0 (<see cref="CharacterSheet"/>),
/// experience, alignment, psionics, sphere.</item>
/// <item><b>Items</b> (I): the inventory, <c>WIND</c> 13500 over <c>BMP</c>
/// 13001. The boxes down the left; the body (<c>BMP</c> 13004) with its 14
/// equipment slots around it (<c>APFM</c> 11216–11229, slot n at 11216 + n,
/// each with its silhouette, <c>BMP</c> 13007 frame 9 + n); the pack
/// (<c>APFM</c> 11230–11241, 12 at a time, MORE for the rest); DROP and SPLIT.
/// With a container open (Open on the map) its contents show in place of
/// the body (<c>APFM</c> 13200 and 11248–11259) over a chest, a bag or a
/// parchment (<c>BMP</c> 13002, 13003, 13005).</item>
/// </list>
/// Items move by clicking one, then where it goes: an equipment slot (worn,
/// if the slot and the class allow), the pack (taken off, or out of the
/// container), the container (put in), another member's box (given).
/// Double-click wears, takes off or takes; right-click describes. DSUN.EXE
/// drags items with the pointer instead; its spell and psionic screens
/// (the other two icons) aren't ported.
/// </summary>
public partial class CharacterScreen : RetroScreen
{
    public enum Page { Character, Items }

    private const int PaletteNumber = 1001;
    private const int SheetWindow = 11500, SheetBackdrop = 11000, ItemsWindow = 13500, ItemsBackdrop = 13001;
    private const int FirstBox = 11300, FirstLeader = 11309, FirstComputerControl = 11313, NameBar = 11318;
    private const int CharacterButton = 10300, ItemsButton = 11304, SpellsButton = 11305, PsionicsButton = 11306, MenuButton = 11308, ReturnButton = 10308;
    private const int SheetArea = 11269, SheetDivider = 104, FirstText = 11319, SecondText = 11320;
    private const int DropButton = 13300, SplitButton = 13302, MoreButton = 13303, SellButton = 13304;
    private const int FirstWorn = 11216, FirstPackSlot = 11230, PackSlots = 12, ContainerArea = 13200, FirstInside = 11248, InsideSlots = 12;
    private const int SlotArt = 13007, EmptyFrame = 0, SelectedFrame = 4, CantFrame = 6, FirstSilhouette = 9;
    private const int Body = 13004, ChestArt = 13002, BagArt = 13003, ParchmentArt = 13005;

    private static readonly Color Ink = new(0.95f, 0.9f, 0.75f);
    private static readonly Color Gold = new(1f, 0.9f, 0.5f);

    private enum Where { Worn, Pack, Inside }

    /// <summary>One item slot: an <c>APFM</c> drawn into.</summary>
    private sealed class ItemSlot
    {
        public required Where Where { get; init; }
        public required int Index { get; init; }
        public required Control Control { get; init; }
        public required TextureRect Back { get; init; }
        public required TextureRect Icon { get; init; }
        public required TextureRect Mark { get; init; }
        public required Label Count { get; init; }
        public Item? Item { get; set; }
    }

    /// <summary>A page: its window and the boxes and name it shares with the other.</summary>
    private sealed class PageView
    {
        public required UiWindowView Window { get; init; }
        public required Control Root { get; init; }
        public List<TextureRect> Pictures { get; } = [];
        public List<Label> Names { get; } = [];
        public required Label Name { get; init; }
    }

    private GffFile _resources = null!;
    private Palette _palette = null!;
    private PageView _sheetPage = null!, _itemsPage = null!;
    private Label _sheet = null!, _sheetMore = null!, _info = null!;
    private TextureRect _body = null!, _containerArt = null!;
    private readonly List<ItemSlot> _slots = [];
    private readonly Dictionary<int, Texture2D?> _slotArt = [];
    private readonly Dictionary<int, Texture2D?> _portraits = [];

    private GameWorld? _world;
    private int _member;
    private Page _page;
    private WorldObject? _container;
    private (Item Item, WorldObject Holder)? _picked;
    private int _packPage, _insidePage;

    /// <summary>A party member's character (its sheet), by slot; null for a member without one.</summary>
    public Func<int, Character?> CharacterOf { get; set; } = _ => null;

    /// <summary>The party members' names, by slot.</summary>
    public Func<int, string> MemberName { get; set; } = slot => $"Character {slot + 1}";

    /// <summary>An item's picture: its map sprite (<c>OJFF</c> = its <c>RDFF</c> number).</summary>
    public Func<int, Texture2D?> ItemIcon { get; set; } = _ => null;

    /// <summary>A spell's or power's name (<c>SPIN</c> n).</summary>
    public Func<int, string?> SpellName { get; set; } = _ => null;

    /// <summary>The party's money (<c>gname[10]</c>).</summary>
    public Func<int> Money { get; set; } = () => 0;

    /// <summary>Something happened worth logging (an item worn, given, dropped).</summary>
    public Action<string>? Did { get; set; }

    public bool IsOpen => Visible;

    public Page Showing => _page;

    public override void _Ready()
    {
        base._Ready();
        Visible = false;
        _resources = Session.Open("RESOURCE.GFF");
        _palette = Session.Assets.Palette(_resources.Read("PAL ", PaletteNumber));
        _sheetPage = BuildPage(SheetWindow, SheetBackdrop);
        _itemsPage = BuildPage(ItemsWindow, ItemsBackdrop);

        // The sheet: the panel the original fills with the character's details.
        UiWindowView sheet = _sheetPage.Window;
        Rect2 area = sheet.RectOf("APFM", SheetArea);
        // BMP 11000 divides the panel in two (at y 104): who the character is above, the rest below.
        _sheet = Text(sheet, area.Position + new Vector2(3, 1), area.Size.X - 5, "");
        _sheet.Size = new Vector2(area.Size.X - 5, SheetDivider - area.Position.Y - 2);
        _sheetMore = Text(sheet, new Vector2(area.Position.X + 3, SheetDivider + 3), area.Size.X - 5, "");
        _sheetMore.Size = new Vector2(area.Size.X - 5, area.End.Y - SheetDivider - 3);
        _sheetMore.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        foreach (int n in new[] { FirstText, SecondText })
        {
            if (sheet.Button(n) is { } b)
            {
                b.Visible = false;
            }
        }

        // The items: the body (or an open container), the slots, the details on the right.
        UiWindowView items = _itemsPage.Window;
        Rect2 inside = items.RectOf("APFM", ContainerArea);
        _body = new TextureRect { Texture = Art("BMP ", Body, 0), Position = new Vector2(inside.Position.X + 2, 12), MouseFilter = MouseFilterEnum.Ignore };
        _containerArt = new TextureRect { Position = inside.Position, Size = inside.Size, MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        items.AddChild(_body);
        items.AddChild(_containerArt);
        items.MoveChild(_body, 1);
        items.MoveChild(_containerArt, 2);
        for (int n = 0; n < Equipment.Slots; n++)
        {
            AddSlot(items, Where.Worn, n, FirstWorn + n);
        }
        for (int n = 0; n < PackSlots; n++)
        {
            AddSlot(items, Where.Pack, n, FirstPackSlot + n);
        }
        for (int n = 0; n < InsideSlots; n++)
        {
            AddSlot(items, Where.Inside, n, FirstInside + n);
        }
        _info = Text(items, new Vector2(228, 18), 88, "");
        _info.Size = new Vector2(88, 140);
        _info.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        if (items.Button(SellButton) is { } sell)
        {
            sell.Visible = false; // selling is the shop's (SHOP)
        }
        items.Button(DropButton)!.Pressed += DropPicked;
        items.Button(SplitButton)!.Pressed += SplitPicked;
        items.Button(MoreButton)!.Pressed += () => TurnPack(+1);
        foreach (int n in new[] { DropButton, SplitButton, MoreButton })
        {
            if (items.ButtonText(n) is { } t)
            {
                t.LabelSettings = GameFonts.Settings(Ink);
            }
        }
        items.Button(DropButton)!.TooltipText = "Drop the chosen item here on the map";
        items.Button(SplitButton)!.TooltipText = "Split the chosen stack in two";
        items.Button(MoreButton)!.TooltipText = "The next twelve things in the pack (or the wheel)";
    }

    private Texture2D? Art(string tag, int number, int frame) =>
        _resources.TryRead(tag, number, out var data) && Session.Assets.Frames(data) is var frames && frame < frames.Count
            ? Textures.ToTexture(frames[frame], _palette)
            : null;

    private Texture2D? SlotArtFrame(int frame)
    {
        if (!_slotArt.TryGetValue(frame, out Texture2D? t))
        {
            _slotArt[frame] = t = Art("BMP ", SlotArt, frame);
        }
        return t;
    }

    private PageView BuildPage(int number, int backdrop)
    {
        UiWindowView window = UiWindowView.Build(number, _palette, backdrop)!;
        window.Visible = false;
        Canvas.AddChild(window);
        Rect2 bar = window.RectOf("BUTN", NameBar);
        var page = new PageView { Window = window, Root = window, Name = Text(window, bar.Position + new Vector2(0, 1), bar.Size.X, "", HorizontalAlignment.Center) };
        if (window.TextBox(4003) is { } nameBox)
        {
            nameBox.Visible = false; // the name is drawn centred on the bar instead
        }
        for (int i = 0; i < GameWorld.PartySize; i++)
        {
            int slot = i;
            TextureButton box = window.Button(FirstBox + i)!;
            var picture = new TextureRect
            {
                Position = new Vector2(1, 1),
                Size = box.Size - new Vector2(2, 2),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                ClipContents = true,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            box.AddChild(picture);
            box.GuiInput += e =>
            {
                if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
                {
                    BoxClicked(slot);
                }
            };
            page.Pictures.Add(picture);
            // As wide as the gap to the next box allows (the sheet's boxes stand two abreast).
            float room = Enumerable.Range(0, GameWorld.PartySize).Select(n => window.RectOf("BUTN", FirstBox + n))
                .Where(r => r.Position.Y == box.Position.Y && r.Position.X > box.Position.X).Select(r => r.Position.X - box.Position.X - 2).DefaultIfEmpty(box.Size.X + 20).Min();
            float width = Math.Min(box.Size.X + 20, room);
            Label name = Text(window, new Vector2(box.Position.X + (box.Size.X - width) / 2, box.Position.Y + box.Size.Y + 1), width, "", HorizontalAlignment.Center);
            name.ClipText = true;
            page.Names.Add(name);
            foreach (int n in new[] { FirstLeader + i, FirstComputerControl + i })
            {
                if (window.Button(n) is { } b)
                {
                    b.MouseFilter = MouseFilterEnum.Ignore; // the leader and computer control: shown, not working yet
                }
            }
        }
        window.Button(CharacterButton)!.Pressed += () => Show(Page.Character);
        window.Button(CharacterButton)!.TooltipText = "Character (C)";
        window.Button(ItemsButton)!.Pressed += () => Show(Page.Items);
        window.Button(ItemsButton)!.TooltipText = "Items (I)";
        foreach (int n in new[] { SpellsButton, PsionicsButton, MenuButton })
        {
            if (window.Button(n) is { } b)
            {
                b.Modulate = new Color(1, 1, 1, 0.45f);
                b.MouseFilter = MouseFilterEnum.Ignore;
            }
        }
        window.Button(ReturnButton)!.Pressed += Close;
        window.Button(ReturnButton)!.TooltipText = "Return (Esc)";
        return page;
    }

    private void AddSlot(UiWindowView window, Where where, int index, int apfm)
    {
        if (window.Slot(apfm) is not { } control)
        {
            return;
        }
        control.MouseFilter = MouseFilterEnum.Stop;
        TextureRect Layer() => new()
        {
            Size = control.Size,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        var slot = new ItemSlot
        {
            Where = where,
            Index = index,
            Control = control,
            Back = Layer(),
            Icon = Layer(),
            Mark = Layer(),
            Count = new Label
            {
                Size = control.Size,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                LabelSettings = GameFonts.Settings(Ink),
                MouseFilter = MouseFilterEnum.Ignore,
            },
        };
        slot.Icon.Position = new Vector2(2, 2);
        slot.Icon.Size = control.Size - new Vector2(4, 4);
        foreach (Control c in new Control[] { slot.Back, slot.Icon, slot.Mark, slot.Count })
        {
            control.AddChild(c);
        }
        control.GuiInput += e => SlotInput(slot, e);
        _slots.Add(slot);
    }

    private static Label Text(Control parent, Vector2 at, float width, string text, HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var label = new Label
        {
            Position = at,
            Size = new Vector2(width, 10),
            Text = text,
            HorizontalAlignment = align,
            MouseFilter = MouseFilterEnum.Ignore,
            LabelSettings = GameFonts.Settings(Ink, lineSpacing: -1),
        };
        parent.AddChild(label);
        return label;
    }

    // -- opening and closing --------------------------------------------------------------

    /// <summary>Show <paramref name="page"/> for party member <paramref name="member"/>, with <paramref name="container"/> open on the items page.</summary>
    public void Open(GameWorld world, int member, Page page, WorldObject? container = null)
    {
        _world = world;
        _member = world[member] is { Kind: ObjectKind.Party } ? member : world.Party.FirstOrDefault()?.Index ?? 0;
        _container = container;
        (_picked, _packPage, _insidePage) = (null, 0, 0);
        Visible = true;
        Show(container is not null ? Page.Items : page);
    }

    public void Close()
    {
        Visible = false;
        (_container, _picked) = (null, null);
    }

    private void Show(Page page)
    {
        _page = page;
        _sheetPage.Window.Visible = page == Page.Character;
        _itemsPage.Window.Visible = page == Page.Items;
        Refresh();
    }

    public override void _Input(InputEvent e)
    {
        if (!Visible || e is not InputEventKey { Pressed: true } key || key.Unicode == '?')
        {
            return; // ? is the key help's
        }
        if (e.IsActionPressed(Controls.Back))
        {
            if (_picked is not null)
            {
                _picked = null;
                Refresh();
            }
            else
            {
                Close();
            }
        }
        else if (e.IsActionPressed(Controls.ToggleCharacter))
        {
            Toggle(Page.Character);
        }
        else if (e.IsActionPressed(Controls.ToggleInventory))
        {
            Toggle(Page.Items);
        }
        else if (e.IsActionPressed(Controls.NextCharacter) && _world is not null)
        {
            var members = _world.Party.Select(p => p.Index).ToList();
            if (members.Count > 0)
            {
                SelectMember(members[(members.IndexOf(_member) + 1) % members.Count]);
            }
        }
        GetViewport().SetInputAsHandled(); // the map gets no keys meanwhile
    }

    private void Toggle(Page page)
    {
        if (_page == page)
        {
            Close();
        }
        else
        {
            Show(page);
        }
    }

    // -- showing --------------------------------------------------------------------------

    private WorldObject? Member => _world?[_member];

    private void Refresh()
    {
        if (_world is null)
        {
            return;
        }
        PageView page = _page == Page.Character ? _sheetPage : _itemsPage;
        for (int i = 0; i < GameWorld.PartySize; i++)
        {
            bool there = _world[i] is { Kind: ObjectKind.Party };
            page.Pictures[i].Texture = there && CharacterOf(i) is { } c ? Portrait(c) : null;
            page.Names[i].Text = there ? MemberName(i) : "";
            page.Names[i].Modulate = i == _member ? Gold : Colors.White;
        }
        page.Name.Text = MemberName(_member);
        if (_page == Page.Character)
        {
            (_sheet.Text, _sheetMore.Text) = SheetText();
        }
        else
        {
            RefreshItems();
        }
    }

    private (string Who, string More) SheetText()
    {
        if (Member is not { } member)
        {
            return ("", "");
        }
        Vitals v = member.Vitals;
        string status = v.Status switch
        {
            Vitals.Well => "",
            >= Vitals.Destroyed => "DESTROYED",
            >= Vitals.Dead => "DEAD",
            >= Vitals.Down => "UNCONSCIOUS",
            _ => "WOUNDED",
        };
        if (CharacterOf(_member) is not { } c)
        {
            return ($"HP {v.Hp}/{v.MaxHp}  {status}", "No character sheet: this member was added for testing.");
        }
        var cc = new CharacterCreation(Session.Rules, Session.Names, new Random());
        var levels = cc.Levels(c);
        string Abil(Ability a) => $"{a.ToString().ToUpperInvariant()} {cc.Adjusted(c, a),2}";
        int ac = CharacterSheet.ArmorClass(cc.Adjusted(c, Ability.Dex), member.Items, _world!.Items);
        string who = $"{Session.Rules.Race(c.Race).Name.ToUpperInvariant()} {c.Gender.ToString().ToUpperInvariant()}\n"
            + string.Join(" / ", c.Classes.Select((k, n) => $"{Session.Rules.Class(k).Name.ToUpperInvariant()} {levels[n]}")) + "\n"
            + $"{Abil(Ability.Str)}   {Abil(Ability.Int)}\n{Abil(Ability.Dex)}   {Abil(Ability.Wis)}\n{Abil(Ability.Con)}   {Abil(Ability.Cha)}";
        string more = $"HP {v.Hp}/{v.MaxHp}  AC {ac}  THAC0 {CharacterSheet.Thac0(c, levels)}\n"
            + $"EXP {c.Experience}   {Spaced(c.Alignment.ToString())}\n"
            + $"PSI: {string.Join(", ", c.Disciplines.Select(d => d.ToString().ToUpperInvariant()))}"
            + (c.Sphere is { } sphere ? $"\nSPHERE: {sphere.ToString().ToUpperInvariant()}" : "")
            + (status.Length > 0 ? $"\n{status}" : "");
        return (who, more);
    }

    private static string Spaced(string pascal) =>
        string.Concat(pascal.Select((ch, i) => i > 0 && char.IsUpper(ch) ? " " + ch : ch.ToString())).ToUpperInvariant();

    private List<Item> Pack => Member?.Items.Where(i => !i.Equipped).ToList() ?? [];

    private void RefreshItems()
    {
        WorldObject? member = Member;
        List<Item> pack = Pack;
        _packPage = Math.Clamp(_packPage, 0, Math.Max(0, (pack.Count - 1) / PackSlots));
        List<Item> inside = _container?.Items ?? [];
        _insidePage = Math.Clamp(_insidePage, 0, Math.Max(0, (inside.Count - 1) / InsideSlots));
        _body.Visible = _container is null;
        _containerArt.Visible = _container is not null;
        if (_container is not null)
        {
            _containerArt.Texture = Art("BMP ", ContainerArtOf(_container), 0);
        }
        foreach (ItemSlot slot in _slots)
        {
            slot.Item = slot.Where switch
            {
                Where.Worn => member?.Items.FirstOrDefault(i => i.Slot == slot.Index),
                Where.Pack => pack.ElementAtOrDefault(_packPage * PackSlots + slot.Index),
                _ => inside.ElementAtOrDefault(_insidePage * InsideSlots + slot.Index),
            };
            bool shown = slot.Where != Where.Inside || _container is not null;
            slot.Control.Visible = shown;
            slot.Back.Texture = slot.Where == Where.Worn && slot.Item is null ? SlotArtFrame(FirstSilhouette + slot.Index) : SlotArtFrame(EmptyFrame);
            slot.Icon.Texture = slot.Item is { } item ? ItemIcon(item.Rdff) : null;
            slot.Count.Text = slot.Item is { Quantity: > 1 } many ? many.Quantity.ToString() : "";
            bool picked = slot.Item is not null && _picked?.Item == slot.Item;
            bool cant = slot.Item is not null && member is not null && slot.Where != Where.Worn && !_world!.CanUse(member, slot.Item);
            slot.Mark.Texture = picked ? SlotArtFrame(SelectedFrame) : cant ? SlotArtFrame(CantFrame) : null;
            slot.Mark.Modulate = cant && !picked ? new Color(1, 1, 1, 0.6f) : Colors.White;
        }
        UiWindowView items = _itemsPage.Window;
        bool stack = _picked is { } p && _world!.Stacks(p.Item.Record) && p.Item.Quantity > 1 && p.Holder == member;
        Enable(items.Button(SplitButton)!, stack);
        Enable(items.Button(DropButton)!, _picked is { } d && d.Holder == member);
        Enable(items.Button(MoreButton)!, pack.Count > PackSlots);
        if (_picked is null)
        {
            ShowInfo(null);
        }
    }

    private static void Enable(TextureButton b, bool on)
    {
        b.Disabled = !on;
        b.Modulate = on ? Colors.White : new Color(1, 1, 1, 0.5f);
    }

    /// <summary>A chest for a wooden container, a bag for a leather one, else a parchment (a body, a skeleton).</summary>
    private int ContainerArtOf(WorldObject container) =>
        container.Id == -GameWorld.DeadBody ? ParchmentArt
        : (container.Item is { } item ? _world!.Items.TypeOf(item) : null) is { IsContainer: true } type
            ? type.MadeOf switch
            {
                ItemMaterial.Wooden => ChestArt,
                ItemMaterial.Leather => BagArt,
                _ => ParchmentArt,
            }
            : ParchmentArt;

    /// <summary>The right-hand panel: <paramref name="item"/> described, or the pack's page, the container and the money.</summary>
    private void ShowInfo(Item? item, WorldObject? holder = null, string? note = null)
    {
        if (_world is null)
        {
            return;
        }
        string text;
        if (item is not null)
        {
            text = ItemText.Describe(_world, item, holder ?? Member, MemberName(_member), SpellName);
        }
        else
        {
            int count = Pack.Count, pages = Math.Max(1, (count + PackSlots - 1) / PackSlots);
            text = $"MONEY {Money()}\n\nPACK {count}" + (pages > 1 ? $" (page {_packPage + 1}/{pages})" : "")
                + (_container is { } c ? $"\n\n{(c.Name.Length > 0 ? c.Name.ToUpperInvariant() : "CONTAINER")}: {c.Items.Count}" : "")
                + "\n\nClick a thing, then where it goes. Right-click: what it is.";
        }
        _info.Text = note is null ? text : $"{note}\n\n{text}";
    }

    private Texture2D? Portrait(Character c)
    {
        int number = new CharacterCreation(Session.Rules, Session.Names, new Random()).Portrait(c);
        if (_portraits.TryGetValue(number, out Texture2D? t))
        {
            return t;
        }
        t = _resources.TryRead("BMP ", number, out var data) && Session.Assets.Frames(data) is [var frame, ..]
            ? new AtlasTexture
            {
                Atlas = Textures.ToTexture(frame, _palette),
                Region = new Rect2(Math.Max(0, (frame.Width - 32) / 2), 0, Math.Min(32, frame.Width), Math.Min(32, frame.Height)),
            }
            : null;
        _portraits[number] = t;
        return t;
    }

    // -- for the checks (--check-character): clicks as the player makes them ---------------

    /// <summary>Click slot <paramref name="index"/> of the worn slots (0), the pack (1) or the container (2), as the mouse would.</summary>
    public void Click(int where, int index, bool doubleClick = false, MouseButton button = MouseButton.Left)
    {
        if (_slots.FirstOrDefault(s => (int)s.Where == where && s.Index == index) is { } slot)
        {
            slot.Control.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = button, Pressed = true, DoubleClick = doubleClick });
        }
    }

    /// <summary>Click party member <paramref name="slot"/>'s box.</summary>
    public void ClickBox(int slot) =>
        (_page == Page.Character ? _sheetPage : _itemsPage).Window.Button(FirstBox + slot)!
            .EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });

    /// <summary>Press <c>BUTN</c> <paramref name="number"/> of the page showing (10300 Character, 11304 Items, 13300 DROP, 13302 SPLIT, 13303 MORE, 10308 Return).</summary>
    public void Press(int number) =>
        (_page == Page.Character ? _sheetPage : _itemsPage).Window.Button(number)?.EmitSignal(BaseButton.SignalName.Pressed);

    /// <summary>The item in a slot, as <see cref="Click"/> numbers them.</summary>
    public Item? ItemAt(int where, int index) => _slots.FirstOrDefault(s => (int)s.Where == where && s.Index == index)?.Item;

    public Item? Picked => _picked?.Item;

    public string Info => _info.Text;

    public string Sheet => _sheet.Text + "\n" + _sheetMore.Text;

    // -- clicks ---------------------------------------------------------------------------

    private WorldObject? HolderOf(ItemSlot slot) => slot.Where == Where.Inside ? _container : Member;

    private void SlotInput(ItemSlot slot, InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true } click)
        {
            switch (click.ButtonIndex)
            {
                case MouseButton.Left when click.DoubleClick:
                    DoubleClicked(slot);
                    break;
                case MouseButton.Left:
                    Clicked(slot);
                    break;
                case MouseButton.Right when slot.Item is { } item:
                    ShowInfo(item, HolderOf(slot));
                    break;
                case MouseButton.WheelDown or MouseButton.WheelUp:
                    int by = click.ButtonIndex == MouseButton.WheelDown ? 1 : -1;
                    if (slot.Where == Where.Inside)
                    {
                        _insidePage = Math.Max(0, _insidePage + by);
                        RefreshItems();
                    }
                    else
                    {
                        TurnPack(by);
                    }
                    break;
            }
            GetViewport().SetInputAsHandled();
        }
    }

    private void Clicked(ItemSlot slot)
    {
        if (_picked is not { } picked)
        {
            if (slot.Item is { } item && HolderOf(slot) is { } holder)
            {
                _picked = (item, holder);
                RefreshItems();
                ShowInfo(item, holder);
            }
            return;
        }
        if (picked.Item == slot.Item)
        {
            _picked = null;
            RefreshItems();
            return;
        }
        string? note = slot.Where switch
        {
            Where.Worn => WearIn(picked, slot.Index),
            Where.Pack => ToPack(picked, slot),
            _ => PutIn(picked, slot),
        };
        RefreshItems();
        if (note is not null)
        {
            ShowInfo(_picked?.Item, _picked?.Holder, note);
        }
    }

    private void DoubleClicked(ItemSlot slot)
    {
        if (slot.Item is not { } item || HolderOf(slot) is not { } holder || Member is not { } member || _world is null)
        {
            return;
        }
        _picked = null;
        string? note = null;
        if (slot.Where == Where.Inside)
        {
            note = Take(item) ? null : $"{MemberName(_member)} can't carry any more.";
        }
        else if (item.Equipped)
        {
            item.Slot = Equipment.Pack;
            Did?.Invoke($"{MemberName(_member)} takes off {ItemText.Name(_world, item)}");
        }
        else
        {
            EquipResult r = _world.Equip(holder, item);
            note = Refusal(r, item);
            if (r == EquipResult.Worn)
            {
                Did?.Invoke($"{MemberName(_member)} wears {ItemText.Name(_world, item)}");
            }
        }
        RefreshItems();
        if (note is not null)
        {
            ShowInfo(item, member, note);
        }
    }

    private string? Refusal(EquipResult r, Item item) => r switch
    {
        EquipResult.Worn => null,
        EquipResult.WrongClass => $"{MemberName(_member)}'s class can't use {ItemText.Name(_world!, item)}.",
        EquipResult.NotWearable => $"{ItemText.Name(_world!, item)} isn't worn there.",
        _ => $"No free place to wear {ItemText.Name(_world!, item)}.",
    };

    /// <summary>Wear the picked item in slot <paramref name="slot"/> (taken out of the container first).</summary>
    private string? WearIn((Item Item, WorldObject Holder) picked, int slot)
    {
        if (Member is not { } member || _world is null)
        {
            return null;
        }
        if (picked.Holder != member && !Take(picked.Item))
        {
            return $"{MemberName(_member)} can't carry any more.";
        }
        EquipResult r = _world.EquipAt(member, picked.Item, slot);
        if (r == EquipResult.Worn)
        {
            _picked = null;
            Did?.Invoke($"{MemberName(_member)} wears {ItemText.Name(_world, picked.Item)}");
        }
        else
        {
            _picked = (picked.Item, member);
        }
        return Refusal(r, picked.Item);
    }

    /// <summary>To the pack: a worn item is taken off, a container's taken; another pack item is picked instead.</summary>
    private string? ToPack((Item Item, WorldObject Holder) picked, ItemSlot slot)
    {
        if (Member is not { } member || _world is null)
        {
            return null;
        }
        if (picked.Holder != member)
        {
            _picked = null;
            return Take(picked.Item) ? null : $"{MemberName(_member)} can't carry any more.";
        }
        if (picked.Item.Equipped)
        {
            picked.Item.Slot = Equipment.Pack;
            _picked = null;
            Did?.Invoke($"{MemberName(_member)} takes off {ItemText.Name(_world, picked.Item)}");
            return null;
        }
        _picked = slot.Item is { } other ? (other, member) : null;
        if (_picked is { } now)
        {
            ShowInfo(now.Item, member);
        }
        return null;
    }

    private string? PutIn((Item Item, WorldObject Holder) picked, ItemSlot slot)
    {
        if (_container is not { } container || _world is null)
        {
            return null;
        }
        if (picked.Holder == container)
        {
            _picked = slot.Item is { } other ? (other, container) : null;
            return null;
        }
        _picked = null;
        if (!_world.MoveItem(picked.Holder, container, picked.Item))
        {
            return $"{container.Name} can't hold any more.";
        }
        Did?.Invoke($"{MemberName(_member)} puts {ItemText.Name(_world, picked.Item)} in {container.Name}");
        return null;
    }

    private bool Take(Item item)
    {
        if (_container is not { } container || Member is not { } member || !_world!.MoveItem(container, member, item))
        {
            return false;
        }
        Did?.Invoke($"{MemberName(_member)} takes {ItemText.Name(_world, item)} from {container.Name}");
        return true;
    }

    /// <summary>A box: with an item picked, give it to that member; otherwise show them.</summary>
    private void BoxClicked(int slot)
    {
        if (_world?[slot] is not { Kind: ObjectKind.Party } to)
        {
            return;
        }
        if (_page == Page.Items && _picked is { } picked && slot != _member)
        {
            _picked = null;
            picked.Item.Slot = Equipment.Pack;
            if (_world.MoveItem(picked.Holder, to, picked.Item))
            {
                Did?.Invoke($"{MemberName(_member)} gives {ItemText.Name(_world, picked.Item)} to {MemberName(slot)}");
                RefreshItems();
                ShowInfo(null, null, $"Given to {MemberName(slot)}.");
            }
            else
            {
                RefreshItems();
                ShowInfo(null, null, $"{MemberName(slot)} can't carry any more.");
            }
            return;
        }
        SelectMember(slot);
    }

    private void SelectMember(int slot)
    {
        _member = slot;
        (_picked, _packPage) = (null, 0);
        Refresh();
    }

    private void TurnPack(int by)
    {
        int pages = Math.Max(1, (Pack.Count + PackSlots - 1) / PackSlots);
        _packPage = ((_packPage + by) % pages + pages) % pages;
        RefreshItems();
    }

    private void DropPicked()
    {
        if (_picked is not { } picked || Member is not { } member || picked.Holder != member || _world is null)
        {
            return;
        }
        _picked = null;
        picked.Item.Slot = Equipment.Pack;
        string name = ItemText.Name(_world, picked.Item);
        bool dropped = _world.DropItem(member, picked.Item, Math.Max(1, picked.Item.Quantity)) is not null;
        if (dropped)
        {
            Did?.Invoke($"{MemberName(_member)} drops {name}");
        }
        RefreshItems();
        ShowInfo(null, null, dropped ? $"{name} is on the ground." : $"{name} can't be put down here.");
    }

    private void SplitPicked()
    {
        if (_picked is not { } picked || Member is not { } member || _world is null)
        {
            return;
        }
        Item? part = _world.Split(member, picked.Item, picked.Item.Quantity / 2);
        _picked = part is null ? picked : (part, member);
        RefreshItems();
        ShowInfo(_picked?.Item, member, part is null ? "The pack is full." : null);
    }
}
