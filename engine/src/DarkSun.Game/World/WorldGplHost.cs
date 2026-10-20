using DarkSun.Data.Regions;
using DarkSun.Game.Scripting;

namespace DarkSun.Game.World;

/// <summary>
/// The game's script host: dialogue as <see cref="DialogueGplHost"/>, and the
/// commands that act on creatures and the map carried out on a
/// <see cref="GameWorld"/>: orders (HUNT, GO, GOXY, FOLLOW, FLEE, FETCH,
/// WAIT, READORDERS), queries (GETXY, RANGE, NEXTTO, GET_LOS, SET_OTHER,
/// GET_STATUS, GETPARTY), TELEPORT, CLONE, items (GIVE, TAKE, DROP, SEARCH,
/// SHOP, CHANGE_MONEY), experience (AWARD), damage (DAMAGE, PHYS_DAMAGE)
/// and the REQUEST codes that are traced. Engine variables and object
/// fields live in the world. FIGHT and REQUEST 14 (the arena's watched bout)
/// leave the fight to the view (<see cref="PendingFight"/>); REQUEST 3, 7
/// and 8 are logged only.
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

    private GameWorld? _map;

    /// <summary>The current region's world, or null outside a region; a region new to the party goes in the journal.</summary>
    public GameWorld? Map
    {
        get => _map;
        set
        {
            _map = value;
            if (value is not null)
            {
                Journal.Visit(value.Region.Number);
            }
        }
    }

    /// <summary>Speech (style 115) is the creature talked to (the run's target), unless that is the party; narration is no one's.</summary>
    protected override string SpeakerOf(GplMachine machine, int style) =>
        style == 115 && Map?[machine.Target] is { Kind: not ObjectKind.Party, Name.Length: > 0 } who ? who.Name : "";

    /// <summary>What each party member carries and how they are, for the whole game (each region's world shares them).</summary>
    public IReadOnlyList<PartyMemberState> PartyMembers { get; } =
        [.. Enumerable.Range(0, GameWorld.PartySize).Select(_ => new PartyMemberState())];

    /// <summary>The character in a party slot, for AWARD (experience); null for a slot without one.</summary>
    public Func<int, Characters.Character?> PartyCharacter { get; set; } = _ => null;

    /// <summary>The party's money: <c>gname[10]</c> (<c>3781:0357</c>, int32), which CHANGE_MONEY adds to (<c>1A0A:28F6</c>).</summary>
    public int Money
    {
        get => Engine[10];
        set => Engine[10] = value;
    }

    /// <summary>
    /// <c>REQUEST(16, who, n)</c> (<c>6BB8:0FA9</c> → <c>361A:0000</c>): the
    /// music's intensity level n (1–5 in the scripts), a section within the
    /// piece playing; set only when sound is on and it changes.
    /// </summary>
    public int MusicLevel { get; private set; }

    public event Action<int>? MusicLevelChanged;

    /// <summary>FIGHT: combat was asked for at this cell (<c>gname[1]</c>, <c>[2]</c>); combat is not modelled yet.</summary>
    public (int X, int Y)? PendingFight { get; set; }

    /// <summary>
    /// REQUEST(14)'s bout, which the party watches (<see cref="GameWorld.StageArenaBout"/>):
    /// the view centres on this combatant, and the script, suspended meanwhile,
    /// resumes when the fight ends.
    /// </summary>
    public WorldObject? WatchedFight { get; set; }

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
            "REQUEST" => Request(machine, world, v),
            "FIGHT" => Fight(world),
            "GIVE" => Noted(world.Give(v[0], v[1], v[2], v[3]), n => Gained(world, n, v[1], v[2], v[3])),
            "TAKE" => Noted(world.Take(v[0], v[1], v[2], v[3]), n => Lost(world, n, v[1], v[2], v[3])),
            "DROP" => world.Drop(v[0], v[1], v[2]),
            "DAMAGE" => Do(() => world.Damage(v[0], v[1], percent: false)),
            "PHYS_DAMAGE" => Do(() => world.Damage(v[0], v[1], percent: true)),
            "AWARD" => Do(() => Award(world, v[0], v[1])),
            "CHANGE_MONEY" => Do(() =>
            {
                Money = unchecked(Money + v[0]);
                if (v[0] != 0)
                {
                    Journal.Event(world.Region.Number, v[0] > 0 ? $"The party gets {v[0]} money ({Money} in all)." : $"The party pays {-v[0]} money ({Money} left).");
                }
            }),
            "SHOP" => Shop(machine, world, v[0]),
            _ => null,
        };
        return command.Mnemonic is "HUNT" or "GO" or "FETCH" or "FOLLOW" or "GOXY" or "WAIT" or "FLEE" or "READORDERS"
            or "GETXY" or "RANGE" or "NEXTTO" or "GET_LOS" or "SET_OTHER" or "GET_STATUS" or "GETPARTY" or "TELEPORT"
            or "CLONE" or "REQUEST" or "FIGHT" or "GIVE" or "TAKE" or "DROP" or "DAMAGE" or "PHYS_DAMAGE" or "AWARD"
            or "CHANGE_MONEY" or "SHOP"
            ? Logged(command, result)
            : base.Execute(machine, command);
    }

    private static int Noted(int moved, Action<int> note)
    {
        if (moved > 0)
        {
            note(moved);
        }
        return moved;
    }

    private string ItemName(GameWorld world, int item) =>
        world.NewItem(Math.Abs(item)) is { } i && world.Items.NameOf(i.Record) is { Length: > 0 } n ? n : "something";

    private static bool IsParty(GameWorld world, int who) =>
        who == WholeParty || world.First(who) is { Kind: ObjectKind.Party };

    /// <summary>The journal notes items the party is given.</summary>
    private void Gained(GameWorld world, int count, int item, int to, int from)
    {
        if (IsParty(world, to) && !IsParty(world, from))
        {
            string whom = world.First(to)?.Name is { Length: > 0 } n ? n : "The party";
            Journal.Event(world.Region.Number, $"{whom} receives {(count > 1 ? $"{count} × " : "")}{ItemName(world, item)}.");
        }
    }

    /// <summary>The journal notes items taken from the party.</summary>
    private void Lost(GameWorld world, int count, int item, int from, int to)
    {
        if (IsParty(world, from) && !IsParty(world, to))
        {
            Journal.Event(world.Region.Number, $"The party gives up {(count > 1 ? $"{count} × " : "")}{ItemName(world, item)}.");
        }
    }

    public override int Search(GplMachine machine, int who, IReadOnlyList<GplSearchTest> tests) =>
        Map?.Search(who, tests) ?? 0;

    /// <summary>
    /// AWARD(who, xp) (<c>043D:10EC</c>, <c>7372:1467</c>): each party member
    /// (32766) or the member in slot who, if alive, gains xp split among their
    /// classes, to at most 2 000 000 000. (A character's
    /// <see cref="Characters.Character.Experience"/> is the total over its
    /// classes, so it grows by xp rounded down to a multiple of their number.)
    /// </summary>
    private void Award(GameWorld world, int who, int xp)
    {
        IEnumerable<int> slots = who == WholeParty ? world.Party.Where(p => !p.Hidden).Select(p => p.Index)
            : who is >= 0 and < GameWorld.PartySize ? [who] : [];
        if (xp > 0 && slots.Any())
        {
            Journal.Event(world.Region.Number, who == WholeParty ? $"The party gains {xp} experience each." : $"{world[who]?.Name ?? "A member"} gains {xp} experience.");
        }
        foreach (int slot in slots)
        {
            GiveExperience(world, slot, xp);
        }
    }

    /// <summary><c>7372:1467</c>: a living member gains <paramref name="xp"/> ÷ their classes in each, up to 2 000 000 000 in all.</summary>
    private void GiveExperience(GameWorld world, int slot, int xp)
    {
        if (world[slot] is { Vitals.Alive: true } && PartyCharacter(slot) is { } c)
        {
            int classes = Math.Max(1, c.Classes.Count);
            c.Experience = (int)Math.Min(2_000_000_000L, c.Experience + (long)(xp / classes) * classes);
        }
    }

    /// <summary>
    /// <paramref name="creature"/> fell (<see cref="GameWorld.Defeated"/>): each
    /// party member gets <see cref="GameWorld.KillExperience"/> as from AWARD
    /// (<c>612E:25F2</c> → <c>7372:1467</c>), at the kill rather than when the fight ends.
    /// </summary>
    public void AwardKill(GameWorld world, WorldObject creature)
    {
        int xp = world.KillExperience(creature);
        if (xp <= 0)
        {
            return;
        }
        Journal.Event(world.Region.Number, $"{creature.Name} is defeated: {xp} experience each.");
        foreach (WorldObject member in world.Party.ToList())
        {
            GiveExperience(world, member.Index, xp);
        }
    }

    /// <summary>SHOP(merchant): open the merchant's shop; the script waits until it closes.</summary>
    private int? Shop(GplMachine machine, GameWorld world, int merchant)
    {
        if (world.First(merchant) is { } m)
        {
            Wait(machine, new GplShopPrompt(m.Index), out _);
        }
        return null;
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
    private int? Request(GplMachine machine, GameWorld world, IReadOnlyList<int> v)
    {
        (int code, int who, int a, int b) = (v[0], v[1], v[2], v[3]);
        switch (code)
        {
            case 14:
                // 87B3:0FE9: the bout is fought inside the request; the script goes on once it is over.
                if (machine.TryTakeAnswer(out _))
                {
                    break;
                }
                if (world.StageArenaBout(who) is { } celgor)
                {
                    PendingFight = (celgor.Cell.X, celgor.Cell.Y);
                    WatchedFight = celgor;
                    machine.Suspend();
                }
                break;
            case 2: // 87B3: 6BB8:0D13(object, a, 1) for each match: open (0) or close (1) a door
                foreach (WorldObject door in world.Select(who).ToList())
                {
                    world.SetDoor(door, a, force: true);
                }
                break;
            case 5: world.ToggleState(who); break;
            case 10: world.SetCellFlags(a, b, CellFlags.Blocked, true); break;
            case 11: world.SetCellFlags(a, b, CellFlags.Blocked, false); break;
            case 12: world.SetCellFlags(a, b, CellFlags.BlocksSight, true); break;
            case 13: world.SetCellFlags(a, b, CellFlags.BlocksSight, false); break;
            case 17 when who < 0: world.Show(who, a, b); break;
            case 18: world.SetAllegiance(who, a); break;
            case 1: world.Rest(raiseDead: true); break; // 87B3:073D(1): "THE PARTY RESTS"
            case 4: world.Rest(raiseDead: false); break;
            case 9: world.Transform(who, a, b != 0); break;
            case 16 when a != MusicLevel:
                MusicLevel = a;
                MusicLevelChanged?.Invoke(a);
                break;
            case 20: return world.CountCreatures(a);
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

    /// <summary>Field 19 is the object's name (the arena's "Do not worry, <c>obj_special(0x25).19</c>": the active member's).</summary>
    public override string? ReadFieldText(GplMachine machine, GplObjectRef field) =>
        Map is { } world && Locate(world, field) is var (obj, f) && f == 19 ? obj.Name : null;

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

    public override void FocusActor(GplMachine machine, int actor) => Map?.GetXY(actor);

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
