namespace DarkSun.Game.Movement;

/// <summary>
/// DSUN.EXE's own pathfinding (segment <c>08BB</c>, called from
/// <c>1A0A:0B0F</c>), ported to compare with <see cref="Pathfinder"/>
/// (<c>dsun-exe-reference.md</c> §3, "Pathfinding"). It is a "bug" algorithm,
/// not a search of the whole map:
/// <list type="number">
/// <item>Draw the straight 8-way line from start to goal (<c>08BB:0000</c>: step
/// by the signs of dx and dy, <c>096C:009B</c>), at most
/// <see cref="MaxPathLength"/> points; if the goal is blocked, aim at the last
/// free point of the line instead.</item>
/// <item>Record the start, and the cell before and after each blocked stretch
/// of the line, as nodes (<c>08BB:00B9</c>, at most <see cref="MaxNodes"/>).</item>
/// <item>From each node whose next line cell is blocked, send two
/// wall-followers round the obstacle, one turning anticlockwise and one
/// clockwise (<c>08BB:02D2</c>, <c>01D5</c>); the first to reach a node further
/// along the line wins. A follower that comes back to its own node fails; if
/// both fail, back up to the previous node. Give up after 500 rounds.</item>
/// <item>Replay the winning moves into the path (<c>08BB:05C1</c>).</item>
/// </list>
/// The caller tries first with the tighter wall-hugging check on (<c>DS:01FA</c>
/// = 1), then without. Only the map is tested (<c>footprint_blocked</c>,
/// <c>1A0A:11DB</c>); other creatures are dealt with when stepping.
/// </summary>
public static class OriginalPathfinder
{
    /// <summary>Path buffer entries (<c>1A0A:0B0F</c> passes 80).</summary>
    public const int MaxPathLength = 80;

    /// <summary>Node buffer entries (30).</summary>
    public const int MaxNodes = 30;

    /// <summary>Wall-following rounds before giving up (<c>08BB:02D2</c>: more than 499).</summary>
    public const int MaxRounds = 500;

    /// <summary>Steps of the 8 directions, 0 = north clockwise (<c>096C:0002</c>, <c>096C:0012</c>).</summary>
    private static readonly int[] Dx = [0, 1, 1, 1, 0, -1, -1, -1];
    private static readonly int[] Dy = [-1, -1, 0, 1, 1, 1, 0, -1];

    /// <summary>The two followers' turns (<c>DS:01FE</c>, <c>DS:0200</c>): anticlockwise and clockwise by 90°.</summary>
    private static readonly int[] Turns = [0, -2, 2];

    private const int End = -1;

    /// <summary>
    /// Cells to walk from <paramref name="start"/> (excluded) towards
    /// <paramref name="goal"/>, as DSUN.EXE finds them; null if it finds none.
    /// The path may end short of the goal: at the last free cell of the line
    /// when the goal is blocked, or when the buffer fills.
    /// </summary>
    /// <param name="blocked">Whether the mover's footprint at a cell is blocked (off the map counts as blocked).</param>
    public static List<Cell>? FindPath(Func<int, int, bool> blocked, Cell start, Cell goal)
    {
        var search = new Search(blocked);
        // DS:01FA: 1 for the first try, then 0 (1A0A:0F82..0FC7).
        for (int hug = 1; hug >= 0; hug--)
        {
            search.TightHug = hug == 1;
            if (search.Run(start, goal) is { } path)
            {
                return path.Count > 0 && path[0] == start ? path[1..] : path;
            }
        }
        return null;
    }

    /// <summary>The direction from a cell towards another: by the signs of dx and dy (8-way mode, <c>096C:009B</c>).</summary>
    public static int DirectionTowards(int x, int y, int gx, int gy)
    {
        int dx = gx - x, dy = gy - y;
        return (Math.Sign(dx), Math.Sign(dy)) switch
        {
            (0, < 0) => 0,
            ( > 0, < 0) => 1,
            ( > 0, 0) => 2,
            ( > 0, > 0) => 3,
            (0, > 0) => 4,
            ( < 0, > 0) => 5,
            ( < 0, 0) => 6,
            ( < 0, < 0) => 7,
            _ => 0,
        };
    }

    private static int Rotate(int by, int direction) => ((direction + by) % 8 + 8) % 8;

    private sealed class Search(Func<int, int, bool> blocked)
    {
        // Path entries (x, y, dir); dir End terminates.
        private readonly int[] _px = new int[MaxPathLength], _py = new int[MaxPathLength], _pd = new int[MaxPathLength];

        // Nodes, as the 18-byte records: x, y, dir, turn, straight link, two side links,
        // parent, next link. Links are node numbers + 1 (0: none yet, -1: dead).
        private readonly Node[] _nodes = new Node[MaxNodes];

        public bool TightHug { get; set; }

        private sealed class Node
        {
            public int X = -1, Y, Dir, Turn, Straight;
            public readonly int[] Side = new int[3]; // [1], [2]
            public int Parent = -1, Next = -1;
        }

