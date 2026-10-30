using DarkSun.Game.Characters;
using DarkSun.Game.Combat;

namespace DarkSun.Game.Tests;

public class StatusWordTests
{
    [Fact]
    public void AnOkayMembersStatusNamesItsStrongestCondition()
    {
        // 78A9:2170's ranks: Poisoned 68 is the strongest; Blessed 25, Hasted 49, Detect Invisible 1.
        Assert.Equal("Poisoned", Conditions.Strongest([Condition.Blessed, Condition.Poisoned, Condition.Hasted]));
        Assert.Equal("Hasted", Conditions.Strongest([Condition.Blessed, Condition.Hasted, Condition.DetectInvisible]));
        Assert.Equal("Prot Evil 10'", Conditions.Strongest([Condition.ProtectionFromEvil10]));
        Assert.Null(Conditions.Strongest([]));
        // The engine's own conditions past Weakened have no name in the original's table.
        Assert.Null(Conditions.Strongest([Condition.ExtraHitPoints, Condition.Prayer]));
    }

    [Fact]
    public void TheQuickCastListKeepsTheSixLatestFirst()
    {
        var c = new Character();
        foreach (int n in new[] { 9, 72, 139 })
        {
            c.RememberCast(n);
        }
        Assert.Equal([139, 72, 9], c.QuickCasts);
        c.RememberCast(9); // already there: stays where it is (78A9:2324)
        Assert.Equal([139, 72, 9], c.QuickCasts);
        foreach (int n in new[] { 1, 2, 3, 4 })
        {
            c.RememberCast(n);
        }
        Assert.Equal([4, 3, 2, 1, 139, 72], c.QuickCasts);
    }
}
