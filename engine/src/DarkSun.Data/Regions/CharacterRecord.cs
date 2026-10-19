using DarkSun.Data.Gff;

namespace DarkSun.Data.Regions;

/// <summary>
/// A creature's 0x47-byte character record: the type-4 entry of its
/// <c>RDFF</c>, after the combat record. DSUN.EXE copies it into the
/// character table (<c>DS:1661</c>, 17 records; <c>7155:0732</c>, object kind
/// 4) and reads:
/// <list type="bullet">
/// <item><see cref="Size"/> (+0x3D): the footprint, half-width = size &amp; 7 and
/// height = size / 8 (<c>1A0A:2AB4</c>, <c>7372:0909</c>). An object without a
/// character record has size 0, one cell.</item>
/// <item>+8: a word multiplied by (difficulty − 1) × 50 + 100 % for creatures
/// outside the party when loaded (likely hit points).</item>
/// </list>
/// The other fields are not decoded; <see cref="Bytes"/> keeps them.
/// </summary>
public sealed record CharacterRecord(byte Size, ushort DifficultyScaled, byte[] Bytes)
{
    public const int Length = 0x47;
    public const int Type = 4;
    public const int SizeOffset = 0x3D;

    public static CharacterRecord Parse(ReadOnlySpan<byte> record) =>
        new(record[SizeOffset], (ushort)(record[8] | record[9] << 8), record[..Length].ToArray());

    /// <summary>The character record of an <c>RDFF</c> resource, or null if it has none.</summary>
    public static CharacterRecord? FromRdff(ReadOnlyMemory<byte> rdff) =>
        Rdff.Entries(rdff).FirstOrDefault(e => e.Type == Type && e.Body.Length >= Length) is { } entry
            ? Parse(entry.Body.Span)
            : null;
}
