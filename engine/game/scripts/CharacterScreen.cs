using DarkSun.Data.Gff;
using DarkSun.Data.Images;
using DarkSun.Data.Items;
using DarkSun.Game.Characters;
using DarkSun.Game.Magic;
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
/// 13001. The boxes down the left; the member's figure (<c>BMP</c> 20000 +
/// their look, on the parchment 13005: <c>74F1:17AF</c>) with its 14
/// equipment slots around it (<c>APFM</c> 11216–11229, slot n at 11216 + n,
/// each with its silhouette, <c>BMP</c> 13007 frame 9 + n); the pack
/// (<c>APFM</c> 11230–11241, 12 at a time, MORE for the rest); DROP and SPLIT.
/// A carried bag or box double-clicked opens in place of the body
/// (<c>6527:12BD</c>): first shut (<c>BMP</c> 20104 a bag, 20105 a box) for 15
/// screen refreshes, then open (13003, 13002, at <c>APFM</c> 13200), its six
/// slots over it: a bag's (leather, <c>IT1R</c> +8 = 5) the upper six of
/// <c>APFM</c> 11248–11259, a box's the lower six. So it holds six things.
/// Double-clicking it again, or right-clicking an empty slot of it, closes
/// it. A container doesn't go in another. (Containers on the map open in
/// their own window, <see cref="ContainerWindow"/>.)</item>
/// </list>
/// <item><b>Spells</b> and <b>Psionics</b>: View Character again, its panel
/// holding the slots DSUN.EXE draws spells into (<c>APFM</c> 11213–11247;
/// <see cref="CharacterScreen"/>'s other part).</item>
/// <item>A <b>store</b> (SHOP): the items page with the merchant's goods in
/// the container's place and the six slots by SELL (<c>APFM</c> 11242–11247)
/// for what the party offers.</item>
/// </list>
/// Items move by clicking one, then where it goes: an equipment slot (worn,
/// if the slot and the class allow), the pack (taken off, or out of the
/// container), the container (put in), another member's box (given).
/// Double-click wears, takes off or takes; right-click opens the item
/// window (<see cref="ItemWindow"/>). DSUN.EXE drags items with the
/// pointer instead. Each box has the leader button (<c>BUTN</c> 11309–11312: "MAKE %s LEADER") and computer control
/// (11313–11316: "COMPUTER CONTROL IS ON / OFF / LOCKED"); the wide button
/// (11308) opens the game menu.
/// </summary>
public partial class CharacterScreen : RetroScreen
{
    public enum Page { Character, Items, Spells, Psionics }

    private const int PaletteNumber = 1001;
    private const int SheetWindow = 11500, SheetBackdrop = 11000, ItemsWindow = 13500, ItemsBackdrop = 13001;
    private const int FirstBox = 11300, FirstLeader = 11309, FirstComputerControl = 11313, NameBar = 11318;
    private const int CharacterButton = 10300, ItemsButton = 11304, SpellsButton = 11305, PsionicsButton = 11306, MenuButton = 11308, ReturnButton = 10308;
    private const int SheetArea = 11269, SheetDivider = 104, FirstText = 11319, SecondText = 11320;
    private const int DropButton = 13300, SplitButton = 13302, MoreButton = 13303, SellButton = 13304;
    private const int FirstWorn = 11216, FirstPackSlot = 11230, PackSlots = 12, ContainerArea = 13200, FirstInside = 11248, InsideSlots = 12;
    private const int ContainerSlots = ContainerWindow.Slots;
    private const int FirstSellSlot = 11242, SellSlots = 6;
    private const int SlotArt = 13007, EmptyFrame = 0, SelectedFrame = 4, CantFrame = 6, FirstSilhouette = 9;
    private const int ChestArt = 13002, BagArt = 13003, ParchmentArt = 13005, Bars = 13000;

    /// <summary>The figures in the body's place: <c>BMP</c> 20000 + the member's look (0 human male … 13 thri-kreen, as the map icons <c>OJFF</c> 300–313).</summary>
    private const int FirstFigure = 20000, Figures = 14;

    /// <summary>Where a figure's centre goes (<c>74F1:17AF</c>: (63, 78) past window item 6, at (57, 18)).</summary>
    private static readonly Vector2 FigureCentre = new(120, 96);
    private const int ShutChestArt = 20105, ShutBagArt = 20104;

    /// <summary>How long a carried container shows shut before it opens: 15 refreshes of the 70 Hz screen (<c>70E7:00B8</c>).</summary>
    private const double Opening = 15 / 70.0;

    private static readonly Color Ink = new(0.95f, 0.9f, 0.75f);
    private static readonly Color Gold = new(1f, 0.9f, 0.5f);

