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
    /// From a creature's size byte, as <c>find_free_cell</c> (<c>7372:0909</c>)
    /// decodes it: half-width = size &amp; 7, height = size / 8. The game reads it
    /// from byte 0x3D of a 0x47-byte creature record (table <c>DS:1661</c>),
    /// whose source is not yet traced.
    /// </summary>
    public static Footprint FromSizeByte(byte size) => new(size & 7, size >> 3);

    /// <summary>
    /// An estimate from the widest sprite frame, until the size bytes are
    /// traced: as many cells across as the sprite covers, rounded down to an
    /// odd number (under 48 pixels: one cell; 48–79: 3 × 2; 80–111: 5 × 3).
    /// </summary>
    public static Footprint EstimateFromWidth(int widestFrame)
    {
        int halfWidth = Math.Clamp((widestFrame / 16 - 1) / 2, 0, 7);
        return new Footprint(halfWidth, halfWidth);
    }

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
