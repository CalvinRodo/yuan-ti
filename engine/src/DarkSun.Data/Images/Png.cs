using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace DarkSun.Data.Images;

/// <summary>
/// Minimal PNG reading and writing, so mods can replace images with PNG files
/// without the libraries depending on Godot. Reads non-interlaced images of
/// every colour type at 8 bits per channel, and palette or grey images at
/// 1, 2, 4 or 8 bits; writes 8-bit RGBA.
/// </summary>
public static class Png
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool IsPng(ReadOnlySpan<byte> data) => data.StartsWith(Signature);

    /// <summary>Decode to RGBA, 4 bytes a pixel.</summary>
    /// <exception cref="InvalidDataException">Not a PNG, or a kind this reader doesn't handle.</exception>
    public static (int Width, int Height, byte[] Rgba) Decode(ReadOnlySpan<byte> data)
    {
        if (!IsPng(data))
        {
            throw new InvalidDataException("not a PNG file");
        }
        int width = 0, height = 0, depth = 0, colorType = 0;
        byte[] palette = [];
        byte[] alphas = [];
        using var compressed = new MemoryStream();
        int at = Signature.Length;
        while (at + 8 <= data.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(data[at..]);
            string type = Encoding.ASCII.GetString(data.Slice(at + 4, 4));
            if (length < 0 || at + 12 + length > data.Length)
            {
                throw new InvalidDataException("PNG chunk runs past the end");
            }
            ReadOnlySpan<byte> body = data.Slice(at + 8, length);
            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(body);
                    height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                    (depth, colorType) = (body[8], body[9]);
                    if (body[12] != 0)
                    {
                        throw new InvalidDataException("interlaced PNG files are not supported; save without interlacing");
                    }
                    break;
                case "PLTE":
                    palette = body.ToArray();
                    break;
                case "tRNS":
                    alphas = body.ToArray();
                    break;
                case "IDAT":
                    compressed.Write(body);
                    break;
            }
            at += 12 + length;
            if (type == "IEND")
            {
                break;
            }
        }
        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException($"PNG colour type {colorType}") };
        if (width <= 0 || height <= 0 || (depth != 8 && !(depth is 1 or 2 or 4 && colorType is 0 or 3)))
        {
            throw new InvalidDataException($"PNG of {width}×{height}, colour type {colorType}, depth {depth} is not supported");
        }

        compressed.Position = 0;
        using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
        int bitsPerPixel = channels * depth;
        int stride = (width * bitsPerPixel + 7) / 8;
        int bpp = Math.Max(1, bitsPerPixel / 8);
        var previous = new byte[stride];
        var row = new byte[stride];
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            int filter = inflater.ReadByte();
            inflater.ReadExactly(row);
            Unfilter(filter, row, previous, bpp);
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                switch (colorType)
                {
                    case 0:
                        {
                            int v = Sample(row, x, depth) * 255 / ((1 << depth) - 1);
                            rgba[o] = rgba[o + 1] = rgba[o + 2] = (byte)v;
                            rgba[o + 3] = 255;
                            break;
                        }
                    case 2:
                        row.AsSpan(x * 3, 3).CopyTo(rgba.AsSpan(o));
                        rgba[o + 3] = 255;
                        break;
                    case 3:
                        {
                            int i = Sample(row, x, depth);
                            if (i * 3 + 2 < palette.Length)
                            {
                                palette.AsSpan(i * 3, 3).CopyTo(rgba.AsSpan(o));
                            }
                            rgba[o + 3] = i < alphas.Length ? alphas[i] : (byte)255;
                            break;
                        }
                    case 4:
                        rgba[o] = rgba[o + 1] = rgba[o + 2] = row[x * 2];
                        rgba[o + 3] = row[x * 2 + 1];
                        break;
                    case 6:
                        row.AsSpan(x * 4, 4).CopyTo(rgba.AsSpan(o));
                        break;
                }
            }
            (previous, row) = (row, previous);
        }
        return (width, height, rgba);
    }

    /// <summary>Encode 8-bit RGBA pixels as a PNG file.</summary>
    public static byte[] Encode(int width, int height, ReadOnlySpan<byte> rgba)
    {
        using var output = new MemoryStream();
        output.Write(Signature);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        (header[8], header[9]) = (8, 6);
        WriteChunk(output, "IHDR", header);
        using (var raw = new MemoryStream())
        {
            using (var deflater = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            {
                for (int y = 0; y < height; y++)
                {
                    deflater.WriteByte(0);
                    deflater.Write(rgba.Slice(y * width * 4, width * 4));
                }
            }
            WriteChunk(output, "IDAT", raw.ToArray());
        }
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static int Sample(byte[] row, int x, int depth)
    {
        if (depth == 8)
        {
            return row[x];
        }
        int bit = x * depth;
        return (row[bit / 8] >> (8 - depth - bit % 8)) & ((1 << depth) - 1);
    }

    private static void Unfilter(int filter, byte[] row, byte[] previous, int bpp)
    {
        for (int i = 0; i < row.Length; i++)
        {
            int a = i >= bpp ? row[i - bpp] : 0, b = previous[i], c = i >= bpp ? previous[i - bpp] : 0;
            row[i] = (byte)(row[i] + filter switch
            {
                0 => 0,
                1 => a,
                2 => b,
                3 => (a + b) / 2,
                4 => Paeth(a, b, c),
                _ => throw new InvalidDataException($"PNG filter {filter}"),
            });
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> body)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, body.Length);
        output.Write(word);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(body);
        uint crc = Crc32(Crc32(0xFFFFFFFF, typeBytes), body) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(word, crc);
        output.Write(word);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++)
        {
            c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
        return c;
    }).ToArray();

    private static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        return crc;
    }
}
