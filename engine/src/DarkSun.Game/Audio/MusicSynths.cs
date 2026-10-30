using AIL2;
using AIL2.Formats;
using AIL2.Rendering;
using DarkSun.Data.Audio;
using MeltySynth;

namespace DarkSun.Game.Audio;

/// <summary>What sounds the music's MIDI messages for <see cref="MusicPlayer"/>.</summary>
public interface IMusicSynth
{
    /// <summary>Get ready to play a sequence (the FM drivers install the timbres it names).</summary>
    void Prepare(ReadOnlySpan<byte> xmi);

    void Send(MidiMessage message);

    void Render(Span<float> left, Span<float> right);

    /// <summary>Silence everything and forget the channels' state.</summary>
    void Reset();
}

/// <summary>General MIDI through a SoundFont (MeltySynth).</summary>
public sealed class SoundFontSynth(SoundFont soundFont, int sampleRate) : IMusicSynth
{
    private readonly Synthesizer _synth = new(soundFont, new SynthesizerSettings(sampleRate) { EnableReverbAndChorus = true });

    public void Prepare(ReadOnlySpan<byte> xmi)
    {
    }

    public void Send(MidiMessage m) => _synth.ProcessMidiMessage(m.Channel, m.Command, m.Data1, m.Data2);

    public void Render(Span<float> left, Span<float> right) => _synth.Render(left, right);

    public void Reset() => _synth.Reset();
}

/// <summary>The FM cards DSUN.EXE's sound set-up offers that <see cref="FmSynth"/> plays.</summary>
public enum FmDevice
{
    /// <summary>Ad Lib (<c>ADV</c> 1): one OPL2, nine voices.</summary>
    AdLib,

    /// <summary>Sound Blaster Pro FM on OPL3 (<c>ADV</c> 11).</summary>
    SoundBlasterPro2,
}

/// <summary>
/// The original's FM music: the Miles AIL AdLib and Sound Blaster drivers
/// (AIL2.NET) on an emulated OPL chip (Nuked-OPL3), with the timbres of
/// <c>STDPATCH.AD</c>, the global timbre library DSUN.EXE loads for them
/// (<c>3256:00D3</c>). There are no FM versions of the tracks (<c>FSEQ</c>
/// isn't shipped); DSUN.EXE plays FM cards the General MIDI version
/// (<c>3256:00D3</c> turns device types 1 and 2 into 4, <c>GSEQ</c>), and so
/// does this. Before a sequence plays, the timbres in its <c>TIMB</c> chunk
/// are installed, as AIL's applications do. Use it all from one thread (AIL's rule).
/// </summary>
public sealed class FmSynth : IMusicSynth, IDisposable
{
    /// <summary>The chip is about half as loud as a SoundFont (RMS over the tracks' first 20 s); this evens them.</summary>
    public const float Gain = 2f;

    private readonly MusicConfiguration _configuration;
    private readonly TimbreBank _bank;
    private readonly int _sampleRate;
    private AILRenderer _renderer;
    private short[] _pcm = [];

    /// <param name="timbreBank">An AIL global timbre library (<c>STDPATCH.AD</c>).</param>
    /// <exception cref="InvalidDataException">The bank can't be read.</exception>
    public FmSynth(ReadOnlySpan<byte> timbreBank, FmDevice device, int sampleRate)
    {
        _bank = TimbreBank.Load(timbreBank);
        _configuration = device switch
        {
            FmDevice.SoundBlasterPro2 => new MusicConfiguration(MusicDevice.SoundBlaster, OplChipMode.Opl3),
            _ => new MusicConfiguration(MusicDevice.AdLib, OplChipMode.Opl2),
        };
        _sampleRate = sampleRate;
        _renderer = Create();
    }

    private AILRenderer Create() => new(_configuration, null, _sampleRate);

    public void Prepare(ReadOnlySpan<byte> xmi)
    {
        AILSession session = _renderer.Session;
        foreach (var (patch, bank) in XmidiSequencer.Timbres(xmi))
        {
            if (!session.HasTimbre((byte)bank, (byte)patch) && _bank.TryGet((byte)bank, (byte)patch, out ReadOnlyMemory<byte> timbre))
            {
                session.InstallTimbre((byte)bank, (byte)patch, timbre);
            }
        }
    }

    public void Send(MidiMessage m) =>
        _renderer.Session.SendChannelMessage((byte)(m.Command | m.Channel), (byte)m.Data1, (byte)m.Data2);

    public void Render(Span<float> left, Span<float> right)
    {
        if (_pcm.Length < left.Length * 2)
        {
            _pcm = new short[left.Length * 2];
        }
        Span<short> pcm = _pcm.AsSpan(0, left.Length * 2);
        _renderer.Render(pcm);
        for (int i = 0; i < left.Length; i++)
        {
            left[i] = pcm[2 * i] * (Gain / 32768f);
            right[i] = pcm[2 * i + 1] * (Gain / 32768f);
        }
    }

    /// <summary>A fresh driver and chip (installed timbres go too; <see cref="Prepare"/> brings them back).</summary>
    public void Reset()
    {
        _renderer.Dispose();
        _renderer = Create();
    }

    public void Dispose() => _renderer.Dispose();
}
