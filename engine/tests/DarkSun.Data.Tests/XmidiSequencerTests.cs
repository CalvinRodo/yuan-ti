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
    public void IndirectControllersReadTheTableAndBranchesGoToTheirMarker()
    {
        byte[] xmi = XmidiTimelineTests.Xmidi([(2, 13)],
            0xB0, 0x73, 0x05, 0xB0, 0x77, 0x00, // indirect callback: entry 5
            0xB0, 0x73, 0x7F, 0xB0, 0x07, 0x00, // indirect volume: entry 127, sent
            0x0A,
            0xB0, 0x78, 0x02,                   // marker 2, at offset 13
            0x91, 70, 100, 5,
            0xFF, 0x2F, 0x00);
        var s = new XmidiSequencer(xmi);
        s.ControllerTable[5] = 9;
        s.ControllerTable[127] = 99;
        var callbacks = new List<int>();
        s.Callback += callbacks.Add;

        var heard = Play(s, 30);

        Assert.Equal([9], callbacks);
        Assert.Contains((0, new MidiMessage(0, 0xB0, 0x07, 99)), heard);
        Assert.Equal([2], s.Markers);
        Assert.True(s.Branch(2));
        Assert.False(s.Branch(3));
        Assert.False(s.Finished);
        heard = [];
        s.AdvanceTo(40, m => heard.Add((40, m)));
        Assert.Contains((40, new MidiMessage(1, 0x90, 70, 100)), heard); // played again from the marker, time going on
    }

    [Fact]
    public void RegionInfoGivesTheFightsMusicLevelsReadAsWords()
    {
        byte[] rdat = new byte[RegionInfo.Size];
        (rdat[6], rdat[8], rdat[10]) = (0xA7, 0xAB, 0xA9);           // creatures 167, 171, 169
        (rdat[0x1A], rdat[0x1B], rdat[0x1C], rdat[0x1D]) = (2, 3, 4, 5); // a level a byte, as the data has them
        rdat[RegionInfo.FightLevelOffset] = 6;

        RegionInfo info = RegionInfo.Parse(rdat);

        Assert.Equal([167, 171, 169], info.FightCreatures);
        Assert.Equal(2, info.FightLevel(167));
        Assert.Equal(4, info.FightLevel(171)); // DSUN.EXE: the byte at +0x1A + 2i
        Assert.Equal(0, info.FightLevel(169));
        Assert.Equal(6, info.FightLevel(300));
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
