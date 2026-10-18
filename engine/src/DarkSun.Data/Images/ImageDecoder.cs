using System.Buffers.Binary;

namespace DarkSun.Data.Images;

/// <summary>
/// Decoder for the image resources (<c>BMP </c>, <c>TILE</c>, <c>ICON</c>,
/// <c>PORT</c>, <c>WALL</c>, …). Port of <c>ImageReading</c> in
/// <c>src/image.py</c>, itself from John Glassmyer's ImageReading.java; the
/// format is in <c>.github/instructions/gff-data-reference.md</c> §4a.
/// Edge cases (repeated rows, runs past the row end) behave as in the Python
/// decoder, so that the two can be compared.
/// </summary>
public static class ImageDecoder
{
    private const byte NoMoreRows = 0xFF;
    private const byte Column256Flag = 0x01;
    private const byte LastRunFlag = 0x80;

    /// <summary>
    /// All frames of an image resource: int32 size (ignored), uint16 frame
    /// count, uint32 offset per frame.
    /// </summary>
    /// <exception cref="InvalidDataException">The data is truncated or inconsistent.</exception>
    public static IReadOnlyList<ImageFrame> ReadFrames(ReadOnlySpan<byte> data)
    {
        try
        {
            int count = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
            var frames = new List<ImageFrame>(count);
            for (int i = 0; i < count; i++)
            {
                int offset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[(6 + i * 4)..]));
                frames.Add(ReadFrame(data, offset));
            }
            return frames;
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
        {
            throw new InvalidDataException("image data is truncated or inconsistent", e);
        }
    }

    private static ImageFrame ReadFrame(ReadOnlySpan<byte> data, int start)
    {
        if (data[start + 4] == 0xFF && data.Length >= start + 9)
        {
            ReadOnlySpan<byte> tag = data.Slice(start + 5, 4);
            if (tag.SequenceEqual("PLAN"u8))
            {
                return ReadPlanar(data, start, rle: false);
            }
            if (tag.SequenceEqual("PLNR"u8))
            {
                return ReadPlanar(data, start, rle: true);
            }
        }
        return ReadRowBased(data, start);
    }

    /// <summary>
    /// Row-based RLE: per row, a row number (0xFF ends the frame) and runs of
    /// startX, flags (bit 0: startX + 256, bit 7: last run), uncompressed and
    /// compressed lengths, then the compressed bytes. A repeated row number
    /// replaces that row's runs.
    /// </summary>
    private static ImageFrame ReadRowBased(ReadOnlySpan<byte> data, int start)
    {
        int width = BinaryPrimitives.ReadUInt16LittleEndian(data[start..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(data[(start + 2)..]);
        int pos = start + 4;
        var rows = new Dictionary<int, List<(int X, List<byte> Pixels)>>();

        while (rows.Count < height)
        {
            int row = data[pos++];
            if (row == NoMoreRows)
            {
                break;
            }
            if (row >= height)
            {
                throw new InvalidDataException($"row number 0x{row:X} >= frame height 0x{height:X}");
            }

            var runs = new List<(int, List<byte>)>();
            while (true)
            {
                int x = data[pos];
                byte flags = data[pos + 1];
                int uncompressedLength = data[pos + 2];
                int compressedLength = data[pos + 3];
                pos += 4;
                if ((flags & Column256Flag) != 0)
                {
                    x += 256;
                }
                // Like a Python slice, a run cut short by the end of the data is
                // read as far as it goes.
                int from = Math.Min(pos, data.Length);
                int available = Math.Min(compressedLength, data.Length - from);
                runs.Add((x, Uncompress(data.Slice(from, available), uncompressedLength)));
                pos += compressedLength;
                if ((flags & LastRunFlag) != 0)
                {
                    break;
                }
            }
            rows[row] = runs;
        }

        var pixels = new byte[width * height];
        var opaque = new bool[width * height];
        foreach (var (row, runs) in rows)
        {
            foreach (var (x, run) in runs)
            {
                // Runs are written into the flat buffer, so one longer than the
                // row continues on the next row, as in image.py.
                int at = row * width + x;
                if (at + run.Count > pixels.Length)
                {
                    throw new InvalidDataException($"run at row {row}, x {x} extends past the frame");
                }
                run.CopyTo(pixels, at);
                opaque.AsSpan(at, run.Count).Fill(true);
            }
        }
        return new ImageFrame(width, height, pixels, opaque);
    }

    /// <summary>
    /// In-run RLE: an even code byte is followed by code/2 + 1 literal bytes;
    /// an odd one by a byte repeated code/2 + 1 times. A run that overflows the
    /// declared length is kept at its actual length, as in image.py.
    /// </summary>
    private static List<byte> Uncompress(ReadOnlySpan<byte> input, int length)
    {
        var output = new List<byte>(new byte[length]);
        int o = 0;
        int i = 0;
        while (i < input.Length)
        {
            int code = input[i++];
            int count = code / 2 + 1;
            if (code % 2 == 0)
            {
                ReadOnlySpan<byte> literal = input.Slice(i, Math.Min(count, input.Length - i));
                AssignSlice(output, o, o + count, literal);
                i += count;
                o += count;
            }
            else
            {
                byte value = input[i++];
                for (int n = 0; n < count; n++)
                {
                    output[o++] = value;
                }
            }
        }
        return output;
    }

    /// <summary>Python's <c>list[start:end] = values</c>, which can grow the list.</summary>
    private static void AssignSlice(List<byte> list, int start, int end, ReadOnlySpan<byte> values)
    {
        start = Math.Min(start, list.Count);
        end = Math.Clamp(end, start, list.Count);
        if (end - start == values.Length)
        {
            for (int k = 0; k < values.Length; k++)
            {
                list[start + k] = values[k];
            }
            return;
        }
        list.RemoveRange(start, end - start);
        list.InsertRange(start, values.ToArray());
    }

    /// <summary>
    /// Planar formats: 0xFF, tag, bits per symbol (0: empty frame), a
    /// dictionary of 2^bits palette indices, then a big-endian bit stream of
    /// symbols, one per pixel row-major. Dictionary value 0 is transparent.
    /// PLAN stores every symbol; PLNR (<paramref name="rle"/>) encodes repeats:
    /// symbol 0 is followed by a count c (repeat the previous value c + 2 more
    /// times) or by 0 (a literal 0).
    /// </summary>
    private static ImageFrame ReadPlanar(ReadOnlySpan<byte> data, int start, bool rle)
    {
        int width = BinaryPrimitives.ReadUInt16LittleEndian(data[start..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(data[(start + 2)..]);
        var pixels = new byte[width * height];
        var opaque = new bool[width * height];

        int bits = data[start + 9];
        if (bits == 0)
        {
            return new ImageFrame(width, height, pixels, opaque);
        }
        if (bits > 16)
        {
            throw new InvalidDataException($"{bits} bits per symbol");
        }
        int dictionarySize = 1 << bits;
        ReadOnlySpan<byte> dictionary = data.Slice(start + 10, Math.Min(dictionarySize, data.Length - start - 10));
        var reader = new BigEndianBitReader(data, start + 10 + dictionarySize);

        int last = 0;
        int remaining = 0;
        for (int i = 0; i < pixels.Length; i++)
        {
            int symbol;
            if (!rle)
            {
                symbol = reader.Read(bits);
            }
            else
            {
                if (remaining == 0)
                {
                    int first = reader.Read(bits);
                    if (first != 0)
                    {
                        last = first;
                        remaining = 1;
                    }
                    else
                    {
                        int second = reader.Read(bits);
                        if (second == 0)
                        {
                            last = 0;
                            remaining = 1;
                        }
                        else
                        {
                            remaining = second + 2;
                        }
                    }
                }
                remaining--;
                symbol = last;
            }

            byte value = dictionary[symbol];
            if (value != 0)
            {
                pixels[i] = value;
                opaque[i] = true;
            }
        }
        return new ImageFrame(width, height, pixels, opaque);
    }

    /// <summary>Reads 1–16 bits at a time, most significant bit first.</summary>
    private ref struct BigEndianBitReader(ReadOnlySpan<byte> data, int byteOffset)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _byte = byteOffset;
        private int _bit;

        public int Read(int count)
        {
            int value = 0;
            int filled = 0;
            while (filled < count)
            {
                int needed = count - filled;
                int take = Math.Min(needed, 8 - _bit);
                int shift = 8 - _bit - take;
                int bits = (_data[_byte] >> shift) & ((1 << take) - 1);
                value |= bits << (needed - take);
                filled += take;
                _bit += take;
                if (_bit == 8)
                {
                    _byte++;
                    _bit = 0;
                }
            }
            return value;
        }
    }
}
