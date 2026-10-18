using DarkSun.Data.Audio;

namespace DarkSun.Data.Cinematics;

/// <summary>
/// The clock callbacks of an XMIDI sequence, in ticks of 1/120 s. DSUN.EXE's
/// cinematic clock counts the callbacks with value 0x7F (<c>3164:0033</c>),
/// so a cinematic runs in step with its music: these times stand in for the
/// playing sequence. Played with <see cref="XmidiSequencer"/>, so loops count
/// as they play; an endless loop is followed until <see cref="MaxTicks"/>.
/// </summary>
public static class XmidiTimeline
{
    public const int TicksPerSecond = XmidiSequencer.TicksPerSecond;

    /// <summary>How far endless loops are followed: 30 minutes.</summary>
    public const int MaxTicks = 30 * 60 * TicksPerSecond;

    /// <summary>The ticks of the clock callbacks of the first sequence in <paramref name="xmi"/>.</summary>
    /// <exception cref="InvalidDataException">There is no <c>EVNT</c> chunk.</exception>
    public static IReadOnlyList<int> ClockTicks(ReadOnlySpan<byte> xmi)
    {
        var sequencer = new XmidiSequencer(xmi);
        var ticks = new List<int>();
        int now = 0;
        sequencer.Callback += value =>
        {
            if (value == XmidiSequencer.ClockCallback)
            {
                ticks.Add(now);
            }
        };
        for (; now <= MaxTicks && !sequencer.Finished; now++)
        {
            sequencer.AdvanceTo(now, _ => { });
        }
        return ticks;
    }
}
