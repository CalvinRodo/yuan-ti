using System.Buffers.Binary;

namespace DarkSun.Data.Audio;

/// <summary>
/// A decoded sound: 16-bit mono samples, and a part to repeat
/// (<see cref="LoopStart"/> to <see cref="LoopEnd"/>): <see cref="LoopCount"/>
/// more times, or for ever if it is −1.
/// </summary>
public sealed record VocSound(int SampleRate, short[] Samples, int LoopStart = -1, int LoopEnd = -1, int LoopCount = 0)
{
    public bool Loops => LoopStart >= 0;

    /// <summary>How long it plays, repeats included; infinity if it repeats for ever.</summary>
    public double Seconds => LoopCount < 0 ? double.PositiveInfinity
        : (Samples.Length + (double)Math.Max(0, LoopEnd - LoopStart) * LoopCount) / SampleRate;
}

/// <summary>
/// Creative Voice (<c>.VOC</c>) files: the game's sound effects (<c>BVOC</c>
/// in <c>RESOURCE.GFF</c>; all 8000 Hz, 8-bit unsigned). A 26-byte header
/// ("Creative Voice File", 0x1A, header size, version, check), then blocks of
/// a type byte and a 24-bit length: 1 sound data (time constant, codec),
/// 2 more data, 3 silence, 6/7 repeat start (count; 0xFFFF for ever) and end,
/// 9 sound data in the newer form (rate, bits, channels, codec); 0 ends.
/// Block 8 (extended rate, for stereo) is not used by the game and is skipped.
/// </summary>
public static class Voc
{
    public static ReadOnlySpan<byte> Signature => "Creative Voice File\x1a"u8;

    /// <exception cref="InvalidDataException">Not a VOC file.</exception>
    public static VocSound Decode(ReadOnlySpan<byte> data)
    {
        if (!data.StartsWith(Signature) || data.Length < 26)
        {
            throw new InvalidDataException("not a Creative Voice file");
        }
        var samples = new List<short>();
        int rate = 0;
        int bits = 8;
        int loopStart = -1, loopEnd = -1, loopCount = 0, repeatStart = -1, repeatCount = 0;
        int at = BinaryPrimitives.ReadUInt16LittleEndian(data[20..]);
        while (at < data.Length)
        {
            int type = data[at];
            if (type == 0 || at + 4 > data.Length)
            {
                break;
            }
            int size = data[at + 1] | data[at + 2] << 8 | data[at + 3] << 16;
            int body = at + 4;
            ReadOnlySpan<byte> block = data.Slice(body, Math.Min(size, data.Length - body));
            switch (type)
            {
                case 1 when block.Length >= 2:
                    rate = rate == 0 ? 1_000_000 / (256 - block[0]) : rate;
                    bits = 8;
                    if (block[1] == 0)
                    {
                        AddPcm(samples, block[2..], 8);
                    }
                    break;
                case 2:
                    AddPcm(samples, block, bits);
                    break;
                case 3 when block.Length >= 3:
                    int silence = BinaryPrimitives.ReadUInt16LittleEndian(block) + 1;
                    rate = rate == 0 ? 1_000_000 / (256 - block[2]) : rate;
                    samples.AddRange(new short[silence]);
                    break;
                case 6 when block.Length >= 2:
                    repeatStart = samples.Count;
                    repeatCount = BinaryPrimitives.ReadUInt16LittleEndian(block);
                    break;
                case 7 when repeatStart >= 0:
                    // The game's two (BVOC 58 and 70) repeat a few seconds 100 and 200 times.
                    (loopStart, loopEnd, loopCount) = (repeatStart, samples.Count, repeatCount == 0xFFFF ? -1 : repeatCount);
                    repeatStart = -1;
                    break;
                case 9 when block.Length >= 12:
                    rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(block);
                    bits = block[4];
                    if (block[5] == 1 && BinaryPrimitives.ReadUInt16LittleEndian(block[6..]) is 0 or 4)
                    {
                        AddPcm(samples, block[12..], bits);
                    }
                    break;
            }
            at = body + size;
        }
        return new VocSound(rate == 0 ? 8000 : rate, [.. samples], loopStart, loopEnd, loopCount);
    }

    /// <summary>
    /// <paramref name="sound"/> as a VOC file (for a mod's WAV): 16-bit PCM in
    /// block 9, its repeated part between blocks 6 and 7, as the game's own
    /// repeating samples are laid out; <see cref="Decode"/> reads it back the same.
    /// </summary>
    public static byte[] Encode(VocSound sound)
    {
        using var o = new MemoryStream();
        using var w = new BinaryWriter(o);
        w.Write(Signature);
        w.Write((ushort)26);
        w.Write((ushort)0x0114); // version 1.20, which has block 9
        w.Write((ushort)(~0x0114 + 0x1234));
        short[] s = sound.Samples;
        int start = sound.Loops ? sound.LoopStart : s.Length;
        int end = sound.Loops ? sound.LoopEnd : s.Length;
        // Block 9: rate, bits, channels, codec 4 (16-bit PCM), 4 reserved bytes, then the samples before any loop.
        Block(w, 9, 12 + start * 2);
        w.Write(sound.SampleRate);
        w.Write((byte)16);
        w.Write((byte)1);
        w.Write((ushort)4);
        w.Write(0);
        Pcm(w, s.AsSpan(0, start));
        if (sound.Loops)
        {
            Block(w, 6, 2);
            w.Write((ushort)(sound.LoopCount < 0 ? 0xFFFF : sound.LoopCount));
            Block(w, 2, (end - start) * 2);
            Pcm(w, s.AsSpan(start, end - start));
            Block(w, 7, 0);
            if (end < s.Length)
            {
                Block(w, 2, (s.Length - end) * 2);
                Pcm(w, s.AsSpan(end));
            }
        }
        w.Write((byte)0);
        w.Flush();
        return o.ToArray();
    }

    private static void Block(BinaryWriter w, byte type, int size)
    {
        w.Write(type);
        w.Write((byte)size);
        w.Write((byte)(size >> 8));
        w.Write((byte)(size >> 16));
    }

    private static void Pcm(BinaryWriter w, ReadOnlySpan<short> samples)
    {
        foreach (short sample in samples)
        {
            w.Write(sample);
        }
    }

    private static void AddPcm(List<short> samples, ReadOnlySpan<byte> pcm, int bits)
    {
        if (bits == 16)
        {
            for (int i = 0; i + 1 < pcm.Length; i += 2)
            {
                samples.Add(BinaryPrimitives.ReadInt16LittleEndian(pcm[i..]));
            }
            return;
        }
        foreach (byte b in pcm)
        {
            samples.Add((short)((b - 128) << 8));
        }
    }
}

/// <summary>
/// The game's sound effect numbers, as <c>SCMD</c> records (byte 7) and GPL's
/// SOUND command give them to <c>play_sound_effect</c> (<c>191F:0A6F</c>,
/// <c>3172:04F1</c>). The sound table (<c>3A98:08A2</c>, 182 entries of 6
/// bytes) makes 0–130 digitised samples (type 5): number n plays <c>BVOC</c>
/// n (0 also plays 1; 0 in an <c>SCMD</c> record means none). 131–180 are
/// type 4, MIDI notes for the music driver, not played here.
/// </summary>
public static class SoundEffects
{
    public const int LastSample = 130;

    /// <summary>The <c>BVOC</c> that sound <paramref name="id"/> plays, or null.</summary>
    public static int? Bvoc(int id) => id switch
    {
        0 => 1,
        > 0 and <= LastSample => id,
        _ => null,
    };
}
