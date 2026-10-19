using DarkSun.Data.Animation;
using DarkSun.Game.Movement;

namespace DarkSun.Game.Tests;

public class PathfinderTests
{
    /// <summary>A map from rows of text: '#' blocked; off the map is blocked.</summary>
    private static Func<int, int, bool> Map(params string[] rows) =>
        (x, y) => y < 0 || y >= rows.Length || x < 0 || x >= rows[y].Length || rows[y][x] == '#';

    [Fact]
    public void WalksAroundWalls()
    {
        var blocked = Map(
            "....",
            ".##.",
            "....");

        List<Cell> path = Pathfinder.FindPath(blocked, new(0, 1), new(3, 1));

        Assert.Equal(new Cell(3, 1), path[^1]);
        Assert.DoesNotContain(path, c => blocked(c.X, c.Y));
        // Up, along and down: no diagonal past the blocked corners.
        Assert.Equal([new Cell(0, 0), new Cell(1, 0), new Cell(2, 0), new Cell(3, 0), new Cell(3, 1)], path);
    }

    [Fact]
    public void DoesNotCutCorners()
    {
        var blocked = Map(
            ".#",
            "..");

        List<Cell> path = Pathfinder.FindPath(blocked, new(0, 0), new(1, 1));

        Assert.Equal([new Cell(0, 1), new Cell(1, 1)], path);
    }

    [Fact]
    public void UnreachableGoalLeadsToNearestCell()
    {
        var blocked = Map(
            "..#.",
            "..#.");

        List<Cell> path = Pathfinder.FindPath(blocked, new(0, 0), new(3, 0));

        Assert.Equal(new Cell(1, 0), path[^1]);
    }
}

public class WalkerTests
{
    /// <summary>
    /// Every stand slot points at record 0 (one frame, stop); every walk slot
    /// at a 4-record walk east of 4 pixels per 7 ticks, like SCMD 2.
    /// </summary>
    internal static Scmd Script()
    {
        var data = new byte[Scmd.HeaderSize + 5 * 8];
        for (int slot = 0; slot < Scmd.SlotCount; slot++)
        {
            data[slot * 2] = (byte)(slot % 8 == 0 ? 0 : 1);
        }
        byte[][] records =
        [
            [0, 0, (byte)ScmdFlags.Stop, 0, 0, 0, 0, 0],
            [1, 7, 0, 4, 0, 0, 0, 0],
            [2, 7, 0, 4, 0, 0, 0, 0],
            [1, 7, 0, 4, 0, 0, 0, 0],
            [2, 7, (byte)ScmdFlags.Stop, 4, 0, 0, 0, 0],
        ];
        for (int i = 0; i < records.Length; i++)
        {
            records[i].CopyTo(data, Scmd.HeaderSize + i * 8);
        }
        return Scmd.Parse(data);
    }

    private static readonly (int, int)[] Frames = [(10, 20), (12, 22), (14, 24)];

    [Fact]
    public void WalksOneCellPerCycle()
    {
        var walker = new Walker(1, Script(), Frames, new Cell(0, 0), Footprint.OneCell, new Occupancy());
        static bool Open(int x, int y) => false;
        walker.Update(0, Open);
        Assert.Equal((8, 16), walker.Feet);

        walker.Follow([new Cell(1, 0)]);
        walker.Update(1, Open);
        Assert.Equal(Direction.East, walker.Facing);
        Assert.Equal(12, walker.Feet.X);
        Assert.Equal((12 - 6, 16 - 22), walker.DrawPosition); // hot spot: bottom centre of frame 1

        for (long t = 8; t <= 22; t += 7)
        {
            walker.Update(t, Open);
        }
        Assert.Equal(24, walker.Feet.X);
        Assert.True(walker.IsWalking); // last frame still showing

        walker.Update(29, Open);
        Assert.False(walker.IsWalking);
        Assert.Equal(new Cell(1, 0), walker.Cell);
        Assert.Equal(24, walker.Feet.X);
    }

    /// <summary>Stand: two frames looping forever. Walk: east 4 px per 7 ticks, looping.</summary>
    private static Scmd LoopingScript()
    {
        var data = new byte[Scmd.HeaderSize + 6 * 8];
        for (int slot = 0; slot < Scmd.SlotCount; slot++)
        {
            data[slot * 2] = (byte)(slot % 8 == 0 ? 0 : 3);
        }
        byte[][] records =
        [
            [0, 10, 0, 0, 0, 0, 0, 0],
            [1, 10, 0, 0, 0, 0, 0, 0],
            [0, 0, (byte)ScmdFlags.Jump, 0, 0, 0, 0, 0],
            [1, 7, 0, 4, 0, 0, 0, 0],
            [2, 7, 0, 4, 0, 0, 0, 0],
            [3, 0, (byte)ScmdFlags.Jump, 0, 0, 0, 0, 0],
        ];
        for (int i = 0; i < records.Length; i++)
        {
            records[i].CopyTo(data, Scmd.HeaderSize + i * 8);
        }
        return Scmd.Parse(data);
    }

    [Fact]
    public void WalksWithLoopingIdleAndWalkAnimations()
    {
        var walker = new Walker(1, LoopingScript(), Frames, new Cell(0, 0), Footprint.OneCell, new Occupancy());
        walker.Update(0, (x, y) => false);
        walker.Follow([new Cell(1, 0), new Cell(2, 0)]);

        for (long t = 1; t < 200; t++)
        {
            walker.Update(t, (x, y) => false);
        }

        Assert.False(walker.IsWalking);
        Assert.Equal(new Cell(2, 0), walker.Cell);
        Assert.Equal(2 * 16 + 8, walker.Feet.X);
    }

