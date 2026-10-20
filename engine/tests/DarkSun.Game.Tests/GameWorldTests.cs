using DarkSun.Data.Gff;
using DarkSun.Data.Images;
using DarkSun.Data.Regions;
using DarkSun.Game.Movement;
using DarkSun.Game.Rules;
using DarkSun.Game.Saves;
using DarkSun.Game.Scripting;
using DarkSun.Game.World;

namespace DarkSun.Game.Tests;

public class GameWorldTests
{
    private const int Width = 20, Height = 10;

    /// <summary>Resources by tag and number.</summary>
    private sealed class Resources : IResourceSource
    {
        public Dictionary<(string, int), byte[]> Data { get; } = [];

        public bool Contains(string tag, int number) => Data.ContainsKey((tag, number));

        public bool TryRead(string tag, int number, out ReadOnlyMemory<byte> data)
        {
            bool found = Data.TryGetValue((tag, number), out byte[]? bytes);
            data = bytes ?? [];
            return found;
        }
    }

    /// <summary>An <c>RDFF</c> combat record (type 2) with a status, an allegiance and a name.</summary>
    internal static byte[] CombatRecord(int ojff, byte status, byte allegiance, string name)
    {
        var data = new byte[DarkSun.Data.Regions.CombatRecord.HeaderSize + DarkSun.Data.Regions.CombatRecord.Size];
        (data[0], data[2], data[8]) = (1, 2, DarkSun.Data.Regions.CombatRecord.Size); // inline, combat record, 58 bytes
        int r = DarkSun.Data.Regions.CombatRecord.HeaderSize;
        data[r + 6] = (byte)-ojff;
        data[r + 7] = 0xFF;
        data[r + 0x1C] = status;
        data[r + 0x1D] = allegiance;
        System.Text.Encoding.Latin1.GetBytes(name).CopyTo(data, r + 40);
        return data;
    }

    private static readonly ImageFrame Frame = new(10, 20, new byte[200], new bool[200]);

    /// <summary>
    /// A 20×10 open region with a creature (<c>OJFF</c> 100, which walks) at
    /// cell (10, 5), a thing (<c>OJFF</c> 200) at (2, 2), and the party at (1, 8).
    /// </summary>
    internal static GameWorld World(byte[]? flags = null, byte? size = null, int width = Width, int height = Height,
        Action<Dictionary<(string, int), byte[]>>? more = null)
    {
        var resources = new Resources();
        more?.Invoke(resources.Data);
        resources.Data[("RDFF", 100)] = CombatRecord(100, 3, 1, "Templar");
        if (size is { } s)
        {
            // A character record (type 4, 0x47 bytes) after the combat record, with the size at +0x3D.
            var character = new byte[10 + 0x47];
            (character[0], character[2], character[6], character[8]) = (3, 4, 0x47, 0x47);
            character[10 + 0x3D] = s;
            resources.Data[("RDFF", 100)] = [.. resources.Data[("RDFF", 100)], .. character, 0xFF];
        }
        resources.Data[("SCMD", 7)] = ScmdBytes();
        // OJFF 200 is an item: a +1 "Torch" (NAME 1) of type 0.
        var item = new byte[10 + 21 + 1];
        (item[0], item[2], item[8]) = (1, 1, 21);
        item[10 + 18] = 1;
        item[10 + 20] = 1;
        item[^1] = 0xFF;
        resources.Data[("RDFF", 200)] = item;
        var names = new byte[50];
        System.Text.Encoding.Latin1.GetBytes("Torch").CopyTo(names, 25);
        resources.Data[("NAME", 1)] = names;
        RegionObject[] objects =
        [
            new(0, 100, 10 * 16 + 8, 6 * 16 - 1, 0, 0, false, [Frame, Frame, Frame], 7),
            new(1, 200, 2 * 16 + 8, 3 * 16 - 1, 0, 0, false, [Frame], 0),
        ];
        var world = new GameWorld(Region.Create(5, width, height, flags ?? new byte[width * height], objects), resources);
        var spec = new CharacterSpec(WalkerTests.Script(), [(10, 20), (12, 22), (14, 24)]);
        for (int i = 0; i < GameWorld.PartySize; i++)
        {
            world.AddPartyMember(i, spec, new Cell(1, 8));
        }
        return world;
    }

