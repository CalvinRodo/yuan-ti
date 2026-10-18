using DarkSun.Data.Animation;
using DarkSun.Game.Animation;

namespace DarkSun.Game.Movement;

/// <summary>
/// A creature walking cell by cell, as DSUN.EXE animates it: each step to a
/// neighbouring cell plays the walk sequence for its direction, whose records
/// move the creature (one 16-pixel cell per cycle in the standard scripts),
/// then it stands facing the way it went.
/// </summary>
public sealed class Walker
{
    public const int CellSize = 16;

    private readonly Scmd _script;
    private readonly IReadOnlyList<(int Width, int Height)> _frames;
    private readonly Queue<Cell> _path = new();
    private Cell? _target;
    private bool _standing;

    public Walker(Scmd script, IReadOnlyList<(int Width, int Height)> frames, Cell cell, Direction facing = Direction.South)
    {
        _script = script;
        _frames = frames;
        Cell = cell;
        Facing = facing;
        Animator = Stand();
    }

    /// <summary>The cell the creature stands in, or is walking from.</summary>
    public Cell Cell { get; private set; }

    public Direction Facing { get; private set; }

    public ScmdAnimator Animator { get; private set; }

    public bool IsWalking => _target is not null || _path.Count > 0;

    /// <summary>Where the creature's feet are, in pixels: the bottom centre of its cell, plus the walk so far.</summary>
    public (int X, int Y) Feet => (Cell.X * CellSize + CellSize / 2 + Animator.OffsetX, (Cell.Y + 1) * CellSize + Animator.OffsetY);

    /// <summary>Top-left corner for drawing the current frame.</summary>
    public (int X, int Y) DrawPosition => (Feet.X - Animator.HotX, Feet.Y - Animator.HotY);

    /// <summary>Walk along <paramref name="path"/>, a list of adjacent cells, after finishing the current step.</summary>
    public void Follow(IEnumerable<Cell> path)
    {
        _path.Clear();
        foreach (Cell c in path)
        {
            _path.Enqueue(c);
        }
    }

    public void Stop() => _path.Clear();

    /// <summary>
    /// Advance to tick <paramref name="now"/>. A new step starts once the
    /// previous walk cycle has shown its last frame for its full delay.
    /// Returns whether the picture changed.
    /// </summary>
    public bool Update(long now, Func<int, int, bool> blocked)
    {
        bool changed = Animator.Update(now);
        if (Animator.Animating || now < Animator.NextStepAt)
        {
            return changed;
        }

        if (_target is { } arrived)
        {
            Cell = arrived;
            _target = null;
        }
        if (_path.TryDequeue(out Cell next))
        {
            int dx = next.X - Cell.X;
            int dy = next.Y - Cell.Y;
            if (Math.Abs(dx) > 1 || Math.Abs(dy) > 1 || (dx == 0 && dy == 0) || blocked(next.X, next.Y))
            {
                _path.Clear();
            }
            else
            {
                Facing = DirectionOf(dx, dy);
                _target = next;
                _standing = false;
                Animator = ScmdAnimator.ForCreature(_script, _frames, _script.WalkStart(Facing));
                Animator.Update(now);
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
