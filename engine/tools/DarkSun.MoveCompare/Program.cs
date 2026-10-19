// Usage: DarkSun.MoveCompare <game-dir> [<pairs-per-region>]
//
// Paths: in every region, random start and goal cells (free for a one-cell
// creature, same seed every run) are given to the engine's A* (Pathfinder)
// and to the port of DSUN.EXE's own pathfinder (OriginalPathfinder). Prints,
// per region and in total: how often each reaches the goal, how long the
// original's paths are next to A*'s, how often it stops short or fails, and
// whether every step of its paths is a valid move.
//
// Walking: for every SCMD a character can walk with, the ticks a walk cycle
// takes in DSUN.EXE (the sum of its record delays, at 96 per second) and in
// the engine (a Walker timed walking 20 cells east on an open map).

using System.Globalization;
using DarkSun.Data.Animation;
using DarkSun.Data.Gff;
using DarkSun.Data.Regions;
using DarkSun.Game.Animation;
using DarkSun.Game.Movement;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("usage: DarkSun.MoveCompare <game-dir> [<pairs-per-region>]");
    return 2;
}
string dir = args[0];
int pairs = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 200;
GffFile objects = GffFile.Open(Path.Combine(dir, "SEGOBJEX.GFF"));
GffFile gplData = GffFile.Open(Path.Combine(dir, "GPLDATA.GFF"));

var regions = Directory.EnumerateFiles(dir, "RGN*.GFF").Order(StringComparer.Ordinal)
    .Select(f => (Name: Path.GetFileName(f), Region: Region.Load(GffFile.Open(f), [objects, gplData]))).ToList();
Compare("Goals anywhere on the map:", regions, pairs, near: false);
Compare("Goals within one screen of the original (up to 10 cells across, 6 down), as a click gives:", regions, pairs, near: true);

Console.WriteLine();
Console.WriteLine("walking: ticks per cell (96 ticks/s)   DSUN.EXE  engine   cells/s DSUN.EXE  engine");
var seen = new HashSet<int>();
var rows = new List<(int Scmd, int Original, double Engine)>();
foreach (ResourceDescriptor r in objects.DescribeResources().Where(r => r.Tag == "OJFF"))
{
    ObjectDefinition def = ObjectDefinition.Parse(objects.Read(r.Tag, r.Number).Span);
    if (def.Scmd <= 0 || !seen.Add(def.Scmd) || !objects.TryRead("SCMD", def.Scmd, out var data))
    {
        continue;
    }
    Scmd scmd = Scmd.Parse(data.Span);
    if (!scmd.CanWalk)
    {
        continue;
    }
    rows.Add((def.Scmd, OriginalTicksPerCell(scmd), EngineTicksPerCell(scmd)));
}
foreach (var group in rows.GroupBy(r => (r.Original, Math.Round(r.Engine, 1))).OrderByDescending(g => g.Count()))
{
    (int original, double engine) = group.Key;
    string o = original == 0 ? "0 (as fast as the PC)" : original.ToString(CultureInfo.InvariantCulture);
    string os = original == 0 ? "-" : (96.0 / original).ToString("F2", CultureInfo.InvariantCulture);
    Console.WriteLine($"  {group.Count(),3} SCMDs (e.g. {string.Join(", ", group.Take(4).Select(g => g.Scmd))})"
        + $"  {o,22}  {engine,6:F1}   {os,8}  {96.0 / engine,6:F2}");
}
return 0;

