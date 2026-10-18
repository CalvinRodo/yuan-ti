using DarkSun.Data.Regions;
using DarkSun.Game.Scripting;

namespace DarkSun.Game.World;

/// <summary>
/// The game's script host: dialogue as <see cref="DialogueGplHost"/>, and the
/// commands that act on creatures and the map carried out on a
/// <see cref="GameWorld"/>: orders (HUNT, GO, GOXY, FOLLOW, FLEE, FETCH,
/// WAIT, READORDERS), queries (GETXY, RANGE, NEXTTO, GET_LOS, SET_OTHER,
/// GET_STATUS, GETPARTY), TELEPORT, CLONE and the REQUEST codes that change
/// objects and cells. Engine variables and object fields live in the world.
/// Items, combat and the remaining REQUEST codes are logged only.
/// </summary>
public sealed class WorldGplHost : DialogueGplHost
{
    /// <summary>
    /// Engine variables (<c>gname[0–14]</c>), kept across regions: pass them
    /// to each region's <see cref="GameWorld"/>. While a world is attached the
    /// host uses that world's set.
    /// </summary>
    public int[] EngineVariables { get; } = new int[15];

    private int[] Engine => Map?.EngineVariables ?? EngineVariables;

    /// <summary>The current region's world, or null outside a region.</summary>
    public GameWorld? Map { get; set; }

    /// <summary>FIGHT: combat was asked for at this cell (<c>gname[1]</c>, <c>[2]</c>); combat is not modelled yet.</summary>
    public (int X, int Y)? PendingFight { get; set; }

    public override int? Execute(GplMachine machine, GplCommand command)
    {
        if (Map is not { } world)
        {
            return base.Execute(machine, command);
        }
        IReadOnlyList<int> v = command.Values;
        int? result = command.Mnemonic switch
        {
            "HUNT" => Order(world, v[0], OrderMode.Hunt, world.ActiveMember),
            "GO" => Order(world, v[1], OrderMode.Go, v[0]),
            "FETCH" => Order(world, v[1], OrderMode.Fetch, v[0]),
            "FOLLOW" => Order(world, v[1], OrderMode.Follow, v[0]),
            "GOXY" => Order(world, v[2], OrderMode.GoXY, v[0], v[1]),
            "WAIT" => Order(world, v[0], OrderMode.Wait, 0),
            "FLEE" => Order(world, v[0], OrderMode.Flee, 0),
            "READORDERS" => world.ReadOrders(v[0]),
            "GETXY" => Do(() => world.GetXY(v[0])),
            "RANGE" => world.Range(v[0], v[1]),
            "NEXTTO" => world.Range(v[0], v[1]) < 2 ? 1 : 0,
            "GET_LOS" => world.InSight(v[0], v[1], v[2]) ? 1 : 0,
            "SET_OTHER" => SetOther(world, v[0]),
            "GET_STATUS" => world.Status(-Math.Abs(v[0])),
            "GETPARTY" => NextPartyMember(world, v[0]),
            "TELEPORT" => Teleport(world, v),
            "CLONE" => world.Clone(v[0], v[1], v[2], v[3]),
            "REQUEST" => Request(world, v),
            "FIGHT" => Fight(world),
            _ => null,
        };
        return command.Mnemonic is "HUNT" or "GO" or "FETCH" or "FOLLOW" or "GOXY" or "WAIT" or "FLEE" or "READORDERS"
            or "GETXY" or "RANGE" or "NEXTTO" or "GET_LOS" or "SET_OTHER" or "GET_STATUS" or "GETPARTY" or "TELEPORT"
            or "CLONE" or "REQUEST" or "FIGHT"
            ? Logged(command, result)
            : base.Execute(machine, command);
    }

    private int? Logged(GplCommand command, int? result)
    {
        Log.Add((command.Where, command));
        return result;
    }

    private static int? Do(Action action)
    {
        action();
        return null;
    }

