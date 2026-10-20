using System.Buffers.Binary;

namespace DarkSun.Game.Scripting;

/// <summary>
/// What a script's object field (<c>obj(id).f</c>) is in each record, from
/// <c>GPLX</c> 1: five groups of 99 words (the field's offset in the item,
/// combat, name, character and item-type records, −1 where it has none), then
/// a type byte per field (at <c>0x3DE</c>), then the five record sizes (21, 58,
/// 25, 71, 20). A type below 16 points into group type − 1 (3: the name
/// table, 4: the character record, 5: the item type); 16 is a signed byte,
/// 18 an object id, 19 and 22 words, 20 a long, 21 an unsigned word; 0x80 and
/// up a string of type &amp; 0x7F bytes in place.
/// </summary>
public sealed class GplFieldMap
{
    public const int Fields = 99, Groups = 5;
    public const int Item = 0, Combat = 1, Name = 2, Character = 3, ItemType = 4;
    private const int TypesAt = 0x3DE;

    private readonly short[] _offsets;
    private readonly byte[] _types;

    private GplFieldMap(short[] offsets, byte[] types, int[] sizes) => (_offsets, _types, Sizes) = (offsets, types, sizes);

    /// <summary>No map: every field is unknown.</summary>
    public static GplFieldMap Empty { get; } = new([.. Enumerable.Repeat((short)-1, Groups * Fields)], new byte[Fields], new int[Groups]);

    /// <summary>Each group's record size.</summary>
    public IReadOnlyList<int> Sizes { get; }

    public static GplFieldMap Parse(ReadOnlySpan<byte> gplx)
    {
        if (gplx.Length < TypesAt + Fields + 2 * Groups)
        {
            return Empty;
        }
        short[] offsets = new short[Groups * Fields];
        for (int i = 0; i < offsets.Length; i++)
        {
            offsets[i] = BinaryPrimitives.ReadInt16LittleEndian(gplx[(2 * i)..]);
        }
        int[] sizes = new int[Groups];
        for (int g = 0; g < Groups; g++)
        {
            sizes[g] = BinaryPrimitives.ReadInt16LittleEndian(gplx[(TypesAt + Fields + 2 * g)..]);
        }
        return new GplFieldMap(offsets, gplx.Slice(TypesAt, Fields).ToArray(), sizes);
    }

    public int OffsetOf(int group, int field) =>
        group is >= 0 and < Groups && field is >= 0 and < Fields ? _offsets[group * Fields + field] : -1;

    public int TypeOf(int field) => field is >= 0 and < Fields ? _types[field] : 0;

    /// <summary>The group a pointer field leads to, or null for a value.</summary>
    public int? PointerTo(int field) => TypeOf(field) is >= 3 and <= 5 and var t ? t - 1 : null;

    public bool IsText(int field) => TypeOf(field) >= 0x80;

    /// <summary>The field's value in <paramref name="record"/> of <paramref name="group"/>, or null if it has none there.</summary>
    public int? Read(ReadOnlySpan<byte> record, int group, int field)
    {
        int at = OffsetOf(group, field), type = TypeOf(field);
        if (at < 0 || type >= 0x80 || at + Width(type) > record.Length)
        {
            return null;
        }
        return type switch
        {
            16 => (sbyte)record[at],
            20 => BinaryPrimitives.ReadInt32LittleEndian(record[at..]),
            21 => BinaryPrimitives.ReadUInt16LittleEndian(record[at..]),
            _ => BinaryPrimitives.ReadInt16LittleEndian(record[at..]),
        };
    }

    /// <summary>A text field (a name), or null.</summary>
    public string? Text(ReadOnlySpan<byte> record, int group, int field)
    {
        int at = OffsetOf(group, field), type = TypeOf(field);
        if (at < 0 || type < 0x80 || at >= record.Length)
        {
            return null;
        }
        ReadOnlySpan<byte> s = record.Slice(at, Math.Min(type & 0x7F, record.Length - at));
        int end = s.IndexOf((byte)0);
        return System.Text.Encoding.Latin1.GetString(end < 0 ? s : s[..end]).Trim();
    }

    /// <summary>Write the field into <paramref name="record"/>; false if it has none there.</summary>
    public bool Write(Span<byte> record, int group, int field, int value)
    {
        int at = OffsetOf(group, field), type = TypeOf(field);
        if (at < 0 || type >= 0x80 || at + Width(type) > record.Length)
        {
            return false;
        }
        switch (type)
        {
            case 16: record[at] = (byte)value; break;
            case 20: BinaryPrimitives.WriteInt32LittleEndian(record[at..], value); break;
            default: BinaryPrimitives.WriteInt16LittleEndian(record[at..], (short)value); break;
        }
        return true;
    }

    private static int Width(int type) => type switch { 16 => 1, 20 => 4, _ => 2 };
}
