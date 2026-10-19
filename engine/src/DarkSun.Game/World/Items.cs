using DarkSun.Data.Items;

namespace DarkSun.Game.World;

/// <summary>
/// An item someone carries: an item <c>RDFF</c> (in <c>SEGOBJEX.GFF</c>) made
/// into an entry of an inventory, as DSUN.EXE keeps one in its item table
/// (<c>DS:165D</c>, 21-byte records chained per owner). On the map the same
/// item is an object whose <c>OJFF</c> number is <see cref="Rdff"/>.
/// </summary>
public sealed class Item
{
    public Item(int rdff, ItemRecord record, int quantity = 1, int slot = Equipment.Pack)
    {
        Rdff = rdff;
        Record = record;
        Quantity = quantity;
        Slot = slot;
    }

    /// <summary>The <c>RDFF</c> (and <c>OJFF</c>) number.</summary>
    public int Rdff { get; }

    /// <summary>Field 72, record +0: −<see cref="Rdff"/>, what <c>name(-n)</c> matches.</summary>
    public int Id => -Rdff;

    public ItemRecord Record { get; }

    /// <summary>Field 82, record +2: how many (stackable items); 1 otherwise.</summary>
    public int Quantity { get; set; }

    /// <summary>Field 85, record +0x11: the equipment slot it is worn in (0–13), or <see cref="Equipment.Pack"/>.</summary>
    public int Slot { get; set; }

    public bool Equipped => Slot < Equipment.Slots;

    /// <summary>A field as SEARCH reads it: 72 id, 82 quantity, 85 slot; others 0 (not traced).</summary>
    public int Field(int field) => field switch
    {
        72 => Id,
        82 => Quantity,
        85 => Slot,
        _ => 0,
    };

    public ItemState Save() => new(Rdff, Quantity, Slot);
}

/// <summary>An item as saved: its <c>RDFF</c> number, quantity and slot.</summary>
public sealed record ItemState(int Rdff, int Quantity, int Slot);

/// <summary>
/// Equipment slots (<c>6466:0854</c>): 14 slots, each taking items of one
/// <c>IT1R</c> placement, from the table at <c>3B67:0000</c>. An item's
/// record +0x11 holds its slot, 255 when it is in the pack. Scripts test
/// slot 13 (feet: the Serpent Boots) and 2 (a bow).
/// </summary>
public static class Equipment
{
    public const int Slots = 14;

    /// <summary>Not worn: in the pack.</summary>
    public const int Pack = 255;

    /// <summary>The most entries an inventory holds (<c>7372:0FEE</c> refuses past 0x4E).</summary>
    public const int Capacity = 0x4E;

    /// <summary>Slot n takes items placed <c>Placements[n]</c>.</summary>
    public static IReadOnlyList<ItemPlacement> Placements { get; } =
    [
        ItemPlacement.Arms, ItemPlacement.Ammunition, ItemPlacement.MissileWeapon, ItemPlacement.Hand,
        ItemPlacement.Finger, ItemPlacement.Waist, ItemPlacement.Legs, ItemPlacement.Head, ItemPlacement.Neck,
        ItemPlacement.Chest, ItemPlacement.Hand, ItemPlacement.Finger, ItemPlacement.Cloak, ItemPlacement.Feet,
    ];

    /// <summary>What each slot is, for the inventory screen (slots 3 and 10, 4 and 11 are the two hands and two rings).</summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        "Arms", "Ammunition", "Missile weapon", "Weapon hand", "Ring", "Waist", "Legs", "Head", "Neck",
        "Body", "Other hand", "Ring", "Cloak", "Feet",
    ];

    /// <summary>The first free slot for an item placed <paramref name="placement"/>, or null.</summary>
    public static int? FreeSlot(ItemPlacement placement, IEnumerable<Item> carried)
    {
        var taken = carried.Where(i => i.Equipped).Select(i => i.Slot).ToHashSet();
        for (int slot = 0; slot < Slots; slot++)
        {
            if (Placements[slot] == placement && placement != ItemPlacement.None && !taken.Contains(slot))
            {
                return slot;
            }
        }
        return null;
    }
}

/// <summary>Hit points and status (combat record +0 and +0x1C) of a creature or a party member.</summary>
public sealed class Vitals
{
    /// <summary>Status 1: well. Scripts take status below 3 as alive.</summary>
    public const int Well = 1;

    /// <summary>Status 3: down (damage reached 0 hit points by less than 10; <c>612E:18DA</c>).</summary>
    public const int Down = 3;

    /// <summary>Status 5: dead.</summary>
    public const int Dead = 5;

    /// <summary>Status 8: destroyed (10 000 damage or more); also GET_STATUS for no such creature.</summary>
    public const int Destroyed = 8;

    public int Hp { get; set; }

    public int MaxHp { get; set; }

    public int Status { get; set; } = Well;

    public bool Alive => Status < Down;

    public VitalsState Save() => new(Hp, MaxHp, Status);

    public void Load(VitalsState s) => (Hp, MaxHp, Status) = (s.Hp, s.MaxHp, s.Status);
}

public sealed record VitalsState(int Hp, int MaxHp, int Status);

/// <summary>
/// What a party member carries and how they are, kept for the whole game
/// (the region's party objects share these): DSUN.EXE keeps them in the
/// character table, not the region.
/// </summary>
public sealed class PartyMemberState
{
    public List<Item> Items { get; } = [];

    public Vitals Vitals { get; } = new() { Hp = 10, MaxHp = 10 };

    public PartyMemberSaveState Save() => new([.. Items.Select(i => i.Save())], Vitals.Save());
}

public sealed record PartyMemberSaveState(List<ItemState> Items, VitalsState Vitals);
