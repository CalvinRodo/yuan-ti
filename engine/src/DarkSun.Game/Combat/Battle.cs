using DarkSun.Data.Items;
using DarkSun.Game.Movement;
using DarkSun.Game.Rules;
using DarkSun.Game.World;

namespace DarkSun.Game.Combat;

/// <summary>
/// A fight, as DSUN.EXE runs one (<c>612E</c>, <c>80F5</c>, <c>7D80</c>;
/// <c>dsun-exe-reference.md</c> §8): rounds with initiative and movement
/// points, turns in initiative order, attacks, spells and abilities, effects
/// with durations or charges, guarding and waiting, and the AI for
/// computer-controlled combatants (<see cref="RunAi"/>). The rules come from
/// <see cref="CombatRules"/>, <see cref="SpellRules"/> and <see cref="CreatureRules"/>,
/// so mods change them with JSON. It knows nothing of the screen: the scene
/// drives it and shows <see cref="Log"/>.
/// </summary>
public sealed partial class Battle
{
    private readonly List<Combatant> _all;

    public Battle(IEnumerable<Combatant> combatants, ICombatMap map, ICombatDice dice,
        CombatRules? rules = null, SpellRules? spells = null, CreatureRules? creatures = null)
    {
        _all = [.. combatants];
        foreach (Combatant c in _all.Where(c => c.HomeSide == 0))
        {
            c.HomeSide = c.Side;
        }
        Map = map;
        Dice = dice;
        Rules = rules ?? CombatRules.Default;
        Spells = spells ?? SpellRules.Default;
        Creatures = creatures ?? CreatureRules.Default;
    }

    public ICombatMap Map { get; }

    public ICombatDice Dice { get; }

    public CombatRules Rules { get; }

    public SpellRules Spells { get; }

    public CreatureRules Creatures { get; }

    /// <summary>Item types, to take corroded armour off a combatant's armour class; without it the class stays.</summary>
    public ItemCatalog? Catalog { get; init; }

    /// <summary>The game's difficulty, 1–4 (<c>DS:11AE</c> + 1): monsters add difficulty − 1 to hit.</summary>
    public int Difficulty { get; init; } = 1;

    public IReadOnlyList<Combatant> Combatants => _all;

    /// <summary>Rounds started.</summary>
    public int Round { get; private set; }

    /// <summary>The round clock in game seconds (<c>DS:035B</c>), up 60 a round; effects end by it.</summary>
    public int Clock { get; private set; }

    /// <summary>Whose turn it is.</summary>
    public Combatant? Current { get; private set; }

    public List<CombatEvent> Log { get; } = [];

    public event Action<CombatEvent>? Happened;

    /// <summary>A combatant went down, died or was destroyed (status 3 or more): the caller takes it off the map, gives experience.</summary>
    public event Action<Combatant>? Defeated;

    /// <summary>No two sides that fight each other are left (<c>612E:281F</c>).</summary>
    public bool Over => !_all.Any(a => a.Fights && _all.Any(a.IsEnemyOf));

    public void Add(Combatant c)
    {
        if (c.HomeSide == 0)
        {
            c.HomeSide = c.Side;
        }
        _all.Add(c);
    }

    private void Note(CombatEvent e)
    {
        Log.Add(e);
        Happened?.Invoke(e);
    }

    private int Roll(int low, int high) => Dice.Roll(low, high);

    /// <summary><paramref name="n"/> dice of <paramref name="sides"/> (<c>612E:1F92</c>).</summary>
    public int RollDice(int n, int sides)
    {
        int total = 0;
        for (int i = 0; i < n; i++)
        {
            total += sides > 0 ? Roll(1, sides) : 0;
        }
        return total;
    }

    // -- rounds and turns (612E:0754, 612E:09B6) -----------------------------------------------------

