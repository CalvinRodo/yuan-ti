using System.Buffers.Binary;
using DarkSun.Data.Audio;
using DarkSun.Game.Audio;

namespace DarkSun.Game.Tests;

public class FmMusicTests
{
    private static byte[] Chunk(string tag, byte[] body)
    {
        var size = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(size, body.Length);
        return [.. System.Text.Encoding.ASCII.GetBytes(tag), .. size, .. body, .. (body.Length % 2 == 1 ? new byte[1] : [])];
    }

    /// <summary>An AIL global timbre library with one timbre: <c>STDPATCH.AD</c>'s acoustic piano (bank 0, patch 0).</summary>
    private static byte[] Bank()
    {
        byte[] piano = [0x0E, 0x00, 0x00, 0x21, 0x8F, 0xF2, 0x45, 0x00, 0x08, 0x21, 0x06, 0xF2, 0x76, 0x00];
        byte[] index = [0x00, 0x00, 0, 0, 0, 0, 0xFF, 0xFF];
        BinaryPrimitives.WriteInt32LittleEndian(index.AsSpan(2), index.Length);
        return [.. index, .. piano];
    }

    /// <summary>A sequence naming that timbre in its <c>TIMB</c> chunk and playing a note of it for a second.</summary>
    private static byte[] Track() =>
        Chunk("FORM", [.. "XMID"u8, .. Chunk("TIMB", [1, 0, 0, 0]),
            .. Chunk("EVNT", [0xC1, 0x00, 0x91, 60, 127, 0x80 | (120 & 0x7F), 0x00, 0x7F, 0x7F, 0xFF, 0x2F, 0x00])]);

    [Fact]
    public void ASequenceNamesItsTimbres()
    {
        byte[] xmi = Chunk("FORM", [.. "XMID"u8, .. Chunk("TIMB", [3, 0, 32, 0, 36, 127, 0, 1]), .. Chunk("EVNT", [0xFF, 0x2F, 0x00])]);
        Assert.Equal([(32, 0), (36, 127), (0, 1)], XmidiSequencer.Timbres(xmi));
        Assert.Empty(XmidiSequencer.Timbres(Chunk("FORM", [.. "XMID"u8, .. Chunk("EVNT", [0xFF, 0x2F, 0x00])])));
    }

    [Theory]
    [InlineData(FmDevice.AdLib)]
    [InlineData(FmDevice.SoundBlasterPro2)]
    public void TheFmDriversPlayTheTimbresTheSequenceNames(FmDevice device)
    {
        var player = new MusicPlayer(new FmSynth(Bank(), device, 44100));
        player.Play(Track());
        float[] left = new float[22050], right = new float[22050];
        player.Render(left, right);
        Assert.True(left.Max(Math.Abs) > 0.01f, "the note sounds");
        Assert.True(player.IsPlaying);

        // Stopped, the chip goes quiet.
        player.Stop();
        player.Render(left, right);
        Assert.True(left.Skip(4410).Max(Math.Abs) < 0.001f, "silent once stopped");
    }

    [Fact]
    public void WithoutItsTimbreANoteIsSilent()
    {
        byte[] xmi = Chunk("FORM", [.. "XMID"u8, .. Chunk("EVNT", [0xC1, 0x05, 0x91, 60, 127, 0x78, 0x7F, 0xFF, 0x2F, 0x00])]);
        var player = new MusicPlayer(new FmSynth(Bank(), FmDevice.AdLib, 44100));
        player.Play(xmi);
        float[] left = new float[22050], right = new float[22050];
        player.Render(left, right);
        Assert.True(left.Max(Math.Abs) < 0.001f);
    }
}