    /// <summary>The walking script of <see cref="WalkerTests"/>, as bytes.</summary>
    private static byte[] ScmdBytes()
    {
        var data = new byte[0x48 + 5 * 8];
        for (int slot = 0; slot < 32; slot++)
        {
            data[slot * 2] = (byte)(slot % 8 == 0 ? 0 : 1);
        }
        byte[][] records =
        [
            [0, 0, 2, 0, 0, 0, 0, 0],
            [1, 7, 0, 4, 0, 0, 0, 0],
            [2, 7, 0, 4, 0, 0, 0, 0],
            [1, 7, 0, 4, 0, 0, 0, 0],
            [2, 7, 2, 4, 0, 0, 0, 0],
        ];
        for (int i = 0; i < records.Length; i++)
        {
            records[i].CopyTo(data, 0x48 + i * 8);
        }
        return data;
    }

    private static void Run(GameWorld world, int ticks)
    {
        for (long t = 0; t < ticks; t++)
        {
            world.Update(t, new GplCreatureScripts());
        }
    }

    [Fact]
    public void RegionObjectsBecomeCreaturesAndThings()
    {
        GameWorld world = World();

        WorldObject templar = Assert.Single(world.Select(-100));
        Assert.Equal(ObjectKind.Creature, templar.Kind);
        Assert.Equal(GameWorld.PartySize, templar.Index);
        Assert.Equal("Templar", templar.Name);
        Assert.Equal(new Cell(10, 5), templar.Cell);
        Assert.NotNull(templar.Walker);

        WorldObject thing = Assert.Single(world.Select(-200));
        Assert.Equal(ObjectKind.Thing, thing.Kind);
        Assert.True(thing.Index >= GameWorld.MaxCreatures);
        Assert.Equal(new Cell(2, 2), thing.Cell);
        Assert.Equal("+1 Torch", thing.Name);
        Assert.NotNull(thing.Item);

        Assert.Same(templar, Assert.Single(world.Select(templar.Index)));
        Assert.Empty(world.Select(GameWorld.None));
        Assert.Equal(4, world.Party.Count());
    }

    [Fact]
    public void GoWalksToTheTargetAndClearsTheOrder()
    {
        GameWorld world = World();
        world.SetOrder(-100, OrderMode.Go, -200, 0);
        Assert.Equal((int)OrderMode.Go, world.ReadOrders(4));

        Run(world, 2000);

        WorldObject templar = world.First(-100)!;
        Assert.True(GplGeometry.Distance(templar.Cell.X, templar.Cell.Y, 2, 2) <= 1);
        Assert.Equal(0, world.ReadOrders(4));
    }

    [Fact]
    public void GoXYReachesTheCell()
    {
        GameWorld world = World();
        world.SetOrder(-100, OrderMode.GoXY, 15, 2);

        Run(world, 2000);

        Assert.Equal(new Cell(15, 2), world.First(-100)!.Cell);
        Assert.Equal(OrderMode.None, world.First(-100)!.Order.Mode);
    }

    [Fact]
    public void OrdersNeedAnExistingTargetAndOnlyReachCreatures()
    {
        GameWorld world = World();
        world.SetOrder(-100, OrderMode.Follow, -999, 0);
        Assert.Equal(0, world.ReadOrders(4));

        world.SetOrder(-200, OrderMode.Wait, 0, 0);
        Assert.Equal(0, world.ReadOrders(world.First(-200)!.Index)); // things (index ≥ 48) take no orders
    }

    [Fact]
    public void RangeFindsTheNearestOtherAndSetsOther()
    {
        GameWorld world = World();
        int range = world.Range(4, 0);

        Assert.Equal(GplGeometry.Distance(10, 5, world[0]!.Cell.X, world[0]!.Cell.Y), range);
        Assert.Equal(0, world.EngineVariable(0x28));
        Assert.Equal(GplGeometry.Far, world.Range(4, -999));
    }

