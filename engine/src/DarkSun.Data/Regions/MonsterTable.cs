using System.Buffers.Binary;

namespace DarkSun.Data.Regions;

/// <summary>
/// <c>RESOURCE.GFF</c> <c>MONR</c> 1: the random monsters of each region, for
/// <c>REQUEST(8)</c> (<c>87B3:0CB4</c>). 42-byte records: +0 the region (255 for
/// the table used anywhere), then 10 slots of {+0 the monster's <c>OJFF</c>,
/// +2 its strength}, an <c>OJFF</c> of 0 for an empty slot. The slots are kept
/// as they are stored, empty ones too, since DSUN.EXE indexes them.
/// </summary>
public sealed class MonsterTable
{
    public const int RecordSize = 42;
    public const int Slots = 10;

    /// <summary>The region number of the table used anywhere (<c>REQUEST(8, …, b ≠ 0)</c>).</summary>
    public const int AnyRegion = 255;

    public readonly record struct Slot(int Ojff, int Strength);

    public static readonly MonsterTable Empty = new([]);

    private readonly (int Region, Slot[] Slots)[] _records;

    private MonsterTable((int Region, Slot[] Slots)[] records) => _records = records;

    public static MonsterTable Parse(ReadOnlySpan<byte> data)
    {
        var records = new (int, Slot[])[data.Length / RecordSize];
        for (int r = 0; r < records.Length; r++)
        {
            ReadOnlySpan<byte> record = data.Slice(r * RecordSize, RecordSize);
            var slots = new Slot[Slots];
            for (int s = 0; s < Slots; s++)
            {
                slots[s] = new Slot(BinaryPrimitives.ReadInt16LittleEndian(record[(2 + s * 4)..]),
                    BinaryPrimitives.ReadInt16LittleEndian(record[(4 + s * 4)..]));
            }
            records[r] = (BinaryPrimitives.ReadInt16LittleEndian(record), slots);
        }
        return new MonsterTable(records);
    }

    /// <summary>The slots of the first record for <paramref name="region"/>, or null if it has none.</summary>
    public IReadOnlyList<Slot>? For(int region)
    {
        foreach (var (r, slots) in _records)
        {
            if (r == region)
            {
                return slots;
            }
        }
        return null;
    }
}
