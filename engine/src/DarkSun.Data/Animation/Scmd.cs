using System.Buffers.Binary;

namespace DarkSun.Data.Animation;

[Flags]
public enum ScmdFlags : byte
{
    None = 0,

    /// <summary>Reaching this record jumps to the index in its frame byte; the record is not shown.</summary>
    Jump = 0x01,

    /// <summary>Stop animating after this record.</summary>
    Stop = 0x02,

    /// <summary>Draw mirrored; a record without it draws unmirrored.</summary>
    Mirror = 0x04,

    /// <summary>Mark the object as moved even without dx/dy.</summary>
    Moved = 0x10,

    /// <summary>Calls <c>1DF3:2BCF</c> in DSUN.EXE; meaning unknown.</summary>
    Unknown20 = 0x20,

    /// <summary>Creatures: take the hot spot from the record instead of (width / 2, height).</summary>
    RecordHotSpot = 0x40,
}

/// <summary>One 8-byte <c>SCMD</c> record.</summary>
/// <param name="Frame"><c>BMP </c> frame; 0xFF keeps the current one. For a jump record, the target index.</param>
/// <param name="Delay">Ticks (96 Hz) before the next step; above 99 it is multiplied by 10.</param>
/// <param name="Sound">Sound effect to play; 0 for none.</param>
public readonly record struct ScmdRecord(
    byte Frame, byte Delay, ScmdFlags Flags, sbyte Dx, sbyte Dy, byte HotX, byte HotY, byte Sound)
{
    public const byte KeepFrame = 0xFF;

    /// <summary>The delay in 96 Hz ticks.</summary>
    public int DelayTicks => Delay > 99 ? Delay * 10 : Delay;
}

/// <summary>Directions as DSUN.EXE numbers them: clockwise from north.</summary>
public enum Direction { North, NorthEast, East, SouthEast, South, SouthWest, West, NorthWest }

/// <summary>
/// An animation script (<c>SCMD</c> resource): a 0x48-byte header of record
/// indexes per facing and action, then 8-byte records. Format and the
/// DSUN.EXE code that runs it: <c>dsun-exe-reference.md</c> §3.
/// </summary>
public sealed class Scmd
{
    public const int HeaderSize = 0x48;
    public const int SlotCount = 32;
    public const int ActionsPerFacing = 8;

    // DS:118C and DS:1184 in DSUN.EXE: header slot for standing and walking, by direction.
    private static readonly byte[] StandSlots = [0, 8, 8, 8, 16, 24, 24, 24];
    private static readonly byte[] WalkSlots = [1, 7, 9, 15, 17, 23, 25, 31];

    private Scmd(ushort[] slots, byte[] extra, ScmdRecord[] records)
    {
        Slots = slots;
        Extra = extra;
        Records = records;
    }

    /// <summary>Start record per slot: facing (N, E, S, W) × 8 + action (0 stand, 1 walk, 7 walk diagonally).</summary>
    public IReadOnlyList<ushort> Slots { get; }

    /// <summary>Header bytes 0x40–0x47; DSUN.EXE reads the word at 0x40 and the byte at 0x44 (meaning unknown).</summary>
    public IReadOnlyList<byte> Extra { get; }

    public IReadOnlyList<ScmdRecord> Records { get; }

    public static Scmd Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
        {
            throw new InvalidDataException($"SCMD is {data.Length} bytes, shorter than its header");
        }
        var slots = new ushort[SlotCount];
        for (int i = 0; i < SlotCount; i++)
        {
            slots[i] = BinaryPrimitives.ReadUInt16LittleEndian(data[(i * 2)..]);
        }

        var records = new ScmdRecord[(data.Length - HeaderSize) / 8];
        for (int i = 0; i < records.Length; i++)
        {
            ReadOnlySpan<byte> r = data.Slice(HeaderSize + i * 8, 8);
            records[i] = new ScmdRecord(r[0], r[1], (ScmdFlags)r[2], (sbyte)r[3], (sbyte)r[4], r[5], r[6], r[7]);
        }
        return new Scmd(slots, data[(SlotCount * 2)..HeaderSize].ToArray(), records);
    }

    public int StandStart(Direction direction) => Slots[StandSlots[(int)direction]];

    /// <summary>
    /// Whether every direction has its own walk sequence that moves: a walk
    /// slot different from the stand slot, whose first record has a dx or dy.
    /// Creature scripts move one cell per walk cycle (e.g. <c>SCMD</c> 2: four
    /// records of 4 pixels, 7 ticks each). Scripts with an all-zero header,
    /// such as the wandering-critter paths, don't qualify.
    /// </summary>
    public bool CanWalk => Enum.GetValues<Direction>().All(d =>
        WalkStart(d) != StandStart(d)
        && WalkStart(d) < Records.Count
        && (Records[WalkStart(d)].Dx != 0 || Records[WalkStart(d)].Dy != 0));

    public int WalkStart(Direction direction) => Slots[WalkSlots[(int)direction]];

    /// <summary>Attacking (<c>896E:0191</c>, action 4) and shooting or throwing (action 5).</summary>
    public const int AttackAction = 4, MissileAction = 5;

    /// <summary>
    /// The first record of action <paramref name="action"/> facing
    /// <paramref name="direction"/> (slot = facing × 8 + action, the facing as
    /// the stand slots map it), or null when the script has no such action
    /// (its slot is the stand slot's).
    /// </summary>
    public int? ActionStart(Direction direction, int action)
    {
        int slot = StandSlots[(int)direction] + action;
        return slot < Slots.Count && Slots[slot] != StandStart(direction) && Slots[slot] < Records.Count ? Slots[slot] : null;
    }
}
