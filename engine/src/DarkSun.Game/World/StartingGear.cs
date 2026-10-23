using DarkSun.Data.Gff;
using DarkSun.Data.Items;

namespace DarkSun.Game.World;

/// <summary>An item a new character starts with: its <c>OJFF</c> and the equipment slot it goes to.</summary>
public readonly record struct GearPiece(int Ojff, int Slot);

/// <summary>
/// What a new character starts with (<c>6ECD:1D48</c>, given when DONE is
/// clicked on the creation screen): one class's entries of the table at
/// <c>38CE:0000</c> (29 entries of 3 bytes: the item's <c>OJFF</c>, the slot).
/// The class is the first of the record's +0x12 bits the code tests, so a
/// character of several classes gets one class's gear; a thri-kreen gets its
/// chatkcha whatever its class. Objects 1010 chatkcha, 1012 bone long sword,
/// 1015 sling, 1017 bow, 1019 quarterstaff, 1020 shield, 1022 / 1023 / 1026
/// leather chest, arm and leg armour, 1070 arrows, 1185 club.
/// </summary>
public static class StartingGear
{
    private static readonly GearPiece[] Table =
    [
        new(1012, 3), new(1185, 10), new(1023, 0),                                 // 0–2 gladiator
        new(1012, 3), new(1022, 9), new(1023, 0), new(1017, 2), new(1070, 1),     // 3–7 ranger
        new(1022, 9), new(1023, 0), new(1026, 6), new(1012, 3), new(1020, 10),    // 8–12 fighter
        new(1185, 3), new(1020, 10), new(1022, 9), new(1023, 0),                  // 13–16 cleric
        new(1012, 3), new(1022, 9), new(1015, 2),                                 // 17–19 thief
        new(1185, 3), new(1022, 9), new(1017, 2), new(1070, 1),                   // 20–23 psionicist
        new(1185, 3), new(1015, 2),                                               // 24–25 druid (and any other)
        new(1015, 2), new(1019, 3),                                               // 26–27 preserver
        new(1010, 2),                                                             // 28 thri-kreen
    ];

    /// <summary>The gear for a character of <paramref name="classes"/>, in the order it is given.</summary>
    public static IReadOnlyList<GearPiece> For(ItemClasses classes, bool thriKreen)
    {
        (int first, int last) = thriKreen ? (28, 28)
            : (classes & ItemClasses.Fighter) != 0 ? (8, 12)
            : (classes & ItemClasses.Gladiator) != 0 ? (0, 2)
            : (classes & ItemClasses.Ranger) != 0 ? (3, 7)
            : (classes & ItemClasses.Clerics) != 0 ? (13, 16)
            : (classes & ItemClasses.Thief) != 0 ? (17, 19)
            : (classes & ItemClasses.Psionicist) != 0 ? (20, 23)
            : (classes & ItemClasses.Preserver) != 0 ? (26, 27)
            : (24, 25);
        return Table[first..(last + 1)];
    }

    /// <summary>
    /// The gear as the items a new character keeps (<see cref="Characters.Character.Items"/>):
    /// each piece worn in its slot if it goes there and its class may use it
    /// (as <c>1A0A:20EC</c>), else in the pack; a stack (the arrows) with its
    /// record's quantity. <paramref name="objectData"/> holds the item
    /// <c>RDFF</c>s; a piece that isn't one is left out.
    /// </summary>
    public static List<ItemState> Items(ItemClasses classes, bool thriKreen, IResourceSource objectData, ItemCatalog catalog)
    {
        var items = new List<ItemState>();
        foreach (GearPiece piece in For(classes, thriKreen))
        {
            if (!objectData.TryRead("RDFF", piece.Ojff, out var data) || ItemRecord.FromRdff(data) is not { } record)
            {
                continue;
            }
            ItemType? type = catalog.TypeOf(record);
            bool worn = type is { Placement: not ItemPlacement.None } && type.UsableBy(classes)
                && piece.Slot is >= 0 and < Equipment.Slots && Equipment.Placements[piece.Slot] == type.Placement;
            items.Add(new ItemState(piece.Ojff, type is { Stacks: true } ? Math.Max(1, (int)record.Quantity) : 1, worn ? piece.Slot : Equipment.Pack));
        }
        return items;
    }
}