        private bool Blocked(int x, int y) => blocked(x, y);

        /// <summary><c>08BB:0761</c>: is the cell one step in <paramref name="dir"/> from (x, y) blocked?</summary>
        private bool BlockedStep(int x, int y, int dir) => Blocked(x + Dx[dir], y + Dy[dir]);

        /// <summary><c>096C:0022</c>: the node at (x, y), numbered from 1, or −1.</summary>
        private int NodeAt(int x, int y)
        {
            for (int i = 0; i < MaxNodes && _nodes[i].X != -1; i++)
            {
                if (_nodes[i].X == x && _nodes[i].Y == y)
                {
                    return i + 1;
                }
            }
            return -1;
        }

        private void SetNode(int i, int x, int y, int dir) =>
            _nodes[i] = new Node { X = x, Y = y, Dir = dir };

        public List<Cell>? Run(Cell start, Cell goal)
        {
            // 08BB:07F3
            for (int i = 0; i < MaxNodes; i++)
            {
                _nodes[i] = new Node();
            }
            int last = Line(start.X, start.Y, goal.X, goal.Y);
            while (last > 0 && Blocked(_px[last], _py[last]))
            {
                last--;
            }
            if (last <= 0)
            {
                return null;
            }
            (int tx, int ty) = (_px[last], _py[last]);
            _pd[last + 1] = End;
            if (CollectNodes() == -1 || Follow(tx, ty) != 0)
            {
                return null;
            }
            _pd[last + 1] = 0;
            _pd[MaxPathLength - 1] = End;
            return Replay(tx, ty, goal.X, goal.Y);
        }

        /// <summary><c>08BB:0000</c>: the straight line; returns the index of its last point.</summary>
        private int Line(int x, int y, int gx, int gy)
        {
            Array.Clear(_pd);
            _pd[MaxPathLength - 1] = End;
            (_px[0], _py[0]) = (x, y);
            int dir = DirectionTowards(x, y, gx, gy);
            _pd[0] = dir;
            int i = 1;
            while (true)
            {
                if (x == gx && y == gy)
                {
                    return i - 1;
                }
                (x, y) = (x + Dx[dir], y + Dy[dir]);
                (_px[i], _py[i]) = (x, y);
                dir = DirectionTowards(x, y, gx, gy);
                _pd[i] = dir;
                if (i + 1 >= MaxPathLength)
                {
                    return i - 1;
                }
                i++;
            }
        }

        /// <summary><c>08BB:00B9</c>: the start, and the free cells either side of each blocked stretch.</summary>
        private int CollectNodes()
        {
            SetNode(0, _px[0], _py[0], _pd[0]);
            int count = 1;
            bool wasBlocked = false;
            for (int p = 0; ; p++)
            {
                if (_pd[p + 1] == End)
                {
                    if (NodeAt(_px[p], _py[p]) == -1)
                    {
                        SetNode(count, _px[p], _py[p], _pd[p]);
                    }
                    return 0;
                }
                bool isBlocked = Blocked(_px[p + 1], _py[p + 1]);
                if (!isBlocked && wasBlocked)
                {
                    SetNode(count++, _px[p + 1], _py[p + 1], _pd[p + 1]);
                }
                else if (isBlocked && !wasBlocked && NodeAt(_px[p], _py[p]) == -1)
                {
                    SetNode(count++, _px[p], _py[p], _pd[p]);
                }
                wasBlocked = isBlocked;
                if (count >= MaxNodes)
                {
                    return -1;
                }
            }
        }

        /// <summary>
        /// <c>08BB:01D5</c>: one step of a wall-follower turning by
        /// <paramref name="turn"/>. Returns the node it lands on (numbered from
        /// 1), 0 for none, −1 when it is boxed in.
        /// </summary>
        private int WallStep(ref int dir, ref int x, ref int y, int turn, out int moved)
        {
            moved = dir;
            int tries = 0;
            int d = dir;
            if ((d & 1) == 1)
            {
                d = Rotate(turn / 2, d);
            }
            while (true)
            {
                if (!BlockedStep(x, y, d))
                {
                    (x, y) = (x + Dx[d], y + Dy[d]);
                    moved = d;
                    dir = Rotate(-turn, d);
                    int node = NodeAt(x, y);
                    return node != -1 ? node : 0;
                }
                if (!TightHug || tries < 1)
                {
                    d = Rotate(turn, d);
                }
                else
                {
                    d = Rotate(-turn / 2, d);
                    if (BlockedStep(x, y, d))
                    {
                        d = Rotate(turn / 2 * 3, d);
                    }
                    else
                    {
                        tries = 0;
                    }
                }
                if (++tries > 3)
                {
                    return -1;
                }
            }
        }

