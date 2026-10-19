using System.Buffers.Binary;

namespace DarkSun.Data.Gff;

/// <summary>
/// One entry of an <c>RDFF</c> resource: a 10-byte header (load action,
/// block number, type, index, from, length) and, for inline data, its bytes.
/// </summary>
/// <param name="LoadAction">1: the record follows inline (<paramref name="Length"/> bytes);
/// 3: a reference to entry <paramref name="Index"/> of another table, no body.</param>
/// <param name="Type">1 item record, 2 combat record; references: 3 a <c>NAME</c>
/// entry, 4 (follows combat records, not decoded), 5 an <c>IT1R</c> entry.</param>
public sealed record RdffEntry(int LoadAction, int BlockNumber, int Type, int Index, int From, int Length, ReadOnlyMemory<byte> Body)
{
    public const int HeaderSize = 10;
    public const int Inline = 1;
    public const int Reference = 3;

    public const int ItemType = 1;
    public const int CombatType = 2;
    public const int NameReference = 3;
    public const int ItemTypeReference = 5;
}

/// <summary>
/// Reads the entries of an <c>RDFF</c> resource (<c>SEGOBJEX.GFF</c>): an item
/// is an item record, then references to its <c>IT1R</c> type and its
/// <c>NAME</c>, then a 0xFF end byte; a creature starts with its combat record.
/// Reading stops at the end byte, at an unknown load action (what follows
/// combat records is not decoded), or at the end of the data.
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
            int bodyLength = action == RdffEntry.Inline ? length : 0;
            if (action is not (RdffEntry.Inline or RdffEntry.Reference) || bodyLength < 0
                || at + RdffEntry.HeaderSize + bodyLength > span.Length)
            {
                break;
            }
            entries.Add(new RdffEntry(action, (sbyte)h[1], BinaryPrimitives.ReadInt16LittleEndian(h[2..]),
                BinaryPrimitives.ReadInt16LittleEndian(h[4..]), BinaryPrimitives.ReadInt16LittleEndian(h[6..]), length,
                data.Slice(at + RdffEntry.HeaderSize, bodyLength)));
            at += RdffEntry.HeaderSize + bodyLength;
        }
        return entries;
    }
}
