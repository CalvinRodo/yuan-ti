using DarkSun.Data.Cinematics;
using DarkSun.Data.Gff;
using DarkSun.Data.Images;

namespace DarkSun.Game.Cinematics;

/// <summary>
/// Plays the cinematics of <c>CINE.GFF</c>: runs <c>ACF </c> scripts
/// against two 320 × 200 VGA pages and a 6-bit palette, as DSUN.EXE's
/// cinematic interpreter does (<c>985F:0000</c> and the overlays it calls;
/// <c>dsun-exe-reference.md</c> §7).
/// <para>
/// Time advances in steps of the 70 Hz timer that drives colour cycling and
/// effects. The script's own clock counts the music's XMIDI callbacks
/// (<see cref="XmidiTimeline"/>), so waits stay in step with the music even
/// though no music is played here; before any music starts, or after it ends,
/// the clock runs at <see cref="FallbackTicksPerSecond"/>.
/// </para>
/// </summary>
public sealed class CinematicPlayer
{
    public const int Width = 320;
    public const int Height = 200;

    /// <summary>The timer rate of colour cycling and effects (<c>33A5:0992(…, 70)</c>).</summary>
    public const int TimerHz = 70;

    /// <summary>Clock rate with no music: the intro music's average callback rate.</summary>
    public const double FallbackTicksPerSecond = 9.4;

    /// <summary>Frames a dissolve takes (DSUN.EXE: as long as plotting 64 000 pixels takes).</summary>
    public const int DissolveFrames = 35;

    /// <summary>The ACF scripts of each cinematic (<c>60B9:042E</c>): 0 the intro, 1 the arrival, 100 the ending.</summary>
    public static readonly IReadOnlyDictionary<int, int[]> Cinematics = new Dictionary<int, int[]>
    {
        [0] = [1, 2, 3, 4, 5, 6, 7, 8],
        [1] = [9, 10],
        [100] = [11, 12, 13],
    };

    private const int Colors = 256;
    private const int ImageSlots = 5;
    private const int CycleSlots = 16;
    private const int DissolveMask = 0x12000;

    private readonly IResourceSource _cine;
    private readonly byte[][] _pages = [new byte[Width * Height], new byte[Width * Height]];
    private readonly byte[] _dac = new byte[Colors * 3];
    private readonly byte[] _staging = new byte[Colors * 3];
    private readonly ImageFrame?[] _images = new ImageFrame?[ImageSlots];
    private readonly Cycle?[] _cycles = new Cycle?[CycleSlots];
    private readonly Queue<int> _queue = new();
    private IEnumerator<Func<bool>?>? _script;
    private Func<bool>? _waiting;
    private IReadOnlyList<ImageFrame> _bma = [];
    private int _bmaFrame;
    private int _front;
    private long _timerFrame;
    private double _pendingSeconds;

    // The script clock: ticks = _tickOffset + callbacks since the music started.
    private IReadOnlyList<int> _music = [];
    private double _musicStart;
    private int _tickOffset;
    private int _mark;

    public CinematicPlayer(IResourceSource cine) => _cine = cine;

    /// <summary>The visible page: palette indexes, row by row.</summary>
    public byte[] Screen => _pages[_front];

    /// <summary>The VGA palette: 256 × RGB, 6 bits per channel.</summary>
    public byte[] Palette => _dac;

    /// <summary>Changes whenever the picture or the palette does.</summary>
    public int Version { get; private set; }

    /// <summary>Nothing left to play (or skipped).</summary>
    public bool Finished => _script is null && _queue.Count == 0;

    public double Seconds => (double)_timerFrame / TimerHz;

    /// <summary>The ACF being played, or 0.</summary>
    public int CurrentScript { get; private set; }

    /// <summary>Music requests (op 0x64): the <c>GSEQ</c> number, for an audio layer.</summary>
    public event Action<int>? MusicStarted;

    /// <summary>Music fades (ops 0x65 in, 0x66 out): seconds, and whether it fades in.</summary>
    public event Action<double, bool>? MusicFade;

