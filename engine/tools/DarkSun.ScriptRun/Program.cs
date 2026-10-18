// Usage: DarkSun.ScriptRun <game-dir> [-v]
//
// Runs MAS 99 (new game), then for every region: MAS <region>, then every
// trigger the region registered (tile and box triggers at their cells,
// object and use-with triggers, creature no-orders and line-of-sight scripts).
// Prints one line per region and a summary; -v also prints the world commands.

using DarkSun.Data.Gff;
using DarkSun.Game.Rules;
using DarkSun.Game.Scripting;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: DarkSun.ScriptRun <game-dir> [-v]");
    return 2;
}
bool verbose = args.Contains("-v");
GffFile gpl = GffFile.Open(Path.Combine(args[0], "GPLDATA.GFF"));
byte[] fieldTypes = gpl.TryRead("GPLX", 1, out var gplx) ? GplDecoder.FieldTypes(gplx.Span) : [];
var host = new LoggingGplHost();
var machine = new GplMachine(new GffScripts(gpl), host, fieldTypes, new BorlandRandom(1));

int runs = 0, instructions = 0;
var errors = new List<string>();
void Count(GplRunResult? r)
{
    if (r is null)
    {
        return;
    }
    runs++;
    instructions += r.Instructions;
    if (r.Error is not null)
    {
        errors.Add(r.Error);
    }
}

Count(machine.NewGame());
Console.WriteLine($"MAS 99: {host.Log.Count} world commands, {Triggers()} triggers kept for every region");

var regions = gpl.DescribeResources().Where(r => r.Tag == "MAS " && r.Number != GplMachine.GameStartFile).Select(r => r.Number).Order();
foreach (int region in regions)
{
    int logStart = host.Log.Count;
    int errorStart = errors.Count;
    Count(machine.EnterRegion(region));
    string registered = Triggers();
    int creatures = machine.Creatures.NoOrders.Count + machine.Creatures.Sight.Count;

    // Fire everything the region registered. Each run can change state, so
    // take the lists first.
    var tiles = machine.Triggers.Records(GplTriggerList.MoveTile).ToList();
    var boxes = machine.Triggers.Records(GplTriggerList.MoveBox).ToList();
    foreach (var t in tiles)
    {
        Count(machine.Run(new GplEntry(t.File, t.Address)));
    }
    foreach (var b in boxes)
    {
        Count(machine.Run(new GplEntry(b.File, b.Address)));
    }
    foreach (GplTriggerList list in new[] { GplTriggerList.Pickup, GplTriggerList.TalkTo, GplTriggerList.Attack, GplTriggerList.Look, GplTriggerList.Use, GplTriggerList.UseWith })
    {
        foreach (var t in machine.Triggers.Records(list).ToList())
        {
            Count(machine.Run(new GplEntry(t.File, t.Address)));
        }
    }
    foreach (GplEntry e in machine.Creatures.NoOrders.Values.Concat(machine.Creatures.Sight.Values.Select(s => s.Entry)).ToList())
    {
        Count(machine.Run(e));
    }
    Console.WriteLine($"MAS {region,2}: {registered}; {creatures} creature scripts; "
        + $"{host.Log.Count - logStart} world commands; {errors.Count - errorStart} errors");
    if (verbose)
    {
        foreach (var (where, command) in host.Log.Skip(logStart))
        {
            Console.WriteLine($"    {where.Kind} {where.File} @{where.Address:x4}: {command}");
        }
    }
}

// Runs that hit the instruction limit are loops waiting for world state the
// logging host never changes (e.g. a dialogue that only ends when an engine
// variable has some value); anything else is an interpreter error.
var loops = errors.Where(e => e.EndsWith("instructions")).ToList();
var failures = errors.Except(loops).ToList();
Console.WriteLine($"{runs} runs, {instructions} instructions, {host.Log.Count} world commands, "
    + $"{failures.Count} errors, {loops.Count} loops waiting on world state");
var commands = host.Log.GroupBy(l => l.Command.Mnemonic).OrderByDescending(g => g.Count());
Console.WriteLine("world commands: " + string.Join(", ", commands.Select(g => $"{g.Key} {g.Count()}")));
foreach (string e in failures.Distinct().Take(30))
{
    Console.WriteLine($"  error: {e}");
}
foreach (string e in loops.Distinct().Take(10))
{
    Console.WriteLine($"  waiting: {e}");
}
return failures.Count == 0 ? 0 : 1;

string Triggers() => string.Join(" ", Enum.GetValues<GplTriggerList>()
    .Select(l => (l, n: machine.Triggers.Count(l))).Where(x => x.n > 0).Select(x => $"{x.l} {x.n}"));
