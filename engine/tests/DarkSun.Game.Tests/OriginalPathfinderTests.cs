using DarkSun.Data.Animation;
using DarkSun.Game.Movement;

namespace DarkSun.Game.Tests;

public class OriginalPathfinderTests
{
    /// <summary>A map from rows of text: '#' blocked; off the map is blocked.</summary>
    private static Func<int, int, bool> Map(params string[] rows) =>
        (x, y) => y < 0 || y >= rows.Length || x < 0 || x >= rows[y].Length || rows[y][x] == '#';

    [Fact]
    public void WalksTheSignLineDiagonalFirst()
    {
        var open = Map("......", "......", "......");

        List<Cell> path = OriginalPathfinder.FindPath(open, new(0, 0), new(4, 2))!;

        Assert.Equal([new(1, 1), new(2, 2), new(3, 2), new(4, 2)], path);
    }

    [Fact]
    public void FollowsTheWallRoundAnObstacle()
    {
        var map = Map(
            ".......",
            "...#...",
            "...#...",
            "...#...",
            ".......");

        List<Cell> path = OriginalPathfinder.FindPath(map, new(1, 2), new(5, 2))!;

        Assert.Equal(new Cell(5, 2), path[^1]);
        Assert.DoesNotContain(path, c => map(c.X, c.Y));
        Cell at = new(1, 2);
        foreach (Cell c in path)
        {
            Assert.Equal(1, Math.Max(Math.Abs(c.X - at.X), Math.Abs(c.Y - at.Y)));
            at = c;
        }
    }

    [Fact]
    public void ABlockedGoalBecomesTheLastFreeCellOfTheLine()
    {
        var map = Map("....##", "....##");

        List<Cell> path = OriginalPathfinder.FindPath(map, new(0, 0), new(5, 0))!;

        Assert.Equal(new Cell(3, 0), path[^1]);
    }

    [Fact]
    public void FindsNothingWhenBoxedIn()
    {
        var map = Map("#####", "#...#", "#####", "#...#", "#####");

        Assert.Null(OriginalPathfinder.FindPath(map, new(2, 1), new(2, 3)));
    }

    [Fact]
    public void LongPathsStopAt79Cells()
    {
        var open = Map(new string('.', 200));

        List<Cell> path = OriginalPathfinder.FindPath(open, new(0, 0), new(150, 0))!;

        Assert.Equal(OriginalPathfinder.MaxPathLength - 2, path.Count);
    }
}

public class ShortWalkCycleTests
{
    [Fact]
    public void ACycleOfHalfACellRunsTwicePerCell()
    {
        // Walk slots point at two records of 4 px, 8 ticks each (like SCMD 239): 8 px a cycle.
        var data = new byte[Scmd.HeaderSize + 3 * 8];
        for (int slot = 0; slot < Scmd.SlotCount; slot++)
        {
            data[slot * 2] = (byte)(slot % 8 == 0 ? 0 : 1);
        }
        byte[][] records = [[0, 0, (byte)ScmdFlags.Stop, 0, 0, 0, 0, 0], [1, 8, 0, 4, 0, 0, 0, 0], [2, 8, (byte)ScmdFlags.Stop, 4, 0, 0, 0, 0]];
        for (int i = 0; i < records.Length; i++)
        {
            records[i].CopyTo(data, Scmd.HeaderSize + i * 8);
        }
        var walker = new Walker(1, Scmd.Parse(data), [(10, 20), (10, 20), (10, 20)], new Cell(0, 0), Footprint.OneCell, new Occupancy());
        static bool Open(int x, int y) => x < 0 || y < 0;
        walker.Update(0, Open);
        walker.Follow([new Cell(1, 0), new Cell(2, 0)]);

        long now = 0;
        while ((walker.IsWalking || walker.Cell.X < 2) && now < 1000)
        {
            walker.Update(++now, Open);
        }

        Assert.Equal(new Cell(2, 0), walker.Cell);
        Assert.InRange(now, 2 * 32, 2 * 32 + 2); // 16 px at 4 px per 8 ticks, per cell
    }
}
