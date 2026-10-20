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
/// What a hit or spell is made of, for resistances (<c>80F5:28F2</c>): a
/// weapon's <c>IT1R</c> +2 (slings bludgeon, swords slash, bows and daggers
/// pierce) with <see cref="Plus1"/>, <see cref="Plus2"/> and <see cref="Plus3"/>
/// added for a +1, +2 (both) or +3 (all three) weapon (<c>612E:1359</c>), or a
/// spell record's +0x13. Named from the spells and weapons that carry them.
/// </summary>
[Flags]
public enum DamageElement : ushort
{
    None = 0,
    Poison = 0x0001,
    Fire = 0x0002,
    Cold = 0x0004,
    Bludgeoning = 0x0008,
    Slashing = 0x0010,
    Piercing = 0x0020,
    Acid = 0x0040,
    Electricity = 0x0080,
    /// <summary>The cause-wounds spells, Chill and Vampiric Touch, Slay Living.</summary>
    Negative = 0x0100,
    /// <summary>Most damaging wizard and priest spells.</summary>
    Magic = 0x0200,
    Psionic = 0x0400,
    /// <summary>Poison and Slay Living.</summary>
    Death = 0x0800,
    Plus1 = 0x1000,
    Plus2 = 0x2000,
    Plus3 = 0x8000,
}

/// <summary>What an item is made of: the low nibble of <c>IT1R</c> +8, named from DSUN.EXE's table at <c>3B68:0000</c> (<c>74F1:23AA</c>).</summary>
public enum ItemMaterial : byte { Wooden, Bone, Stone, Obsidian, Metal, Leather }

/// <summary>
/// <c>IT1R</c> +0x0F. 1, 2, 8 and 0x80 are read by DSUN.EXE: Use is offered
/// for 1 (<c>68BD:0C6D</c>); 2 stacks (<c>7372:0FEE</c>) and moves the
/// item's power from its quantity to its charges (<c>78A9:2324</c>); 8 is a
/// container (VIEW INVENTORY, <c>6527:12BD</c>); 0x80 marks armour (the verb
/// window's flag 8). 0x40 is on two-handed weapons only (staves, the great
/// axe, polearms, bows; inferred). 4 (doors, chests, nests, obelisks) and
/// 0x10 are not traced.
/// </summary>
[Flags]
public enum ItemTypeFlags : byte
{
    None = 0,
    Usable = 0x01,
    Stacks = 0x02,
    Unknown04 = 0x04,
    Container = 0x08,
    Unknown10 = 0x10,
    TwoHanded = 0x40,
    Armor = 0x80,
}

/// <summary>
/// Who can use an item (<c>IT1R</c> +0x10). DSUN.EXE ORs the word at
/// <c>DS:0718</c> + 2 × code over a character's class codes into its
/// character record's +0x12 (<c>6868:0000</c>) and lets it wear an item when
/// the two share a bit (<c>6466:0854</c>). The codes come from
/// <c>38D4:00ED</c>: 1–4 cleric of air, earth, fire, water; 5–8 druid; 9
/// fighter; 10 gladiator; 11 preserver; 12 psionicist; 13–16 ranger; 17
/// thief. 0x800 and 0x1000 belong to no class the player can be (by the
/// items that have them, likely the defiler and the templar).
/// </summary>
[Flags]
public enum ItemClasses : ushort
{
    None = 0,
    AirCleric = 0x0001,
    EarthCleric = 0x0002,
    FireCleric = 0x0004,
    WaterCleric = 0x0008,
    Druid = 0x0010,
    Fighter = 0x0020,
    Gladiator = 0x0040,
    Preserver = 0x0080,
    Psionicist = 0x0100,
    Ranger = 0x0200,
    Thief = 0x0400,
    Defiler = 0x0800,
    Templar = 0x1000,
    Clerics = AirCleric | EarthCleric | FireCleric | WaterCleric,
    All = 0x1FFF,
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

    /// <summary><see cref="Flags"/> by name.</summary>
    public ItemTypeFlags Kind => (ItemTypeFlags)Flags;

    public bool Stacks => (Kind & ItemTypeFlags.Stacks) != 0;

    public bool IsContainer => (Kind & ItemTypeFlags.Container) != 0;

    /// <summary>The material (<see cref="Material"/> &amp; 0x0F), or null for a code past the table (none in the data).</summary>
    public ItemMaterial? MadeOf => (Material & 0x0F) <= (int)ItemMaterial.Leather ? (ItemMaterial)(Material & 0x0F) : null;