    private enum Where { Worn, Pack, Inside, Sell }

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
        /// <summary>The items page's hit points and status words under the boxes (<c>74F1:0000</c>).</summary>
        public List<Label> Hp { get; } = [];
        public List<Label> States { get; } = [];
        public PartyFrames Frames { get; set; } = null!;
        public required Label Name { get; init; }
    }

    private GffFile _resources = null!;
    private Palette _palette = null!;
    private PageView _sheetPage = null!, _itemsPage = null!;
    private Label _sheet = null!, _sheetMore = null!, _info = null!, _status = null!, _itemsStatus = null!;
    private TextureRect _body = null!, _figure = null!, _containerArt = null!;
    private ItemWindow _itemWindow = null!;
    private readonly List<ItemSlot> _slots = [];
    private readonly Dictionary<int, Texture2D?> _slotArt = [];

    private GameWorld? _world;
    private int _member;
    private Page _page;
    private WorldObject? _container, _store;

    /// <summary>The carried bag or box open in the container's place (its <see cref="Item.Contents"/> are <see cref="_container"/>'s items), or null.</summary>
    private Item? _openItem;

    /// <summary>The open container still shows shut (<see cref="Opening"/>): its slots aren't shown yet.</summary>
    private bool _opening;
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

    /// <summary>The party's leader (the selected character, <c>gname[5]</c>).</summary>
    public Func<int> Leader { get; set; } = () => 0;

    /// <summary>Make a member the leader.</summary>
    public Action<int>? SetLeader { get; set; }

    /// <summary>The wide button: the game menu (the screens close first).</summary>
    public Action? OpenGameMenu { get; set; }

    /// <summary>The screens closed (a store's script goes on).</summary>
    public Action? Closed { get; set; }

    /// <summary>Change the party's money (a store).</summary>
    public Action<int>? SetMoney { get; set; }

    public bool IsOpen => Visible;

    public Page Showing => _page;

    /// <summary>
    /// Party member <paramref name="slot"/>'s portrait on the page showing, in
    /// viewport pixels (where a fight shows the member's damage while this
    /// screen covers the map, <c>896E:0ED3</c>), or null.
    /// </summary>
    public Rect2? PortraitRect(int slot) =>
        IsOpen && slot >= 0 && slot < Current.Pictures.Count && Current.Pictures[slot].IsVisibleInTree() ? Current.Pictures[slot].GetGlobalRect() : null;

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
        // The status line: BMP 11000's bar under the window.
        _status = Text(sheet, new Vector2(74, 175), 172, "", HorizontalAlignment.Center);
        _status.ClipText = true;
        BuildMagic(sheet);

        // The items: the body (or an open container), the slots, the details on the right.
        UiWindowView items = _itemsPage.Window;
        Rect2 inside = items.RectOf("APFM", ContainerArea);
        // 74F1:175A: the parchment where a container opens, then (74F1:17AF) the member's figure on it.
        _body = new TextureRect { Texture = Art("BMP ", ParchmentArt, 0), Position = inside.Position, MouseFilter = MouseFilterEnum.Ignore };
        _figure = new TextureRect { MouseFilter = MouseFilterEnum.Ignore };
        _body.AddChild(_figure);
        _containerArt = new TextureRect { Position = inside.Position, Size = inside.Size, MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        // 74F1:0000 draws BMP 13000's bars: frame 1 behind the name at (53, 3), frame 3, the
        // status line, at (46, 161); the status text starts at APFM 11270.
        var nameBar = new TextureRect { Texture = Art("BMP ", Bars, 1), Position = new Vector2(53, 3), MouseFilter = MouseFilterEnum.Ignore };
        var statusBar = new TextureRect { Texture = Art("BMP ", Bars, 3), Position = new Vector2(46, 161), MouseFilter = MouseFilterEnum.Ignore };
        items.AddChild(_body);
        items.AddChild(_containerArt);
        items.AddChild(nameBar);
        items.AddChild(statusBar);
        items.MoveChild(_body, 1);
        items.MoveChild(_containerArt, 2);
        items.MoveChild(statusBar, 3);
        items.MoveChild(nameBar, 4);
        Rect2 statusAt = items.RectOf("APFM", 11270);
        _itemsStatus = Text(items, statusAt.Position - new Vector2(0, 1), 176, "", HorizontalAlignment.Center);
        _itemsStatus.ClipText = true;
        _sheetPage.Frames = new PartyFrames(sheet, _status, MemberView, Leader);
        _itemsPage.Frames = new PartyFrames(items, _itemsStatus, MemberView, Leader);
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
        for (int n = 0; n < SellSlots; n++)
        {
            AddSlot(items, Where.Sell, n, FirstSellSlot + n);
        }
        _info = Text(items, new Vector2(228, 18), 88, "");
        _info.Size = new Vector2(88, 140);
        _info.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        items.Button(SellButton)!.Pressed += SellPressed;
        items.Button(SellButton)!.TooltipText = "Sell what is in the six slots below";
        items.Button(DropButton)!.Pressed += DropPicked;
        items.Button(SplitButton)!.Pressed += SplitPicked;
        items.Button(MoreButton)!.Pressed += () => TurnPack(+1);
        foreach (int n in new[] { DropButton, SplitButton, MoreButton, SellButton })
        {
            if (items.ButtonText(n) is { } t)
            {
                t.LabelSettings = GameFonts.Settings(Ink);
            }
        }
        items.Button(DropButton)!.TooltipText = "Drop the chosen item here on the map";
        items.Button(SplitButton)!.TooltipText = "Split the chosen stack in two";
        items.Button(MoreButton)!.TooltipText = "The next twelve things in the pack (or the wheel)";
        _itemWindow = new ItemWindow { Use = UseItem };
        Canvas.AddChild(_itemWindow);
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
            if (number == ItemsWindow)
            {
                // 74F1:0000 under each box: its hit points, "%d/%d" centred on x 27 at the box's
                // top + 33 (6CCA:1A1B), and its status word centred in x 1–51 at + 39 (78A9:1FBB).
                page.Hp.Add(Text(window, new Vector2(2, box.Position.Y + 33), 50, "", HorizontalAlignment.Center));
                page.States.Add(Text(window, new Vector2(1, box.Position.Y + 39), 50, "", HorizontalAlignment.Center));
            }
            if (window.Button(FirstLeader + i) is { } leader)
            {
                leader.ToggleMode = true;
                leader.Pressed += () => LeaderPressed(slot);
            }
            if (window.Button(FirstComputerControl + i) is { } computer)
            {
                computer.ToggleMode = true;
                computer.Pressed += () => ComputerPressed(slot);
            }
        }
        window.Button(CharacterButton)!.Pressed += () => Show(Page.Character);
        window.Button(CharacterButton)!.TooltipText = "Character (C)";
        window.Button(ItemsButton)!.Pressed += () => Show(Page.Items);
        window.Button(ItemsButton)!.TooltipText = "Items (I)";
        window.Button(SpellsButton)!.Pressed += () => Show(Page.Spells);
        window.Button(SpellsButton)!.TooltipText = "Spells: cast and learn";
        window.Button(PsionicsButton)!.Pressed += () => Show(Page.Psionics);
        window.Button(PsionicsButton)!.TooltipText = "Psionics: pick and use powers";
        window.Button(MenuButton)!.Pressed += () =>
        {
            Close();
            OpenGameMenu?.Invoke();
        };
        window.Button(MenuButton)!.TooltipText = "Game menu";
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

    /// <summary>
    /// Show <paramref name="page"/> for party member <paramref name="member"/>,
    /// with <paramref name="container"/> open on the items page, or
    /// <paramref name="store"/>'s goods (a merchant: SHOP).
    /// </summary>
    public void Open(GameWorld world, int member, Page page, WorldObject? container = null, WorldObject? store = null)
    {
        _world = world;
        _member = world[member] is { Kind: ObjectKind.Party } ? member : world.Party.FirstOrDefault()?.Index ?? 0;
        _store = store;
        _container = container ?? store;
        _openItem = null;
        _opening = false;
        (_picked, _packPage, _insidePage) = (null, 0, 0);
        ResetMagic();
        ResetStore();
        _status.Text = "";
        Visible = true;
        Show(_container is not null ? Page.Items : page);
    }

    public void Close()
    {
        if (!Visible)
        {
            return;
        }
        Visible = false;
        CloseSpellInfo();
        _itemWindow.Close();
        // An item of the member's still picked up stays on the pointer, to use with something (pointer mode 3).
        HeldOnClose = _picked is { } held && held.Holder == Member && _store is null ? held.Item : null;
        (_container, _store, _picked, _openItem, _opening) = (null, null, null, null, false);
        ResetStore();
        Closed?.Invoke();
    }

    private void Show(Page page)
    {
        _page = page;
        _sheetPage.Window.Visible = page != Page.Items;
        _itemsPage.Window.Visible = page == Page.Items;
        _sheet.Visible = _sheetMore.Visible = page == Page.Character;
        ShowMagic(page);
        Refresh();
    }

    private PageView Current => _page == Page.Items ? _itemsPage : _sheetPage;

    public override void _Input(InputEvent e)
    {
        if (!Visible || e is not InputEventKey { Pressed: true } key || key.Unicode == '?')
        {
            return; // ? is the key help's
        }
        if (e.IsActionPressed(Controls.Back) && _spellInfo is not null)
        {
            CloseSpellInfo();
        }
        else if (e.IsActionPressed(Controls.Back))
        {
            if (_picked is not null || _pickedSpell is not null)
            {
                (_picked, _pickedSpell) = (null, null);
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

    /// <summary>Show the party's hit points and status again (a fight goes on while the screen is open).</summary>
    public void ShowVitals()
    {
        if (IsOpen)
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        if (_world is null)
        {
            return;
        }
        PageView page = Current;
        int leader = Leader();
        for (int i = 0; i < GameWorld.PartySize; i++)
        {
            bool there = _world[i] is { Kind: ObjectKind.Party };
            page.Pictures[i].Texture = there && CharacterOf(i) is { } c ? Portrait(c) : null;
            if (i < page.Hp.Count)
            {
                // The active member in palette colour 47, the others in 52 (DS:326E).
                var colour = GameFonts.Settings(PaletteColour(i == _member ? 47 : 52));
                page.Hp[i].LabelSettings = page.States[i].LabelSettings = colour;
                page.Hp[i].Text = there ? $"{_world[i]!.Vitals.Hp}/{_world[i]!.Vitals.MaxHp}" : "";
                page.States[i].Text = there ? PartyFrames.StatusName(_world[i]!.Vitals.Status) : "";
            }
            if (page.Window.Button(FirstLeader + i) is { } l)
            {
                l.Visible = there;
                l.SetPressedNoSignal(i == leader);
                l.TooltipText = i == leader ? $"{MemberName(i)} is leader" : $"Make {MemberName(i)} leader";
            }
            if (page.Window.Button(FirstComputerControl + i) is { } cc)
            {
                cc.Visible = there;
                cc.SetPressedNoSignal(CharacterOf(i)?.ComputerControl == true);
                cc.TooltipText = $"Computer control is {(CharacterOf(i) is null ? "locked" : CharacterOf(i)!.ComputerControl ? "on" : "off")}";
            }
        }
        page.Name.Text = MemberName(_member);
        page.Frames.Refresh();
        switch (_page)
        {
            case Page.Character:
                (_sheet.Text, _sheetMore.Text) = SheetText();
                break;
            case Page.Items:
                RefreshItems();
                break;
            default:
                RefreshMagic();
                break;
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
        int ac = CharacterSheet.ArmorClass(cc.Adjusted(c, Ability.Dex), member.Items, _world!.Items, Session.Rules);
        string who = $"{Session.Rules.Race(c.Race).Name.ToUpperInvariant()} {c.Gender.ToString().ToUpperInvariant()}\n"
            + string.Join(" / ", c.Former.Select(f => cc.Active(c, f) ? $"{Session.Rules.Class(f.Class).Name.ToUpperInvariant()} {f.Level}" : $"({Session.Rules.Class(f.Class).Name.ToUpperInvariant()} {f.Level})")
                .Concat(c.Classes.Select((k, n) => $"{Session.Rules.Class(k).Name.ToUpperInvariant()} {levels[n]}"))) + "\n"
            + $"{Abil(Ability.Str)}   {Abil(Ability.Int)}\n{Abil(Ability.Dex)}   {Abil(Ability.Wis)}\n{Abil(Ability.Con)}   {Abil(Ability.Cha)}";
        string more = $"HP {v.Hp}/{v.MaxHp}  AC {ac}  THAC0 {CharacterSheet.Thac0(cc.ActiveLevels(c), Session.Rules)}\n"
            + $"EXP {c.Experience}   {Spaced(c.Alignment.ToString())}\n"
            + $"PSI {_magic.Psp(c)}/{_magic.MaxPsp(c)}: {string.Join(", ", c.Disciplines.Select(d => d.ToString().ToUpperInvariant()))}"
            + (c.Sphere is { } sphere ? $"\nSPHERE: {sphere.ToString().ToUpperInvariant()}" : "")
            + (status.Length > 0 ? $"\n{status}" : "");
        return (who, more);
    }

    private static string Spaced(string pascal) =>
        string.Concat(pascal.Select((ch, i) => i > 0 && char.IsUpper(ch) ? " " + ch : ch.ToString())).ToUpperInvariant();

    private List<Item> Pack => Member?.Items.Where(i => !i.Equipped && !_offered.Contains(i)).ToList() ?? [];

    private void RefreshItems()
    {
        WorldObject? member = Member;
        if (_openItem is { } open && member?.Items.Contains(open) != true)
        {
            CloseContainer(); // the open bag was given away, dropped or sold
        }
        List<Item> pack = Pack;
        Equipment.Arrange(pack);
        _packPage = Math.Clamp(_packPage, 0, Pages(pack, PackSlots) - 1);
        List<Item> inside = _container?.Items ?? [];
        Equipment.Arrange(inside);
        _insidePage = Math.Clamp(_insidePage, 0, Pages(inside, InsideShown.Count) - 1);
        _body.Visible = _container is null;
        ShowFigure();
        _containerArt.Visible = _container is not null;
        if (_container is not null)
        {
            int art = ContainerArtOf(_container);
            _containerArt.Texture = Art("BMP ", !_opening ? art : art == BagArt ? ShutBagArt : ShutChestArt, 0);
        }
        foreach (ItemSlot slot in _slots)
        {
            slot.Item = slot.Where switch
            {
                Where.Worn => member?.Items.FirstOrDefault(i => i.Slot == slot.Index),
                Where.Pack => pack.FirstOrDefault(i => i.Cell == _packPage * PackSlots + slot.Index),
                Where.Sell => _offered.ElementAtOrDefault(slot.Index),
                _ => InsideCell(slot) is int cell ? inside.FirstOrDefault(i => i.Cell == cell) : null,
            };
            bool shown = slot.Where switch
            {
                Where.Inside => _container is not null && !_opening && InsideCell(slot) is not null,
                Where.Sell => _store is not null,
                _ => true,
            };
            slot.Control.Visible = shown;
            slot.Back.Texture = slot.Where == Where.Worn && slot.Item is null ? SlotArtFrame(FirstSilhouette + slot.Index) : SlotArtFrame(EmptyFrame);
            // The picked item is on the pointer (HeldIcon): its place shows empty.
            bool picked = slot.Item is not null && _picked?.Item == slot.Item;
            slot.Icon.Texture = slot.Item is { } item && !picked ? ItemIcon(item.Rdff) : null;
            slot.Count.Text = slot.Item is { Quantity: > 1 } many && !picked ? many.Quantity.ToString() : "";
            bool cant = slot.Item is not null && member is not null && slot.Where != Where.Worn && !_world!.CanUse(member, slot.Item);
            slot.Mark.Texture = !picked && cant ? SlotArtFrame(CantFrame) : null;
            slot.Mark.Modulate = cant && !picked ? new Color(1, 1, 1, 0.6f) : Colors.White;
        }
        UiWindowView items = _itemsPage.Window;
        bool stack = _picked is { } p && _world!.Stacks(p.Item.Record) && p.Item.Quantity > 1 && p.Holder == member;
        Enable(items.Button(SplitButton)!, stack);
        Enable(items.Button(DropButton)!, _picked is { } d && d.Holder == member);
        Enable(items.Button(MoreButton)!, Pages(pack, PackSlots) > 1);
        items.Button(SellButton)!.Visible = _store is not null;
        if (_store is not null)
        {
            Enable(items.Button(SellButton)!, _offered.Count > 0);
            items.ButtonText(SellButton)!.Text = _offer is null ? "SELL" : "TAKE IT";
        }
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

    /// <summary>
    /// The member's figure in the body's place (<c>74F1:17AF</c>), centred on
    /// <see cref="FigureCentre"/>; none for a member without a character.
    /// </summary>
    private void ShowFigure()
    {
        Texture2D? figure = Member is not null && CharacterOf(_member) is { } c ? Art("BMP ", FirstFigure + FigureOf(c), 0) : null;
        _figure.Texture = figure;
        if (figure is not null)
        {
            _figure.Position = FigureCentre - _body.Position - new Vector2(figure.GetWidth() / 2, figure.GetHeight() / 2);
        }
    }

    /// <summary>A character's look (combat record +0x18): its map icon's, else the one its race and sex give.</summary>
    private static int FigureOf(Character c) =>
        c.Sprite - Session.Rules.IconBase is >= 0 and < Figures and int look
            ? look
            : new CharacterCreation(Session.Rules, Session.Names, new Random()).Icon(c) - Session.Rules.IconBase;

    /// <summary>The figure in the body's place: <c>BMP</c> number, or 0 for none (for the headless checks).</summary>
    public int Figure => _figure.Texture is null || Member is null || CharacterOf(_member) is not { } c ? 0 : FirstFigure + FigureOf(c);

    /// <summary>
    /// What a container shows as (<c>6527:12BD</c>): a bag if it is leather
    /// (<c>IT1R</c> +8 = 5), else a chest; something else (a store's goods, a
    /// body) a parchment.
    /// </summary>
    private int ContainerArtOf(WorldObject container) =>
        container.Id == -GameWorld.DeadBody ? ParchmentArt
        : (container.Item is { } item ? _world!.Items.TypeOf(item) : null) is { IsContainer: true } type
            ? type.MadeOf == ItemMaterial.Leather ? BagArt : ChestArt
            : ParchmentArt;

    /// <summary>
    /// Which of the twelve container slots (<c>APFM</c> 11248–11259) are the
    /// open container's: a store's goods take all twelve, a page at a time; a
    /// bag's six things the upper six, anything else's the lower six (<c>6527:12BD</c>).
    /// </summary>
    private (int First, int Count) InsideShown =>
        _store is not null ? (0, InsideSlots) : _container is { } c && ContainerArtOf(c) == BagArt ? (0, ContainerSlots) : (ContainerSlots, ContainerSlots);

    /// <summary>The container's cell a container slot shows (its page counted), or null for a slot that isn't the container's.</summary>
    private int? InsideCell(ItemSlot slot)
    {
        var (first, count) = InsideShown;
        int n = slot.Index - first;
        return n >= 0 && n < count ? _insidePage * count + n : null;
    }

    /// <summary>
    /// The item window (<see cref="ItemWindow"/>) for <paramref name="item"/>, held by
    /// <paramref name="holder"/>, by the pointer at <paramref name="pointer"/> (game pixels).
    /// USE is there for the member's own things (or what is in their open bag), not a store's.
    /// </summary>
    private void Describe(Item item, WorldObject? holder, Vector2 pointer)
    {
        if (_world is null)
        {
            return;
        }
        // 91F7:12D8: the members whose class may use it (74F1:30DC), by name.
        List<string> users = [.. _world.Party.Where(p => !p.Hidden && CharacterOf(p.Index) is not null && _world.CanUse(p, item)).Select(p => MemberName(p.Index))];
        bool own = _store is null && holder is not null && (holder == Member || _openItem is not null && holder == _container);
        _described = own ? holder : null;
        _itemWindow.Open(_world, item, ItemIcon(item.Rdff), users, _palette, pointer, new Vector2(Width, Height),
            useOffered: own && ItemMagic.Offered(item, _world.Items.TypeOf(item.Record), Session.SpellRules));
    }

    /// <summary>Who holds the item the item window shows, when it may be used (the member, or their open bag).</summary>
    private WorldObject? _described;

    /// <summary>
    /// USE in the item window (<c>91F7:03D0</c>, <see cref="ItemMagic.Plan"/>): a scroll or
    /// book teaches its spell or power; any other spell is cast at level 6, using one of the
    /// item's charges. In a fight that is the map's (<see cref="CastInFight"/>: on the
    /// member's turn, aimed there if it needs a target). Outside one, as for the member's
    /// own spells, only healing works, on the member using it (and a lemon makes them whole);
    /// anything else waits for a fight.
    /// </summary>
    private void UseItem(Item item)
    {
        if (_world is null || _described is not { } holder || Member is not { } member || CharacterOf(_member) is not { } c || !holder.Items.Contains(item))
        {
            return;
        }
        ItemUse use = ItemMagic.Plan(item, Session.SpellRules);
        string name = ItemText.Name(_world, item);
        string SpellName(int n) => Session.SpellName(n)?.ToUpperInvariant() ?? $"SPELL {n}";
        string message;
        if (use.Kind == ItemUseKind.Nothing)
        {
            message = Spellcasting.NothingHappens;
        }
        else if (use.Kind == ItemUseKind.Learn)
        {
            message = _world.LearnFrom(holder, item, c, SpellName) ?? Spellcasting.NothingHappens;
            Did?.Invoke($"{MemberName(_member)} uses {name}: {message.ToLowerInvariant()}");
        }
        else if (Fighting())
        {
            if (CastInFight?.Invoke(_member, item, holder, use) is not { } refused)
            {
                Close();
                return;
            }
            message = refused;
        }
        else
        {
            Vitals v = member.Vitals;
            string? result = use.Kind switch
            {
                ItemUseKind.FullHeal when v.Status < Vitals.Dead && v.Hp < v.MaxHp => Healed(v),
                ItemUseKind.FullHeal => Spellcasting.NothingHappens,
                ItemUseKind.OnSelf => Spellcasting.Heal(use.Spell, v, _random),
                _ => null,
            };
            message = result ?? "ONLY IN COMBAT";
            if (result is not null && result != Spellcasting.NothingHappens)
            {
                Did?.Invoke($"{MemberName(_member)} uses {name}: {result.ToLowerInvariant()}");
                if (_world.UseItemCharge(holder, item))
                {
                    message += $"; {name.ToUpperInvariant()} IS USED UP";
                }
            }
        }
        RefreshItems();
        Refresh();
        _status.Text = message;
        ShowInfo(null, null, message);
    }

    private static string Healed(Vitals v)
    {
        int before = v.Hp;
        (v.Hp, v.Status) = (v.MaxHp, Vitals.Well);
        return $"HEALED {v.Hp - before}";
    }

    /// <summary>Is the party fighting (USE casts on the map then)?</summary>
    public Func<bool> Fighting { get; set; } = () => false;

    /// <summary>
    /// USE in a fight: member, item, its holder and what it does; null when it was cast or
    /// is waiting for its target (the screens close), else why not.
    /// </summary>
    public Func<int, Item, WorldObject, ItemUse, string?>? CastInFight { get; set; }

    /// <summary>The item window (for the headless checks).</summary>
    public ItemWindow ItemWindow => _itemWindow;

    /// <summary>The right-hand panel: <paramref name="item"/> described, or the pack's page, the container and the money.</summary>
    private void ShowInfo(Item? item, WorldObject? holder = null, string? note = null)
    {
        if (_world is null)
        {
            return;
        }
        // In a store the panel stops above SELL and its six slots.
        _info.Size = new Vector2(88, _store is null ? 140 : 40);
        string text;
        if (item is not null && _store is not null)
        {
            text = $"{ItemText.Name(_world, item)}\n" + (holder == _store ? $"PRICE {Shop.Price(item)}$" : Shop.Offer(item) > 0 ? $"WORTH {Shop.Offer(item)}$" : "I COULDN'T SELL THAT");
            _itemsPage.Window.Button(SellButton)!.TooltipText = ItemText.Describe(_world, item, Member, MemberName(_member), SpellName);
        }
        else if (item is not null)
        {
            text = ItemText.Describe(_world, item, holder ?? Member, MemberName(_member), SpellName);
        }
        else if (_store is not null)
        {
            text = StoreText();
        }
        else
        {
            int count = Pack.Count, pages = Math.Max(1, (count + PackSlots - 1) / PackSlots);
            text = $"MONEY {Money()}\n\nPACK {count}" + (pages > 1 ? $" (page {_packPage + 1}/{pages})" : "")
                + (_container is { } c ? $"\n\n{(c.Name.Length > 0 ? c.Name.ToUpperInvariant() : "CONTAINER")}: {c.Items.Count}" : "")
                + "\n\nClick a thing, then where it goes. Right-click: what it is.";
        }
        _info.ClipText = _store is not null;
        _info.Text = note is null ? text : _store is not null ? $"{note}\n{text}" : $"{note}\n\n{text}";
    }

    /// <summary>A party box's picture: the character's map sprite standing, facing forwards.</summary>
    private Texture2D? Portrait(Character c) => CharacterGenView.StandingSprite(c.Sprite, _palette);

    // -- for the checks (--check-character): clicks as the player makes them ---------------

    /// <summary>Click slot <paramref name="index"/> of the worn slots (0), the pack (1) or the container (2, its own six), as the mouse would.</summary>
    public void Click(int where, int index, bool doubleClick = false, MouseButton button = MouseButton.Left)
    {
        if (SlotAt(where, index) is { } slot)
        {
            slot.Control.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = button, Pressed = true, DoubleClick = doubleClick });
        }
    }

    /// <summary>Click party member <paramref name="slot"/>'s box.</summary>
    public void ClickBox(int slot) =>
        Current.Window.Button(FirstBox + slot)!
            .EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });

    /// <summary>
    /// Press <c>BUTN</c> <paramref name="number"/> of the page showing (10300
    /// Character, 11304 Items, 11305 Spells, 11306 Psionics, 11308 the game
    /// menu, 11309+ leader, 11313+ computer control, 11319 / 11320 the text
    /// buttons, 13300 DROP, 13302 SPLIT, 13303 MORE, 13304 SELL, 10308 Return).
    /// </summary>
    public void Press(int number)
    {
        if (Current.Window.Button(number) is { } b)
        {
            if (b.ToggleMode)
            {
                b.SetPressedNoSignal(!b.ButtonPressed);
            }
            b.EmitSignal(BaseButton.SignalName.Pressed);
        }
    }

    /// <summary>The page's status line (the spells, psionics and leader messages).</summary>
    public string Status => _status.Text;

    /// <summary>The item in a slot, as <see cref="Click"/> numbers them.</summary>
    /// <summary>A container shows in the body's place (for the headless check).</summary>
    public bool ContainerShown => _container is not null && _containerArt.Visible;

    /// <summary>The carried bag or box open, or null (for the headless check).</summary>
    public Item? OpenItem => _openItem;

    public Item? ItemAt(int where, int index) => SlotAt(where, index)?.Item;

    /// <summary>The slot <see cref="Click"/> and <see cref="ItemAt"/> mean: the container's are counted from its first.</summary>
    private ItemSlot? SlotAt(int where, int index)
    {
        int n = where == (int)Where.Inside ? InsideShown.First + index : index;
        return _slots.FirstOrDefault(s => (int)s.Where == where && s.Index == n);
    }

    public Item? Picked => _picked?.Item;

    /// <summary>The member's item that was picked up when the screen closed, for Use with… on the map; null if none.</summary>
    public Item? HeldOnClose { get; private set; }

    public string Info => _info.Text;

    public string Sheet => _sheet.Text + "\n" + _sheetMore.Text;

    // -- clicks ---------------------------------------------------------------------------

    private WorldObject? HolderOf(ItemSlot slot) => slot.Where == Where.Inside ? _container : Member;

    private Color PaletteColour(int index) => _palette[index] is var c ? Color.Color8(c.R, c.G, c.B) : Colors.White;

    /// <summary>A member as View Character's frames show them.</summary>
    private PartyMemberView? MemberView(int slot)
    {
        if (_world?[slot] is not { Kind: ObjectKind.Party } m)
        {
            return null;
        }
        Character? c = CharacterOf(slot);
        int psp = c is null ? 0 : _magic.MaxPsp(c);
        return new PartyMemberView(MemberName(slot), m.Vitals.Hp, m.Vitals.MaxHp, c is null ? 0 : _magic.Psp(c), psp, m.Vitals.Status,
            c?.ComputerControl == true, Locked: c is null);
    }

    // -- leader and computer control --------------------------------------------------------

    private void LeaderPressed(int slot)
    {
        if (_world?[slot] is not { Kind: ObjectKind.Party })
        {
            return;
        }
        SetLeader?.Invoke(slot);
        Refresh();
        Say($"{MemberName(slot).ToUpperInvariant()} IS LEADER");
    }

    private void ComputerPressed(int slot)
    {
        string message = "COMPUTER CONTROL IS LOCKED";
        if (CharacterOf(slot) is { } c)
        {
            c.ComputerControl = !c.ComputerControl;
            message = $"COMPUTER CONTROL IS {(c.ComputerControl ? "ON" : "OFF")}";
        }
        Refresh();
        Say(message);
    }

    /// <summary>A message: the status line, and on the items page the right-hand panel.</summary>
    private void Say(string message)
    {
        Current.Frames.Resting = message;
        if (_page == Page.Items)
        {
            ShowInfo(null, null, message);
        }
    }

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
                    Describe(item, HolderOf(slot), Canvas.GetGlobalTransform().AffineInverse() * (slot.Control.GetGlobalTransform() * click.Position));
                    break;
                case MouseButton.Right when slot.Where == Where.Inside && _store is null:
                    CloseContainer();
                    RefreshItems();
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
            Where.Sell => OfferPicked(picked),
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
        if (slot.Where is Where.Worn or Where.Pack && _store is null && _world.IsContainerItem(item))
        {
            // 6527:12BD: a carried bag or box opens in the container's place (again: closes).
            if (_openItem == item)
            {
                CloseContainer();
            }
            else
            {
                OpenContainer(item);
            }
            RefreshItems();
            return;
        }
        if (slot.Where == Where.Inside)
        {
            note = Take(item);
        }
        else if (slot.Where == Where.Sell)
        {
            _offered.Remove(item);
            _offer = null;
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
        if (picked.Holder != member && Take(picked.Item) is { } refused)
        {
            return refused;
        }
        if (!member.Items.Contains(picked.Item))
        {
            _picked = null; // coins: they became money
            return null;
        }
        if (_offered.Remove(picked.Item))
        {
            _offer = null;
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
        int cell = _packPage * PackSlots + slot.Index;
        if (picked.Holder != member)
        {
            _picked = null;
            string? refused = Take(picked.Item);
            if (refused is null && member.Items.Contains(picked.Item))
            {
                PlaceAt(Pack, picked.Item, cell);
            }
            return refused;
        }
        if (_offered.Remove(picked.Item))
        {
            _picked = null;
            _offer = null;
            PlaceAt(Pack, picked.Item, cell);
            return null;
        }
        _picked = null;
        if (picked.Item.Equipped)
        {
            picked.Item.Slot = Equipment.Pack;
            Did?.Invoke($"{MemberName(_member)} takes off {ItemText.Name(_world, picked.Item)}");
        }
        PlaceAt(Pack, picked.Item, cell);
        return null;
    }

    /// <summary>
    /// Put <paramref name="item"/> in cell <paramref name="cell"/> of <paramref name="items"/>
    /// (a pack's or a container's): what was there takes the item's old cell, or the first free one.
    /// </summary>
    private static void PlaceAt(List<Item> items, Item item, int cell)
    {
        if (items.FirstOrDefault(i => i != item && i.Cell == cell) is { } there)
        {
            there.Cell = item.Cell is int old && items.Contains(item) && items.All(i => i == item || i == there || i.Cell != old) ? old : null;
        }
        item.Cell = cell;
        Equipment.Arrange(items);
    }

    /// <summary>Open the carried container <paramref name="item"/>: its contents in the container's place.</summary>
    private void OpenContainer(Item item)
    {
        _openItem = item;
        _insidePage = 0;
        _container = new WorldObject(-1, ObjectKind.Thing, item.Id) { Name = ItemText.Name(_world!, item), Item = item.Record, Items = item.Contents };
        // 6527:15CD, then 6527:151B: shut a moment, then open.
        _opening = true;
        GetTree().CreateTimer(Opening).Timeout += () =>
        {
            if (_openItem == item && _opening)
            {
                _opening = false;
                RefreshItems();
            }
        };
    }

    /// <summary>Put the open container away: the body shows again.</summary>
    private void CloseContainer()
    {
        _openItem = null;
        _opening = false;
        _container = _store;
        _insidePage = 0;
    }

    private string? PutIn((Item Item, WorldObject Holder) picked, ItemSlot slot)
    {
        if (_container is not { } container || _world is null)
        {
            return null;
        }
        if (_store is null && picked.Holder != container && _world.IsContainerItem(picked.Item))
        {
            // "Cannot put container in bag" / "… in box".
            return $"Cannot put a container in {container.Name}.";
        }
        if (InsideCell(slot) is not int cell)
        {
            return null;
        }
        if (picked.Holder == container)
        {
            _picked = null;
            PlaceAt(container.Items, picked.Item, cell);
            return null;
        }
        if (_store is not null)
        {
            return "Put what you would sell in the six slots below SELL.";
        }
        if (container.Items.Count >= ContainerSlots)
        {
            return $"{container.Name} is full.";
        }
        _picked = null;
        if (!_world.MoveItem(picked.Holder, container, picked.Item))
        {
            return $"{container.Name} can't hold any more.";
        }
        if (container.Items.Contains(picked.Item))
        {
            PlaceAt(container.Items, picked.Item, cell);
        }
        Did?.Invoke($"{MemberName(_member)} puts {ItemText.Name(_world, picked.Item)} in {container.Name}");
        return null;
    }

    /// <summary>Take <paramref name="item"/> out of the open container (from a store: buy it). Null if done, else why not.</summary>
    private string? Take(Item item)
    {
        if (_container is not { } container || Member is not { } member || _world is null)
        {
            return null;
        }
        if (_store is not null)
        {
            return Buy(item);
        }
        if (_world.TakeMoney(container, item) is > 0 and var coins)
        {
            // 74F1: coins taken become the party's money.
            SetMoney?.Invoke(Money() + coins);
            Did?.Invoke($"{MemberName(_member)} takes {coins} coins from {container.Name}");
            return null;
        }
        if (!_world.MoveItem(container, member, item))
        {
            return $"{MemberName(_member)} can't carry any more.";
        }
        Did?.Invoke($"{MemberName(_member)} takes {ItemText.Name(_world, item)} from {container.Name}");
        return null;
    }

    /// <summary>A box: with an item picked, give it to that member; otherwise show them.</summary>
    private void BoxClicked(int slot)
    {
        if (_world?[slot] is not { Kind: ObjectKind.Party } to)
        {
            return;
        }
        if (_page is Page.Spells or Page.Psionics && _pickedSpell is int spell)
        {
            CastAt(slot, spell);
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

    /// <summary>The pages <paramref name="items"/>' cells take, at least one.</summary>
    private static int Pages(IReadOnlyList<Item> items, int perPage) =>
        Math.Max(1, items.Select(i => (i.Cell ?? 0) / perPage + 1).DefaultIfEmpty(1).Max());

    /// <summary>The picked item's icon, for the pointer while it is held; null with nothing picked.</summary>
    public Texture2D? HeldIcon => _picked is { } p ? ItemIcon(p.Item.Rdff) : null;

    private void TurnPack(int by)
    {
        int pages = Pages(Pack, PackSlots);
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
