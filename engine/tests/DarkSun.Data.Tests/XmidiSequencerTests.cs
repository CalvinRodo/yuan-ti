using DarkSun.Data.Audio;
using DarkSun.Data.Regions;

namespace DarkSun.Data.Tests;

public class XmidiSequencerTests
{
    private static List<(long Tick, MidiMessage Message)> Play(XmidiSequencer s, long until)
    {
        var heard = new List<(long, MidiMessage)>();
        for (long t = 0; t <= until; t++)
        {
            s.AdvanceTo(t, m => heard.Add((t, m)));
        }
        return heard;
    }

    [Fact]
    public void NotesEndAfterTheirDuration()
    {
        var s = new XmidiSequencer(XmidiTimelineTests.Xmidi(
            0xC1, 33,                 // program change, channel 1
            0x91, 60, 100, 20,        // note 60 for 20 ticks
            5,
            0x91, 64, 90, 0x81, 0x00, // note 64 for 128 ticks
            0xFF, 0x2F, 0x00));

        var heard = Play(s, 200);

        Assert.Equal(
        [
            (0, new MidiMessage(1, 0xC0, 33, 0)),
            (0, new MidiMessage(1, 0x90, 60, 100)),
            (5, new MidiMessage(1, 0x90, 64, 90)),
            (20, new MidiMessage(1, 0x80, 60, 0)),
            (133, new MidiMessage(1, 0x80, 64, 0)),
        ], heard);
        Assert.True(s.Finished);
    }

    [Fact]
    public void EndlessLoopsKeepPlayingAndXmidiControllersAreNotSent()
    {
        var s = new XmidiSequencer(XmidiTimelineTests.Xmidi(
            0xB0, 0x74, 0x00,       // FOR, endless
            0xB0, 0x07, 100,        // volume: sent
            0xB0, 0x77, 0x7F,       // callback: not sent
            0x90, 50, 64, 10,
            10,
            0xB0, 0x75, 0x7F));     // NEXT
        int callbacks = 0;
        s.Callback += _ => callbacks++;

        var heard = Play(s, 1000);

        Assert.Equal(101, heard.Count(h => h.Message.Command == 0x90));
        Assert.Equal(101, callbacks);
        Assert.DoesNotContain(heard, h => h.Message.Command == 0xB0 && h.Message.Data1 >= 0x6E);
        Assert.False(s.Finished);
    }

    [Fact]
    public void RegionInfoGivesTheMusic()
    {
        byte[] rdat = new byte[RegionInfo.Size];
        (rdat[0], rdat[1], rdat[2], rdat[RegionInfo.MusicOffset]) = (0xE0, 8, 21, 2);

        RegionInfo info = RegionInfo.Parse(rdat);

        Assert.Equal(2, info.Music);
        Assert.Equal((0xE0, 8, 2), info.Cycle1);
    }
}