    /// <summary>
    /// <see cref="Material"/> bit 0x20: a fixture, which can't be picked up
    /// (<c>68BD:0C6D</c>; doors, geysers, nests). Bit 0x40 is set on ordinary
    /// items, 0x80 on named magic ones and with a material below 4 picks
    /// DSUN.EXE's magic-item cases (<c>612E:2501</c>), and 0x04 adds 10 to a
    /// value DSUN.EXE works out (<c>612E:0190</c>); none of these three is traced further.
    /// </summary>
    public bool Fixed => (Material & 0x20) != 0;

    /// <summary>The classes that may use it (<see cref="LegalClasses"/>).</summary>
    public ItemClasses Classes => (ItemClasses)LegalClasses;

    /// <summary>May a character of <paramref name="classes"/> use it (DSUN.EXE: any bit in common)?</summary>
    public bool UsableBy(ItemClasses classes) => (Classes & classes) != 0;

    /// <summary>Damage as dice, e.g. "1d8+1"; empty for items that do none.</summary>
    public string Damage => DamageDice == 0 || DamageSides == 0 ? ""
        : $"{DamageDice}d{DamageSides}{(DamageBonus > 0 ? "+" : "")}{(DamageBonus != 0 ? DamageBonus.ToString() : "")}";

    public static ItemType Parse(int number, ReadOnlySpan<byte> r) => new(
        number, (WeaponKind)r[0], r[1], BinaryPrimitives.ReadUInt16LittleEndian(r[2..]), r[4],
        BinaryPrimitives.ReadUInt16LittleEndian(r[5..]), r[7], r[8], (ItemPlacement)r[9], r[10], r[11], r[12], r[13],
        (sbyte)r[14], r[15], BinaryPrimitives.ReadUInt16LittleEndian(r[16..]), (sbyte)r[18], r[19])
    {
        Bytes = r.ToArray(),
    };

    /// <summary>The 20 bytes as <c>IT1R</c> has them (<c>GPLX</c> group 4, for scripts).</summary>
    public byte[] Bytes { get; init; } = [];

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
/// <c>RDFF</c> (<c>7155:0732</c>). Confirmed by the code: +0x0A the
/// <see cref="ItemType"/> number, +0x0F flags (bits 1 and 8 tested), +0x12 the
/// <c>NAME</c> number, +0x14 the bonus (shown as <c>"%+d name"</c>, added to
/// the type's damage bonus). The other fields follow libgff's
/// <c>ds1_item_t</c>; DSUN.EXE sets <see cref="Next"/> and
/// <see cref="PackIndex"/> to 9999 (none) when it loads an item.
/// The shipped data agrees: every <c>RDFF</c> that references an <c>IT1R</c>
/// or <c>NAME</c> entry references the record's own type and name.
/// <para>
/// Magic: a magic item's <see cref="Quantity"/> is the spell or power it
/// casts (<c>SPIN</c> n; the psionic disciplines' items 145–171) and
/// <see cref="Charges"/> (+0x0E) its uses left: each use takes one, and at 0
/// the item is gone (<c>78A9:2324</c>). For a type that stacks the two swap:
/// the quantity is the count, the charges the spell. +0x0F keeps a copy of
/// the spell to restore (<c>78A9</c>, the shipped items agree; doors use it
/// for their state: the door flags).
/// </para>
/// <para>
/// <see cref="Special"/> (+0x10, 1–6) is how easily the item breaks: 6
/// never; DSUN.EXE picks a carried item with the lowest value below 6 and
/// breaks it when 1d20 is under 15 − its <see cref="Bonus"/>
/// (<c>80F5:1F4B</c>), making it a "Broken Weapon" (type 107, <c>NAME</c>
/// 203, special 1; <c>612E:2700</c>). Its callers (combat) aren't traced.
/// </para>
/// </summary>
public sealed record ItemRecord(
    int Id, ushort Quantity, short Next, ushort Value, short PackIndex, short Type, short Icon,
    byte Charges, byte Flags, byte Special, byte Slot, ushort NameIndex, sbyte Bonus)
{
    public const int Size = 21;

    /// <summary>"No item" in the item table's links (0x270F).</summary>
    public const int None = 9999;

    /// <summary><see cref="Special"/> 6: never breaks.</summary>
    public const int Unbreakable = 6;

    public bool Breakable => Special < Unbreakable;

    /// <summary>
    /// The spell or power a magic item casts (<see cref="Quantity"/>, or
    /// <see cref="Charges"/> for a type that stacks), when +0x0F holds the same
    /// number as its copy; 0 for an item without one.
    /// </summary>
    public int PowerFor(ItemType? type)
    {
        int power = type is { Stacks: true } ? Charges : Quantity;
        return power > 0 && power == Flags ? power : 0;
    }

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