    [Fact]
    public void SightIsBlockedByWalls()
    {
        var flags = new byte[Width * Height];
        GameWorld open = World(flags);
        Assert.True(open.InSight(4, -200, 20));
        Assert.False(open.InSight(4, -200, 3));

        for (int y = 0; y < Height; y++)
        {
            flags[y * Width + 6] = (byte)CellFlags.BlocksSight;
        }
        Assert.False(World(flags).InSight(4, -200, 20));
    }

    [Fact]
    public void TeleportToAnotherRegionHidesAndRequestSeventeenShows()
    {
        GameWorld world = World();
        world.Teleport(-100, 255, 0, 0);
        Assert.Empty(world.Select(-100));
        Assert.True(world[4]!.Hidden);

        world.Show(-100, 3, 7);
        Assert.Equal(new Cell(3, 7), world.First(-100)!.Cell);
    }

    [Fact]
    public void PartyTeleportToAnotherRegionIsPending()
    {
        GameWorld world = World();
        world.Teleport(DialogueGplHost.WholeParty, 42, 30, 9);
        Assert.Equal((42, 30, 9), world.PendingRegion);
    }

    [Fact]
    public void CloneMakesWalkingCopies()
    {
        GameWorld world = World();
        Assert.Equal(2, world.Clone(-100, 2, 15, 5));

        var templars = world.Select(-100).ToList();
        Assert.Equal(3, templars.Count);
        Assert.All(templars, t => Assert.NotNull(t.Walker));
        Assert.Equal(templars.Count, templars.Select(t => t.Cell).Distinct().Count());
    }

    [Fact]
    public void CellFlagRequestsChangeTheMap()
    {
        GameWorld world = World();
        world.SetCellFlags(3, 3, CellFlags.Blocked, true);
        Assert.True(world.Region.IsBlocked(3, 3));
        world.SetCellFlags(3, 3, CellFlags.Blocked, false);
        Assert.False(world.Region.IsBlocked(3, 3));
    }

    [Fact]
    public void StatusAndAllegianceComeFromTheCombatRecord()
    {
        GameWorld world = World();
        Assert.Equal(3, world.Status(-100));
        Assert.Equal(8, world.Status(-200));

        world.SetAllegiance(-100, 2);
        Assert.Equal(2, world.First(-100)!.ReadField(74)); // GPLX: field 74 is combat record +0x1D
    }

    [Fact]
    public void IdleCreaturesQueueTheirNoOrdersScript()
    {
        GameWorld world = World();
        var scripts = new GplCreatureScripts();
        scripts.SetNoOrders(-100, new GplEntry(12, 0x40));

        world.Update(0, scripts);

        var (entry, actor, _) = Assert.Single(world.PendingScripts);
        Assert.Equal(12, entry.File);
        Assert.Equal(4, actor);
    }

    [Fact]
    public void ThingsGivenANoOrdersScriptRunItToo()
    {
        // 1A0A:0554 moves any object given one into the creature slots: region 42's gate starts the arena this way.
        GameWorld world = World();
        var scripts = new GplCreatureScripts();
        scripts.SetNoOrders(-200, new GplEntry(2, 1));

        world.Update(0, scripts);

        var (entry, actor, _) = Assert.Single(world.PendingScripts);
        Assert.Equal((2, 1), (entry.File, (int)entry.Address));
        Assert.Equal(world.First(-200)!.Index, actor);
    }

    /// <summary>A 40×40 region with what REQUEST 14 makes: the mage (296), the rampager (171) and sligs (269), all hostile in their records.</summary>
    internal static GameWorld Arena() => World(width: 40, height: 40, more: data =>
    {
        foreach (var (ojff, name) in new[] { (296, "Defiler"), (171, "Rampager"), (269, "Slig") })
        {
            data[("RDFF", ojff)] = CombatRecord(ojff, 3, 2, name);
            data[("OJFF", ojff)] = [0, 0, 8, 0, 15, 0, 0, 0, 0, 0, 0, 0, 7, 0, 0, 0]; // anchor (8, 15), BMP 7
        }
        data[("BMP ", 7)] = CinematicPlayerTests.Screen(3);
    });