    /// <summary>
    /// A new round (<c>612E:0754</c>): the clock goes up 60, effects that ran
    /// out end, and each fighter rolls initiative (base + 0 … dice − 1 +
    /// Dexterity's reaction), gets Move × 10 movement points (none when
    /// Paralyzed or Stuck, half Slowed, double Hasted; <c>80F5:36CC</c>) and all
    /// its action flags. Fields whose time is up go. Then each round's effects
    /// come (<c>8053:0163</c>): regeneration, poison and acid, Protection from
    /// Evil 10' Radius, and a party member's upkeep for the powers it keeps up
    /// (<c>6698:04C7</c>).
    /// </summary>
    public void StartRound()
    {
        Round++;
        Clock += SpellEffect.Round;
        Current = null;
        Note(new RoundStarted(Round));
        foreach (Combatant c in _all)
        {
            foreach (ActiveEffect e in c.Effects.Where(e => e.EndsAt <= Clock).ToList())
            {
                EndEffect(c, e);
            }
        }
        ExpireFields();
        foreach (Combatant c in _all.Where(c => c.Fights))
        {
            c.Initiative = Rules.InitiativeBase + Roll(0, Rules.InitiativeDice - 1) + AbilityTables.DexReaction(c.Stats[1]);
            c.TieBreak = Roll(0, 199);
            int points = c.Move * Rules.MovementPerMove;
            c.MovementPoints = c.Has(Condition.Paralyzed) || c.Has(Condition.Stuck) ? 0
                : c.Has(Condition.Slowed) ? points / 2
                : c.Has(Condition.Hasted) ? points * 2
                : points;
            c.Flags = TurnFlags.All;
            if (c.MovementPoints < Rules.StepCost)
            {
                c.Flags &= ~TurnFlags.MayMove;
            }
            c.TriedSpells = false;
        }
        foreach (Combatant c in _all.Where(c => c.Fights).ToList())
        {
            RoundEffects(c);
            KeepUpPowers(c);
        }
    }

    /// <summary>
    /// The next to act (<c>612E:09B6</c>): the highest initiative, a tie to the
    /// higher tie-breaker; null when the round is over. Its guard ends, and on
    /// its first turn of a round its mind is set (<see cref="MindOf"/>) and a
    /// cloud it stands in acts on it again (<c>8053:00EB</c>).
    /// </summary>
    public Combatant? NextTurn()
    {
        Current = _all.Where(c => c.Fights && c.Initiative >= 0 && c.Flags != TurnFlags.None)
            .OrderByDescending(c => c.Initiative).ThenByDescending(c => c.TieBreak).FirstOrDefault();
        if (Current is { } c)
        {
            c.Guarding = false;
            c.Flags &= ~TurnFlags.Waited;
            if ((c.Flags & TurnFlags.NewRound) != 0)
            {
                c.CurrentMind = MindOf(c);
                c.Flags &= ~TurnFlags.NewRound;
                UpdateFields(c, moved: false, turn: true);
            }
            Note(new TurnStarted(c));
            if (!c.Alive || Current != c)
            {
                return NextTurn();
            }
        }
        return Current;
    }

    /// <summary>END TURN, and how every turn ends (<c>612E:2596(c, 0)</c>): out of this round.</summary>
    public void EndTurn(Combatant c)
    {
        c.Initiative = -1;
        c.Flags = TurnFlags.None;
        if (Current == c)
        {
            Current = null;
        }
    }

    /// <summary>
    /// GUARD (<c>612E:2514(c, 1)</c>): the turn ends, and until its next turn it
    /// attacks the first enemy that moves next to it.
    /// </summary>
    public void Guard(Combatant c)
    {
        c.Guarding = (c.Flags & TurnFlags.MayAttack) != 0;
        EndTurn(c);
        if (c.Guarding)
        {
            Note(new Guarded(c));
        }
    }

