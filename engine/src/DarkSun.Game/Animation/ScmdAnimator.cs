using DarkSun.Data.Animation;

namespace DarkSun.Game.Animation;

/// <summary>
/// Runs one object's <c>SCMD</c> script as DSUN.EXE does
/// (<c>object_animate_step</c>, <c>1DF3:23A9</c>, and
/// <c>object_apply_scmd_record</c>, <c>1DF3:1BEF</c>; see
/// <c>dsun-exe-reference.md</c> §3). Time is counted in 96 Hz ticks.
/// </summary>
/// <remarks>
/// Not modelled yet: the creature hot spot (width / 2, height) unless a record
/// has <see cref="ScmdFlags.RecordHotSpot"/>, the zero delay of moving records
/// in fast mode, stopping when an object leaves the map, and flags 0x10 and
/// 0x20.
/// </remarks>
public sealed class ScmdAnimator
{
    public const int TicksPerSecond = 96;

    private readonly Scmd _script;
    private readonly int _frameCount;
    private long _lastStep;
    private int _delay;
    private readonly Queue<int> _sounds = new();

    /// <param name="frameCount">Frames in the object's <c>BMP </c>; out-of-range frames show frame 0.</param>
    /// <param name="startIndex">First record: 0 for objects, a stand slot for creatures.</param>
    public ScmdAnimator(Scmd script, int frameCount, int startIndex = 0, bool mirrored = false)
    {
        _script = script;
        _frameCount = frameCount;
        Index = startIndex;
        Mirrored = mirrored;
        Animating = true;
    }

    public bool Animating { get; private set; }

    /// <summary>The next record to apply (a byte in DSUN.EXE).</summary>
    public int Index { get; private set; }

    public int Frame { get; private set; }
    public bool Mirrored { get; private set; }
    public int HotX { get; private set; }
    public int HotY { get; private set; }

    /// <summary>Total movement from the records' dx and dy, in pixels.</summary>
    public int OffsetX { get; private set; }
    public int OffsetY { get; private set; }

    /// <summary>Sound effects triggered since the last call, oldest first.</summary>
    public IEnumerable<int> TakeSounds()
    {
        while (_sounds.Count > 0)
        {
            yield return _sounds.Dequeue();
        }
    }

    /// <summary>
    /// Apply at most one record if the current one's delay has elapsed by
    /// <paramref name="now"/> (in ticks). Returns whether anything changed.
    /// </summary>
    public bool Update(long now)
    {
        if (!Animating || _lastStep + _delay > now || _script.Records.Count == 0)
        {
            return false;
        }

        int index = Index;
        if (index >= _script.Records.Count)
        {
            Animating = false;
            index = 0;
        }
        ScmdRecord record = _script.Records[index];
        Apply(record, now);

        // The jump test reads the record the index now points at: the next one,
        // or this one again after a stop. Past the end there is nothing to read.
        ScmdRecord landed = record;
        if (!record.Flags.HasFlag(ScmdFlags.Stop))
        {
            index = (byte)(index + 1);
            landed = index < _script.Records.Count ? _script.Records[index] : default;
        }
        Index = landed.Flags.HasFlag(ScmdFlags.Jump) ? landed.Frame : index;
        return true;
    }

    private void Apply(ScmdRecord record, long now)
    {
        if (record.Frame != ScmdRecord.KeepFrame)
        {
            Frame = record.Frame < _frameCount ? record.Frame : 0;
            HotX = record.HotX;
            HotY = record.HotY;
            OffsetX += record.Dx;
            OffsetY += record.Dy;
        }
        _delay = record.DelayTicks;
        _lastStep = now;
        if (record.Sound != 0)
        {
            _sounds.Enqueue(record.Sound);
        }
        if (record.Flags.HasFlag(ScmdFlags.Stop))
        {
            Animating = false;
        }
        Mirrored = record.Flags.HasFlag(ScmdFlags.Mirror);
    }
}
