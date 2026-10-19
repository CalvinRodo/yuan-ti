using DarkSun.Data.Cinematics;
using DarkSun.Data.Ui;

namespace DarkSun.Data.Tests;

public class AcfTests
{
    /// <summary>An instruction: header word (op in the high byte, byte length in the low), then the arguments.</summary>
    internal static Bytes Op(Bytes b, int op, params int[] args)
    {
        b.Byte((byte)((args.Length + 1) * 2), (byte)op);
        foreach (int a in args)
        {
            b.Short(a);
        }
        return b;
    }

    /// <summary>An image resource of one 1 × 1 frame of colour <paramref name="color"/>.</summary>
    internal static byte[] Dot(byte color, int frames = 1)
    {
        byte[] frame = new Bytes().Short(1).Short(1).Byte(0).Byte(0, 0x80, 1, 2).Byte(0, color).Byte(0xFF).ToArray();
        var image = new Bytes().Int(0).Short(frames);
        for (int i = 0; i < frames; i++)
        {
            image.Int(6 + frames * 4 + i * frame.Length);
        }
        for (int i = 0; i < frames; i++)
        {
            image.Byte(frame);
        }
        byte[] data = image.ToArray();
        BitConverter.GetBytes(data.Length).CopyTo(data, 0);
        return data;
    }

    [Fact]
    public void ParsesInstructionsUntilTheEnd()
    {
        var b = new Bytes();
        Op(b, 0x03);
        Op(b, 0x64, 26);
        Op(b, 0x19, 1, 80, 95, 4);
        Op(b, 0x01, 0, 27, 0xF243);
        Op(b, 0x00);
        Op(b, 0x05); // after the end: ignored

        Acf acf = Acf.Parse(b.ToArray());

        Assert.Equal([0x03, 0x64, 0x19, 0x01, 0x00], acf.Instructions.Select(i => i.Op));
        Assert.Equal([1, 80, 95, 4], acf.Instructions[2].Args.Select(a => (int)a));
        Assert.Equal(new BmaChunk(27, 0xF243), Assert.Single(acf.Chunks));
    }

    [Fact]
    public void ZeroLengthIsInvalid()
    {
        Assert.Throws<InvalidDataException>(() => Acf.Parse([0x00, 0x05]));
    }

    [Fact]
    public void BmaChunksAreImagesBackToBack()
    {
        byte[] first = Dot(7, frames: 2), second = Dot(9);
        byte[] bma = [.. first, .. second];

        var frames = Acf.ReadBma(bma, [new BmaChunk(2, first.Length), new BmaChunk(1, second.Length)]);

        Assert.Equal([7, 7, 9], frames.Select(f => (int)f.Pixels[0]));
        Assert.Throws<InvalidDataException>(() => Acf.ReadBma(bma, [new BmaChunk(1, bma.Length + 1)]));
    }
}

public class XmidiTimelineTests
{
    /// <summary>An XMIDI file: FORM XDIR, CAT holding FORM XMID with one EVNT chunk.</summary>
    internal static byte[] Xmidi(params byte[] events)
    {
        static byte[] Chunk(string tag, byte[] body)
        {
            var size = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(size, body.Length);
            return [.. System.Text.Encoding.ASCII.GetBytes(tag), .. size, .. body, .. (body.Length % 2 == 1 ? new byte[1] : [])];
        }
        byte[] xdir = Chunk("FORM", [.. "XDIR"u8, .. Chunk("INFO", [1, 0])]);
        byte[] xmid = Chunk("FORM", [.. "XMID"u8, .. Chunk("EVNT", events)]);
        return [.. xdir, .. Chunk("CAT ", [.. "XMID"u8, .. xmid])];
    }

    [Fact]
    public void CountsClockCallbacksAndFollowsLoops()
    {
        byte[] xmi = Xmidi(
            0xB0, 0x77, 0x7F,          // callback at 0
            10,                        // 10 ticks
            0xB0, 0x74, 2,             // FOR 2
            0x90, 60, 100, 0x81, 0x00, // a note lasting 128 ticks (does not advance time)
            5,
            0xB0, 0x77, 0x7F,          // callback at 15, then 20
            0xB0, 0x77, 0x10,          // other callbacks don't count
            0xB0, 0x75, 0x7F,          // NEXT
            0xFF, 0x2F, 0x00);

        Assert.Equal([0, 15, 20], XmidiTimeline.ClockTicks(xmi));
    }

