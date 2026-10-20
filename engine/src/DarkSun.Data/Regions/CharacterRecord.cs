using System.Buffers.Binary;
using DarkSun.Data.Gff;

namespace DarkSun.Data.Regions;

/// <summary>
/// A creature's 0x47-byte character record: the type-4 entry of its
/// <c>RDFF</c>, after the combat record. DSUN.EXE copies it into the
/// character table (<c>DS:1661</c>, 17 records; <c>7155:0732</c>, object kind
/// 4). The fields it reads (<c>dsun-exe-reference.md</c> §8):
/// <list type="bullet">
/// <item><see cref="Size"/> (+0x3D): the footprint, half-width = size &amp; 7 and
/// height = size / 8 (<c>1A0A:2AB4</c>, <c>7372:0909</c>). An object without a
/// character record has size 0, one cell.</item>
/// <item><see cref="HitPoints"/> (+8): multiplied by (difficulty − 1) × 50 + 100 %
/// for creatures outside the party, then made the combat record's hit points
/// (<c>7155:0000</c>).</item>
/// <item><see cref="ExperienceValue"/> (+4): what killing it is worth (<c>612E:25F2</c>).</item>
/// </list>
/// The rest is in <see cref="Bytes"/>; the properties read it.
/// </summary>
public sealed record CharacterRecord(byte Size, ushort DifficultyScaled, byte[] Bytes)
{
    public const int Length = 0x47;
    public const int Type = 4;
    public const int SizeOffset = 0x3D;

    /// <summary>Natural attacks a creature can have (<c>80F5:2ACC</c>).</summary>
    public const int NaturalAttackCount = 3;

    /// <summary>+0, int32: a party member's experience. Creatures have other numbers here (mostly 1000).</summary>
    public int Experience => BinaryPrimitives.ReadInt32LittleEndian(Bytes);

    /// <summary>
    /// +4, int32: experience for killing it, shared among the party
    /// (<c>612E:25F2</c>); AD&amp;D's values by hit dice (35, 65, 120, 175, 270,
    /// 420, 650, 975, 1400 …). For a party member, the most it has ever had.
    /// </summary>
    public int ExperienceValue => BinaryPrimitives.ReadInt32LittleEndian(Bytes.AsSpan(4));

    /// <summary>+8: hit points before the difficulty is applied.</summary>
    public int HitPoints => DifficultyScaled;

    /// <summary>+0x12: the classes as <c>IT1R</c> class bits (what it may wear, <c>6466:0854</c>).</summary>
    public ushort ClassBits => BinaryPrimitives.ReadUInt16LittleEndian(Bytes.AsSpan(0x12));

    /// <summary>+0x18: race (1 human … 8 thri-kreen; creatures use other numbers too).</summary>
    public byte Race => Bytes[0x18];

    /// <summary>+0x1A: alignment, 1–9; 3, 6 and 9 are evil (<c>80F5:377E</c>, Protection from Evil).</summary>
    public byte Alignment => Bytes[0x1A];

    /// <summary>+0x21–0x23: class codes (0 none).</summary>
    public ReadOnlySpan<byte> ClassCodes => Bytes.AsSpan(0x21, 3);

    /// <summary>+0x24–0x26: the level in each class code.</summary>
    public ReadOnlySpan<byte> Levels => Bytes.AsSpan(0x24, 3);

    /// <summary>+0x27: armour class before armour and effects (<c>612E:2113</c>).</summary>
    public sbyte BaseArmorClass => (sbyte)Bytes[0x27];

    /// <summary>
    /// +0x29: magic resistance, % (<c>80F5:1901</c>; vrock 70, babau 50,
    /// psurlon 45): rolled against before a spell's save, +75 against mind
    /// spells with Mind Bar, halved by Low Resistance, less 30 + level against
    /// Lower Resistance.
    /// </summary>
    public sbyte MagicResistance => (sbyte)Bytes[0x29];

    /// <summary>+0x2A: attacks a round, in halves (3 alternates 1 and 2).</summary>
    public byte AttackHalves => Bytes[0x2A];

    /// <summary>
    /// Natural attack <paramref name="i"/> (0–2; <c>80F5:2ACC</c>): attacks a
    /// round in halves (like <see cref="AttackHalves"/>), dice and sides of
    /// damage, bonus, from +0x2B, +0x2E, +0x31 and +0x34 + i. Used when it
    /// fights without a weapon (<c>612E:1B7A</c>); most have 2 (one attack) of 1d2.
    /// </summary>
    public NaturalAttack NaturalAttack(int i) =>
        new((sbyte)Bytes[0x2B + i], (sbyte)Bytes[0x2E + i], (sbyte)Bytes[0x31 + i], (sbyte)Bytes[0x34 + i]);

    /// <summary>+0x37–0x3B: saving throws (paralysation, rod, petrification, breath, spell).</summary>
    public ReadOnlySpan<byte> Saves => Bytes.AsSpan(0x37, 5);

    /// <summary>+0x42: the sound it makes when a blow hurts it (<c>80F5:0079</c>; 0: sound 4).</summary>
    public short HurtSound => BinaryPrimitives.ReadInt16LittleEndian(Bytes.AsSpan(0x42));

    /// <summary>+0x44: the sound of its blows (<c>896E:0000</c>; 0 none).</summary>
    public short AttackSound => BinaryPrimitives.ReadInt16LittleEndian(Bytes.AsSpan(0x44));

    /// <summary>+0x3C: its own allegiance, which it goes back to when a charm ends (<c>8053:066F</c>).</summary>
    public byte HomeAllegiance => Bytes[0x3C];

    /// <summary>
    /// +0x3E: the spells a creature casts, 0 none: a row of 7 at <c>3FB9:0110</c>
    /// (<c>6798:032B</c>); 1 Magic Missile, Fireball, Acid Arrow, Ice Storm, Cone of Cold.
    /// </summary>
    public byte SpellSet => Bytes[0x3E];

    /// <summary>
    /// +0x46: the psionic powers a creature uses, 0 none: a row of 10 at
    /// <c>37F0:001C</c> (1-based; power = byte + 139), drawn at random (<c>6466:0453</c>).
    /// </summary>
    public byte PsionicSet => Bytes[0x46];

    public static CharacterRecord Parse(ReadOnlySpan<byte> record) =>
        new(record[SizeOffset], (ushort)(record[8] | record[9] << 8), record[..Length].ToArray());

    /// <summary>The character record of an <c>RDFF</c> resource, or null if it has none.</summary>
    public static CharacterRecord? FromRdff(ReadOnlyMemory<byte> rdff) =>
        Rdff.Entries(rdff).FirstOrDefault(e => e.Type == Type && e.Body.Length >= Length) is { } entry
            ? Parse(entry.Body.Span)
            : null;
}

/// <summary>A natural attack: <paramref name="Halves"/> / 2 attacks a round of <paramref name="Dice"/>d<paramref name="Sides"/> + <paramref name="Bonus"/>.</summary>
public readonly record struct NaturalAttack(int Halves, int Dice, int Sides, int Bonus)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Exists => Halves > 0;
}