        /// <summary><c>08BB:02D2</c>: link the nodes from the start to the target node. 0 on success, −1 if it fails.</summary>
        private int Follow(int tx, int ty)
        {
            int i = 0;
            int result = 0;
            var fx = new int[3];
            var fy = new int[3];
            var fd = new int[3];
            var back = new int[3];
            var backDir = new int[3];
            while (true)
            {
                // Straight on while the line ahead is free.
                while (true)
                {
                    if (i < 0 || i >= MaxNodes)
                    {
                        return result;
                    }
                    Node n = _nodes[i];
                    if (n.X < 0 || result != 0 || (n.X == tx && n.Y == ty))
                    {
                        return result;
                    }
                    if (n.Straight == -1 || BlockedStep(n.X, n.Y, n.Dir))
                    {
                        break;
                    }
                    n.Straight = i + 2;
                    if (i + 1 < MaxNodes && _nodes[i + 1].X >= 0)
                    {
                        n.Next = i + 2;
                    }
                    n.Turn = 0;
                    if (i + 1 < MaxNodes)
                    {
                        _nodes[i + 1].Parent = i;
                    }
                    i++;
                }

                // Round the obstacle with two followers.
                Node node = _nodes[i];
                node.Straight = -1;
                for (int k = 1; k <= 2; k++)
                {
                    (fx[k], fy[k], fd[k], back[k]) = (node.X, node.Y, node.Dir, 0);
                }
                int found = 0;
                for (int round = 0; result != -1 && found == 0; round++)
                {
                    if (round > MaxRounds - 1)
                    {
                        result = -1;
                    }
                    for (int k = 1; k <= 2; k++)
                    {
                        if (node.Side[k] == -1)
                        {
                            continue;
                        }
                        int hit = WallStep(ref fd[k], ref fx[k], ref fy[k], Turns[k], out _);
                        node.Side[k] = hit;
                        if (hit == 0)
                        {
                            continue;
                        }
                        int j = hit - 1;
                        if (hit == -1 || j == i)
                        {
                            node.Side[k] = -1; // boxed in, or back where it started
                        }
                        else if (j < i)
                        {
                            // Behind: remember it, unless that node's same side already failed.
                            if (_nodes[j].Side[k] == -1)
                            {
                                node.Side[k] = -1;
                            }
                            else
                            {
                                back[k] = j;
                                backDir[k] = fd[k];
                            }
                        }
                        else
                        {
                            // Ahead: done with this obstacle. If the follower passed an
                            // earlier node, the detour starts from there instead.
                            if (back[k] != 0)
                            {
                                Node from = _nodes[back[k]];
                                from.Side[k] = node.Side[k];
                                from.Dir = backDir[k];
                                int other = 3 - k;
                                if (from.Side[other] != -1)
                                {
                                    from.Side[other] = 0;
                                    from.Straight = -1;
                                }
                                node = from;
                                i = back[k];
                            }
                            found = 1;
                            _nodes[j].Parent = i;
                            int to = node.Side[k];
                            node.Turn = Turns[k];
                            node.Next = to;
                            i = to - 1;
                        }
                    }
                    if (found == 0 && node.Side[1] == -1 && node.Side[2] == -1)
                    {
                        // Both failed: back up to the node before this one.
                        node.Next = -1;
                        int parent = node.Parent;
                        if (parent is -1 or 0)
                        {
                            result = -1;
                        }
                        else
                        {
                            Node p = _nodes[parent];
                            if (p.Turn == 0)
                            {
                                return -1;
                            }
                            for (int s = 1; s <= 2; s++)
                            {
                                if (p.Side[s] == i + 1)
                                {
                                    p.Side[s] = -1;
                                }
                            }
                            if (p.Straight == i + 1)
                            {
                                p.Straight = -1;
                            }
                            i = parent;
                            found = -1;
                        }
                    }
                }
                if (result != 0)
                {
                    return result;
                }
            }
        }

        /// <summary><c>08BB:05C1</c>: walk the linked nodes, writing the cells passed.</summary>
        private List<Cell>? Replay(int tx, int ty, int gx, int gy)
        {
            var path = new List<Cell>();
            int current = 0;
            bool reload = true;
            int x = 0, y = 0, dir = 0, turn = 0;
            for (int guard = 0; guard < 10_000; guard++)
            {
                if (reload)
                {
                    Node n = _nodes[current];
                    (x, y, dir, turn) = (n.X, n.Y, n.Dir, n.Turn);
                    reload = false;
                }
                path.Add(new Cell(x, y));
                if (path.Count >= MaxPathLength - 1)
                {
                    return path; // the buffer is full: the path stops here
                }
                if (x == tx && y == ty)
                {
                    return path;
                }
                if (turn == 0)
                {
                    (x, y) = (x + Dx[dir], y + Dy[dir]);
                    dir = DirectionTowards(x, y, gx, gy);
                }
                else if (WallStep(ref dir, ref x, ref y, turn, out _) == -1)
                {
                    return null;
                }
                int at = NodeAt(x, y);
                if (current < at - 1)
                {
                    current = at - 1;
                    reload = true;
                }
            }
            return null;
        }
    }
}
