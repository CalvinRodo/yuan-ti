namespace DarkSun.Game.Movement;

/// <summary>A cell on a region map.</summary>
public readonly record struct Cell(int X, int Y)
{
    public static Cell operator +(Cell a, Cell b) => new(a.X + b.X, a.Y + b.Y);
}

/// <summary>
/// A* over region cells with 8-way movement. A diagonal step needs both cells
/// beside it to be free, so paths don't cut corners. This is the engine's
/// choice; how DSUN.EXE finds paths is not yet known.
/// </summary>
public static class Pathfinder
{
    private const int Straight = 10;
    private const int Diagonal = 14;

    private static readonly Cell[] Steps =
        [new(0, -1), new(1, -1), new(1, 0), new(1, 1), new(0, 1), new(-1, 1), new(-1, 0), new(-1, -1)];

    /// <summary>
    /// Cells to walk from <paramref name="start"/> (excluded) to
    /// <paramref name="goal"/>. If the goal can't be reached, the path leads to
    /// the reachable cell nearest to it; empty if that is the start.
    /// </summary>
    /// <param name="blocked">Whether a cell blocks movement (off-map cells must count as blocked).</param>
    /// <param name="maxNodes">Search limit, to bound the cost of unreachable goals.</param>
    public static List<Cell> FindPath(Func<int, int, bool> blocked, Cell start, Cell goal, int maxNodes = 20_000)
    {
        var cameFrom = new Dictionary<Cell, Cell>();
        var cost = new Dictionary<Cell, int> { [start] = 0 };
        var open = new PriorityQueue<Cell, int>();
        open.Enqueue(start, Heuristic(start, goal));
        Cell best = start;
        int bestDistance = Heuristic(start, goal);
        int expanded = 0;

        while (open.TryDequeue(out Cell current, out _) && expanded++ < maxNodes)
        {
            if (current == goal)
            {
                best = goal;
                break;
            }
            int distance = Heuristic(current, goal);
            if (distance < bestDistance)
            {
                best = current;
                bestDistance = distance;
            }

            foreach (Cell step in Steps)
            {
                Cell next = current + step;
                bool diagonal = step.X != 0 && step.Y != 0;
                if (blocked(next.X, next.Y)
                    || (diagonal && (blocked(current.X + step.X, current.Y) || blocked(current.X, current.Y + step.Y))))
                {
                    continue;
                }
                int nextCost = cost[current] + (diagonal ? Diagonal : Straight);
                if (!cost.TryGetValue(next, out int known) || nextCost < known)
                {
                    cost[next] = nextCost;
                    cameFrom[next] = current;
                    open.Enqueue(next, nextCost + Heuristic(next, goal));
                }
            }
        }

        var path = new List<Cell>();
        for (Cell c = best; c != start; c = cameFrom[c])
        {
            path.Add(c);
        }
        path.Reverse();
        return path;
    }

    /// <summary>Octile distance.</summary>
    private static int Heuristic(Cell a, Cell b)
    {
        int dx = Math.Abs(a.X - b.X);
        int dy = Math.Abs(a.Y - b.Y);
        return Straight * Math.Max(dx, dy) + (Diagonal - Straight) * Math.Min(dx, dy);
    }
}
