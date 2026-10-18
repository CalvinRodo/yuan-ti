using DarkSun.Game.Rules;

namespace DarkSun.Game.Tests;

public class BorlandRandomTests
{
    [Fact]
    public void MatchesBorlandSequenceFromSeedOne()
    {
        var rng = new BorlandRandom(1);
        int[] values = [.. Enumerable.Range(0, 6).Select(_ => rng.Next())];
        Assert.Equal([346, 130, 10982, 1090, 11656, 7117], values);
    }

    [Fact]
    public void SeedResetsTheSequence()
    {
        var rng = new BorlandRandom(99);
        rng.Next();
        rng.Seed(1);
        Assert.Equal(346, rng.Next());
    }

    [Fact]
    public void RangeScalesLikeGplRand()
    {
        var rng = new BorlandRandom(0x1234);
        short[] values = [rng.Range(5), rng.Range(5), rng.Range(5), rng.Range(0), rng.Range(100)];
        Assert.Equal([1, 2, 1, 0, 62], values);
    }

    [Fact]
    public void RangeStaysWithinBounds()
    {
        var rng = new BorlandRandom(7);
        for (int i = 0; i < 10_000; i++)
        {
            Assert.InRange(rng.Range(20), 0, 20);
        }
    }
}
