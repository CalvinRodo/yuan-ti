using DarkSun.Data.Regions;

namespace DarkSun.Game.Movement;

/// <summary>
/// The cells a creature covers, as DSUN.EXE tests them (<c>footprint_blocked</c>,
/// <c>1A0A:11DB</c>): x − <see cref="HalfWidth"/> to x + <see cref="HalfWidth"/>,
/// and y − <see cref="Height"/> to y, where (x, y) is the cell of its feet.
/// </summary>
public readonly record struct Footprint(int HalfWidth, int Height)
{
    public static readonly Footprint OneCell = new(0, 0);

    /// <summary>
    /// From a creature's size byte, as DSUN.EXE decodes it (<c>1A0A:2AB4</c>,
    /// <c>find_free_cell</c> <c>7372:0909</c>): half-width = size &amp; 7, height =
    /// size / 8. The byte is +0x3D of the creature's character record
    /// (<c>CharacterRecord</c>, from its <c>RDFF</c>).
    /// </summary>
    public static Footprint FromSizeByte(byte size) => new(size & 7, size >> 3);

    /// <summary>The footprint of a creature with this character record; without one, a single cell, as in DSUN.EXE.</summary>
    public static Footprint Of(CharacterRecord? record) => FromSizeByte(record?.Size ?? 0);

    public IEnumerable<Cell> Cells(Cell feet)
    {
        for (int y = feet.Y - Height; y <= feet.Y; y++)
        {
            for (int x = feet.X - HalfWidth; x <= feet.X + HalfWidth; x++)
            {
                yield return new Cell(x, y);
            }
        }
    }
}