    /// <summary>Instructions this player does not carry out, for diagnostics.</summary>
    public HashSet<int> UnsupportedOps { get; } = [];

    /// <summary>The script clock (<c>3931:02C5</c>).</summary>
    public int Ticks
    {
        get
        {
            double since = Seconds - _musicStart;
            if (_music.Count == 0)
            {
                return _tickOffset + (int)(since * FallbackTicksPerSecond);
            }
            int musicTick = (int)(since * XmidiTimeline.TicksPerSecond);
            int count = UpperBound(_music, musicTick);
            if (count == _music.Count)
            {
                // The music has ended: carry on at the fallback rate.
                double after = since - (double)_music[^1] / XmidiTimeline.TicksPerSecond;
                count += (int)(after * FallbackTicksPerSecond);
            }
            return _tickOffset + count;
        }
    }

    /// <summary>Queue cinematic <paramref name="number"/> (see <see cref="Cinematics"/>).</summary>
    public void PlayCinematic(int number) => Play(Cinematics[number]);

    public void Play(IEnumerable<int> scripts)
    {
        foreach (int n in scripts)
        {
            _queue.Enqueue(n);
        }
    }

    /// <summary>Stop at once, as a key press does in DSUN.EXE (<c>985F:0CE5</c>).</summary>
    public void Skip()
    {
        _queue.Clear();
        _script = null;
        _waiting = null;
        CurrentScript = 0;
    }

    /// <summary>Let <paramref name="seconds"/> of real time pass.</summary>
    public void Advance(double seconds)
    {
        _pendingSeconds += seconds;
        while (_pendingSeconds >= 1.0 / TimerHz && !Finished)
        {
            _pendingSeconds -= 1.0 / TimerHz;
            Step();
        }
    }

    /// <summary>One 70 Hz timer step: colour cycling, then the script until it waits.</summary>
    public void Step()
    {
        _timerFrame++;
        CycleColors();
        for (int guard = 0; guard < 100_000; guard++)
        {
            if (_script is null)
            {
                if (_queue.Count == 0)
                {
                    return;
                }
                Start(_queue.Dequeue());
            }
            if (_waiting is not null && !_waiting())
            {
                return;
            }
            _waiting = null;
            if (!_script!.MoveNext())
            {
                End();
                continue;
            }
            _waiting = _script.Current;
            if (_waiting is null)
            {
                return; // wait for the next timer step
            }
        }
    }

    // -- scripts ---------------------------------------------------------------------

    private void Start(int number)
    {
        CurrentScript = number;
        if (!_cine.TryRead("ACF ", number, out var data))
        {
            _script = Enumerable.Empty<Func<bool>?>().GetEnumerator();
            return;
        }
        Acf acf = Acf.Parse(data.Span);
        _bma = _cine.TryRead("BMA ", number, out var bma) ? Acf.ReadBma(bma.Span, acf.Chunks) : [];
        _bmaFrame = 0;
        // Each script starts its clock at 0 (985F:0000 clears 3931:02C5).
        _tickOffset -= Ticks;
        _mark = 0;
        _script = Run(acf).GetEnumerator();
    }

    /// <summary>The end of a script (<c>985F:0612</c>): colour cycling stops and images are freed.</summary>
    private void End()
    {
        Array.Clear(_cycles);
        Array.Clear(_images);
        _script = null;
        _waiting = null;
        CurrentScript = 0;
    }

