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
    private static Scmd Script()
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
        var walker = new Walker(Script(), Frames, new Cell(0, 0));
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

    [Fact]
    public void StopsBeforeABlockedCell()
    {
        var walker = new Walker(Script(), Frames, new Cell(0, 0));
        walker.Follow([new Cell(1, 0)]);

        walker.Update(0, (x, y) => x == 1);

        Assert.False(walker.IsWalking);
        Assert.Equal(new Cell(0, 0), walker.Cell);
    }

    [Theory]
    [InlineData(0, -1, Direction.North)]
    [InlineData(1, 1, Direction.SouthEast)]
    [InlineData(-1, 0, Direction.West)]
    [InlineData(-1, -1, Direction.NorthWest)]
    public void DirectionsAreClockwiseFromNorth(int dx, int dy, Direction expected) =>
        Assert.Equal(expected, Walker.DirectionOf(dx, dy));
}
