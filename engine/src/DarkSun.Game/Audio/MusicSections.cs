using DarkSun.Data.Audio;

namespace DarkSun.Game.Audio;

/// <summary>
/// The music level, which picks a section of the region tracks as DSUN.EXE
/// does, through the indirect controller table every track shares
/// (<c>577D:610E</c>): section n starts at branch marker n; the sections'
/// loops end on an indirect NEXT (table entry 127) and their callbacks are
/// indirect (entry 0). Setting a level puts it in entry 0 and 0 in entry 127
/// (<c>361A:0000</c>), so the loop ends, and the next callback branches to it
/// and puts the table back (<c>3164:0033</c>). A new track keeps the table.
/// </summary>
public sealed class MusicSections
{
    /// <summary>Levels below this are sections (<c>3164:0033</c>: callbacks 1–0x22 branch).</summary>
    private const int Limit = 0x23;

    private readonly byte[] _table = XmidiSequencer.IdentityTable();
    private XmidiSequencer? _sequence;

    /// <summary>The section playing, as far as the music knows (<c>4931:02C8</c>): 1 when a track starts, then where the last branch went.</summary>
    public int Section { get; private set; }

    /// <summary>The level asked for that no callback has branched to yet, if any.</summary>
    public int? Pending => _table[127] == 0 ? _table[0] : null;

    /// <summary>Play the sections of <paramref name="sequence"/>, the track that has started.</summary>
    public void Follow(XmidiSequencer sequence)
    {
        if (_sequence is not null)
        {
            _sequence.Callback -= OnCallback;
        }
        _sequence = sequence;
        sequence.ControllerTable = _table;
        sequence.Callback += OnCallback;
        Section = 1;
    }

    /// <summary>
    /// The music level (<c>361A:0000</c>): section <paramref name="level"/>,
    /// from the next point the track allows a change. The caller checks it
    /// against the track (<see cref="MusicTracks.Levels"/>); 0 and levels of
    /// 0x23 and over are ignored.
    /// </summary>
    public void SetLevel(int level)
    {
        if (level is <= 0 or >= Limit)
        {
            return;
        }
        _table[0] = (byte)level;
        _table[127] = 0;
    }

    /// <summary>
    /// A callback from the track (<c>3164:0033</c>, as far as the music goes):
    /// 1–0x22 branches to that section, and with 0x23 too, the table is put
    /// back (entry 0: no change asked; entry 127: loops go on).
    /// </summary>
    private void OnCallback(int value)
    {
        if (value is 0 or > Limit)
        {
            return;
        }
        if (value < Limit && _sequence!.Branch(value))
        {
            Section = value;
        }
        _table[0] = 0;
        _table[127] = 0x7F;
    }
}