    private IEnumerable<Func<bool>?> Run(Acf acf)
    {
        foreach (AcfInstruction i in acf.Instructions)
        {
            switch (i.Op)
            {
                case 0x00:
                    yield break;
                case 0x01 or 0x03 or 0x04: // chunk table; timer on / off
                    break;
                case 0x05: // next animation frame, on the next tick
                    foreach (var w in WaitTicks(1)) { yield return w; }
                    if (_bmaFrame < _bma.Count)
                    {
                        Draw(_pages[_front], _bma[_bmaFrame++], 0, 0);
                    }
                    break;
                case 0x06:
                    foreach (var w in WaitTicks(i.Arg(0))) { yield return w; }
                    break;
                case 0x07: // restart the animation and the clock
                    _tickOffset -= Ticks;
                    _mark = 0;
                    _bmaFrame = 0;
                    break;
                case 0x08: // show the other page, then wait
                    Flip();
                    foreach (var w in WaitTicks(i.Arg(1))) { yield return w; }
                    break;
                case 0x14:
                    SetStaging(i.Arg(0) >> 8, i.Arg(0) & 0xFF, i.Arg(1) >> 8, i.Arg(1) & 0xFF);
                    break;
                case 0x15:
                    _staging.CopyTo(_dac, 0);
                    Version++;
                    break;
                case 0x16:
                    LoadPalette(i.Arg(0));
                    break;
                case 0x19 when i.Arg(0) < CycleSlots && i.Arg(1) <= 0xFF && i.Arg(2) <= 0xFF && i.Arg(2) >= i.Arg(1):
                    _cycles[i.Arg(0)] = new Cycle(i.Arg(1), i.Arg(2) - i.Arg(1) + 1, Math.Max(1, i.Arg(3)));
                    break;
                case 0x1A when i.Arg(0) < CycleSlots:
                    _cycles[i.Arg(0)] = null;
                    break;
                case 0x28 when i.Arg(0) < ImageSlots:
                    _images[i.Arg(0)] = _cine.TryRead("BMP ", i.Arg(1), out var bmp) && ImageDecoder.ReadFrames(bmp.Span) is [var f, ..] ? f : null;
                    break;
                case 0x29 when i.Arg(0) < ImageSlots:
                    _images[i.Arg(0)] = null;
                    break;
                case 0x2A when i.Arg(0) < ImageSlots:
                    if (_images[i.Arg(0)] is { } image)
                    {
                        Draw(_pages[1 - _front], image, i.SignedArg(1), i.SignedArg(2));
                    }
                    break;
                case 0x2B:
                    Flip();
                    break;
                case 0x2C:
                    break;
                case 0x3C:
                    foreach (var w in Dissolve(i.Arg(0))) { yield return w; }
                    break;
                case 0x3F:
                    {
                        int first = Math.Min(i.Arg(0), 255), last = Math.Min(i.Arg(1), 255);
                        var target = new byte[Colors * 3];
                        for (int c = 0; c < Colors; c++)
                        {
                            (target[c * 3], target[c * 3 + 1], target[c * 3 + 2]) = ((byte)(i.Arg(2) >> 2), (byte)(i.Arg(3) >> 2), (byte)(i.Arg(4) >> 2));
                        }
                        foreach (var w in Fade(Math.Min(first, last), Math.Max(first, last), target, i.Arg(5))) { yield return w; }
                        break;
                    }
                case 0x40:
                    if (ReadPalette(i.Arg(0)) is { } palette)
                    {
                        foreach (var w in Fade(0, 255, palette, i.Arg(1))) { yield return w; }
                    }
                    break;
                case 0x5B:
                    foreach (var w in Blinds(Math.Max(1, i.Arg(0)), i.Arg(1))) { yield return w; }
                    break;
                case 0x64:
                    StartMusic(i.Arg(0));
                    break;
                case 0x65 or 0x66: // music fade in / out over n ms (9936:0048 / 0073)
                    MusicFade?.Invoke(i.Arg(0) / 1000.0, i.Op == 0x65);
                    break;
                case 0x67:
                    break;
                case 0x7F:
                    foreach (var w in Pan(i)) { yield return w; }
                    break;
                default:
                    UnsupportedOps.Add(i.Op);
                    break;
            }
        }
    }

    /// <summary>Op 6: n ticks of the script clock, each counted from the last (<c>985F:0A91</c>).</summary>
    private IEnumerable<Func<bool>?> WaitTicks(int n)
    {
        for (int k = 0; k < n; k++)
        {
            yield return () => Ticks > _mark;
            _mark = Ticks;
        }
    }

