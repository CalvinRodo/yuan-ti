using DarkSun.Data.Audio;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// Sound effects (an autoload): the game's sound numbers, from <c>SCMD</c>
/// records and GPL's SOUND command, played as the <c>BVOC</c> samples of
/// <c>RESOURCE.GFF</c> (<see cref="SoundEffects"/>; mods can replace them).
/// Samples are decoded once and played on a few voices; when all are busy the
/// oldest stops. <c>[audio] sound_volume</c> (0–1) in <c>user://settings.cfg</c>
/// sets the loudness; 0 turns sound effects off.
/// </summary>
public partial class Sounds : Node
{
    private const int Voices = 8;

    /// <summary>The same sound again within this many seconds is not started twice (e.g. several torches in step).</summary>
    private const double RepeatGuard = 0.05;

    private readonly AudioStreamPlayer[] _voices = new AudioStreamPlayer[Voices];
    private readonly Dictionary<int, (AudioStreamWav Stream, double Seconds)?> _streams = [];
    private readonly double[] _stopAt = new double[Voices];
    private readonly Dictionary<int, double> _lastPlayed = [];
    private int _next;
    private double _clock;

    public static Sounds? Instance { get; private set; }

    /// <summary>Loudness of sound effects, 0–1.</summary>
    public float Volume { get; set; } = 1f;

    /// <summary>Sounds started, for checks.</summary>
    public int Played { get; private set; }

    public override void _EnterTree() => Instance = this;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        Volume = Settings.SoundVolume;
        for (int i = 0; i < Voices; i++)
        {
            _voices[i] = new AudioStreamPlayer();
            AddChild(_voices[i]);
        }
    }

    public override void _Process(double delta)
    {
        _clock += delta;
        // A repeating sample stops when its repeats are done.
        for (int i = 0; i < Voices; i++)
        {
            if (_voices[i].Playing && _clock >= _stopAt[i])
            {
                _voices[i].Stop();
            }
        }
    }

    /// <summary>Play sound effect <paramref name="id"/>, if it is a sample the game has.</summary>
    public void Play(int id)
    {
        if (Volume <= 0 || Stream(id) is not { } entry)
        {
            return;
        }
        var (stream, seconds) = entry;
        if (_lastPlayed.TryGetValue(id, out double last) && _clock - last < RepeatGuard)
        {
            return;
        }
        _lastPlayed[id] = _clock;
        int free = Array.FindIndex(_voices, v => !v.Playing);
        int index = free >= 0 ? free : _next++ % Voices;
        AudioStreamPlayer voice = _voices[index];
        _stopAt[index] = _clock + seconds;
        voice.Stream = stream;
        voice.VolumeDb = Mathf.LinearToDb(Volume);
        voice.Play();
        Played++;
    }

    public void Play(IEnumerable<int> ids)
    {
        foreach (int id in ids)
        {
            Play(id);
        }
    }

    private (AudioStreamWav Stream, double Seconds)? Stream(int id)
    {
        if (_streams.TryGetValue(id, out var cached))
        {
            return cached;
        }
        (AudioStreamWav, double)? stream = null;
        if (SoundEffects.Bvoc(id) is int number && Session.Install is not null
            && Session.Open("RESOURCE.GFF").TryRead("BVOC", number, out var data))
        {
            try
            {
                VocSound sound = Voc.Decode(data.Span);
                stream = (ToStream(sound), sound.Seconds);
            }
            catch (InvalidDataException e)
            {
                GD.PrintErr($"Sounds: BVOC {number}: {e.Message}");
            }
        }
        _streams[id] = stream;
        return stream;
    }

    private static AudioStreamWav ToStream(VocSound sound)
    {
        var bytes = new byte[sound.Samples.Length * 2];
        Buffer.BlockCopy(sound.Samples, 0, bytes, 0, bytes.Length);
        var stream = new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = sound.SampleRate,
            Stereo = false,
            Data = bytes,
        };
        if (sound.Loops)
        {
            stream.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
            stream.LoopBegin = sound.LoopStart;
            stream.LoopEnd = sound.LoopEnd;
        }
        return stream;
    }
}
