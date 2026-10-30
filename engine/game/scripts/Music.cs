using DarkSun.Game.Audio;
using Godot;
using MeltySynth;

namespace DarkSun.Engine;

/// <summary>
/// The music, playing across scenes (an autoload). Sequences are the
/// General MIDI versions (<c>GSEQ</c>) from <c>RESOURCE.GFF</c> (1–23: the
/// regions' and menus' music) or <c>CINE.GFF</c> (26–29: the cinematics'),
/// played on the device <see cref="Settings.MusicDevice"/> names
/// (<c>[audio] music_device</c>):
/// <list type="bullet">
/// <item><c>soundfont</c>: synthesized with a SoundFont found by
/// <see cref="SoundFontLocator"/>: the <c>DARKSUN_SOUNDFONT</c> variable, the
/// <c>audio/soundfont</c> setting, a <c>.sf2</c> in the user data or game
/// folder, then the system's;</item>
/// <item><c>adlib</c> or <c>opl3</c>: the original's FM sound, its AdLib or
/// Sound Blaster Pro driver on an emulated chip with the game's
/// <c>STDPATCH.AD</c> timbres (<see cref="FmSynth"/>);</item>
/// <item><c>auto</c> (the default): a SoundFont if there is one, else AdLib.</item>
/// </list>
/// </summary>
public partial class Music : Node
{
    /// <summary>DSUN.EXE changes track with a 5-second fade out and in (<c>1A0A:0672</c>).</summary>
    public const double ChangeFade = 5.0;

    private const int SampleRate = 44100;
    private const string SettingsPath = "user://settings.cfg";

    /// <summary>AIL's global timbre library, which DSUN.EXE loads for the FM cards (<c>3256:00D3</c>).</summary>
    public const string FmTimbres = "STDPATCH.AD";

    private AudioStreamPlayer _output = null!;
    private AudioStreamGeneratorPlayback? _playback;
    private MusicPlayer? _player;
    private float[] _left = [], _right = [];
    private Vector2[] _frames = [];

    public static Music? Instance { get; private set; }

    /// <summary>The track playing (file and number), if any.</summary>
    public (string File, int Number)? Current { get; private set; }

    /// <summary>What plays the music (a SoundFont's path, or the FM card), or why nothing does.</summary>
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
        UseDevice(Settings.MusicDevice);
    }

    /// <summary>The device the music is to play on (<see cref="Settings.MusicDevice"/>).</summary>
    public string? Device { get; private set; }

    // The game folder the device was loaded with (the FM timbres are in it; it may come later than the device).
    private string? _loadedWith;

    /// <summary>Play on <paramref name="device"/> from now on, starting the track again there.</summary>
    public void UseDevice(string device)
    {
        Device = device;
        _loadedWith = Session.Install?.Directory;
        _player = device switch
        {
            Settings.AdLib => LoadFm(FmDevice.AdLib),
            Settings.Opl3 => LoadFm(FmDevice.SoundBlasterPro2),
            Settings.SoundFontDevice => LoadSoundFont(),
            _ => LoadSoundFont(quiet: true) ?? LoadFm(FmDevice.AdLib),
        };
        if (_player is not null || _loadedWith is not null)
        {
            GD.Print($"Music: {Status}");
        }
        if (Current is { } track)
        {
            Current = null;
            Play(track.File, track.Number, fadeIn: 0.5);
        }
    }

    private MusicPlayer? LoadSoundFont(bool quiet = false)
    {
        var config = new ConfigFile();
        config.Load(SettingsPath);
        string? configured = config.GetValue("audio", "soundfont", "").AsString();
        string? path = SoundFontLocator.Find(configured, ProjectSettings.GlobalizePath("user://"), Session.Install?.Directory);
        if (path is null)
        {
            Status = "no SoundFont (.sf2) found: set DARKSUN_SOUNDFONT or audio/soundfont in settings.cfg";
            return null;
        }
        try
        {
            var player = new MusicPlayer(new SoundFont(path), SampleRate);
            Status = $"SoundFont {path}";
            return player;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Status = $"can't load {path}: {e.Message}";
            if (!quiet)
            {
                GD.PrintErr($"Music: {Status}");
            }
            return null;
        }
    }

    /// <summary>The original's FM music, with the game's timbre library <c>STDPATCH.AD</c>.</summary>
    private MusicPlayer? LoadFm(FmDevice device)
    {
        string? bank = Session.Install?.FindFile(FmTimbres);
        if (bank is null)
        {
            Status = $"no {FmTimbres} in the game folder for the FM music";
            return null;
        }
        try
        {
            var player = new MusicPlayer(new FmSynth(File.ReadAllBytes(bank), device, SampleRate), SampleRate);
            Status = device == FmDevice.AdLib ? "Ad Lib (OPL2)" : "Sound Blaster Pro (OPL3)";
            return player;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Status = $"can't load {bank}: {e.Message}";
            GD.PrintErr($"Music: {Status}");
            return null;
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
        if (_player is null && Device is { } device && Session.Install?.Directory is { } folder && folder != _loadedWith)
        {
            UseDevice(device);
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
