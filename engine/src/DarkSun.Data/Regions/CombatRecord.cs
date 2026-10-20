using System.Buffers.Binary;
using System.Text;

namespace DarkSun.Data.Regions;

/// <summary>
/// A creature's combat record: <c>RDFF</c> resource type 2 (after its 10-byte
/// header), 58 bytes; DSUN.EXE keeps one per creature in the table at
/// <c>DS:1665</c>. Layout from libgff's <c>ds1_combat_t</c>, meanings from
/// DSUN.EXE (<c>dsun-exe-reference.md</c> §8); <c>RDFF</c> <i>n</i> belongs to
/// <c>OJFF</c> <i>n</i>, and its <see cref="Id"/> is −<i>n</i>.
/// </summary>
/// <param name="Hp">+0: hit points. DSUN.EXE replaces a creature's with its
/// character record's (<see cref="CharacterRecord.HitPoints"/>) when it loads one
/// (<c>7155:0000</c>); 85 of the 290 creatures have a different number here.</param>
/// <param name="Psp">+2: psionic strength points; psionic powers cost them.</param>
/// <param name="CharIndex">+4: the character record's index in its table, set when loaded.</param>
/// <param name="Move">+0x1B: movement, × 10 points a round.</param>
/// <param name="Status">+0x1C: 1 okay, 2, 3 down, 5 dead, 8 destroyed.</param>
/// <param name="Allegiance">+0x1D: &amp; 3 fights; 1 the party's side, 2 its enemies (who give experience when killed).</param>
/// <param name="Thac0">+0x1F.</param>
/// <param name="Flags">+0x21: 0x20 computer control, 0x40 locked.</param>
/// <param name="Stats">+0x22: Str, Dex, Con, Int, Wis, Cha. The AI casts spells only with Int above 9.</param>
public sealed record CombatRecord(
    short Hp, short Psp, short CharIndex, short Id, sbyte Ac, byte Move, byte Status, byte Allegiance,
    sbyte Thac0, byte Priority, byte Flags, byte[] Stats, string Name)
{
    public const int HeaderSize = 10;
    public const int Size = 58;
    public const short CombatType = 2;

    /// <summary>Offsets DSUN.EXE writes: <c>REQUEST(18)</c> sets allegiance, GET_STATUS reads status.</summary>
    public const int StatusOffset = 0x1C, AllegianceOffset = 0x1D;

    /// <summary><see cref="Mind"/> values the AI acts on.</summary>
    public const int MindFlees = 1, MindIdle = 8;

    /// <summary>
    /// +0x16: the special abilities, a row of the 32-bit masks at <c>3E45:0000</c>
    /// (<c>84C1:0000</c>): breath cones, paralysis, webs (<c>6466:05EF</c>); 0 none.
    /// </summary>
    public byte SpecialAttack { get; init; }

    /// <summary>+0x17 (not traced).</summary>
    public byte SpecialDefense { get; init; }

    /// <summary>
    /// +0x1E: what the AI does when no condition overrides it (<c>8053:000E</c>):
    /// <see cref="MindFlees"/> (civilians), <see cref="MindIdle"/> (bystanders);
    /// 0, 2 (most), 4 and 10 fight.
    /// </summary>
    public byte Mind { get; init; }

    /// <summary>+0x21 &amp; 0x20: the AI plays it in combat (<c>612E:04A7</c>).</summary>
    public bool ComputerControlled => (Flags & 0x20) != 0;

    /// <summary>Allegiance 2: an enemy of the party, worth experience when it falls (<c>612E:25F2</c>).</summary>
    public bool IsEnemy => Allegiance == 2;

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
            Encoding.Latin1.GetString(end < 0 ? name : name[..end]).Trim())
        {
            SpecialAttack = r[0x16],
            SpecialDefense = r[0x17],
            Mind = r[0x1E],
        };
    }
}
