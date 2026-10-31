using System.Buffers.Binary;
using DarkSun.Data.Items;
using DarkSun.Game.Characters;
using DarkSun.Game.Magic;
using DarkSun.Game.Scripting;

namespace DarkSun.Game.World;

/// <summary>
/// One record a script's object field reads (<c>obj(id).f.g</c>): DSUN.EXE's
/// item, combat, name, character and item-type records, as <see cref="GplFieldMap"/>
/// lays them out. A field that points elsewhere (the combat record's sheet,
/// an item's name and type, an object id) leads to the next record of a path.
/// </summary>
internal abstract class GplRecord(GplFieldMap map, int group)
{
    protected GplFieldMap Map { get; } = map;

    protected int Group { get; } = group;

    /// <summary>The record's bytes as DSUN.EXE would hold them, for the fields not modelled.</summary>
    protected abstract byte[] Bytes();

    public virtual int Read(int field) => Map.Read(Bytes(), Group, field) ?? 0;

    public virtual string? Text(int field) => Map.Text(Bytes(), Group, field);

    public abstract void Write(int field, int value);

    /// <summary>The record a pointer field leads to, or null.</summary>
    public virtual GplRecord? Follow(int field) => null;
}

/// <summary>A map object: a creature's or party member's combat record (group 1), or an item's record (group 0) for an item on the ground.</summary>
internal sealed class GplObjectRecord(GplFieldMap map, WorldObject obj, GameWorld world, WorldGplHost host)
    : GplRecord(map, obj.Kind == ObjectKind.Thing && obj.Item is not null ? GplFieldMap.Item : GplFieldMap.Combat)
{
    public WorldObject Object => obj;

    protected override byte[] Bytes() =>
        Group == GplFieldMap.Item ? GplItemRecord.BytesOf(obj.Item!)
        : obj.Kind == ObjectKind.Party ? host.PartyCombatBytes(obj)
        : obj.Record?.Bytes ?? [];

    public override int Read(int field) => obj.Knows(field) ? obj.ReadField(field) : base.Read(field);

    public override string? Text(int field) => field == GplRecords.NameField && Group == GplFieldMap.Combat ? obj.Name : base.Text(field);

    public override void Write(int field, int value)
    {
        if (Group != GplFieldMap.Combat || obj.Kind != ObjectKind.Party || !host.WritePartyCombat(obj, field, value))
        {
            obj.WriteField(field, value);
        }
    }

    public override GplRecord? Follow(int field)
    {
        int value = Read(field);
        return Map.PointerTo(field) switch
        {
            GplFieldMap.Character when Group == GplFieldMap.Combat => new GplSheetRecord(Map, obj, host),
            GplFieldMap.Name when obj.Item is { } item => new GplNameRecord(Map, world.Items.NameOf(item)),
            GplFieldMap.ItemType when obj.Item is { } item => world.Items.TypeOf(item) is { } type ? new GplItemTypeRecord(Map, type) : null,
            not null => null,
            // An object id (type 18) names the object; any other value is an object index.
            _ => (Map.TypeOf(field) == GplRecords.IdType ? world.First(value) : world[value]) is { } next
                ? new GplObjectRecord(Map, next, world, host)
                : null,
        };
    }
}

/// <summary>
/// A creature's or party member's character record (group 3, <c>obj.71.f</c>):
/// a party member's from its sheet (writes to its abilities, experience and
/// most hit points reach the sheet), a creature's from its <c>RDFF</c> with
/// what scripts wrote.
/// </summary>
internal sealed class GplSheetRecord(GplFieldMap map, WorldObject obj, WorldGplHost host) : GplRecord(map, GplFieldMap.Character)
{
    protected override byte[] Bytes() => obj.Kind == ObjectKind.Party ? host.PartySheetBytes(obj) : obj.Character?.Bytes ?? [];

    public override int Read(int field) => obj.Kind != ObjectKind.Party && obj.SheetField(field) is int v ? v : base.Read(field);

    public override void Write(int field, int value)
    {
        if (obj.Kind == ObjectKind.Party)
        {
            host.WritePartySheet(obj, field, value);
        }
        else
        {
            obj.WriteSheetField(field, value);
        }
    }
}

/// <summary>A carried item's record (group 0): id, quantity, slot and charges as the item has them, the rest from its <c>RDFF</c>.</summary>
internal sealed class GplItemRecord(GplFieldMap map, Item item, ItemCatalog catalog) : GplRecord(map, GplFieldMap.Item)
{
    protected override byte[] Bytes() => BytesOf(item.Record, item);

    public override void Write(int field, int value)
    {
        switch (field)
        {
            case 82: item.Quantity = value; break;
            case 85: item.Slot = value; break;
            case 86: item.Charges = value; break;
        }
    }

    public override GplRecord? Follow(int field) => Map.PointerTo(field) switch
    {
        GplFieldMap.Name => new GplNameRecord(Map, catalog.NameOf(item.Record)),
        GplFieldMap.ItemType => catalog.TypeOf(item.Record) is { } type ? new GplItemTypeRecord(Map, type) : null,
        _ => null,
    };

    /// <summary>An item record's 21 bytes (<c>DS:165D</c>), with the live quantity, slot and charges of <paramref name="live"/>.</summary>
    public static byte[] BytesOf(ItemRecord r, Item? live = null)
    {
        var b = new byte[ItemRecord.Size];
        BinaryPrimitives.WriteInt16LittleEndian(b, (short)(live?.Id ?? r.Id));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)(live?.Quantity ?? r.Quantity));
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(4), r.Next);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), r.Value);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(8), r.PackIndex);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(10), r.Type);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(12), r.Icon);
        b[14] = (byte)(live?.Charges ?? r.Charges);
        (b[15], b[16], b[17]) = (r.Flags, r.Special, (byte)(live?.Slot ?? r.Slot));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(18), r.NameIndex);
        b[20] = (byte)r.Bonus;
        return b;
    }

    public static byte[] BytesOf(Item item) => BytesOf(item.Record, item);
}

/// <summary>A name table entry (group 2): field 94 the name.</summary>
internal sealed class GplNameRecord(GplFieldMap map, string name) : GplRecord(map, GplFieldMap.Name)
{
    protected override byte[] Bytes()
    {
        var b = new byte[25];
        System.Text.Encoding.Latin1.GetBytes(name.Length > 24 ? name[..24] : name).CopyTo(b, 0);
        return b;
    }

    public override void Write(int field, int value)
    {
    }
}

/// <summary>An item type (group 4, <c>IT1R</c>), read only.</summary>
internal sealed class GplItemTypeRecord(GplFieldMap map, ItemType type) : GplRecord(map, GplFieldMap.ItemType)
{
    protected override byte[] Bytes() => type.Bytes;

    public override void Write(int field, int value)
    {
    }
}

internal static class GplRecords
{
    /// <summary>Field 19: a creature's name (combat record +0x28).</summary>
    public const int NameField = 19;

    /// <summary>Field type 18: an object id (<c>name(-n)</c>).</summary>
    public const int IdType = 18;
}