    [Fact]
    public void StopsBeforeABlockedCell()
    {
        var walker = new Walker(1, Script(), Frames, new Cell(0, 0), Footprint.OneCell, new Occupancy());
        walker.Follow([new Cell(1, 0)]);

        walker.Update(0, (x, y) => x == 1);

        Assert.False(walker.IsWalking);
        Assert.Equal(new Cell(0, 0), walker.Cell);
    }

    /// <summary>Run walkers for <paramref name="ticks"/>, checking after every tick that no cell has two owners.</summary>
    private static void Run(Occupancy occupancy, Func<int, int, bool> blocked, int ticks, params Walker[] walkers)
    {
        for (long t = 0; t < ticks; t++)
        {
            foreach (Walker w in walkers)
            {
                w.Update(t, blocked);
            }
            var claimed = walkers.SelectMany(w => w.Footprint.Cells(w.Cell)).ToList();
            Assert.Equal(claimed.Count, claimed.Distinct().Count());
        }
    }

    [Fact]
    public void ClaimsAndReleasesCellsAsItWalks()
    {
        var occupancy = new Occupancy();
        var walker = new Walker(1, Script(), Frames, new Cell(0, 0), Footprint.OneCell, occupancy);
        walker.Follow([new Cell(1, 0)]);

        walker.Update(0, (x, y) => false);
        Assert.Equal(1, occupancy.OwnerOf(new Cell(0, 0)));
        Assert.Equal(1, occupancy.OwnerOf(new Cell(1, 0))); // reserved while stepping in

        Run(occupancy, (x, y) => false, 40, walker);
        Assert.Null(occupancy.OwnerOf(new Cell(0, 0)));
        Assert.Equal(1, occupancy.OwnerOf(new Cell(1, 0)));
    }

    [Fact]
    public void WalksAroundAnotherCreature()
    {
        var occupancy = new Occupancy();
        static bool Open(int x, int y) => x < 0 || y < 0 || x > 5 || y > 3;
        var mover = new Walker(1, Script(), Frames, new Cell(0, 1), Footprint.OneCell, occupancy);
        var standing = new Walker(2, Script(), Frames, new Cell(2, 1), Footprint.OneCell, occupancy);

        Assert.True(mover.WalkTo(new Cell(4, 1), Open));
        Run(occupancy, Open, 2000, mover, standing);

        Assert.Equal(new Cell(4, 1), mover.Cell);
        Assert.Equal(new Cell(2, 1), standing.Cell);
    }

    [Fact]
    public void GivesUpWhenAnotherCreatureBlocksTheOnlyWay()
    {
        var occupancy = new Occupancy();
        static bool Corridor(int x, int y) => y != 0 || x < 0 || x > 4;
        var mover = new Walker(1, Script(), Frames, new Cell(0, 0), Footprint.OneCell, occupancy);
        _ = new Walker(2, Script(), Frames, new Cell(2, 0), Footprint.OneCell, occupancy);

        mover.WalkTo(new Cell(4, 0), Corridor);
        Run(occupancy, Corridor, 2000, mover);

        Assert.False(mover.IsWalking);
        Assert.Equal(new Cell(1, 0), mover.Cell);
    }

    [Fact]
    public void BigCreaturesNeedRoomForTheirFootprint()
    {
        var occupancy = new Occupancy();
        // A gap one cell wide at x = 3 in a wall along y = 2.
        static bool Gap(int x, int y) => x < 0 || y < 0 || x > 6 || y > 5 || (y == 2 && x != 3);
        var big = new Walker(1, Script(), Frames, new Cell(3, 5), new Footprint(1, 1), occupancy);

        Assert.False(big.CanStand(new Cell(3, 2), Gap));
        Assert.True(big.CanStand(new Cell(3, 4), Gap));
        big.WalkTo(new Cell(3, 0), Gap);
        Run(occupancy, Gap, 2000, big);
        Assert.Equal(4, big.Cell.Y); // as close as it fits
    }

    [Fact]
    public void RefusesToStartOnOccupiedCells()
    {
        var occupancy = new Occupancy();
        _ = new Walker(1, Script(), Frames, new Cell(1, 1), new Footprint(1, 0), occupancy);
        Assert.Throws<InvalidOperationException>(() =>
            new Walker(2, Script(), Frames, new Cell(2, 1), Footprint.OneCell, occupancy));
    }

    [Theory]
    [InlineData(0, -1, Direction.North)]
    [InlineData(1, 1, Direction.SouthEast)]
    [InlineData(-1, 0, Direction.West)]
    [InlineData(-1, -1, Direction.NorthWest)]
    public void DirectionsAreClockwiseFromNorth(int dx, int dy, Direction expected) =>
        Assert.Equal(expected, Walker.DirectionOf(dx, dy));
}

public class FootprintTests
{
    [Fact]
    public void DecodesTheSizeByte() => Assert.Equal(new Footprint(1, 2), Footprint.FromSizeByte(0x11));

    [Fact]
    public void CoversHalfWidthEachSideAndHeightUpwards() =>
        Assert.Equal(
            [new Cell(4, 4), new Cell(5, 4), new Cell(6, 4), new Cell(4, 5), new Cell(5, 5), new Cell(6, 5)],
            new Footprint(1, 1).Cells(new Cell(5, 5)));

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 1, 0)]
    [InlineData(8, 0, 1)]
    [InlineData(9, 1, 1)]
    [InlineData(17, 1, 2)]
    public void DecodesSizeBytes(byte size, int halfWidth, int height) =>
        Assert.Equal(new Footprint(halfWidth, height), Footprint.FromSizeByte(size));
}
