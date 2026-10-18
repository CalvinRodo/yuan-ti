namespace DarkSun.Data.Regions;

/// <summary>
/// A <c>GMAP</c> cell byte (DSUN.EXE accessors in segment <c>142F</c>; see
/// <c>dsun-exe-reference.md</c> §3).
/// </summary>
[Flags]
public enum CellFlags : byte
{
    None = 0,

    /// <summary>Wall index: 0 none, else <c>WALL</c> region × 100 + index − 1.</summary>
    WallMask = 0x1F,

    /// <summary>A creature stands here. Set at run time only.</summary>
    Occupied = 0x20,

    /// <summary>Blocks movement (<c>gmap_is_blocked</c>, <c>142F:00DC</c>).</summary>
    Blocked = 0x40,

    /// <summary>Blocks line of sight (<c>line_of_sight</c>, <c>0898:00A4</c>).</summary>
    BlocksSight = 0x80,
}
