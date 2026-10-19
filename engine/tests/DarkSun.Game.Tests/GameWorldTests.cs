using DarkSun.Data.Gff;
using DarkSun.Data.Images;
using DarkSun.Data.Regions;
using DarkSun.Game.Movement;
using DarkSun.Game.Rules;
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
    private static byte[] CombatRecord(int ojff, byte status, byte allegiance, string name)
    {
        var data = new byte[DarkSun.Data.Regions.CombatRecord.HeaderSize + DarkSun.Data.Regions.CombatRecord.Size];
        data[2] = 2;
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
    private static GameWorld World(byte[]? flags = null)
    {
        var resources = new Resources();
        resources.Data[("RDFF", 100)] = CombatRecord(100, 3, 1, "Templar");
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
        var world = new GameWorld(Region.Create(5, Width, Height, flags ?? new byte[Width * Height], objects), resources);
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
        Assert.Equal(2, world.First(-100)!.ReadField(75));
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

    private sealed class SingleScript(byte[] code) : IGplScripts
    {
        public byte[]? Load(int file, GplFileKind kind) => file == 1 ? code : null;
    }
}
