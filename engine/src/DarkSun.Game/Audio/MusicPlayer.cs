using DarkSun.Data.Audio;
using MeltySynth;

namespace DarkSun.Game.Audio;

/// <summary>
/// Plays the game's XMIDI music through a synthesizer (<see cref="IMusicSynth"/>:
/// a SoundFont, or the original's FM drivers on an emulated OPL chip), with
/// the fades of DSUN.EXE's music code: <see cref="Change"/> is <c>35D1:0265</c> (fade the old track out, then the new one in, over the
/// same time), <see cref="FadeIn"/> / <see cref="FadeOut"/> are
/// <c>35D1:012D</c> with 0x32 / 0x33. Call <see cref="Render"/> from the
/// audio output; it advances the sequence by the samples rendered.
/// The music level picks a section of the track (<see cref="MusicSections"/>).
/// </summary>
public sealed class MusicPlayer
{
    private readonly IMusicSynth _synth;
    private readonly int _sampleRate;
    private readonly Action<MidiMessage> _send;
    private XmidiSequencer? _sequencer;
    private long _samples; // since the current sequence started
    private (byte[] Xmi, double FadeIn)? _queued;
    private readonly MusicSections _sections = new();

    // Volume ramp, in samples.
    private float _gain = 1f;
    private float _gainFrom = 1f, _gainTo = 1f;
    private long _rampLength, _rampAt;

    public MusicPlayer(SoundFont soundFont, int sampleRate = 44100)
        : this(new SoundFontSynth(soundFont, sampleRate), sampleRate)
    {
    }

    public MusicPlayer(IMusicSynth synth, int sampleRate = 44100)
    {
        _sampleRate = sampleRate;
        _synth = synth;
        _send = synth.Send;
    }

    public int SampleRate => _sampleRate;

    /// <summary>Overall loudness, 0–1 (a player setting, on top of fades).</summary>
    public float Volume { get; set; } = 1f;

    public bool IsPlaying => _sequencer is not null;

    /// <summary>The section playing (<see cref="MusicSections.Section"/>).</summary>
    public int Section => _sections.Section;

    /// <summary>The music level (<see cref="MusicSections.SetLevel"/>).</summary>
    public void SetLevel(int level) => _sections.SetLevel(level);

    /// <summary>Start a sequence at once, fading in over <paramref name="fadeIn"/> seconds (0: full volume).</summary>
    public void Play(byte[] xmi, double fadeIn = 0)
    {
        StopNow();
        _sequencer = new XmidiSequencer(xmi);
        _synth.Prepare(xmi);
        _sections.Follow(_sequencer);
        _samples = 0;
        if (fadeIn > 0)
        {
            _gain = 0;
            FadeIn(fadeIn);
        }
        else
        {
            SetGain(1);
        }
    }

    /// <summary>Change track as DSUN.EXE does: fade out what plays, then fade the new one in, each over <paramref name="fade"/> seconds.</summary>
    public void Change(byte[] xmi, double fade)
    {
        if (_sequencer is null || fade <= 0)
        {
            Play(xmi, fade);
            return;
        }
        _queued = (xmi, fade);
        Ramp(0, fade);
    }

    public void FadeIn(double seconds) => Ramp(1, seconds, from: 0);

    public void FadeOut(double seconds) => Ramp(0, seconds);

    public void Stop()
    {
        _queued = null;
        StopNow();
    }

    /// <summary>Fill the buffers with the next samples of music (silence when nothing plays).</summary>
    public void Render(Span<float> left, Span<float> right)
    {
        int done = 0;
        while (done < left.Length)
        {
            if (_sequencer is null)
            {
                left[done..].Clear();
                right[done..].Clear();
                return;
            }
            // Render up to the next sequencer tick.
            long tick = _samples * XmidiSequencer.TicksPerSecond / _sampleRate;
            _sequencer.AdvanceTo(tick, _send);
            long nextTickSample = ((tick + 1) * _sampleRate + XmidiSequencer.TicksPerSecond - 1) / XmidiSequencer.TicksPerSecond;
            int count = (int)Math.Clamp(nextTickSample - _samples, 1, left.Length - done);
            Span<float> l = left.Slice(done, count), r = right.Slice(done, count);
            _synth.Render(l, r);
            ApplyGain(l, r);
            _samples += count;
            done += count;

            if (_sequencer.Finished)
            {
                StopNow();
            }
            else if (_rampAt >= _rampLength && _gainTo == 0 && _gain == 0)
            {
                // A fade out has ended: stop, and start what was waiting.
                var queued = _queued;
                _queued = null;
                StopNow();
                if (queued is { } q)
                {
                    Play(q.Xmi, q.FadeIn);
                }
            }
        }
    }

    private void ApplyGain(Span<float> left, Span<float> right)
    {
        for (int i = 0; i < left.Length; i++)
        {
            if (_rampAt < _rampLength)
            {
                _rampAt++;
                _gain = _gainFrom + (_gainTo - _gainFrom) * _rampAt / _rampLength;
            }
            float g = _gain * Volume;
            left[i] *= g;
            right[i] *= g;
        }
    }

    private void Ramp(float to, double seconds, float? from = null)
    {
        _gainFrom = from ?? _gain;
        _gainTo = to;
        _rampLength = Math.Max(1, (long)(seconds * _sampleRate));
        _rampAt = 0;
        if (from is { } f)
        {
            _gain = f;
        }
    }

    private void SetGain(float gain)
    {
        _gain = _gainFrom = _gainTo = gain;
        _rampLength = _rampAt = 0;
    }

    private void StopNow()
    {
        _sequencer?.ReleaseAll(_send);
        _sequencer = null;
        _synth.Reset();
        SetGain(1);
    }
}

/// <summary>Finds a General MIDI SoundFont (.sf2) to play the music with.</summary>
public static class SoundFontLocator
{
    /// <summary>Environment variable naming a SoundFont file.</summary>
    public const string Variable = "DARKSUN_SOUNDFONT";

    /// <summary>Where SoundFonts are usually installed, better ones first.</summary>
    public static IEnumerable<string> SystemCandidates()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] directories =
        [
            "/usr/share/sounds/sf2", "/usr/share/soundfonts", "/usr/local/share/soundfonts",
            Path.Combine(home, ".local/share/soundfonts"), Path.Combine(home, "Library/Audio/Sounds/Banks"),
            @"C:\soundfonts",
        ];
        string[] preferred = ["FluidR3_GM.sf2", "GeneralUser", "default-GM.sf2", "TimGM6mb.sf2"];
        var found = directories.Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.sf2"))
            .ToList();
        return found.OrderBy(f =>
        {
            int rank = Array.FindIndex(preferred, p => Path.GetFileName(f).StartsWith(p, StringComparison.OrdinalIgnoreCase));
            return rank < 0 ? preferred.Length : rank;
        });
    }

    /// <summary>
    /// The first SoundFont that exists: the environment variable, then
    /// <paramref name="configured"/> (files or folders, in order), then the system's.
    /// </summary>
    public static string? Find(params string?[] configured)
    {
        IEnumerable<string?> candidates = [Environment.GetEnvironmentVariable(Variable), .. configured];
        foreach (string? c in candidates)
        {
            if (string.IsNullOrEmpty(c))
            {
                continue;
            }
            if (File.Exists(c) && c.EndsWith(".sf2", StringComparison.OrdinalIgnoreCase))
            {
                return c;
            }
            if (Directory.Exists(c) && Directory.EnumerateFiles(c, "*.sf2").FirstOrDefault() is { } inFolder)
            {
                return inFolder;
            }
        }
        return SystemCandidates().FirstOrDefault();
    }
}