    [Fact]
    public void TheArenaBoutPutsTheMageOnThePartysSideAndThePartyOut()
    {
        GameWorld world = Arena();
        world.ActiveMember = 0; // the party at (1, 8)

        WorldObject celgor = world.StageArenaBout()!;

        Assert.Equal("Defiler", celgor.Name);
        Assert.Equal(1, celgor.Allegiance);
        Assert.Equal(1, celgor.ReadField(0x4A));
        Assert.True(GplGeometry.Distance(celgor.Cell.X, celgor.Cell.Y, 0, 26) <= 6); // (1 − 3, 8 + 18), off the map's edge
        WorldObject rampager = world.First(-171)!;
        Assert.Equal((2, 2), (rampager.Allegiance, rampager.ReadField(0x4A)));
        Assert.Equal(2, world.Select(-269).Count());
        Assert.All(world.Party, p => Assert.Equal(GameWorld.WatchingAllegiance, p.Allegiance));
        world.ActiveMember = 30;
        Assert.Null(world.StageArenaBout());
    }

    [Fact]
    public void Request14WaitsForTheBoutThenGoesOn()
    {
        GameWorld world = Arena();
        var host = new WorldGplHost { Map = world };
        int gate = world.First(-200)!.Index;
        // REQUEST(14, gate, 0, 0); gnum[3] = 1: the script goes on only once the bout is over (87B3:0FE9 fights it inside).
        byte[] code = new Asm().Op(0x22).Num(14).Num(gate).Num(0).Num(0).Set(GplVarType.GlobalNum, 3, 1).Op(0x31).ToArray();
        var machine = new GplMachine(new SingleScript(code), host, [], new BorlandRandom(1));
        var gnum3 = new GplVariable(GplVarType.GlobalNum, 3);

        GplRunResult run = machine.Run(new GplEntry(1, 0));

        Assert.True(run.Suspended);
        Assert.Equal("Defiler", host.WatchedFight!.Name);
        Assert.Equal((host.WatchedFight.Cell.X, host.WatchedFight.Cell.Y), host.PendingFight);
        Assert.Equal(0, machine.Memory.Read(gnum3));
        Assert.Null(machine.Resume().Error);
        Assert.Equal(1, machine.Memory.Read(gnum3));
        Assert.Single(world.Select(-296)); // not staged twice
    }

    [Fact]
    public void AnObjectsNameFieldPrintsAsItsName()
    {
        GameWorld world = World();
        var host = new WorldGplHost { Map = world };
        var machine = new GplMachine(new SingleScript([GplOpcodes.EndFunction, 0x31]), host, [], new BorlandRandom(1));
        world[0]!.Name = "Barek";
        Assert.Equal("Barek", host.ReadFieldText(machine, new GplObjectRef(0, [19])));
        Assert.Null(host.ReadFieldText(machine, new GplObjectRef(0, [75])));
    }

    [Fact]
    public void CloneMakesAnObjectThatIsntOnTheMapFromItsRecord()
    {
        GameWorld world = Arena();
        Assert.Equal(2, world.Clone(-269, 2, 20, 20)); // 7372:0A44 loads the RDFF
        Assert.Equal(2, world.Select(-269).Count());
        Assert.Equal(0, world.Clone(-999, 1, 20, 20));
    }

    [Fact]
    public void HostRunsOrdersAndQueriesThroughTheWorld()
    {
        GameWorld world = World();
        var host = new WorldGplHost { Map = world };
        // GO(-200, -100): the templar goes to the thing; GETXY(-100).
        byte[] code = new Asm().Op(0x3A).Num(-200).Num(-100).Op(0x09).Num(-100).Op(0x31).ToArray();
        var machine = new GplMachine(new SingleScript(code), host, [], new BorlandRandom(1));

        GplRunResult run = machine.Run(new GplEntry(1, 0));

        Assert.Null(run.Error);
        Assert.Equal((int)OrderMode.Go, world.ReadOrders(4));
        Assert.Equal(10, host.ReadEngineVariable(machine, 0x21));
        Assert.Equal(5, host.ReadEngineVariable(machine, 0x22));
    }

