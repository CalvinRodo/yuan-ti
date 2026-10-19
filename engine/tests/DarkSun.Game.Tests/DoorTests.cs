using DarkSun.Data.Animation;
using DarkSun.Data.Gff;
using DarkSun.Data.Images;
using DarkSun.Data.Regions;
using DarkSun.Game.World;

namespace DarkSun.Game.Tests;

public class DoorTests
{
    private const int Width = 20, Height = 10;

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

    /// <summary>A door's RDFF: an item record with its size (+0x0E) and flags (+0x0F).</summary>
    private static byte[] DoorRdff(int ojff, byte size, byte flags)
    {
        var data = new byte[10 + 21 + 1];
        (data[0], data[2], data[8]) = (1, 1, 21);
        data[10] = (byte)-ojff;
        data[11] = 0xFF;
        (data[10 + 14], data[10 + 15]) = (size, flags);
        data[^1] = 0xFF;
        return data;
    }

    /// <summary>A door SCMD: slot 1 (closed) record 1, slot 2 (opening) record 2, slot 3 (closing) record 5.</summary>
    private static byte[] DoorScmd()
    {
        var data = new byte[0x48 + 8 * 8];
        (data[2], data[4], data[6]) = (1, 2, 5);
        for (int i = 0; i < 8; i++)
        {
            data[0x48 + i * 8 + 2] = 2; // stop
        }
        return data;
    }

    private static readonly ImageFrame Frame = new(16, 32, new byte[512], new bool[512]);

    /// <summary>A double door (OJFF 2051 at cell (5, 4), 3 wide; 2052 at (8, 4)), a stuck door (2053 at (5, 7)) and a secret door (2055 at (12, 7)).</summary>
    private static GameWorld World(byte flags = 1)
    {
        var r = new Resources();
        r.Data[("SCMD", 9)] = DoorScmd();
        r.Data[("RDFF", 2051)] = DoorRdff(2051, 2, flags);
        r.Data[("RDFF", 2052)] = DoorRdff(2052, 0, flags);
        r.Data[("RDFF", 2053)] = DoorRdff(2053, 0, 5);
        r.Data[("RDFF", 2055)] = DoorRdff(2055, 0, 9);
        RegionObject Door(int i, int ojff, int x, int y) => new(i, ojff, x * 16 + 8, y * 16 + 8, 0, 0, false, [Frame], 9);
        var region = Region.Create(5, Width, Height, new byte[Width * Height],
            [Door(0, 2051, 5, 4), Door(1, 2052, 8, 4), Door(2, 2053, 5, 7), Door(3, 2055, 12, 7)]);
        return new GameWorld(region, r);
    }

    private static WorldObject Door(GameWorld w, int ojff) => w.First(-ojff)!;

    [Fact]
    public void ClosedDoorsBlockTheirCellsAndStartClosed()
    {
        GameWorld w = World();

        Assert.True(Door(w, 2051).IsDoor);
        Assert.All(new[] { 5, 6, 7, 8 }, x => Assert.True(w.Region.IsBlocked(x, 4)));
        Assert.True(w.Region.BlocksSight(6, 4));
        Assert.Equal(1, Door(w, 2051).AnimationStart);
    }

    [Fact]
    public void UsingADoorOpensBothLeavesWithTheOpeningAnimation()
    {
        GameWorld w = World();
        var started = new List<(int Ojff, int? Start)>();
        w.Changed += (o, start) => started.Add((o.Source!.Ojff, start));

        DoorUse use = w.UseObject(Door(w, 2051));

        Assert.True(use.Toggled);
        Assert.Equal(0, Door(w, 2051).DoorFlags & Doors.Closed);
        Assert.Equal(0, Door(w, 2052).DoorFlags & Doors.Closed);
        Assert.Equal([(2051, 2), (2052, 2)], started);
        // The first cell stays blocked: the cell west of it is free (6BB8:0FF8).
        Assert.True(w.Region.IsBlocked(5, 4));
        Assert.False(w.Region.IsBlocked(6, 4));
        Assert.False(w.Region.IsBlocked(7, 4));
        Assert.Null(w.DoorRefusal(use));

        w.UseObject(Door(w, 2051));
        Assert.Equal((2051, (int?)5), started[^2]);
        Assert.True(w.Region.IsBlocked(6, 4));
    }

    [Fact]
    public void StuckAndSecretDoorsDoNotMove()
    {
        GameWorld w = World();

        DoorUse stuck = w.UseObject(Door(w, 2053));
        Assert.False(stuck.Toggled);
        Assert.Equal("THE DOOR WON'T OPEN", w.DoorRefusal(stuck));

        DoorUse secret = w.UseObject(Door(w, 2055));
        Assert.Equal("THERE'S A SECRET DOOR!", w.DoorRefusal(secret));
        Assert.Equal(0, Door(w, 2055).DoorFlags & Doors.Secret);
    }

    [Fact]
    public void ScriptsMoveDoorsEvenWhenStuck()
    {
        GameWorld w = World();

        Assert.True(w.SetDoor(Door(w, 2053), 0, force: true));
        Assert.False(w.Region.IsBlocked(5, 7) && w.Region.IsBlocked(4, 7)); // open: its cell freed or kept as the hinge
        Assert.True(w.SetDoor(Door(w, 2053), Doors.Closed | Doors.Locked, force: true));
        Assert.Equal(Doors.Closed | Doors.Locked | Doors.Stuck, Door(w, 2053).DoorFlags);
    }

    [Fact]
    public void OpenDoorsStartOpen()
    {
        GameWorld w = World(flags: 0);

        Assert.Equal(2, Door(w, 2051).AnimationStart);
        Assert.False(w.Region.IsBlocked(6, 4));
    }
}
