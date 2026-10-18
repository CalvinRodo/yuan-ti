using DarkSun.Data.Regions;

namespace DarkSun.Game.World;

/// <summary>Distances and sight lines as DSUN.EXE measures them for scripts.</summary>
public static class GplGeometry
{
    /// <summary>"Too far": also what RANGE returns when nothing matches.</summary>
    public const int Far = 999;

    /// <summary>
    /// Distance in cells, ported from <c>0898:000C</c>: an integer square root
    /// of dx² + dy², started from max + min / 4 or min / 8 and stepped up,
    /// in 16-bit arithmetic. Differences over 255 give 999.
    /// </summary>
    public static int Distance(int x1, int y1, int x2, int y2)
    {
        int dx = Math.Abs(x2 - x1);
        int dy = Math.Abs(y2 - y1);
        if (dx > 0xFF || dy > 0xFF)
        {
            return Far;
        }
        int squared = dx * dx + dy * dy;
        if (squared > 0xFFFF)
        {
            return Far;
        }
        int high = Math.Max(dx, dy);
        int low = Math.Min(dx, dy);
        int estimate = low + (low >> 1);
        int add = low >> 2;
        if (estimate <= high)
        {
            add >>= 1;
            if ((estimate << 1) <= high)
            {
                add = 0;
            }
        }
        int s = high + add;
        int acc = ((s & 0xFF) * (s & 0xFF) + s) & 0xFFFF;
        while (acc < squared)
        {
            s++;
            acc = (acc + s) & 0xFFFF;
        }
        return s;
    }

    /// <summary>
    /// Whether the line between two cells is clear of <c>GMAP</c> 0x80 cells
    /// (the end cells excluded). DSUN.EXE walks the line with its own stepper
    /// (<c>0898:0142</c>); this uses Bresenham's, which may differ in a few
    /// cells.
    /// </summary>
    public static bool LineOfSight(Region region, int x1, int y1, int x2, int y2)
    {
        int dx = Math.Abs(x2 - x1), sx = x1 < x2 ? 1 : -1;
        int dy = -Math.Abs(y2 - y1), sy = y1 < y2 ? 1 : -1;
        int err = dx + dy;
        int x = x1, y = y1;
        while (x != x2 || y != y2)
        {
            int e2 = 2 * err;
            if (e2 >= dy)
            {
                err += dy;
                x += sx;
            }
            if (e2 <= dx)
            {
                err += dx;
                y += sy;
            }
            if ((x != x2 || y != y2) && region.BlocksSight(x, y))
            {
                return false;
            }
        }
        return true;
    }
}
