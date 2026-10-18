using DarkSun.Data.Gff;
using DarkSun.Game.Cinematics;

namespace DarkSun.Game.Tests;

public class CinematicPlayerTests
{
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

    /// <summary>An ACF script from (op, args) instructions, ending with op 0.</summary>
    private static byte[] Script(params int[][] instructions)
    {
        var bytes = new List<byte>();
        foreach (int[] i in instructions.Append([0]))
        {
            bytes.Add((byte)(i.Length * 2));
            bytes.Add((byte)i[0]);
            foreach (int a in i[1..])
            {
                bytes.Add((byte)a);
                bytes.Add((byte)(a >> 8));
            }
        }
        return [.. bytes];
    }

    /// <summary>A full-screen image of one colour: per row, two runs of 160 (repeat codes 128 + 32).</summary>
    private static byte[] Screen(byte color)
    {
        var frame = new List<byte> { 0x40, 0x01, 0xC8, 0x00 };
        for (int row = 0; row < 200; row++)
        {
            frame.AddRange([(byte)row, 0, 0x00, 160, 4, 0xFF, color, 63, color]);
            frame.AddRange([160, 0x80, 160, 4, 0xFF, color, 63, color]);
        }
        frame.Add(0xFF);
        return [.. BitConverter.GetBytes(10 + frame.Count), 1, 0, 10, 0, 0, 0, .. frame];
    }

    private static CinematicPlayer Player(byte[] acf, Action<Resources>? more = null)
    {
        var r = new Resources();
        r.Data[("ACF ", 1)] = acf;
        more?.Invoke(r);
        var player = new CinematicPlayer(r);
        player.Play([1]);
        return player;
    }

    private static void RunToEnd(CinematicPlayer p)
    {
        for (int i = 0; i < 70 * 120 && !p.Finished; i++)
        {
            p.Step();
        }
    }

    [Fact]
    public void StagedColoursApplyTogether()
    {
        // 0x14: (index << 8 | R, G << 8 | B) (9B64:0000).
        var p = Player(Script([0x14, (5 << 8) | 10, (20 << 8) | 30], [0x06, 1], [0x15], [0x06, 100]));
        p.Step();
        Assert.Equal([0, 0, 0], p.Palette[15..18]);
        RunToEnd(p);
        Assert.Equal([10, 20, 30], p.Palette[15..18]);
    }

    [Fact]
    public void WaitsCountTheClock()
    {
        var p = Player(Script([0x06, 47]));
        double expected = 47 / CinematicPlayer.FallbackTicksPerSecond;
        while (!p.Finished)
        {
            p.Step();
        }
        Assert.InRange(p.Seconds, expected - 0.05, expected + 0.2);
    }

    [Fact]
    public void ImagesGoToTheHiddenPageUntilFlipped()
    {
        var p = Player(Script([0x28, 0, 3], [0x2A, 0, 0, 0], [0x06, 1], [0x2B], [0x06, 1]),
            r => r.Data[("BMP ", 3)] = Screen(9));
        p.Step();
        Assert.Equal(0, p.Screen[0]);
        RunToEnd(p);
        Assert.All(p.Screen, px => Assert.Equal(9, px));
    }

    [Fact]
    public void DissolveCopiesEveryPixelButTheCorner()
    {
        var p = Player(Script([0x28, 0, 3], [0x2A, 0, 0, 0], [0x3C, 1]),
            r => r.Data[("BMP ", 3)] = Screen(9));
        RunToEnd(p);
        // The LFSR never reaches 0, so pixel (0, 0) keeps its colour (on the old page, now hidden).
        Assert.Equal(64000, p.Screen.Count(px => px == 9));
    }

    [Fact]
    public void ColourCyclingRotatesUp()
    {
        byte[] pal = new byte[768];
        for (int i = 0; i < 4; i++)
        {
            pal[(10 + i) * 3] = (byte)(i + 1);
        }
        var p = Player(Script([0x16, 1], [0x19, 0, 10, 13, 2], [0x06, 1000]), r => r.Data[("PAL ", 1)] = pal);
        p.Step(); // palette loaded, cycle set
        p.Step();
        p.Step(); // two timer steps: one rotation
        Assert.Equal([4, 1, 2, 3], new[] { 10, 11, 12, 13 }.Select(c => (int)p.Palette[c * 3]));
    }

    [Fact]
    public void FadesStepOneUnitPerTimerStep()
    {
        byte[] pal = new byte[768];
        pal[0] = 63;
        var p = Player(Script([0x16, 1], [0x3F, 0, 0, 0, 0, 0, 0]), r => r.Data[("PAL ", 1)] = pal);
        p.Step();
        Assert.Equal(62, p.Palette[0]);
        for (int i = 0; i < 62; i++)
        {
            p.Step();
        }
        Assert.Equal(0, p.Palette[0]);
    }

    [Fact]
    public void SkipStopsEverything()
    {
        var p = Player(Script([0x06, 1000]));
        p.Step();
        p.Skip();
        Assert.True(p.Finished);
    }
}
