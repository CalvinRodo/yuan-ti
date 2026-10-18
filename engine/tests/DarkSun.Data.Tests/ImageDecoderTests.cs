using DarkSun.Data.Images;

namespace DarkSun.Data.Tests;

public class ImageDecoderTests
{
    /// <summary>An image resource holding one frame.</summary>
    private static byte[] Image(Bytes frame)
    {
        byte[] body = frame.ToArray();
        return new Bytes().Int(10 + body.Length).Short(1).Int(10).Byte(body).ToArray();
    }

    [Fact]
    public void DecodesRowRle()
    {
        var frame = new Bytes().Short(4).Short(2)
            // Row 0: one run at x = 1: "repeat 5 twice" (code 3), "literal 7" (code 0).
            .Byte(0).Byte(1, 0x80, 3, 4).Byte(3, 5, 0, 7)
            // Row 1: one run at x = 0 drawing palette index 0.
            .Byte(1).Byte(0, 0x80, 1, 2).Byte(0, 0)
            .Byte(0xFF);

        ImageFrame f = Assert.Single(ImageDecoder.ReadFrames(Image(frame)));

        Assert.Equal((4, 2), (f.Width, f.Height));
        Assert.Equal([0, 5, 5, 7, 0, 0, 0, 0], f.Pixels);
        Assert.Equal([false, true, true, true, true, false, false, false], f.Opaque);
    }

    [Fact]
    public void Column256FlagOffsetsTheRun()
    {
        var frame = new Bytes().Short(300).Short(1)
            .Byte(0).Byte(10, 0x81, 1, 2).Byte(0, 9)
            .Byte(0xFF);

        ImageFrame f = Assert.Single(ImageDecoder.ReadFrames(Image(frame)));

        Assert.Equal(9, f.Pixels[266]);
        Assert.Single(f.Opaque, o => o);
    }

    [Fact]
    public void DecodesPlan()
    {
        // 2 bits per symbol; symbols 1, 2, 0 -> 01 10 00 -> 0x60.
        var frame = new Bytes().Short(3).Short(1).Byte(0xFF).Text("PLAN");
        frame.Byte(2).Byte(0, 10, 20, 30).Byte(0x60);

        ImageFrame f = Assert.Single(ImageDecoder.ReadFrames(Image(frame)));

        Assert.Equal([10, 20, 0], f.Pixels);
        Assert.Equal([true, true, false], f.Opaque);
    }

    [Fact]
    public void DecodesPlnrRepeats()
    {
        // Symbols: 3 (value 30), then 0,2 (repeat 30 four more times), then 0,0
        // (a literal 0, transparent): 11 00 10 00 00 -> 0xC8 0x00.
        var frame = new Bytes().Short(6).Short(1).Byte(0xFF).Text("PLNR");
        frame.Byte(2).Byte(0, 10, 20, 30).Byte(0xC8, 0x00);

        ImageFrame f = Assert.Single(ImageDecoder.ReadFrames(Image(frame)));

        Assert.Equal([30, 30, 30, 30, 30, 0], f.Pixels);
        Assert.False(f.Opaque[5]);
    }

    [Fact]
    public void TruncatedDataIsInvalidData()
    {
        var frame = new Bytes().Short(4).Short(2).Byte(0).Byte(1, 0x80, 3, 4);
        Assert.Throws<InvalidDataException>(() => ImageDecoder.ReadFrames(Image(frame)[..^2]));
    }

    [Fact]
    public void ConvertsToRgba()
    {
        var palette = new Palette([new Rgb(1, 2, 3), new Rgb(4, 5, 6)]);
        var f = new ImageFrame(2, 1, [1, 0], [true, false]);

        Assert.Equal([4, 5, 6, 255, 0, 0, 0, 0], f.ToRgba(palette));
    }
}

public class PaletteTests
{
    [Fact]
    public void ScalesSixBitValues()
    {
        Palette p = Palette.FromBytes([63, 0, 32, .. new byte[765]]);

        Assert.Equal(256, p.Count);
        Assert.Equal(new Rgb(252, 0, 128), p[0]);
    }

    [Fact]
    public void DetectsEightBitValues()
    {
        Palette p = Palette.FromBytes([200, 10, 20, .. new byte[765]]);
        Assert.Equal(new Rgb(200, 10, 20), p[0]);
    }

    [Fact]
    public void ReadsPaddedPalettes()
    {
        Palette p = Palette.FromBytes([1, 2, 3, 99, .. new byte[1020]], PaletteDepth.SixBit);

        Assert.Equal(256, p.Count);
        Assert.Equal(new Rgb(4, 8, 12), p[0]);
    }
}
