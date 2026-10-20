using System.Buffers.Binary;

namespace DarkSun.Data.Gff;

/// <summary>
/// One entry of an <c>RDFF</c> resource: a 10-byte header (load action,
/// block number, type, index, from, length) and the data that follows it.
/// DSUN.EXE's loader (<c>7155:0000</c>) numbers the objects an <c>RDFF</c>
/// makes in order (the object itself is block 0), and an entry's
/// <paramref name="BlockNumber"/> names the one it belongs to.
/// </summary>
/// <param name="LoadAction">What the entry does; every kind is followed by
/// <paramref name="Length"/> bytes of data (0 for references):
/// <list type="bullet">
/// <item>1 (<see cref="Inline"/>): the object's own record (<c>7155:0397</c>).</item>
/// <item>2 (<see cref="NewObject"/>): a new object, stored in field
/// <paramref name="From"/> of block <paramref name="BlockNumber"/>
/// (<c>7155:03D7</c>, <c>0826:012A</c>): a creature's first carried item.</item>
/// <item>3 (<see cref="Reference"/>): a character record (type 4), or field
/// <paramref name="From"/> of block <paramref name="BlockNumber"/> set to
/// <paramref name="Index"/>: an item's <c>IT1R</c> type (field 4) and <c>NAME</c> (field 2).</item>
/// <item>4 (<see cref="NextObject"/>): a new object linked after block
/// <paramref name="BlockNumber"/>'s (item record +4, <c>7155:0484</c>): the next carried item.</item>
/// </list></param>
/// <param name="Type">1 an item record, 2 a combat record, 3 a <c>NAME</c> entry
/// (<paramref name="Index"/>), 4 a character record (0x47 bytes, after the combat
/// record), 5 an <c>IT1R</c> entry (<paramref name="Index"/>).</param>
public sealed record RdffEntry(int LoadAction, int BlockNumber, int Type, int Index, int From, int Length, ReadOnlyMemory<byte> Body)
{
    public const int HeaderSize = 10;
    public const int Inline = 1;
    public const int NewObject = 2;
    public const int Reference = 3;
    public const int NextObject = 4;

    public const int ItemType = 1;
    public const int CombatType = 2;
    public const int NameReference = 3;
    public const int CharacterType = 4;
    public const int ItemTypeReference = 5;
}

/// <summary>
/// Reads the entries of an <c>RDFF</c> resource (<c>SEGOBJEX.GFF</c>): an item
/// is an item record, then references to its <c>IT1R</c> type and its
/// <c>NAME</c>, then a 0xFF end byte; a creature is its combat record, its
/// character record (type 4), then the items it carries, each an item record
/// (load action 2 for the first, 4 for the others) and its two references.
/// Reading stops at the end byte, at an unknown load action, or at the end of
/// the data.
/// </summary>
public static class Rdff
{
    public static IReadOnlyList<RdffEntry> Entries(ReadOnlyMemory<byte> data)
    {
        var entries = new List<RdffEntry>();
        ReadOnlySpan<byte> span = data.Span;
        int at = 0;
        while (at + RdffEntry.HeaderSize <= span.Length && span[at] != 0xFF)
        {
            ReadOnlySpan<byte> h = span[at..];
            int action = (sbyte)h[0];
            int length = BinaryPrimitives.ReadInt16LittleEndian(h[8..]);
            if (action is < RdffEntry.Inline or > RdffEntry.NextObject || length < 0
                || at + RdffEntry.HeaderSize + length > span.Length)
            {
                break;
            }
            entries.Add(new RdffEntry(action, (sbyte)h[1], BinaryPrimitives.ReadInt16LittleEndian(h[2..]),
                BinaryPrimitives.ReadInt16LittleEndian(h[4..]), BinaryPrimitives.ReadInt16LittleEndian(h[6..]), length,
                data.Slice(at + RdffEntry.HeaderSize, length)));
            at += RdffEntry.HeaderSize + length;
        }
        return entries;
    }
}