    [Fact]
    public void EndlessLoopsStopAtTheLimit()
    {
        byte[] xmi = Xmidi(0xB0, 0x74, 0x7F, 120, 0xB0, 0x77, 0x7F, 0xB0, 0x75, 0x7F);

        IReadOnlyList<int> ticks = XmidiTimeline.ClockTicks(xmi);

        Assert.Equal(XmidiTimeline.MaxTicks / 120, ticks.Count);
        Assert.Equal(240, ticks[1]);
    }
}

public class UiResourceTests
{
    [Fact]
    public void ParsesAWindowAndItsItems()
    {
        var b = new Bytes().Text("WIND").Int(0).Int(3000).Zero(UiWindow.HeaderSize - 12);
        byte[] header = b.ToArray();
        header[0xBE] = 64; header[0xC0] = 32; header[0xF3] = 2;
        var data = new Bytes().Byte(header)
            .Zero(4).Text("BUTN").Int(2048).Short(94).Short(70).Zero(14)
            .Zero(4).Text("ACCL").Int(8100).Short(-1).Short(0).Zero(14)
            .ToArray();

        UiWindow w = UiWindow.Parse(data);

        Assert.Equal((3000, 64, 32), (w.Number, w.Width, w.Height));
        Assert.Equal([new WindowItem("BUTN", 2048, 94, 70), new WindowItem("ACCL", 8100, -1, 0)], w.Items);
        Assert.Throws<InvalidDataException>(() => UiWindow.Parse(data[..^1]));
    }

    [Fact]
    public void ParsesButtonsAndAccelerators()
    {
        // +0x5A is the button's own number; the icon it shows is at +0x64.
        byte[] butn = new Bytes().Text("BUTN").Int(110).Int(2048).Zero(0x28 - 12).Short(127).Short(12)
            .Zero(0x5A - 0x2C).Short(2048).Zero(0x64 - 0x5C).Short(12100).Zero(110 - 0x66).ToArray();
        Assert.Equal(new UiButton(2048, 127, 12, 12100), UiButton.Parse(butn));

        byte[] accl = new Bytes().Text("ACCL").Int(0x18).Int(8100).Short(2)
            .Byte(0).Short('S').Short(8100).Byte(0).Short('s').Short(8101).ToArray();
        Assert.Equal([new Accelerator(0, 'S', 8100), new Accelerator(0, 's', 8101)], AcceleratorTable.Parse(accl));
    }

    /// <summary>A <c>BUTN</c> or <c>APFM</c>: size, alignment word, icon, and a text if <paramref name="text"/> isn't null.</summary>
    internal static byte[] Control(string tag, int number, int w, int h, int align, int icon, string? text = null)
    {
        var b = new Bytes().Text(tag).Int(text is null ? 110 : 130).Int(number).Zero(0x28 - 12).Short((short)w).Short((short)h)
            .Zero(0x58 - 0x2C).Short((short)align).Short((short)number).Zero(0x64 - 0x5C).Short((short)icon).Zero(0x6E - 0x66);
        if (text is not null)
        {
            b.Text(text).Zero(130 - 0x6E - text.Length);
        }
        return b.ToArray();
    }

    [Fact]
    public void ControlsHaveSizeAlignmentIconAndText()
    {
        UiControl b = UiControl.Parse(Control("BUTN", 11319, 42, 12, 0xA0, 11109, "XXXXX"));
        Assert.Equal(("BUTN", 11319, 42, 12, 11109, "XXXXX"), (b.Tag, b.Number, b.Width, b.Height, b.Icon, b.Text));
        Assert.Equal(new UiAlignment(0, 0, UiAlignment.Centre, UiAlignment.Centre, 0), b.Align);
        Assert.Equal(0xA0, b.Align.Word);
        Assert.Equal(new UiAlignment(UiAlignment.Inset, 0, UiAlignment.Centre, UiAlignment.End, 6), UiAlignment.Parse(0x1E6));

        UiControl slot = UiControl.Parse(Control("APFM", 11213, 18, 18, 0x1E6, 0));
        Assert.Equal(("APFM", 18, 18, 0, ""), (slot.Tag, slot.Width, slot.Height, slot.Icon, slot.Text));

        byte[] ebox = new Bytes().Text("EBOX").Int(168).Int(4002).Zero(0x22 - 12).Short(236).Short(46)
            .Zero(0x7E - 0x26).Short(229).Short(199).Zero(168 - 0x82).ToArray();
        UiControl box = UiControl.Parse(ebox);
        Assert.Equal((236, 46, 199, 229), (box.Width, box.Height, box.TextColour, box.Background));
        Assert.Throws<InvalidDataException>(() => UiControl.Parse("ACCL0000000000000000000000000000000000000000000000"u8));
    }
}