    /// <summary>
    /// WAIT (<c>612E:2514(c, 2)</c>): it acts again later this round, its
    /// initiative down to 10 (or by one below that). With no one left to act
    /// before it, waiting ends the turn.
    /// </summary>
    public void Wait(Combatant c)
    {
        c.Initiative = c.Initiative > 10 ? 10 : c.Initiative > 1 ? c.Initiative - 1 : c.Initiative;
        c.Flags |= TurnFlags.Waited;
        if (Current == c)
        {
            Current = null;
        }
        Note(new Waited(c));
        if (!_all.Any(o => o != c && o.Fights && o.Initiative >= c.Initiative && o.Flags != TurnFlags.None))
        {
            EndTurn(c);
        }
    }

    /// <summary>
    /// Plays whole rounds: each combatant under the AI acts (<see cref="RunAi"/>);
    /// any other just ends its turn. Stops when the fight is over or after
    /// <paramref name="rounds"/>. For tests and computer-only fights; the scene
    /// gives the player their turns instead.
    /// </summary>
    public void Fight(int rounds)
    {
        for (int r = 0; r < rounds && !Over; r++)
        {
            StartRound();
            while (!Over && NextTurn() is { } c)
            {
                if (c.UnderAi)
                {
                    RunAi(c);
                }
                if (Current == c)
                {
                    EndTurn(c);
                }
            }
        }
    }

    // -- the map ---------------------------------------------------------------------------------------

    /// <summary>Distance in cells (<c>0898:000C</c>).</summary>
    public static int Distance(Cell a, Cell b) => GplGeometry.Distance(a.X, a.Y, b.X, b.Y);

    /// <summary>Between two combatants, their nearest cells (<c>1A0A:2BA5</c>, footprints for larger creatures).</summary>
    public static int Distance(Combatant a, Combatant b) =>
        a.Size == 0 && b.Size == 0 ? Distance(a.Cell, b.Cell) : a.Footprint.Min(x => b.Footprint.Min(y => Distance(x, y)));