    private static int? Order(GameWorld world, int who, OrderMode mode, int target, int extra = 0)
    {
        world.SetOrder(who, mode, target, extra);
        return null;
    }

    /// <summary>SET_OTHER: a name (or 9999) picks its first match; an index is taken as it is.</summary>
    private static int SetOther(GameWorld world, int value)
    {
        if (value >= 0 && value != GameWorld.None)
        {
            world.SetEngineVariable(0x2C, value);
            return 1;
        }
        if (world.First(value) is { } obj)
        {
            world.SetEngineVariable(0x2C, obj.Index);
            return 1;
        }
        return 0;
    }

    private static int NextPartyMember(GameWorld world, int after)
    {
        var party = world.Party.Where(p => !p.Hidden).Select(p => p.Index).ToList();
        int at = after == EndOfParty ? -1 : party.IndexOf(after);
        return at + 1 < party.Count ? party[at + 1] : EndOfParty;
    }

    private static int? Teleport(GameWorld world, IReadOnlyList<int> v)
    {
        world.Teleport(v[0], v[1], v[2], v[3]);
        return v[0] == WholeParty ? 1 : null;
    }

    /// <summary>REQUEST(code, who, a, b): <c>87B3:0000</c>; see <c>dsun-exe-reference.md</c> §4.</summary>
    private static int? Request(GameWorld world, IReadOnlyList<int> v)
    {
        (int code, int who, int a, int b) = (v[0], v[1], v[2], v[3]);
        switch (code)
        {
            case 5: world.ToggleState(who); break;
            case 10: world.SetCellFlags(a, b, CellFlags.Blocked, true); break;
            case 11: world.SetCellFlags(a, b, CellFlags.Blocked, false); break;
            case 12: world.SetCellFlags(a, b, CellFlags.BlocksSight, true); break;
            case 13: world.SetCellFlags(a, b, CellFlags.BlocksSight, false); break;
            case 17 when who < 0: world.Show(who, a, b); break;
            case 18: world.SetAllegiance(who, a); break;
        }
        return null;
    }

    private int? Fight(GameWorld world)
    {
        PendingFight = (world.EngineVariable(0x21), world.EngineVariable(0x22));
        return null;
    }

    public override int ReadEngineVariable(GplMachine machine, int index) =>
        index is >= 0x20 and < 0x2F ? Engine[index - 0x20] : 0;

    public override void WriteEngineVariable(GplMachine machine, int index, int value)
    {
        if (index is >= 0x20 and < 0x2F)
        {
            Engine[index - 0x20] = value;
        }
    }

    public override int ReadField(GplMachine machine, GplObjectRef field) =>
        Map is { } world && Locate(world, field) is var (obj, f) ? obj.ReadField(f) : base.ReadField(machine, field);

    public override void WriteField(GplMachine machine, GplObjectRef field, int value)
    {
        if (Map is { } world && Locate(world, field) is var (obj, f))
        {
            obj.WriteField(f, value);
        }
        else
        {
            base.WriteField(machine, field, value);
        }
    }

    public override int TriggerId(GplMachine machine, int obj) => Map?[obj] is { } o ? o.Id : obj;

    public override string SpeakerName(GplMachine machine) =>
        Map?[Map.ActiveMember] is { Name: { Length: > 0 } name } ? name : base.SpeakerName(machine);

    /// <summary>
    /// The object and field a reference names. Every field of the path but
    /// the last is taken to hold an object index (an inference: the shipped
    /// scripts use one-field paths).
    /// </summary>
    private static (WorldObject Object, int Field)? Locate(GameWorld world, GplObjectRef field)
    {
        if (field.Path.Length == 0 || world.Resolve(field.Id) is not { } obj)
        {
            return null;
        }
        foreach (byte f in field.Path[..^1])
        {
            if (world[obj.ReadField(f)] is not { } next)
            {
                return null;
            }
            obj = next;
        }
        return (obj, field.Path[^1]);
    }
}
