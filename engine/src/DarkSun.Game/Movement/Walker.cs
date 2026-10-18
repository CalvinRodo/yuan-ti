using DarkSun.Data.Animation;
using DarkSun.Game.Animation;

namespace DarkSun.Game.Movement;

/// <summary>
/// A creature walking cell by cell, as DSUN.EXE animates it: each step to a
/// neighbouring cell plays the walk sequence for its direction, whose records
/// move the creature (one 16-pixel cell per cycle in the standard scripts),
/// then it stands facing the way it went.
/// </summary>
/// <remarks>
/// The creature holds its <see cref="Footprint"/> in an <see cref="Occupancy"/>
/// map: the cells it stands on, plus the cells it is stepping into. Paths are
/// planned on the region map alone, since other creatures move. A step into
/// another creature's cells waits, then takes a way around it if that is at
/// most <see cref="MaxDetour"/> cells longer, or waits again on the same path;
/// it gives up after <see cref="MaxReplans"/> tries.
/// </remarks>
public sealed class Walker
{
    public const int CellSize = 16;

    /// <summary>Extra cells a way around a creature may take before waiting is preferred.</summary>
    public const int MaxDetour = 8;

    /// <summary>Ticks to wait before planning around a creature in the way (about 0.25 s).</summary>
    public const int WaitTicks = 24;

    /// <summary>
    /// New paths planned for one goal before giving up: about 4 s of waiting,
    /// enough for a crowd of walkers to clear the way.
    /// </summary>
    public const int MaxReplans = 16;

    private readonly Scmd _script;
    private readonly IReadOnlyList<(int Width, int Height)> _frames;
    private readonly Occupancy _occupancy;
    private readonly Queue<Cell> _path = new();
    private Cell? _target;
    private Cell? _goal;
    private int _replans;
    private long _waitUntil;
    private bool _standing;

    /// <exception cref="InvalidOperationException">Another creature occupies the cells at <paramref name="cell"/>.</exception>
    public Walker(int id, Scmd script, IReadOnlyList<(int Width, int Height)> frames, Cell cell, Footprint footprint,
        Occupancy occupancy, Direction facing = Direction.South)
    {
        Id = id;
        _script = script;
        _frames = frames;
        Footprint = footprint;
        _occupancy = occupancy;
        if (!occupancy.TryClaim(id, footprint.Cells(cell)))
        {
            throw new InvalidOperationException($"cells at {cell} are occupied");
        }
        Cell = cell;
        Facing = facing;
        Animator = Stand();
    }

    public int Id { get; }

    public Scmd Script => _script;

    public IReadOnlyList<(int Width, int Height)> Frames => _frames;

    public Footprint Footprint { get; }

    /// <summary>The cell of the creature's feet, or the cell it is walking from.</summary>
    public Cell Cell { get; private set; }

    public Direction Facing { get; private set; }

    public ScmdAnimator Animator { get; private set; }

    public bool IsWalking => _target is not null || _path.Count > 0;

    /// <summary>Where the creature's feet are, in pixels: the bottom centre of its cell, plus the walk so far.</summary>
    public (int X, int Y) Feet => (Cell.X * CellSize + CellSize / 2 + Animator.OffsetX, (Cell.Y + 1) * CellSize + Animator.OffsetY);

    /// <summary>Top-left corner for drawing the current frame.</summary>
    public (int X, int Y) DrawPosition => (Feet.X - Animator.HotX, Feet.Y - Animator.HotY);

    /// <summary>
    /// Whether the creature could stand with its feet at <paramref name="cell"/>:
    /// no footprint cell blocked by the map or held by another creature.
    /// </summary>
    public bool CanStand(Cell cell, Func<int, int, bool> blocked) =>
        Footprint.Cells(cell).All(c => !blocked(c.X, c.Y) && !_occupancy.IsOccupiedByOther(c, Id));

    /// <summary>
    /// Plan a path to <paramref name="goal"/> (or as near as it can get) and
    /// walk it after the current step. Returns whether there is anywhere to go.
    /// </summary>
    public bool WalkTo(Cell goal, Func<int, int, bool> blocked)
    {
        _goal = goal;
        _replans = 0;
        return Plan(blocked, avoidCreatures: false);
    }

    /// <summary>Walk along <paramref name="path"/>, a list of adjacent cells, without replanning.</summary>
    public void Follow(IEnumerable<Cell> path)
    {
        _goal = null;
        _path.Clear();
        foreach (Cell c in path)
        {
            _path.Enqueue(c);
        }
    }

    public void Stop()
    {
        _goal = null;
        _path.Clear();
    }

    /// <summary>
    /// Put the creature at <paramref name="cell"/> at once (TELEPORT), if its
    /// footprint fits there: it stops, and stands facing the way it was.
    /// </summary>
    public bool Teleport(Cell cell, Func<int, int, bool> blocked)
    {
        if (!CanStand(cell, blocked))
        {
            return false;
        }
        Stop();
        _target = null;
        _occupancy.ReleaseAll(Id);
        _occupancy.TryClaim(Id, Footprint.Cells(cell));
        Cell = cell;
        _standing = false;
        Animator = Stand();
        return true;
    }

