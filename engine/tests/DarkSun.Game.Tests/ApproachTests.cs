using DarkSun.Game.Movement;

namespace DarkSun.Game.Tests;

public class ApproachTests
{
    /// <summary>A map from rows of text: '#' blocks.</summary>
    private static Func<Cell, bool> Map(params string[] rows) =>
        c => c.Y >= 0 && c.Y < rows.Length && c.X >= 0 && c.X < rows[c.Y].Length && rows[c.Y][c.X] != '#';

    [Fact]
    public void WalksToTheNearestCellNextToTheObject()
    {
        var open = Map(
            "..........",
            "..........",
            "..........");
        Assert.Equal(new Cell(6, 1), Approach.Goal(open, new Cell(0, 1), [new Cell(7, 1)]));
    }

    [Fact]
    public void GoesRoundAWallToTheSideItCanReach()
    {
        // The door (D) is in a wall; the character is behind the wall's far end.
        var map = Map(
            "....#...",
            "....#...",
            "##D##...",
            "........");
        Cell? goal = Approach.Goal(c => map(c) && c != new Cell(2, 2), new Cell(6, 0), [new Cell(2, 2)]);
        Assert.Equal(3, goal!.Value.Y); // below the wall, not above it behind the other wall
        Assert.True(Approach.Distance(goal.Value, [new Cell(2, 2)]) <= Approach.Reach);
    }

    [Fact]
    public void StopsAsNearAsItCanBehindACounter()
    {
        var map = Map(
            "...",
            "###",
            "...",
            "...");
        Assert.Equal(new Cell(1, 2), Approach.Goal(map, new Cell(1, 3), [new Cell(1, 0)]));
        Assert.Null(Approach.Goal(map, new Cell(1, 3), [new Cell(1, 0)], maxReach: 1));
    }

    [Fact]
    public void ADiagonalNeedsBothCellsBesideIt()
    {
        var map = Map(
            ".#.",
            "#..",
            "...");
        // From (0,0) nothing is reachable: the only way out is a cut corner.
        Assert.Equal(new Cell(0, 0), Approach.Goal(map, new Cell(0, 0), [new Cell(2, 2)], maxReach: 2));
    }
}