    /// <summary>Until <paramref name="ticks"/> have passed since <paramref name="start"/> (the effects' closing wait).</summary>
    private Func<bool> Until(int start, int ticks) => () => Ticks - start >= ticks;

    // -- effects -----------------------------------------------------------------------

    /// <summary>
    /// Op 0x3C (<c>9794:0000</c>): copy the hidden page onto the visible one
    /// pixel by pixel in the order of a 17-bit LFSR (x = low 9 bits, y = the
    /// rest), show it, and wait until <paramref name="ticks"/> have passed.
    /// </summary>
    private IEnumerable<Func<bool>?> Dissolve(int ticks)
    {
        int start = Ticks;
        byte[] from = _pages[1 - _front], to = _pages[_front];
        int state = 1;
        int perFrame = (1 << 17) / DissolveFrames + 1;
        bool done = false;
        while (!done)
        {
            for (int n = 0; n < perFrame && !done; n++)
            {
                int x = state & 0x1FF, y = state >> 9;
                if (x < Width && y < Height)
                {
                    to[y * Width + x] = from[y * Width + x];
                }
                state = (state & 1) != 0 ? (state >> 1) ^ DissolveMask : state >> 1;
                done = state == 1;
            }
            Version++;
            yield return null;
        }
        Flip();
        yield return Until(start, ticks);
    }

    /// <summary>
    /// Ops 0x3F / 0x40 (<c>9794:046C</c>, <c>07DD</c>): step every channel of
    /// colours <paramref name="first"/>…<paramref name="last"/> one unit toward
    /// the target each timer step until they match, then wait until
    /// <paramref name="ticks"/> have passed since the start.
    /// </summary>
    private IEnumerable<Func<bool>?> Fade(int first, int last, byte[] target, int ticks)
    {
        int start = Ticks;
        while (true)
        {
            bool changed = false;
            for (int k = first * 3; k < (last + 1) * 3; k++)
            {
                byte goal = (byte)(target[k] & 0x3F);
                if (_dac[k] != goal)
                {
                    _dac[k] = (byte)(_dac[k] < goal ? _dac[k] + 1 : _dac[k] - 1);
                    changed = true;
                }
            }
            if (!changed)
            {
                break;
            }
            Version++;
            yield return null;
        }
        yield return Until(start, ticks);
    }

    /// <summary>
    /// Op 0x5B (<c>93D2:112C</c>): reveal the hidden page in
    /// <paramref name="bands"/> bands that open up and down from lines spaced
    /// 200 / bands apart, one row a step, then show it and wait until
    /// <paramref name="ticks"/> have passed.
    /// </summary>
    private IEnumerable<Func<bool>?> Blinds(int bands, int ticks)
    {
        int start = Ticks;
        byte[] from = _pages[1 - _front], to = _pages[_front];
        int spacing = Height / bands;
        for (int spread = 0; spread * 2 <= spacing; spread++)
        {
            for (int line = 0; line <= bands; line++)
            {
                foreach (int y in new[] { line * spacing - spread, line * spacing + spread })
                {
                    if (y is >= 0 and < Height)
                    {
                        Array.Copy(from, y * Width, to, y * Width, Width);
                    }
                }
            }
            Version++;
            yield return null;
        }
        Flip();
        yield return Until(start, ticks);
    }

