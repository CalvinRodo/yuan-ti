using DarkSun.Game.Audio;
using Godot;
using MeltySynth;

namespace DarkSun.Engine;

/// <summary>
/// The music, playing across scenes (an autoload). Sequences are the
/// General MIDI versions (<c>GSEQ</c>) from <c>RESOURCE.GFF</c> (1–23: the
/// regions' and menus' music) or <c>CINE.GFF</c> (26–29: the cinematics'),
/// synthesized with a SoundFont found by <see cref="SoundFontLocator"/>: the
/// <c>DARKSUN_SOUNDFONT</c> variable, the <c>audio/soundfont</c> setting in
/// <c>user://settings.cfg</c>, a <c>.sf2</c> in the user data or game folder,
/// then the system's. With none, the game is silent.
/// </summary>
public partial class Music : Node
{
    /// <summary>DSUN.EXE changes track with a 5-second fade out and in (<c>1A0A:0672</c>).</summary>
    public const double ChangeFade = 5.0;

    private const int SampleRate = 44100;
    private const string SettingsPath = "user://settings.cfg";

    private AudioStreamPlayer _output = null!;
    private AudioStreamGeneratorPlayback? _playback;
    private MusicPlayer? _player;
    private float[] _left = [], _right = [];
    private Vector2[] _frames = [];

    public static Music? Instance { get; private set; }

    /// <summary>The track playing (file and number), if any.</summary>
    public (string File, int Number)? Current { get; private set; }

    /// <summary>The SoundFont in use, or why there is none.</summary>
    public string Status { get; private set; } = "";

    public override void _EnterTree() => Instance = this;

    private float _volume = 1f;

    /// <summary>Loudness of the music, 0–1 (<c>[audio] music_volume</c>); 0 is silent.</summary>
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (_output is not null)
            {
                _output.VolumeDb = _volume <= 0 ? -80 : -2 + Mathf.LinearToDb(_volume);
            }
        }
    }

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        var stream = new AudioStreamGenerator { MixRate = SampleRate, BufferLength = 0.2f };
        _output = new AudioStreamPlayer { Stream = stream, VolumeDb = -2 };
        Volume = Settings.MusicVolume;
        AddChild(_output);
        _output.Play();
        _playback = (AudioStreamGeneratorPlayback)_output.GetStreamPlayback();
        LoadSoundFont();
    }

    private void LoadSoundFont()
    {
        var config = new ConfigFile();
        config.Load(SettingsPath);
        string? configured = config.GetValue("audio", "soundfont", "").AsString();
        string? path = SoundFontLocator.Find(configured, ProjectSettings.GlobalizePath("user://"), Session.Install?.Directory);
        if (path is null)
        {
            Status = "no SoundFont (.sf2) found: set DARKSUN_SOUNDFONT or audio/soundfont in settings.cfg";
            GD.Print($"Music: {Status}");
            return;
        }
        try
        {
            _player = new MusicPlayer(new SoundFont(path), SampleRate);
            Status = path;
            GD.Print($"Music: SoundFont {path}");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Status = $"can't load {path}: {e.Message}";
            GD.PrintErr($"Music: {Status}");
        }
    }

    /// <summary>
    /// Play <c>GSEQ</c> <paramref name="number"/> from <paramref name="file"/>:
    /// at once (fading in over <paramref name="fadeIn"/> seconds), or, with
    /// <paramref name="change"/>, after fading out what plays. Asking for the
    /// track already playing does nothing.
    /// </summary>
    public void Play(string file, int number, double fadeIn = 0, bool change = false)
    {
        if (Current == (file, number) && _player?.IsPlaying == true)
        {
            return;
        }
        Current = (file, number);
        if (_player is null || Session.Install?.FindFile(file) is null
            || !Session.Open(file).TryRead("GSEQ", number, out var xmi))
        {
            return;
        }
        if (change)
        {
            _player.Change(xmi.ToArray(), fadeIn);
        }
        else
        {
            _player.Play(xmi.ToArray(), fadeIn);
        }
    }

    /// <summary>A track change as DSUN.EXE makes them: fade out, then in, over 5 s each.</summary>
    public void ChangeTo(string file, int number) => Play(file, number, ChangeFade, change: true);

    /// <summary>
    /// The music level (<c>361A:0000</c>): section <paramref name="level"/> of
    /// the track playing, if it has that many (<see cref="MusicTracks.Levels"/>).
    /// </summary>
    public void SetLevel(int level)
    {
        if (Current is { } track && level <= MusicTracks.Levels(track.Number))
        {
            _player?.SetLevel(level);
        }
    }

    public void FadeIn(double seconds) => _player?.FadeIn(seconds);

    public void FadeOut(double seconds) => _player?.FadeOut(seconds);

    public void Stop()
    {
        Current = null;
        _player?.Stop();
    }

    public override void _Process(double delta)
    {
        if (_playback is null)
        {
            return;
        }
        int frames = _playback.GetFramesAvailable();
        if (frames <= 0)
        {
            return;
        }
        if (_left.Length < frames)
        {
            _left = new float[frames];
            _right = new float[frames];
            _frames = new Vector2[frames];
        }
        if (_player is null)
        {
            Array.Clear(_left, 0, frames);
            Array.Clear(_right, 0, frames);
        }
        else
        {
            _player.Render(_left.AsSpan(0, frames), _right.AsSpan(0, frames));
        }
        for (int i = 0; i < frames; i++)
        {
            _frames[i] = new Vector2(_left[i], _right[i]);
        }
        _playback.PushBuffer(frames == _frames.Length ? _frames : _frames[..frames]);
    }
}