// Random pairs of free cells per region, through both pathfinders.
static void Compare(string title, List<(string Name, Region Region)> regions, int pairs, bool near)
{
    Console.WriteLine();
    Console.WriteLine(title);
    Console.WriteLine("region     pairs  A* reach  orig reach  orig short  orig none  length orig/A*  bad steps");
    var total = new Tally();
    foreach (var (name, region) in regions)
    {
        var free = new List<Cell>();
        for (int y = 0; y < region.Height; y++)
        {
            for (int x = 0; x < region.Width; x++)
            {
                if (!region.IsBlocked(x, y))
                {
                    free.Add(new Cell(x, y));
                }
            }
        }
        if (free.Count < 2)
        {
            continue;
        }
        var random = new Random(region.Number);
        var t = new Tally();
        for (int n = 0; n < pairs; n++)
        {
            Cell start = free[random.Next(free.Count)], goal = free[random.Next(free.Count)];
            if (near)
            {
                var nearby = free.Where(c => Math.Abs(c.X - start.X) <= 10 && Math.Abs(c.Y - start.Y) <= 6).ToList();
                goal = nearby[random.Next(nearby.Count)];
            }
            if (start == goal)
            {
                continue;
            }
            t.Pairs++;
            List<Cell> astar = Pathfinder.FindPath(region.IsBlocked, start, goal);
            bool astarReached = astar.Count > 0 && astar[^1] == goal;
            if (astarReached)
            {
                t.AStarReached++;
            }
            List<Cell>? original = OriginalPathfinder.FindPath(region.IsBlocked, start, goal);
            if (original is null || original.Count == 0)
            {
                t.OriginalNone++;
                continue;
            }
            if (original[^1] == goal)
            {
                t.OriginalReached++;
                if (astarReached)
                {
                    t.LengthRatios.Add((double)original.Count / astar.Count);
                }
            }
            else
            {
                t.OriginalShort++;
            }
            Cell at = start;
            foreach (Cell c in original)
            {
                if (Math.Max(Math.Abs(c.X - at.X), Math.Abs(c.Y - at.Y)) != 1 || region.IsBlocked(c.X, c.Y))
                {
                    t.BadSteps++;
                }
                at = c;
            }
        }
        if (!near)
        {
            Console.WriteLine($"{name,-10} {t}");
        }
        total.Add(t);
    }
    Console.WriteLine($"{"all",-10} {total}");
}

// DSUN.EXE: one walk cycle east, from the walk slot until a stop or jump, a record per delay.
static int OriginalTicksPerCell(Scmd scmd)
{
    int ticks = 0, moved = 0;
    for (int i = scmd.WalkStart(Direction.East); i < scmd.Records.Count && moved < 16; i++)
    {
        ScmdRecord r = scmd.Records[i];
        ticks += r.DelayTicks;
        moved += Math.Abs((int)r.Dx);
        if ((r.Flags & (ScmdFlags.Stop | ScmdFlags.Jump)) != 0)
        {
            break;
        }
    }
    return moved == 0 ? 0 : ticks * 16 / moved;
}

// The engine: a one-cell Walker walking 20 cells east on an open map.
static double EngineTicksPerCell(Scmd scmd)
{
    const int cells = 20;
    var frames = Enumerable.Repeat((16, 32), scmd.Records.Count == 0 ? 1 : 256).ToList();
    var walker = new Walker(1, scmd, frames, new Cell(0, 0), Footprint.OneCell, new Occupancy());
    static bool Open(int x, int y) => x < 0 || y < 0 || x > 100 || y > 100;
    walker.Update(0, Open);
    walker.Follow(Enumerable.Range(1, cells).Select(x => new Cell(x, 0)));
    long now = 0;
    while ((walker.IsWalking || walker.Cell.X < cells) && now < 100_000)
    {
        walker.Update(++now, Open);
    }
    return (double)now / cells;
}

sealed class Tally
{
    public int Pairs, AStarReached, OriginalReached, OriginalShort, OriginalNone, BadSteps;
    public List<double> LengthRatios { get; } = [];

    public void Add(Tally t)
    {
        (Pairs, AStarReached, OriginalReached, OriginalShort, OriginalNone, BadSteps) =
            (Pairs + t.Pairs, AStarReached + t.AStarReached, OriginalReached + t.OriginalReached,
             OriginalShort + t.OriginalShort, OriginalNone + t.OriginalNone, BadSteps + t.BadSteps);
        LengthRatios.AddRange(t.LengthRatios);
    }

    private string Percent(int n) => Pairs == 0 ? "-" : $"{100.0 * n / Pairs,5:F1}%";

    public override string ToString() =>
        $"{Pairs,5}  {Percent(AStarReached),8}  {Percent(OriginalReached),10}  {Percent(OriginalShort),10}  {Percent(OriginalNone),9}"
        + $"  {(LengthRatios.Count == 0 ? "-" : $"{LengthRatios.Average():F2} (max {LengthRatios.Max():F1})"),15}  {BadSteps,9}";
}
