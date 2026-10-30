using System.Buffers.Binary;
using System.Text;

namespace DarkSun.Data.Audio;

/// <summary>A MIDI channel message: <paramref name="Command"/> is the status high nibble (0x80–0xE0).</summary>
public readonly record struct MidiMessage(int Channel, int Command, int Data1, int Data2);

/// <summary>
/// Plays an XMIDI sequence (<c>GSEQ</c>, <c>LSEQ</c>, … in <c>RESOURCE.GFF</c>
/// and <c>CINE.GFF</c>) as Miles AIL does: time in ticks of 1/120 s (delay
/// bytes below 0x80 add to it; tempo events are ignored), notes carry their
/// duration, and the XMIDI controllers are acted on instead of sent: FOR /
/// NEXT loops (0x74 / 0x75; a count of 0 or 127 loops for ever), callbacks
/// (0x77), and the indirect prefix (0x73), which takes the next controller's
/// value on its channel from <see cref="ControllerTable"/>. <see cref="Branch"/>
/// jumps to a marker (0x78) listed in the <c>RBRN</c> chunk. Port of the event
/// handling in <c>src/xmi_tool.py</c>, but played live, so endless loops stay
/// endless.
/// </summary>
public sealed class XmidiSequencer
{
    public const int TicksPerSecond = 120;

    private const int IndirectController = 0x73;
    private const int ForController = 0x74;
    private const int NextController = 0x75;
    private const int CallbackController = 0x77;

    /// <summary>Callback value that DSUN.EXE counts as the cinematic clock (<c>3164:0033</c>).</summary>
    public const int ClockCallback = 0x7F;

    private readonly byte[] _events;
    private readonly PriorityQueue<(int Channel, int Note), long> _noteOffs = new();
    private readonly Stack<(int Start, int Left)> _loops = new();
    private readonly Dictionary<int, int> _branches = [];
    private readonly int[] _indirect = [-1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1];
    private int _pos;
    private long _next; // tick of the event at _pos (delays read so far)
    private bool _ended;

    /// <exception cref="InvalidDataException">There is no <c>EVNT</c> chunk.</exception>
    public XmidiSequencer(ReadOnlySpan<byte> xmi)
    {
        _events = FindChunk(xmi, "EVNT").ToArray();
        ReadOnlySpan<byte> rbrn = FindChunk(xmi, "RBRN", required: false);
        // RBRN: a count, then {marker, offset into EVNT} (u16, u32).
        for (int i = 0, n = rbrn.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(rbrn) : 0; i < n && 2 + 6 * i + 6 <= rbrn.Length; i++)
        {
            int at = 2 + 6 * i;
            _branches[BinaryPrimitives.ReadUInt16LittleEndian(rbrn[at..])] = (int)BinaryPrimitives.ReadUInt32LittleEndian(rbrn[(at + 2)..]);
        }
    }

    /// <summary>
    /// The table indirect controllers read (AIL's controller table; DSUN.EXE's
    /// is at <c>577D:610E</c>, set to 0–126 at start-up, <c>3256:06B6</c>).
    /// </summary>
    public byte[] ControllerTable { get; set; } = IdentityTable();

    /// <summary>A controller table holding its own indices.</summary>
    public static byte[] IdentityTable() => [.. Enumerable.Range(0, 128).Select(i => (byte)i)];

    /// <summary>
    /// The timbres a sequence names in its <c>TIMB</c> chunk (a count, then
    /// {patch, bank} bytes), which AIL installs before playing it: the FM
    /// drivers' instruments (bank 127 the drums, by note).
    /// </summary>
    public static IReadOnlyList<(int Patch, int Bank)> Timbres(ReadOnlySpan<byte> xmi)
    {
        ReadOnlySpan<byte> timb = FindChunk(xmi, "TIMB", required: false);
        int count = timb.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(timb) : 0;
        var timbres = new List<(int, int)>();
        for (int i = 0; i < count && 2 + 2 * i + 2 <= timb.Length; i++)
        {
            timbres.Add((timb[2 + 2 * i], timb[3 + 2 * i]));
        }
        return timbres;
    }

    /// <summary>The branch markers the sequence has (its <c>RBRN</c> chunk).</summary>
    public IReadOnlyCollection<int> Markers => _branches.Keys;

    /// <summary>
    /// Go on from marker <paramref name="marker"/> (AIL's <c>branch_index</c>),
    /// leaving any loop; false if there is no such marker. Sounding notes
    /// keep their durations, and time goes on from now.
    /// </summary>
    public bool Branch(int marker)
    {
        if (!_branches.TryGetValue(marker, out int offset) || offset >= _events.Length)
        {
            return false;
        }
        _pos = offset;
        _loops.Clear();
        Array.Fill(_indirect, -1);
        _ended = false;
        return true;
    }

    /// <summary>The tick reached by <see cref="AdvanceTo"/>.</summary>
    public long Tick { get; private set; }

    /// <summary>The end of the track has been played and every note released.</summary>
    public bool Finished => _ended && _noteOffs.Count == 0;

    /// <summary>Callback events (controller 0x77) as they are reached, with their value.</summary>
    public event Action<int>? Callback;

