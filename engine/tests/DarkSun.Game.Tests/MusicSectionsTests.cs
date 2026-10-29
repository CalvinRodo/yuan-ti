using System.Buffers.Binary;
using DarkSun.Data.Audio;
using DarkSun.Game.Audio;

namespace DarkSun.Game.Tests;

public class MusicSectionsTests
{
    /// <summary>
    /// A region track as the shipped ones are made: section 1 loops for ever
    /// with an indirect callback in it; sections 2 and 3 loop on an indirect
    /// NEXT, then reach an indirect callback. Each plays its own note.
    /// </summary>
    private static byte[] Track()
    {
        byte[] Section(int marker, bool indirectNext) =>
        [
            0xB0, 0x78, (byte)marker,
            0xB0, 0x74, 0x00,
            0x90, (byte)(63 + marker), 100, 5,
            10,
            .. indirectNext ? new byte[] { 0xB0, 0x73, 0x7F, 0xB0, 0x75, 0x7F, 0xB0, 0x73, 0x00, 0xB0, 0x77, 0x00 }
                : [0xB0, 0x73, 0x00, 0xB0, 0x77, 0x00, 0xB0, 0x75, 0x7F],
        ];
        byte[] one = Section(1, false), two = Section(2, true), three = Section(3, true);
        byte[] events = [.. one, .. two, .. three, 0xFF, 0x2F, 0x00];
        byte[] rbrn = [3, 0, 1, 0, 0, 0, 0, 0, 2, 0, (byte)one.Length, 0, 0, 0, 3, 0, (byte)(one.Length + two.Length), 0, 0, 0];

        static byte[] Chunk(string tag, byte[] body)
        {
            var size = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(size, body.Length);
            return [.. System.Text.Encoding.ASCII.GetBytes(tag), .. size, .. body, .. (body.Length % 2 == 1 ? new byte[1] : [])];
        }
        return Chunk("FORM", [.. "XMID"u8, .. Chunk("RBRN", rbrn), .. Chunk("EVNT", events)]);
    }

    private static List<int> Notes(XmidiSequencer s, ref long tick, int ticks)
    {
        var notes = new List<int>();
        for (long end = tick + ticks; tick < end; tick++)
        {
            s.AdvanceTo(tick, m =>
            {
                if (m.Command == 0x90)
                {
                    notes.Add(m.Data1);
                }
            });
        }
        return notes;
    }

    [Fact]
    public void ALevelPlaysItsSectionFromTheNextPlaceTheTrackAllows()
    {
        var sections = new MusicSections();
        var s = new XmidiSequencer(Track());
        sections.Follow(s);
        long tick = 0;

        Assert.Equal([64, 64, 64], Notes(s, ref tick, 30)); // section 1, for ever
        Assert.Equal(1, sections.Section);

        sections.SetLevel(3);
        Assert.Equal(3, sections.Pending);
        Assert.All(Notes(s, ref tick, 40).Skip(1), n => Assert.Equal(66, n)); // the callback in section 1's loop branches
        Assert.Equal(3, sections.Section);
        Assert.Null(sections.Pending);

        sections.SetLevel(2); // section 3's loop ends; its callback branches
        Assert.All(Notes(s, ref tick, 40).Skip(1), n => Assert.Equal(65, n));
        Assert.Equal(2, sections.Section);
        Assert.False(s.Finished);
    }

    [Fact]
    public void LevelsOutsideTheSectionsAreIgnored()
    {
        var sections = new MusicSections();
        sections.Follow(new XmidiSequencer(Track()));

        sections.SetLevel(0);
        sections.SetLevel(0x23);

        Assert.Null(sections.Pending);
        Assert.Equal(4, MusicTracks.Levels(2));
        Assert.Equal(6, MusicTracks.Levels(8)); // seven sections, six allowed
        Assert.Equal(1, MusicTracks.Levels(17));
    }
}
