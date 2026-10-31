using DarkSun.Data.Regions;
using DarkSun.Game.Characters;
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
/// leave the fight to the view (<see cref="PendingFight"/>), as REQUEST 6
/// (cinematics), 15 (the move pointer) and 19 (game over) leave theirs.
/// </summary>
public sealed partial class WorldGplHost : DialogueGplHost
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
    /// piece playing; asked for only when it changes (<c>DS:0D9A</c>).
    /// </summary>
    public int MusicLevel { get; private set; }

    /// <summary>A music level asked for: by a script, a fight's start or its end.</summary>
    public event Action<int>? MusicLevelChanged;

    /// <summary>
    /// A fight started with a creature (<c>6BB8:0EFA</c>, from <c>612E:1359</c>):
    /// the level the region gives it (<see cref="Data.Regions.RegionInfo.FightLevel"/>).
    /// <see cref="MusicLevel"/> stays as it was, so a script can ask for it again.
    /// </summary>
    public void FightMusic(int level) => MusicLevelChanged?.Invoke(level);

    /// <summary>A fight is over (<c>612E:04A7</c> → <c>6BB8:0FD4</c>): level 1, if it isn't already.</summary>
    public void FightMusicOver()
    {
        if (MusicLevel != 1)
        {
            MusicLevel = 1;
            MusicLevelChanged?.Invoke(1);
        }
    }

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
            "GETPARTY" => Other(world, NextPartyMember(world, v[0])),
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
            "STAT_ROLL" => StatRoll(machine, world, v[0], v[1], v[2]),
            "SETTHING" => SetThing(world, v[0], v[1]),
            "PASS_TIME" => Do(() => world.PassTime(v[0] * 60)),
            _ => null,
        };
        return command.Mnemonic is "HUNT" or "GO" or "FETCH" or "FOLLOW" or "GOXY" or "WAIT" or "FLEE" or "READORDERS"
            or "GETXY" or "RANGE" or "NEXTTO" or "GET_LOS" or "SET_OTHER" or "GET_STATUS" or "GETPARTY" or "TELEPORT"
            or "CLONE" or "REQUEST" or "FIGHT" or "GIVE" or "TAKE" or "DROP" or "DAMAGE" or "PHYS_DAMAGE" or "AWARD"
            or "CHANGE_MONEY" or "SHOP" or "STAT_ROLL" or "SETTHING" or "PASS_TIME"
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
        CheckLevels(world); // 8E77:0A8F after an award
    }

    /// <summary>A party member gained a level: its slot and what the game says (<see cref="CharacterCreation.LevelMessage"/>).</summary>
    public event Action<int, string>? LevelGained;

    private readonly Random _levelRolls = new();

    /// <summary>
    /// The level check (<c>8E77:0A8F</c> → <c>8E77:08A6</c>): after an award and at the end of a fight,
    /// each party member standing (status below 3) gains the levels its experience has reached
    /// (<see cref="CharacterCreation.CheckLevels"/>); its hit points rise with its maximum.
    /// </summary>
    public void CheckLevels(GameWorld world)
    {
        var creation = new CharacterCreation(Rules, CharacterNames.Empty, _levelRolls);
        foreach (WorldObject member in world.Party.ToList())
        {
            if (member.Vitals.Status >= Vitals.Down || PartyCharacter(member.Index) is not { } c)
            {
                continue;
            }
            int before = c.Hp;
            IReadOnlyList<LevelGain> gains = creation.CheckLevels(c);
            if (c.Hp != before)
            {
                member.Vitals.MaxHp += c.Hp - before;
                member.Vitals.Hp = Math.Max(1, member.Vitals.Hp + c.Hp - before);
            }
            foreach (LevelGain gain in gains)
            {
                string name = gain.Class == CharacterClass.Psionicist ? "Psionic" : Rules.Class(gain.Class).Name;
                string message = CharacterCreation.LevelMessage(c.Name.Length > 0 ? c.Name : member.Name, gain, name);
                Journal.Event(world.Region.Number, message + ".");
                LevelGained?.Invoke(member.Index, message);
            }
        }
    }

    /// <summary>
    /// <c>7372:1467</c>: a living member gains <paramref name="xp"/> ÷ their classes in each, up to 2 000 000 000 in all.
    /// The levels it brings come with the next level check (<see cref="CheckLevels"/>).
    /// </summary>
    private void GiveExperience(GameWorld world, int slot, int xp)
    {
        if (world[slot] is { Vitals.Alive: true } && PartyCharacter(slot) is { } c)
        {
            int classes = Math.Max(1, c.Classes.Count);
            Creation.NoteLevels(c);
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

    /// <summary>GETPARTY (<c>043D:1468</c>) also makes the member it returns "other" (<c>gname[8]</c>, <c>3781:0363</c>), which the scripts then read: <c>GETXY(gname[8])</c>.</summary>
    private static int Other(GameWorld world, int index)
    {
        world.SetEngineVariable(0x28, index);
        return index;
    }

    private static int NextPartyMember(GameWorld world, int after)
    {
        var party = world.Party.Where(p => !p.Hidden).Select(p => p.Index).ToList();
        int at = after == EndOfParty ? -1 : party.IndexOf(after);
        return at + 1 < party.Count ? party[at + 1] : EndOfParty;
    }

    private static int? Teleport(GameWorld world, IReadOnlyList<int> v)
    {
        world.Teleport(v[0], v[1], v[2], v[3], v[4]);
        return v[0] == WholeParty ? 1 : null;
    }

    /// <summary>
    /// REQUEST(code, who, a, b): <c>87B3:0000</c>; see <c>dsun-exe-reference.md</c> §4. The code is
    /// looked up by name in <see cref="GplRequests"/>; the numbers are written only there.
    /// </summary>
    private int? Request(GplMachine machine, GameWorld world, IReadOnlyList<int> v)
    {
        (int code, int who, int a, int b) = (v[0], v[1], v[2], v[3]);
        switch (GplRequests.Name(code))
        {
            case "ARENA_BOUT":
                // 87B3:0FE9: the bout is fought inside the request; the script goes on once it is over.
                if (machine.TryTakeAnswer(out _))
                {
                    break;
                }
                if (world.StageArenaBout() is { } celgor)
                {
                    PendingFight = (celgor.Cell.X, celgor.Cell.Y);
                    WatchedFight = celgor;
                    machine.Suspend();
                }
                break;
            case "SET_DOOR": // 87B3: 6BB8:0D13(object, a, 1) for each match: open (0) or close (1) a door
                foreach (WorldObject door in world.Select(who).ToList())
                {
                    world.SetDoor(door, a, force: true);
                }
                break;
            case "SKILL_CHECK": return SkillCheck(machine, world, who, a, b);
            case "TOGGLE_STATE": world.ToggleState(who); break;
            case "TRAP" when who < 0: return SetOffTrap(machine, world, who, a, b);
            case "BLOCK_CELL": world.SetCellFlags(a, b, CellFlags.Blocked, true); break;
            case "UNBLOCK_CELL": world.SetCellFlags(a, b, CellFlags.Blocked, false); break;
            case "BLOCK_SIGHT": world.SetCellFlags(a, b, CellFlags.BlocksSight, true); break;
            case "UNBLOCK_SIGHT": world.SetCellFlags(a, b, CellFlags.BlocksSight, false); break;
            case "PLACE_OBJECT" when who < 0: world.Show(who, a, b); break;
            case "SET_ALLEGIANCE": world.SetAllegiance(who, a); break;
            case "REST_RAISE": world.Rest(raiseDead: true, a); break; // 87B3:073D(1, a): "THE PARTY RESTS"
            case "REST": world.Rest(raiseDead: false, a); break;
            case "TRANSFORM": world.Transform(who, a, b != 0); break;
            case "MUSIC_LEVEL" when a != MusicLevel:
                MusicLevel = a;
                MusicLevelChanged?.Invoke(a);
                break;
            case "COUNT_CREATURES": return world.CountCreatures(a);
            case "RANDOM_MONSTER": return RandomMonster(machine, world, a, b);
            case "CINEMATIC":
                // 87B3:0AC7 → 60B9:042E: cinematic a, played inside the request; the script goes on after it.
                if (machine.TryTakeAnswer(out _))
                {
                    break;
                }
                if (CinematicRequested is { } play && play(a))
                {
                    machine.Suspend();
                }
                break;
            case "MOVE_CURSOR": MoveCursorRequested?.Invoke(); break; // 1587:2927(1): DS:11B8 = 1, the move pointer
            case "GAME_OVER": GameOverRequested = true; break;        // DS:116A = 1: the main loop ends (1512), back to the start
            // Not traced: TRAP and PLACE_OBJECT for an object that isn't a name (no shipped script does either).
            default: break;
        }
        return null;
    }

    /// <summary>
    /// <c>REQUEST(6, who, n)</c>: play cinematic n, the ending's 100 or the pictures 200 and 300.
    /// The view returns true if it plays it; the script then waits until it is over and resumes
    /// (<see cref="GplMachine.Resume"/>). False: nothing to play (no such files, or a headless check).
    /// </summary>
    public Func<int, bool>? CinematicRequested { get; set; }

    /// <summary><c>REQUEST(15)</c>: back to the move pointer from look or attack (32 calls in the shipped scripts).</summary>
    public Action? MoveCursorRequested { get; set; }

    /// <summary>
    /// <c>REQUEST(19)</c> (GPL 20, a riddle answered wrong): the game is over, as when the whole
    /// party falls. The view ends it once the script is done; DSUN.EXE goes straight back to the start.
    /// </summary>
    public bool GameOverRequested { get; set; }

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
        Map is { } world && Record(world, field, out int f) is { } record ? record.Read(f) : base.ReadField(machine, field);

    /// <summary>A text field: field 19 a creature's name (the arena's "Do not worry, <c>obj_special(0x25).19</c>": the active member's), 94 an item's (<c>.2.94</c>).</summary>
    public override string? ReadFieldText(GplMachine machine, GplObjectRef field) =>
        Map is { } world && Record(world, field, out int f) is { } record ? record.Text(f) : null;

    public override void WriteField(GplMachine machine, GplObjectRef field, int value)
    {
        if (Map is { } world && Record(world, field, out int f) is { } record)
        {
            record.Write(f, value);
        }
        else
        {
            base.WriteField(machine, field, value);
        }
    }

    public override int TriggerId(GplMachine machine, int obj) => Map?[obj] is { } o ? o.Id : obj;

    /// <summary>The objects <paramref name="value"/> names, and a name itself too, for those that show up later (a hidden one revealed).</summary>
    public override IReadOnlyList<int> ScriptObjects(GplMachine machine, int value) =>
        Map is not { } world ? [value]
        : value < 0 ? [.. world.Select(value).Select(o => o.Index), value]
        : world[value] is not null ? [value] : [];

    public override void FocusActor(GplMachine machine, int actor) => Map?.GetXY(actor);

    public override string SpeakerName(GplMachine machine) =>
        Map?[Map.ActiveMember] is { Name: { Length: > 0 } name } ? name : base.SpeakerName(machine);

}