    /// <summary>Play every event up to <paramref name="tick"/>, in time order, into <paramref name="output"/>.</summary>
    public void AdvanceTo(long tick, Action<MidiMessage> output)
    {
        while (true)
        {
            long eventTick = _ended ? long.MaxValue : PeekEventTick();
            if (_noteOffs.TryPeek(out var off, out long offTick) && offTick <= tick && offTick <= eventTick)
            {
                _noteOffs.Dequeue();
                output(new MidiMessage(off.Channel, 0x80, off.Note, 0));
                continue;
            }
            if (eventTick > tick)
            {
                break;
            }
            Execute(output);
        }
        Tick = tick;
    }

    /// <summary>Release every sounding note now (stopping the sequence).</summary>
    public void ReleaseAll(Action<MidiMessage> output)
    {
        while (_noteOffs.TryDequeue(out var off, out _))
        {
            output(new MidiMessage(off.Channel, 0x80, off.Note, 0));
        }
        _ended = true;
    }

    /// <summary>Skip delay bytes; the tick of the next event, or MaxValue at the end.</summary>
    private long PeekEventTick()
    {
        while (_pos < _events.Length && _events[_pos] < 0x80)
        {
            _next += _events[_pos++];
        }
        if (_pos >= _events.Length)
        {
            _ended = true;
            return long.MaxValue;
        }
        return _next;
    }

    private void Execute(Action<MidiMessage> output)
    {
        byte status = _events[_pos];
        int channel = status & 0x0F;
        switch (status & 0xF0)
        {
            case 0xF0 when status == 0xFF:
                int type = Byte(_pos + 1);
                _pos += 2;
                int length = ReadVarLength();
                _pos += length;
                if (type == 0x2F)
                {
                    _ended = true;
                }
                break;
            case 0xF0:
                _pos++;
                _pos += ReadVarLength();
                break;
            case 0x90:
                {
                    int note = Byte(_pos + 1), velocity = Byte(_pos + 2);
                    _pos += 3;
                    long duration = ReadDuration();
                    output(new MidiMessage(channel, velocity > 0 ? 0x90 : 0x80, note, velocity));
                    if (velocity > 0)
                    {
                        _noteOffs.Enqueue((channel, note), _next + duration);
                    }
                    break;
                }
            case 0xB0:
                {
                    int controller = Byte(_pos + 1), value = Byte(_pos + 2);
                    _pos += 3;
                    if (controller == IndirectController)
                    {
                        _indirect[channel] = value;
                        break;
                    }
                    if (_indirect[channel] >= 0)
                    {
                        value = ControllerTable[_indirect[channel] & 0x7F] & 0x7F;
                        _indirect[channel] = -1;
                    }
                    Controller(channel, controller, value, output);
                    break;
                }
            case 0xC0 or 0xD0:
                output(new MidiMessage(channel, status & 0xF0, Byte(_pos + 1), 0));
                _pos += 2;
                break;
            default: // 0x80, 0xA0, 0xE0
                output(new MidiMessage(channel, status & 0xF0, Byte(_pos + 1), Byte(_pos + 2)));
                _pos += 3;
                break;
        }
    }

    private void Controller(int channel, int controller, int value, Action<MidiMessage> output)
    {
        switch (controller)
        {
            case ForController:
                _loops.Push((_pos, value is 0 or 127 ? int.MaxValue : value));
                break;
            case NextController when _loops.Count > 0:
                var (start, left) = _loops.Pop();
                if (value >= 64 && left > 1)
                {
                    _loops.Push((start, left == int.MaxValue ? left : left - 1));
                    _pos = start;
                }
                break;
            case NextController:
                break;
            case CallbackController:
                Callback?.Invoke(value);
                break;
            case >= 0x6E and <= 0x78:
                // Other XMIDI controls (channel lock, protection, patch bank, indirect, beat, branch).
                break;
            default:
                output(new MidiMessage(channel, 0xB0, controller, value));
                break;
        }
    }

    private int Byte(int at) => at < _events.Length ? _events[at] : 0;

    private int ReadVarLength()
    {
        int value = 0;
        while (_pos < _events.Length)
        {
            byte b = _events[_pos++];
            value = (value << 7) | (b & 0x7F);
            if ((b & 0x80) == 0)
            {
                break;
            }
        }
        return value;
    }

    /// <summary>A note's duration: 7 bits per byte, ended by the first byte of 0x80 or less (as XmiTool reads it).</summary>
    private long ReadDuration()
    {
        long value = 0;
        while (_pos < _events.Length)
        {
            byte b = _events[_pos++];
            value = (value << 7) | (uint)(b & 0x7F);
            if (b <= 0x80)
            {
                break;
            }
        }
        return value;
    }

    /// <summary>The data of the first <paramref name="wanted"/> chunk, searching the IFF tree.</summary>
    private static ReadOnlySpan<byte> FindChunk(ReadOnlySpan<byte> data, string wanted, bool required = true)
    {
        for (int at = 0; at + 8 <= data.Length;)
        {
            string tag = Encoding.ASCII.GetString(data.Slice(at, 4));
            int size = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(at + 4)..]);
            int body = at + 8;
            if (tag == wanted)
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
        return required ? throw new InvalidDataException($"no {wanted} chunk in XMIDI data") : [];
    }
}