    [Fact]
    public void TriggersStartWithTheActorsCellInGetxysVariables()
    {
        GameWorld world = World();
        var host = new WorldGplHost { Map = world };
        var machine = new GplMachine(new SingleScript([GplOpcodes.EndFunction, 0x31]), host, [], new BorlandRandom(1));
        WorldObject torch = world.First(-200)!, actor = world[0]!;
        machine.Triggers.AddObject(GplTriggerList.Use, 1, 1, torch.Id, global: false);

        Assert.NotNull(machine.OnObject(GplTriggerList.Use, actor.Index, torch.Index));

        Assert.Equal((actor.Cell.X, actor.Cell.Y, 0), (host.ReadEngineVariable(machine, 0x21), host.ReadEngineVariable(machine, 0x22), host.ReadEngineVariable(machine, 0x23)));
    }

    [Fact]
    public void ARegionComesBackAsItWasLeft()
    {
        GameWorld before = World();
        WorldObject templar = before.First(-100)!, torch = before.First(-200)!;
        Assert.Equal(1, before.Clone(-200, 1, 6, 6));     // a cloned torch
        before.Teleport(templar.Index, 5, 15, 2);           // moved within the region
        before.Teleport(torch.Index, 255, 0, 0);           // the original taken off the map
        before.SetAllegiance(-100, 9);
        templar.WriteField(40, 7);
        before.SetCellFlags(3, 3, Data.Regions.CellFlags.Blocked, true);
        RegionState state = before.Snapshot();

        GameWorld after = World();
        var changed = after.Restore(state);

        WorldObject t = after.First(-100)!;
        Assert.Equal((new Cell(15, 2), 9, 7), (t.Cell, t.Allegiance, t.ReadField(40)));
        Assert.True(after[torch.Index]!.Hidden);
        WorldObject copy = Assert.Single(after.Objects, o => o.IsClone);
        Assert.Equal((new Cell(6, 6), -200, false), (copy.Cell, copy.Id, copy.Hidden));
        Assert.True(after.Region.IsBlocked(3, 3));
        Assert.Contains(copy, changed);
        Assert.Equal(4, after.Party.Count());
    }

    [Fact]
    public void MemoryRecallsOnlyRegionsVisited()
    {
        var memory = new RegionMemory();
        GameWorld world = World();
        Assert.Null(memory.Recall(world));

        world.SetAllegiance(-100, 3);
        memory.Remember(world);

        GameWorld again = World();
        Assert.NotNull(memory.Recall(again));
        Assert.Equal(3, again.First(-100)!.Allegiance);
        memory.Forget();
        Assert.False(memory.Knows(again.Region.Number));
    }

