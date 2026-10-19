using System.Buffers.Binary;
using DarkSun.Data.Gff;
using DarkSun.Data.Text;

namespace DarkSun.Data.Items;

/// <summary>
/// Where an item is worn or held (<c>IT1R</c> +9). Named from the items that
/// use each value (inferred: e.g. every chest armour has 1, every ring 9).
/// </summary>
public enum ItemPlacement : byte
{
    None = 0,
    Chest = 1,
    Waist = 2,
    Arms = 3,
    Feet = 4,
    Hand = 5,
    Head = 6,
    Neck = 7,
    Cloak = 8,
    Finger = 9,
    Legs = 10,
    Ammunition = 11,
    MissileWeapon = 12,
}

/// <summary>What kind of weapon an item is (<c>IT1R</c> +0, bits; from the items that have them).</summary>
[Flags]
public enum WeaponKind : byte
{
    None = 0,
    Melee = 0x01,
    Missile = 0x02,
    Shield = 0x04,
    UsesAmmunition = 0x08,
    Thrown = 0x10,
}

/// <summary>
/// An item type: a 20-byte <c>IT1R</c> record (<c>GPLDATA.GFF</c> <c>IT1R</c> 1,
/// 115 types). DSUN.EXE loads the table to <c>DS:1669</c> and indexes it
/// ×0x14; it reads <see cref="Range"/> (+0x0A) and <see cref="DamageBonus"/>
/// (+0x0E) when showing a weapon's to-hit and damage (<c>78A9:2A9F</c>). The
/// other offsets follow libgff's <c>ds_item1r_t</c>; <c>Unknown*</c> fields
/// are its <c>data0</c>/<c>data1</c>/<c>data2</c>.
/// </summary>
public sealed record ItemType(
    int Number, WeaponKind Weapon, byte Unknown1, ushort DamageType, byte Weight, ushort Unknown5,
    byte BaseHp, byte Material, ItemPlacement Placement, byte Range, byte Attacks, byte DamageSides,
    byte DamageDice, sbyte DamageBonus, byte Flags, ushort LegalClasses, sbyte ArmorClass, byte Unknown19)
{
    public const int Size = 20;

    public bool IsWeapon => (Weapon & (WeaponKind.Melee | WeaponKind.Missile)) != 0;

    /// <summary>Damage as dice, e.g. "1d8+1"; empty for items that do none.</summary>
    public string Damage => DamageDice == 0 || DamageSides == 0 ? ""
        : $"{DamageDice}d{DamageSides}{(DamageBonus > 0 ? "+" : "")}{(DamageBonus != 0 ? DamageBonus.ToString() : "")}";

    public static ItemType Parse(int number, ReadOnlySpan<byte> r) => new(
        number, (WeaponKind)r[0], r[1], BinaryPrimitives.ReadUInt16LittleEndian(r[2..]), r[4],
        BinaryPrimitives.ReadUInt16LittleEndian(r[5..]), r[7], r[8], (ItemPlacement)r[9], r[10], r[11], r[12], r[13],
        (sbyte)r[14], r[15], BinaryPrimitives.ReadUInt16LittleEndian(r[16..]), (sbyte)r[18], r[19]);

    /// <summary>Every record of an <c>IT1R</c> resource.</summary>
    public static IReadOnlyList<ItemType> ParseAll(ReadOnlySpan<byte> data)
    {
        var types = new ItemType[data.Length / Size];
        for (int i = 0; i < types.Length; i++)
        {
            types[i] = Parse(i, data.Slice(i * Size, Size));
        }
        return types;
    }
}

/// <summary>
/// One item: the 21-byte record DSUN.EXE keeps in its item table
/// (<c>DS:165D</c>, 399 entries), copied from the first entry of an item's
/// <c>RDFF</c> (<c>7155:0482</c>). Confirmed by the code: +0x0A the
/// <see cref="ItemType"/> number, +0x0F flags (bits 1 and 8 tested), +0x12 the
/// <c>NAME</c> number, +0x14 the bonus (shown as <c>"%+d name"</c>, added to
/// the type's damage bonus). The other fields follow libgff's
/// <c>ds1_item_t</c>; DSUN.EXE sets <see cref="Next"/> and
/// <see cref="PackIndex"/> to 9999 (none) when it loads an item.
/// The shipped data agrees: every <c>RDFF</c> that references an <c>IT1R</c>
/// or <c>NAME</c> entry references the record's own type and name.
/// </summary>
public sealed record ItemRecord(
    int Id, ushort Quantity, short Next, ushort Value, short PackIndex, short Type, short Icon,
    byte Charges, byte Flags, byte Special, byte Slot, ushort NameIndex, sbyte Bonus)
{
    public const int Size = 21;

    /// <summary>"No item" in the item table's links (0x270F).</summary>
    public const int None = 9999;

    public static ItemRecord Parse(ReadOnlySpan<byte> r) => new(
        BinaryPrimitives.ReadInt16LittleEndian(r), BinaryPrimitives.ReadUInt16LittleEndian(r[2..]),
        BinaryPrimitives.ReadInt16LittleEndian(r[4..]), BinaryPrimitives.ReadUInt16LittleEndian(r[6..]),
        BinaryPrimitives.ReadInt16LittleEndian(r[8..]), BinaryPrimitives.ReadInt16LittleEndian(r[10..]),
        BinaryPrimitives.ReadInt16LittleEndian(r[12..]), r[14], r[15], r[16], r[17],
        BinaryPrimitives.ReadUInt16LittleEndian(r[18..]), (sbyte)r[20]);

    /// <summary>The item record of an <c>RDFF</c> resource, or null if it is something else (a creature).</summary>
    public static ItemRecord? FromRdff(ReadOnlyMemory<byte> rdff) =>
        Rdff.Entries(rdff) is [{ LoadAction: RdffEntry.Inline, Type: RdffEntry.ItemType } first, ..] && first.Body.Length >= Size
            ? Parse(first.Body.Span)
            : null;
}

/// <summary>
/// The item types and names, from <c>GPLDATA.GFF</c> (<c>IT1R</c> 1 and
/// <c>NAME</c> 1), and what they say about an item.
/// </summary>
public sealed class ItemCatalog(IReadOnlyList<ItemType> types, NameTable names)
{
    public IReadOnlyList<ItemType> Types { get; } = types;

    public NameTable Names { get; } = names;

    public static ItemCatalog Load(IResourceSource gplData) => new(
        gplData.TryRead("IT1R", 1, out var it1r) ? ItemType.ParseAll(it1r.Span) : [],
        NameTable.Read(gplData));

    public ItemType? TypeOf(ItemRecord item) => item.Type >= 0 && item.Type < Types.Count ? Types[item.Type] : null;

    /// <summary>
    /// The item's name as DSUN.EXE shows it (<c>78A9:2A9F</c>): its <c>NAME</c>
    /// entry, prefixed with its bonus (<c>"%+d %Fs"</c>, e.g. "+1 Long Sword")
    /// when it has one; empty when it has no name.
    /// </summary>
    public string NameOf(ItemRecord item)
    {
        if (item.NameIndex >= ItemRecord.None || Names[item.NameIndex] is not { } name)
        {
            return "";
        }
        return item.Bonus == 0 ? name : $"{(item.Bonus > 0 ? "+" : "")}{item.Bonus} {name}";
    }
}