    /// <summary>
    /// Op 0x7F (<c>9944:1AD7</c>; the ending's first picture, <c>ACF</c> 11): a pan.
    /// Arguments: four <c>BMP</c>s a1–a4, a rectangle (a5, a6)–(a7, a8) (clamped to
    /// the screen, corners put in order), a distance a9, a panel step a10, a strip
    /// step a11, and a closing wait a12. Within the rectangle, each frame: a2
    /// slides in from the left, pushing a1 off to the right, a10 pixels a frame
    /// (0: they stay, a1 alone showing); over them a strip of a3 with a4 to its
    /// left scrolls right a11 pixels a frame. The steps are made divisors of the
    /// distance (<c>985F:0C77</c>), and it goes on until the panels have moved
    /// the distance (by 1 a frame when they stay), a frame a retrace (the flip
    /// waits for one: <c>0980:5814</c>); then it waits until a12 ticks have passed
    /// since it began. Coordinates are taken on the screen, clipped to the
    /// rectangle (the shipped script's is the whole screen).
    /// </summary>
    private IEnumerable<Func<bool>?> Pan(AcfInstruction i)
    {
        int start = Ticks;
        ImageFrame? Image(int arg) => i.Arg(arg) != 0 && _cine.TryRead("BMP ", i.Arg(arg), out var bmp) && ImageDecoder.ReadFrames(bmp.Span) is [var f, ..] ? f : null;
        if (Image(0) is not { } back || Image(1) is not { } front || Image(2) is not { } strip || Image(3) is not { } stripLeft)
        {
            yield break; // DSUN.EXE gives up when a picture is missing
        }
        int left = Math.Min(Math.Min(i.Arg(4), Width - 1), Math.Min(i.Arg(6), Width - 1));
        int right = Math.Max(Math.Min(i.Arg(4), Width - 1), Math.Min(i.Arg(6), Width - 1));
        int top = Math.Min(Math.Min(i.Arg(5), Height - 1), Math.Min(i.Arg(7), Height - 1));
        int bottom = Math.Max(Math.Min(i.Arg(5), Height - 1), Math.Min(i.Arg(7), Height - 1));
        int distance = Math.Min(i.Arg(8), Width);
        bool still = i.Arg(9) == 0;
        int step = Divisor(still ? 1 : Math.Min(i.Arg(9), Width), distance);
        int stripStep = Divisor(Math.Min(i.Arg(10), Width), distance);
        if (step <= 0)
        {
            step = Math.Max(1, distance);
        }
        int slid = 0, stripX = 0;
        // A mod's true-colour picture, matched to the colours on screen now (as Draw does).
        byte[] Indexed(ImageFrame f) => f.Rgba is null ? f.Pixels : f.IndexedFor(Data.Images.Palette.FromBytes(_dac, PaletteDepth.SixBit));
        var pixels = new Dictionary<ImageFrame, byte[]>();
        foreach (ImageFrame f in new[] { back, front, strip, stripLeft })
        {
            pixels[f] = Indexed(f);
        }
        void Compose()
        {
            byte[] page = _pages[1 - _front];
            for (int y = top; y <= bottom; y++)
            {
                for (int x = left; x <= right; x++)
                {
                    // Left of the seam the incoming picture, its right end first; right of it the one pushed out.
                    (ImageFrame image, int sx) = x < left + slid ? (front, right + 1 - slid + (x - left)) : (back, x - slid);
                    if (sx >= 0 && sx < image.Width && y < image.Height)
                    {
                        page[y * Width + x] = pixels[image][y * image.Width + sx];
                    }
                }
            }
            DrawClipped(page, strip, stripX, top);
            DrawClipped(page, stripLeft, stripX - Width, top);
            void DrawClipped(byte[] to, ImageFrame image, int x0, int y0)
            {
                for (int y = Math.Max(top, y0); y <= Math.Min(bottom, y0 + image.Height - 1); y++)
                {
                    for (int x = Math.Max(left, x0); x <= Math.Min(right, x0 + image.Width - 1); x++)
                    {
                        int at = (y - y0) * image.Width + (x - x0);
                        if (image.Opaque[at])
                        {
                            to[y * Width + x] = pixels[image][at];
                        }
                    }
                }
            }
        }
        for (int moved = 0; moved < distance; moved += step)
        {
            if (!still)
            {
                slid = Math.Min(slid + step, right - left + 1);
            }
            stripX += stripStep;
            Compose();
            Flip();
            yield return null;
        }
        yield return Until(start, i.Arg(11));
    }

