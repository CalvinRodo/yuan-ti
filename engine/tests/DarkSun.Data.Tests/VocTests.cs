using DarkSun.Data.Audio;

namespace DarkSun.Data.Tests;

public class VocTests
{
    /// <summary>A VOC file: header, then blocks of (type, body).</summary>
    private static byte[] File(params (byte Type, byte[] Body)[] blocks)
    {
        var b = new Bytes().Text("Creative Voice File").Byte(0x1A).Short(26).Short(0x010A).Short(0x1129);
        foreach (var (type, body) in blocks)
        {
            b.Byte(type, (byte)body.Length, (byte)(body.Length >> 8), (byte)(body.Length >> 16)).Byte(body);
        }
        return b.Byte(0).ToArray();
    }

    [Fact]
    public void DecodesUnsigned8BitSoundData()
    {
        // Time constant 131: 1 000 000 / (256 − 131) = 8000 Hz; codec 0.
        VocSound s = Voc.Decode(File((1, [131, 0, 0x80, 0xFF, 0x00]), (2, [0x90])));

        Assert.Equal(8000, s.SampleRate);
        Assert.Equal([0, 127 << 8, -128 << 8, 16 << 8], s.Samples.Select(v => (int)v));
        Assert.False(s.Loops);
    }

    [Fact]
    public void SilenceAndRepeats()
    {
        VocSound s = Voc.Decode(File((3, [2, 0, 131]), (6, [100, 0]), (1, [131, 0, 0x80, 0x80]), (7, [])));

        Assert.Equal(5, s.Samples.Length);
        Assert.Equal((3, 5, 100), (s.LoopStart, s.LoopEnd, s.LoopCount));
        Assert.Equal((5 + 2 * 100) / 8000.0, s.Seconds, 6);
    }

    [Fact]
    public void EndlessRepeatsLoopForEver()
    {
        VocSound s = Voc.Decode(File((6, [0xFF, 0xFF]), (1, [131, 0, 0x80]), (7, [])));

        Assert.Equal(-1, s.LoopCount);
        Assert.True(double.IsPositiveInfinity(s.Seconds));
    }

    [Fact]
    public void DecodesNewer16BitBlocks()
    {
        // 22050 Hz, 16 bits, mono, PCM.
        VocSound s = Voc.Decode(File((9, [0x22, 0x56, 0, 0, 16, 1, 4, 0, 0, 0, 0, 0, 0x34, 0x12])));

        Assert.Equal(22050, s.SampleRate);
        Assert.Equal([0x1234], s.Samples.Select(v => (int)v));
    }

    [Fact]
    public void NotAVocFile() => Assert.Throws<InvalidDataException>(() => Voc.Decode("RIFF...."u8));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(45, 45)]
    [InlineData(130, 130)]
    [InlineData(131, null)]
    [InlineData(255, null)]
    public void SoundNumbersPlayTheirSample(int id, int? bvoc) => Assert.Equal(bvoc, SoundEffects.Bvoc(id));
}
