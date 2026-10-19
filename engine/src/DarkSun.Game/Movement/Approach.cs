namespace DarkSun.Game.Movement;

/// <summary>
/// Where to stand to use an object: the cell nearest by walking from which
/// the object is in reach. DSUN.EXE's own rule is not traced; this is the
/// engine's choice.
/// </summary>
public static class Approach
{
    /// <summary>Cells from an object within which it can be used (1: next to it, diagonals included).</summary>
    public const int Reach = 1;

    /// <summary>
    /// How far away a character may stop when it can't get next to the object
    /// (a creature behind a counter, a lever across a gap); beyond this the
    /// caller decides what to do.
    /// </summary>
    public const int MaxReach = 3;

    private static readonly Cell[] Steps =
        [new(0, -1), new(1, -1), new(1, 0), new(1, 1), new(0, 1), new(-1, 1), new(-1, 0), new(-1, -1)];

    /// <summary>Cells between <paramref name="cell"/> and the nearest of <paramref name="targets"/> (8-way).</summary>
    public static int Distance(Cell cell, IEnumerable<Cell> targets) =>
        targets.Min(t => Math.Max(Math.Abs(t.X - cell.X), Math.Abs(t.Y - cell.Y)));

    /// <summary>
    /// The cell to walk to from <paramref name="start"/> to use an object at
    /// <paramref name="targets"/>: the nearest by steps within
    /// <see cref="Reach"/> of it, or failing that the reachable cell closest to
    /// it within <paramref name="maxReach"/>; null if there is none. Moves are
    /// 8-way and a diagonal needs both cells beside it, like the walker's, and
    /// cost as in <see cref="Pathfinder"/> (10 straight, 14 diagonal), so the
    /// walk is the straightest of equally short ones.
    /// </summary>
    /// <param name="canStand">Whether the character can stand on a cell (the start always counts).</param>
    public static Cell? Goal(Func<Cell, bool> canStand, Cell start, IReadOnlyCollection<Cell> targets,
        int maxReach = MaxReach, int maxNodes = 4000)
    {
        if (targets.Count == 0)
        {
            return null;
        }
        var cost = new Dictionary<Cell, int> { [start] = 0 };
        var done = new HashSet<Cell>();
        var queue = new PriorityQueue<Cell, int>();
        queue.Enqueue(start, 0);
        Cell? best = null;
        int bestDistance = int.MaxValue;
        while (queue.TryDequeue(out Cell cell, out int walked) && done.Count < maxNodes)
        {
            if (!done.Add(cell) || walked > cost[cell])
            {
                continue;
            }
            int distance = Distance(cell, targets);
            if (distance <= Reach)
            {
                return cell; // cheapest first: no shorter walk gets in reach
            }
            if (distance < bestDistance)
            {
                (best, bestDistance) = (cell, distance);
            }
            foreach (Cell step in Steps)
            {
                Cell next = cell + step;
                bool diagonal = step.X != 0 && step.Y != 0;
                int nextCost = walked + (diagonal ? 14 : 10);
                if (done.Contains(next) || (cost.TryGetValue(next, out int known) && known <= nextCost) || !canStand(next)
                    || (diagonal && (!canStand(cell with { X = next.X }) || !canStand(cell with { Y = next.Y }))))
                {
                    continue;
                }
                cost[next] = nextCost;
                queue.Enqueue(next, nextCost);
            }
        }
        return bestDistance <= maxReach ? best : null;
    }
}