    /// <summary>
    /// <c>985F:0C77(a, total)</c>: <paramref name="a"/> if it divides
    /// <paramref name="total"/>, else the nearest divisor of it (the lower one
    /// on a tie); −1 when a is larger than the total.
    /// </summary>
    public static int Divisor(int a, int total)
    {
        if (a <= 0)
        {
            return 0;
        }
        if (total < a)
        {
            return -1;
        }
        if (total % a == 0)
        {
            return a;
        }
        int below = 0, above = 0;
        for (int d = 1; d <= total; d++)
        {
            if (total % d != 0)
            {
                continue;
            }
            if (d >= a)
            {
                above = d;
                break;
            }
            below = d;
        }
        return below != 0 && (above == 0 || a - below <= above - a) ? below : above;
    }

    // -- pages and palette ----------------------------------------------------------------

    private void Flip()
    {
        _front = 1 - _front;
        Version++;
    }

    /// <summary>Draw a frame's opaque pixels (<c>1A0A:3180</c>), clipped to the page.</summary>
    private void Draw(byte[] page, ImageFrame frame, int x, int y)
    {
        // A mod's true-colour image is matched to the colours on screen now.
        byte[] pixels = frame.Rgba is null ? frame.Pixels : frame.IndexedFor(Data.Images.Palette.FromBytes(_dac, PaletteDepth.SixBit));
        for (int row = 0; row < frame.Height; row++)
        {
            int py = y + row;
            if (py is < 0 or >= Height)
            {
                continue;
            }
            for (int col = 0; col < frame.Width; col++)
            {
                int px = x + col, at = row * frame.Width + col;
                if (px is >= 0 and < Width && frame.Opaque[at])
                {
                    page[py * Width + px] = pixels[at];
                }
            }
        }
        if (page == _pages[_front])
        {
            Version++;
        }
    }

    private void SetStaging(int index, int r, int g, int b)
    {
        _staging[index * 3] = (byte)(r & 0x3F);
        _staging[index * 3 + 1] = (byte)(g & 0x3F);
        _staging[index * 3 + 2] = (byte)(b & 0x3F);
    }

    /// <summary>Op 0x16 (<c>985F:0BD0</c>): set the palette to <c>PAL</c> n.</summary>
    private void LoadPalette(int number)
    {
        if (ReadPalette(number) is { } palette)
        {
            palette.CopyTo(_dac, 0);
            Version++;
        }
    }

    private byte[]? ReadPalette(int number)
    {
        if (!_cine.TryRead("PAL ", number, out var data) || data.Length < Colors * 3)
        {
            return null;
        }
        byte[] palette = data[..(Colors * 3)].ToArray();
        for (int k = 0; k < palette.Length; k++)
        {
            palette[k] &= 0x3F;
        }
        return palette;
    }

    /// <summary>
    /// Colour cycling (<c>22F8:047C</c>, on the 70 Hz timer): every
    /// <c>speed</c> steps a range rotates by one, its last colour moving to the front.
    /// </summary>
    private void CycleColors()
    {
        Span<byte> last = stackalloc byte[3];
        foreach (Cycle? cycle in _cycles)
        {
            if (cycle is null || --cycle.Countdown > 0)
            {
                continue;
            }
            cycle.Countdown = cycle.Speed;
            int first = cycle.First * 3, count = Math.Min(cycle.Count, Colors - cycle.First);
            if (count < 2)
            {
                continue;
            }
            Span<byte> range = _dac.AsSpan(first, count * 3);
            range[^3..].CopyTo(last);
            range[..^3].CopyTo(range[3..]);
            last.CopyTo(range);
            Version++;
        }
    }

    private void StartMusic(int number)
    {
        int now = Ticks;
        _music = _cine.TryRead("GSEQ", number, out var xmi) ? XmidiTimeline.ClockTicks(xmi.Span) : [];
        _musicStart = Seconds;
        _tickOffset = now;
        MusicStarted?.Invoke(number);
    }

    private static int UpperBound(IReadOnlyList<int> sorted, int value)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (sorted[mid] <= value)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo;
    }

    private sealed class Cycle(int first, int count, int speed)
    {
        public int First { get; } = first;
        public int Count { get; } = count;
        public int Speed { get; } = speed;
        public int Countdown { get; set; } = speed;
    }
}
