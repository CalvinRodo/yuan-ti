using System.Buffers.Binary;
using System.Text;

namespace DarkSun.Data.Regions;

/// <summary>
/// A creature's combat record: <c>RDFF</c> resource type 2 (after its 10-byte
/// header), 58 bytes; DSUN.EXE keeps one per creature in the table at
/// <c>DS:1665</c>. Layout from <c>src/ds1_parsers.py</c>; <c>RDFF</c> <i>n</i>
/// belongs to <c>OJFF</c> <i>n</i>, and its <see cref="Id"/> is −<i>n</i>.
/// </summary>
public sealed record CombatRecord(
    short Hp, short Psp, short CharIndex, short Id, sbyte Ac, byte Move, byte Status, byte Allegiance,
    sbyte Thac0, byte Priority, byte Flags, byte[] Stats, string Name)
{
    public const int HeaderSize = 10;
    public const int Size = 58;
    public const short CombatType = 2;

    /// <summary>Offsets DSUN.EXE writes: <c>REQUEST(18)</c> sets allegiance, GET_STATUS reads status.</summary>
    public const int StatusOffset = 0x1C, AllegianceOffset = 0x1D;

    /// <summary>The combat record of an <c>RDFF</c> resource, or null if it is another type.</summary>
    public static CombatRecord? Parse(ReadOnlySpan<byte> rdff)
    {
        if (rdff.Length < HeaderSize + Size || BinaryPrimitives.ReadInt16LittleEndian(rdff[2..]) != CombatType)
        {
            return null;
        }
        ReadOnlySpan<byte> r = rdff.Slice(HeaderSize, Size);
        ReadOnlySpan<byte> name = r.Slice(40, 18);
        int end = name.IndexOf((byte)0);
        return new CombatRecord(
            BinaryPrimitives.ReadInt16LittleEndian(r), BinaryPrimitives.ReadInt16LittleEndian(r[2..]),
            BinaryPrimitives.ReadInt16LittleEndian(r[4..]), BinaryPrimitives.ReadInt16LittleEndian(r[6..]), (sbyte)r[26], r[27], r[StatusOffset], r[AllegianceOffset],
            (sbyte)r[31], r[32], r[33], r.Slice(34, 6).ToArray(),
            Encoding.Latin1.GetString(end < 0 ? name : name[..end]).Trim());
    }
}