    [Fact]
    public void ASavedGameComesBackFromDisk()
    {
        GameWorld world = World();
        var host = new WorldGplHost { Map = world };
        var machine = new GplMachine(new SingleScript([GplOpcodes.EndFunction]), host, [], new BorlandRandom(7));
        machine.Memory.Area(GplArea.GlobalFlags)[3] = 0x21;
        machine.Memory.Area(GplArea.GlobalStrings)[0] = (byte)'Z';
        machine.Triggers.AddObject(GplTriggerList.TalkTo, 0x10, 12, -100, global: true);
        machine.Creatures.SetSight(-100, 1, new GplEntry(12, 0x40), 5);
        machine.Creatures.SetNoOrders(-100, new GplEntry(12, 0x50));
        machine.Random.Next();
        world.EngineVariables[5] = 2;
        world.SetAllegiance(-100, 6);
        var memory = new RegionMemory();
        memory.Load([World().Snapshot() with { Region = 9 }]);
        string path = Path.Combine(Path.GetTempPath(), $"darksun-save-{Guid.NewGuid():N}.json");
        try
        {
            SaveGame.Capture("Test", machine, world.EngineVariables, world, memory,
                [new PartyMemberSave(5, 1, 8, DarkSun.Data.Animation.Direction.East)], 0, ["MyMod"]).Write(path);

            SaveGame save = SaveGame.Read(path);
            GameWorld fresh = World();
            var again = new GplMachine(new SingleScript([GplOpcodes.EndFunction]), new WorldGplHost { Map = fresh }, [], new BorlandRandom(1));
            var recalled = new RegionMemory();
            save.Restore(again, fresh.EngineVariables, recalled);

            Assert.Equal(("Test", 5), (save.Name, save.Region));
            Assert.Equal(["MyMod"], save.Mods);
            Assert.Equal(DarkSun.Data.Animation.Direction.East, Assert.Single(save.Party).Facing);
            Assert.Equal(0x21, again.Memory.Area(GplArea.GlobalFlags)[3]);
            Assert.Equal((byte)'Z', again.Memory.Area(GplArea.GlobalStrings)[0]);
            Assert.Equal(new GplEntry(12, 0x10), again.Triggers.MatchObject(GplTriggerList.TalkTo, -100));
            Assert.Equal((1, new GplEntry(12, 0x40), 5), again.Creatures.Sight[-100]);
            Assert.Equal(new GplEntry(12, 0x50), again.Creatures.NoOrders[-100]);
            Assert.Equal(machine.Random.State, again.Random.State);
            Assert.Equal(2, fresh.EngineVariables[5]);
            Assert.True(recalled.Knows(9) && recalled.Knows(5));
            Assert.NotNull(recalled.Recall(fresh));
            Assert.Equal(6, fresh.First(-100)!.Allegiance);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnUnreadableSaveSaysWhy()
    {
        string path = Path.Combine(Path.GetTempPath(), $"darksun-save-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ \"Version\": ");
        try
        {
            Assert.Throws<InvalidDataException>(() => SaveGame.Read(path));
            var future = new SaveGame { Version = 99 };
            var e = Assert.Throws<InvalidDataException>(() => future.Restore(
                new GplMachine(new SingleScript([]), new LoggingGplHost(), [], new BorlandRandom(1)), new int[15], new RegionMemory()));
            Assert.Contains("version 99", e.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class SingleScript(byte[] code) : IGplScripts
    {
        public byte[]? Load(int file, GplFileKind kind) => file == 1 ? code : null;
    }

    [Fact]
    public void CreatureFootprintsComeFromTheirCharacterRecord()
    {
        WorldObject small = World().First(-100)!;
        Assert.Equal(Footprint.OneCell, small.Walker!.Footprint);
        Assert.Null(small.Character);

        GameWorld world = World(size: 9);
        WorldObject large = world.First(-100)!;
        Assert.Equal(9, large.Character!.Size);
        Assert.Equal(new Footprint(1, 1), large.Walker!.Footprint);
        Assert.Equal(6, world.Occupancy.Cells.Count(c => c.Value == large.Index)); // 3 × 2 cells held
    }

    [Fact]
    public void VerbsComeFromTheObjectAndItsTriggers()
    {
        GameWorld world = World();
        var triggers = new GplTriggers();
        WorldObject templar = world.First(-100)!, torch = world.First(-200)!;

        Assert.Equal([Verb.Attack, Verb.Look], ObjectVerbs.For(templar, world.Items, triggers));
        Assert.Equal([Verb.Get, Verb.Look], ObjectVerbs.For(torch, world.Items, triggers));

        triggers.AddObject(GplTriggerList.TalkTo, 0x10, 12, -100, global: false);
        triggers.AddObject(GplTriggerList.Use, 0x20, 12, -200, global: false);
        triggers.AddUseWith(-200, -100, 0x30, 12, global: false);

        Assert.Equal([Verb.Talk, Verb.UseWith, Verb.Attack, Verb.Look], ObjectVerbs.For(templar, world.Items, triggers));
        Assert.Equal([Verb.Get, Verb.Use, Verb.UseWith, Verb.Look], ObjectVerbs.For(torch, world.Items, triggers));
        Assert.Equal([-100], ObjectVerbs.UseWithPartners(torch, triggers));
    }
}