    /// <summary>The line between two cells is clear of sight-blocking cells, the ends left out (<c>0898:00A4</c>).</summary>
    public bool InSight(Cell from, Cell to)
    {
        int dx = Math.Abs(to.X - from.X), sx = from.X < to.X ? 1 : -1;
        int dy = -Math.Abs(to.Y - from.Y), sy = from.Y < to.Y ? 1 : -1;
        int err = dx + dy;
        int x = from.X, y = from.Y;
        while (x != to.X || y != to.Y)
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
            if ((x != to.X || y != to.Y) && BlocksSight(new Cell(x, y)))
            {
                return false;
            }
        }
        return true;
    }

    public bool InSight(Combatant a, Combatant b) => InSight(a.Cell, b.Cell);

    /// <summary>Someone standing (alive) on the cell, or with the cell in its footprint.</summary>
    public Combatant? At(Cell cell) => _all.FirstOrDefault(c => c.Alive && (c.Size == 0 ? c.Cell == cell : c.Footprint.Contains(cell)));

    public bool Free(Cell cell) => Walkable(cell) && At(cell) is null;

    /// <summary>Would <paramref name="c"/>'s footprint fit with its cell at <paramref name="at"/> (<c>1A0A:11DB</c>)?</summary>
    public bool Fits(Combatant c, Cell at)
    {
        int w = c.Size & 7, h = c.Size >> 3;
        for (int dy = -h; dy <= 0; dy++)
        {
            for (int dx = -w; dx <= w; dx++)
            {
                var cell = new Cell(at.X + dx, at.Y + dy);
                if (!Walkable(cell) || At(cell) is { } other && other != c)
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static readonly Cell[] Around =
        [new(0, -1), new(1, -1), new(1, 0), new(1, 1), new(0, 1), new(-1, 1), new(-1, 0), new(-1, -1)];

    /// <summary>Direction 0–7 clockwise from north, from <paramref name="from"/> toward <paramref name="to"/>.</summary>
    public static int Direction(Cell from, Cell to)
    {
        double angle = Math.Atan2(to.X - from.X, -(to.Y - from.Y));
        return ((int)Math.Round(angle / (Math.PI / 4)) + 8) % 8;
    }

    /// <summary>The free cell next to <paramref name="target"/> nearest <paramref name="mover"/> (<c>1A0A:2E99</c>).</summary>
    public Cell? FreeCellNextTo(Combatant target, Combatant mover) =>
        Around.Select(d => target.Cell + d).Where(c => c == mover.Cell || Fits(mover, c))
            .OrderBy(c => Distance(c, mover.Cell)).Cast<Cell?>().FirstOrDefault();

    /// <summary>
    /// Where to flee from <paramref name="from"/> (order mode 1, <c>1A0A:0B0F</c>):
    /// starting at the free cell next to it nearest the fleer, the free cell
    /// within x ± 4, y − 6 … + 4 of the fleer farthest from that start (<c>1A0A:2C86</c>).
    /// </summary>
    public Cell? FleeCell(Combatant fleer, Combatant from)
    {
        Cell start = FreeCellNextTo(from, fleer) ?? from.Cell;
        Cell? best = null;
        int bestDistance = Distance(fleer.Cell, start);
        for (int dy = -6; dy <= 4; dy++)
        {
            for (int dx = -4; dx <= 4; dx++)
            {
                var c = new Cell(fleer.Cell.X + dx, fleer.Cell.Y + dy);
                if (Fits(fleer, c) && Distance(c, start) > bestDistance)
                {
                    (best, bestDistance) = (c, Distance(c, start));
                }
            }
        }
        return best;
    }

    // -- movement (612E:2446) --------------------------------------------------------------------------

    /// <summary>
    /// One step to a neighbouring cell its footprint fits (<c>612E:2446</c>): 10
    /// points straight, 14 diagonal; under 10 left, it can't move again this
    /// round. The fields it enters, leaves or walks in act (<see cref="Fields"/>),
    /// and each guard of another side next to where it steps attacks it.
    /// </summary>
    public bool Step(Combatant c, Cell to)
    {
        if ((c.Flags & TurnFlags.MayMove) == 0 || Math.Max(Math.Abs(to.X - c.Cell.X), Math.Abs(to.Y - c.Cell.Y)) != 1 || !Fits(c, to))
        {
            return false;
        }
        Cell from = c.Cell;
        c.MovementPoints -= from.X != to.X && from.Y != to.Y ? Rules.DiagonalStepCost : Rules.StepCost;
        if (c.MovementPoints < Rules.StepCost)
        {
            c.Flags &= ~TurnFlags.MayMove;
        }
        c.Facing = Direction(from, to);
        c.Cell = to;
        if (c.Source is { } obj)
        {
            obj.FixedCell = to;
        }
        Note(new Moved(c, from, to));
        UpdateFields(c, moved: true, turn: false);
        foreach (Combatant guard in _all.Where(g => g.Guarding && g != c && g.Vitals.Status == Vitals.Well
            && g.IsEnemyOf(c) && Distance(g, c) < 2).ToList())
        {
            if (!c.Alive)
            {
                break;
            }
            guard.Flags |= TurnFlags.MayAttack;
            Attack(guard, c);
            guard.Guarding = false;
        }
        return true;
    }

    /// <summary>Walks toward <paramref name="goal"/> while it has points; the number of steps taken.</summary>
    public int MoveToward(Combatant c, Cell goal)
    {
        int steps = 0;
        while ((c.Flags & TurnFlags.MayMove) != 0 && c.Alive && c.Cell != goal)
        {
            List<Cell> path = Pathfinder.FindPath((x, y) => new Cell(x, y) != c.Cell && !Fits(c, new Cell(x, y)), c.Cell, goal, 4000);
            if (path.Count == 0 || !Step(c, path[0]))
            {
                break;
            }
            steps++;
        }
        return steps;
    }
}
