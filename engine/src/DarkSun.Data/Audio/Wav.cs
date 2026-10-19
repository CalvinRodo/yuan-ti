using System.Buffers.Binary;
using System.Text;

namespace DarkSun.Data.Audio;

/// <summary>
/// WAV files, the form mods use for sound effects: <c>ModTool export</c>
/// writes a <c>BVOC</c> as 16-bit mono PCM, with a <c>smpl</c> chunk for its
/// repeated part; a mod's <c>BVOC/n.wav</c> (8- or 16-bit PCM, mono or
/// stereo, any rate) is read back with that loop and stored as a VOC
/// (<see cref="Voc.Encode"/>).
/// </summary>
public static class Wav
{
    public static byte[] Encode(VocSound sound)
    {
        using var o = new MemoryStream();
        using var w = new BinaryWriter(o);
        bool loops = sound.Loops;
        int dataBytes = sound.Samples.Length * 2;
        w.Write("RIFF"u8);
        w.Write(0); // filled in below
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1); // PCM
        w.Write((short)1); // mono
        w.Write(sound.SampleRate);
        w.Write(sound.SampleRate * 2);
        w.Write((short)2);
        w.Write((short)16);
        if (loops)
        {
            // smpl: manufacturer, product, period, MIDI note, pitch fraction, SMPTE format and offset, 1 loop, no extra data.
            w.Write("smpl"u8);
            w.Write(36 + 24);
            w.Write(0);
            w.Write(0);
            w.Write(1_000_000_000 / Math.Max(1, sound.SampleRate));
            w.Write(60);
            w.Write(0);
            w.Write(0);
            w.Write(0);
            w.Write(1);
            w.Write(0);
            // The loop: id, type (forward), first and last sample, fraction, play count (0 for ever).
            w.Write(0);
            w.Write(0);
            w.Write(sound.LoopStart);
            w.Write(sound.LoopEnd - 1);
            w.Write(0);
            w.Write(sound.LoopCount < 0 ? 0 : sound.LoopCount);
        }
        w.Write("data"u8);
        w.Write(dataBytes);
        foreach (short s in sound.Samples)
        {
            w.Write(s);
        }
        w.Flush();
        byte[] bytes = o.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);
        return bytes;
    }

    /// <exception cref="InvalidDataException">Not a PCM WAV file this reads.</exception>
    public static VocSound Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || !data.StartsWith("RIFF"u8) || !data[8..].StartsWith("WAVE"u8))
        {
            throw new InvalidDataException("not a WAV file");
        }
        int rate = 0, channels = 0, bits = 0;
        short[]? samples = null;
        int loopStart = -1, loopEnd = -1, loopCount = 0;
        int at = 12;
        while (at + 8 <= data.Length)
        {
            string id = Encoding.ASCII.GetString(data.Slice(at, 4));
            int size = BinaryPrimitives.ReadInt32LittleEndian(data[(at + 4)..]);
            if (size < 0)
            {
                break;
            }
            ReadOnlySpan<byte> chunk = data.Slice(at + 8, Math.Min(size, data.Length - at - 8));
            switch (id)
            {
                case "fmt " when chunk.Length >= 16:
                    int format = BinaryPrimitives.ReadUInt16LittleEndian(chunk);
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..]);
                    rate = BinaryPrimitives.ReadInt32LittleEndian(chunk[4..]);
                    bits = BinaryPrimitives.ReadUInt16LittleEndian(chunk[14..]);
                    if (format != 1 && !(format == 0xFFFE && chunk.Length >= 40))
                    {
                        throw new InvalidDataException($"WAV format {format}: save it as plain PCM");
                    }
                    if (bits is not (8 or 16) || channels is < 1 or > 2)
                    {
                        throw new InvalidDataException($"{bits}-bit, {channels} channels: save it as 8- or 16-bit PCM, mono or stereo");
                    }
                    break;
                case "data" when channels > 0:
                    samples = Samples(chunk, bits, channels);
                    break;
                case "smpl" when chunk.Length >= 36 + 24 && BinaryPrimitives.ReadInt32LittleEndian(chunk[28..]) > 0:
                    ReadOnlySpan<byte> loop = chunk[36..];
                    loopStart = BinaryPrimitives.ReadInt32LittleEndian(loop[8..]);
                    loopEnd = BinaryPrimitives.ReadInt32LittleEndian(loop[12..]) + 1;
                    int plays = BinaryPrimitives.ReadInt32LittleEndian(loop[20..]);
                    loopCount = plays == 0 ? -1 : plays;
                    break;
            }
            at += 8 + size + (size & 1);
        }
        if (samples is null || rate <= 0)
        {
            throw new InvalidDataException("WAV file without fmt and data chunks");
        }
        if (loopStart < 0 || loopEnd > samples.Length || loopEnd <= loopStart)
        {
            (loopStart, loopEnd, loopCount) = (-1, -1, 0);
        }
        return new VocSound(rate, samples, loopStart, loopEnd, loopCount);
    }

    private static short[] Samples(ReadOnlySpan<byte> pcm, int bits, int channels)
    {
        int bytes = bits / 8;
        int frames = pcm.Length / (bytes * channels);
        var samples = new short[frames];
        for (int f = 0; f < frames; f++)
        {
            int sum = 0;
            for (int c = 0; c < channels; c++)
            {
                int at = (f * channels + c) * bytes;
                sum += bits == 16 ? BinaryPrimitives.ReadInt16LittleEndian(pcm[at..]) : (pcm[at] - 128) << 8;
            }
            samples[f] = (short)(sum / channels);
        }
        return samples;
    }
}
