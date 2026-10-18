using System.Buffers.Binary;
using System.Text;

namespace DarkSun.Data.Cinematics;

/// <summary>
/// The callback events of an XMIDI sequence (<c>GSEQ</c>, <c>LSEQ</c>, …),
/// in XMIDI ticks (120 per second). DSUN.EXE's cinematic clock counts the
/// callbacks with value 0x7F (<c>3164:0033</c>), so a cinematic runs in step
/// with its music: these times stand in for the playing sequence.
/// FOR / NEXT loops (controllers 0x74 / 0x75) are followed; an endless loop
/// is repeated until <see cref="MaxTicks"/>.
/// </summary>
public static class XmidiTimeline
{
    public const int TicksPerSecond = 120;

    /// <summary>How far endless loops are followed: 30 minutes.</summary>
    public const int MaxTicks = 30 * 60 * TicksPerSecond;

    public const int CallbackController = 0x77;
    public const int ClockValue = 0x7F;

    private const int ForController = 0x74;
    private const int NextController = 0x75;

    /// <summary>The ticks of the clock callbacks of the first sequence in <paramref name="xmi"/>.</summary>
    /// <exception cref="InvalidDataException">There is no <c>EVNT</c> chunk.</exception>
    public static IReadOnlyList<int> ClockTicks(ReadOnlySpan<byte> xmi)
    {
        ReadOnlySpan<byte> events = FindEvents(xmi);
        var ticks = new List<int>();
        var loops = new Stack<(int Start, int Left)>();
        int tick = 0;
        int pos = 0;
        while (pos < events.Length && tick <= MaxTicks)
        {
            byte b = events[pos];
            if (b < 0x80)
            {
                tick += b;
                pos++;
                continue;
            }
            switch (b & 0xF0)
            {
                case 0xF0 when b == 0xFF:
                    if (pos + 2 >= events.Length || events[pos + 1] == 0x2F)
                    {
                        return ticks;
                    }
                    pos += 3 + events[pos + 2];
                    break;
                case 0xF0:
                    pos++;
                    int length = ReadVarLength(events, ref pos);
                    pos += length;
                    break;
                case 0xB0:
                    int controller = events[pos + 1], value = events[pos + 2];
                    pos += 3;
                    if (controller == CallbackController && value == ClockValue)
                    {
                        ticks.Add(tick);
                    }
                    else if (controller == ForController)
                    {
                        loops.Push((pos, value is 0 or 127 ? int.MaxValue : value));
                    }
                    else if (controller == NextController && loops.Count > 0)
                    {
                        if (value < 64)
                        {
                            loops.Pop();
                        }
                        else
                        {
                            var (start, left) = loops.Pop();
                            if (left > 1)
                            {
                                loops.Push((start, left == int.MaxValue ? left : left - 1));
                                pos = start;
                            }
                        }
                    }
                    break;
                case 0x90:
                    pos += 3;
                    SkipDuration(events, ref pos);
                    break;
                case 0xC0 or 0xD0:
                    pos += 2;
                    break;
                default:
                    pos += 3;
                    break;
            }
        }
        return ticks;
    }

    /// <summary>
    /// A note's inline duration: 7 bits per byte, ended by the first byte of
    /// 0x80 or less (as XmiTool reads it; 0x80 itself ends it).
    /// </summary>
    private static void SkipDuration(ReadOnlySpan<byte> data, ref int pos)
    {
        while (pos < data.Length && data[pos++] > 0x80)
        {
        }
    }

    private static int ReadVarLength(ReadOnlySpan<byte> data, ref int pos)
    {
        int value = 0;
        while (pos < data.Length)
        {
            byte b = data[pos++];
            value = (value << 7) | (b & 0x7F);
            if ((b & 0x80) == 0)
            {
                break;
            }
        }
        return value;
    }

    /// <summary>The data of the first <c>EVNT</c> chunk, searching the IFF tree.</summary>
    private static ReadOnlySpan<byte> FindEvents(ReadOnlySpan<byte> data)
    {
        for (int at = 0; at + 8 <= data.Length;)
        {
            string tag = Encoding.ASCII.GetString(data.Slice(at, 4));
            int size = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(at + 4)..]);
            int body = at + 8;
            if (tag == "EVNT")
            {
                return data.Slice(body, Math.Min(size, data.Length - body));
            }
            if (tag is "FORM" or "CAT ")
            {
                at = body + 4; // into the container, past its type
                continue;
            }
            at = body + size + (size & 1);
        }
        throw new InvalidDataException("no EVNT chunk in XMIDI data");
    }
}