    /// <summary>Take the creature off the map: release its cells.</summary>
    public void Remove()
    {
        Stop();
        _occupancy.ReleaseAll(Id);
    }

    /// <summary>
    /// Advance to tick <paramref name="now"/>. A step ends when its walk
    /// cycle has stopped, or has carried the creature a whole cell (for
    /// scripts whose walk loops), and its last frame has been shown for its
    /// delay. A standing creature can start a step at any time, even while an
    /// idle animation loops. Returns whether the picture changed.
    /// </summary>
    /// <param name="blocked">Whether the region map blocks a cell (<c>GMAP</c> 0x40, off-map cells included).</param>
    public bool Update(long now, Func<int, int, bool> blocked)
    {
        // Check a step's end before animating, so a looping walk can't run on
        // past the cell.
        if (_target is not null && !StepDone(now))
        {
            return Animator.Update(now);
        }
        bool changed = _target is null && Animator.Update(now);

        if (_target is { } arrived)
        {
            var kept = Footprint.Cells(arrived).ToHashSet();
            _occupancy.Release(Id, Footprint.Cells(Cell).Where(c => !kept.Contains(c)));
            Cell = arrived;
            _target = null;
        }
        if (now >= _waitUntil && _path.TryPeek(out Cell next))
        {
            if (TryStep(next, now, blocked))
            {
                return true;
            }
        }
        if (!_standing)
        {
            _standing = true;
            Animator = Stand();
            Animator.Update(now);
            return true;
        }
        return changed;
    }

    private bool StepDone(long now) =>
        now >= Animator.NextStepAt
        && (!Animator.Animating || Math.Max(Math.Abs(Animator.OffsetX), Math.Abs(Animator.OffsetY)) >= CellSize);

    private bool TryStep(Cell next, long now, Func<int, int, bool> blocked)
    {
        int dx = next.X - Cell.X;
        int dy = next.Y - Cell.Y;
        bool adjacent = Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1 && (dx != 0 || dy != 0);
        if (!adjacent || Footprint.Cells(next).Any(c => blocked(c.X, c.Y)))
        {
            // The map changed under the path, or the path was bad: plan again.
            if (_goal is null || ++_replans > MaxReplans || !Plan(blocked, avoidCreatures: false))
            {
                Stop();
            }
            return false;
        }
        if (!_occupancy.TryClaim(Id, Footprint.Cells(next)))
        {
            // Another creature is in the way: wait, and take a way around it if
            // there is one; if not, keep the path and try the step again.
            _waitUntil = now + WaitTicks;
            if (_goal is null || ++_replans > MaxReplans)
            {
                Stop();
            }
            else
            {
                List<Cell> around = PathTo(blocked, avoidCreatures: true);
                if (around.Count > 0 && around[^1] == _goal.Value && around.Count <= _path.Count + MaxDetour)
                {
                    SetPath(around);
                }
            }
            return false;
        }

        _path.Dequeue();
        Facing = DirectionOf(dx, dy);
        _target = next;
        _standing = false;
        Animator = ScmdAnimator.ForCreature(_script, _frames, _script.WalkStart(Facing));
        Animator.Update(now);
        return true;
    }

    private bool Plan(Func<int, int, bool> blocked, bool avoidCreatures)
    {
        SetPath(PathTo(blocked, avoidCreatures));
        return _path.Count > 0;
    }

    private List<Cell> PathTo(Func<int, int, bool> blocked, bool avoidCreatures) =>
        Pathfinder.FindPath(
            avoidCreatures
                ? (x, y) => !CanStand(new Cell(x, y), blocked)
                : (x, y) => Footprint.Cells(new Cell(x, y)).Any(c => blocked(c.X, c.Y)),
            Cell,
            _goal!.Value);

    private void SetPath(IEnumerable<Cell> path)
    {
        _path.Clear();
        foreach (Cell c in path)
        {
            _path.Enqueue(c);
        }
    }

    private ScmdAnimator Stand() => ScmdAnimator.ForCreature(_script, _frames, _script.StandStart(Facing));

    /// <summary>Direction of a step: 0 N, then clockwise (DSUN.EXE tables <c>DS:2F52</c>/<c>DS:2F5B</c>).</summary>
    public static Direction DirectionOf(int dx, int dy) => (Math.Sign(dx), Math.Sign(dy)) switch
    {
        (0, -1) => Direction.North,
        (1, -1) => Direction.NorthEast,
        (1, 0) => Direction.East,
        (1, 1) => Direction.SouthEast,
        (0, 1) => Direction.South,
        (-1, 1) => Direction.SouthWest,
        (-1, 0) => Direction.West,
        _ => Direction.NorthWest,
    };
}
