using DarkSun.Data.Animation;
using DarkSun.Game.Animation;

namespace DarkSun.Game.Tests;

public class ScmdAnimatorTests
{
    /// <summary>An SCMD with the given slot values and 8-byte records.</summary>
    private static Scmd Build(Dictionary<int, ushort> slots, params byte[][] records)
    {
        var data = new byte[Scmd.HeaderSize + records.Length * 8];
        foreach (var (slot, value) in slots)
        {
            data[slot * 2] = (byte)value;
            data[slot * 2 + 1] = (byte)(value >> 8);
        }
        for (int i = 0; i < records.Length; i++)
        {
            records[i].CopyTo(data, Scmd.HeaderSize + i * 8);
        }
        return Scmd.Parse(data);
    }

    // frame, delay, flags, dx, dy, hot x, hot y, sound
    private static byte[] Rec(byte frame, byte delay, ScmdFlags flags = 0, sbyte dx = 0, sbyte dy = 0, byte sound = 0) =>
        [frame, delay, (byte)flags, (byte)dx, (byte)dy, 4, 9, sound];

    [Fact]
    public void ParsesHeaderAndRecords()
    {
        Scmd s = Build(new() { [0] = 3, [8] = 5, [9] = 6, [31] = 7 }, Rec(1, 15, ScmdFlags.Mirror, dx: -4));

        Assert.Equal(3, s.StandStart(Direction.North));
        Assert.Equal(5, s.StandStart(Direction.SouthEast));
        Assert.Equal(6, s.WalkStart(Direction.East));
        Assert.Equal(7, s.WalkStart(Direction.NorthWest));
        Assert.Equal(new ScmdRecord(1, 15, ScmdFlags.Mirror, -4, 0, 4, 9, 0), Assert.Single(s.Records));
    }

    [Fact]
    public void WaitsForTheDelayAndFollowsJumps()
    {
        Scmd s = Build([], Rec(1, 10), Rec(2, 5, ScmdFlags.Mirror), Rec(0, 0, ScmdFlags.Jump));
        var a = new ScmdAnimator(s, frameCount: 3);

        Assert.True(a.Update(0));
        Assert.Equal((1, 1, false), (a.Frame, a.Index, a.Mirrored));
        Assert.False(a.Update(9));

        Assert.True(a.Update(10));
        Assert.Equal((2, 0, true), (a.Frame, a.Index, a.Mirrored)); // landed on the jump to 0

        Assert.False(a.Update(14));
        Assert.True(a.Update(15));
        Assert.Equal((1, false), (a.Frame, a.Mirrored));
        Assert.Equal((4, 9), (a.HotX, a.HotY));
    }

    [Fact]
    public void StopHoldsTheFrame()
    {
        Scmd s = Build([], Rec(2, 0, ScmdFlags.Stop), Rec(1, 0));
        var a = new ScmdAnimator(s, frameCount: 3);

        Assert.True(a.Update(0));
        Assert.False(a.Animating);
        Assert.Equal((2, 0), (a.Frame, a.Index));
        Assert.False(a.Update(1000));
    }

    [Fact]
    public void RunningPastTheEndStops()
    {
        Scmd s = Build([], Rec(1, 0));
        var a = new ScmdAnimator(s, frameCount: 2);

        Assert.True(a.Update(0));
        Assert.True(a.Update(0)); // index past the end: record 0 is applied once more
        Assert.False(a.Animating);
        Assert.False(a.Update(1));
    }

    [Fact]
    public void LongDelaysAreTimesTen()
    {
        Assert.Equal(99, new ScmdRecord(0, 99, 0, 0, 0, 0, 0, 0).DelayTicks);
        Assert.Equal(1000, new ScmdRecord(0, 100, 0, 0, 0, 0, 0, 0).DelayTicks);
    }

    [Fact]
    public void KeepFrameAndOutOfRangeFrames()
    {
        Scmd s = Build([], Rec(2, 0), Rec(ScmdRecord.KeepFrame, 0), Rec(9, 0));
        var a = new ScmdAnimator(s, frameCount: 3);

        a.Update(0);
        a.Update(0);
        Assert.Equal(2, a.Frame);
        a.Update(0);
        Assert.Equal(0, a.Frame);
    }

    [Fact]
    public void AccumulatesMovementAndSounds()
    {
        Scmd s = Build([], Rec(0, 0, dx: 3, dy: -2, sound: 7), Rec(0, 0, dx: 3, dy: -2));
        var a = new ScmdAnimator(s, frameCount: 1);

        a.Update(0);
        a.Update(0);
        Assert.Equal((6, -4), (a.OffsetX, a.OffsetY));
        Assert.Equal([7], a.TakeSounds());
        Assert.Empty(a.TakeSounds());
    }
}
