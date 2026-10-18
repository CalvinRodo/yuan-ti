using System.Buffers.Binary;
using DarkSun.Data.Images;

namespace DarkSun.Data.Cinematics;

/// <summary>One cinematic instruction: an opcode and its word arguments (as stored, unsigned).</summary>
public sealed record AcfInstruction(int Op, IReadOnlyList<ushort> Args)
{
    public int Arg(int index) => index < Args.Count ? Args[index] : 0;

    /// <summary>An argument read as a signed word.</summary>
    public int SignedArg(int index) => (short)Arg(index);

    public override string ToString() => $"{Op:x2}({string.Join(", ", Args)})";
}

/// <summary>
/// A chunk of a <c>BMA </c> animation, from an ACF chunk instruction (op 1):
/// <paramref name="Frames"/> frames in <paramref name="Bytes"/> bytes.
/// </summary>
public readonly record struct BmaChunk(int Frames, int Bytes);

/// <summary>
/// An <c>ACF </c> cinematic script from <c>CINE.GFF</c>. The script is a list
/// of 16-bit words; each instruction's first word holds the opcode in its
/// high byte and the instruction length in bytes in its low byte, and the
/// arguments follow. Opcode 0 ends the script (DSUN.EXE <c>985F:0000</c>;
/// <c>dsun-exe-reference.md</c> §7).
/// </summary>
public sealed class Acf
{
    /// <summary>Chunk table entries (op 1): describe the matching <c>BMA </c>, run nothing.</summary>
    public const int ChunkOp = 0x01;

    private Acf(IReadOnlyList<AcfInstruction> instructions)
    {
        Instructions = instructions;
        Chunks = [.. instructions.Where(i => i.Op == ChunkOp).Select(i => new BmaChunk(i.Arg(1), i.Arg(2)))];
    }

    public IReadOnlyList<AcfInstruction> Instructions { get; }

    /// <summary>How the script's <c>BMA </c> (same number) is split into separately encoded images.</summary>
    public IReadOnlyList<BmaChunk> Chunks { get; }

    /// <exception cref="InvalidDataException">An instruction has length 0 or runs past the end.</exception>
    public static Acf Parse(ReadOnlySpan<byte> data)
    {
        var instructions = new List<AcfInstruction>();
        int words = data.Length / 2;
        int at = 0;
        while (at < words)
        {
            ushort head = BinaryPrimitives.ReadUInt16LittleEndian(data[(at * 2)..]);
            int op = head >> 8;
            int length = (head & 0xFF) >> 1;
            if (length == 0 || at + length > words)
            {
                throw new InvalidDataException($"bad ACF instruction 0x{head:x4} at word {at}");
            }
            var args = new ushort[length - 1];
            for (int i = 0; i < args.Length; i++)
            {
                args[i] = BinaryPrimitives.ReadUInt16LittleEndian(data[((at + 1 + i) * 2)..]);
            }
            instructions.Add(new AcfInstruction(op, args));
            at += length;
            if (op == 0)
            {
                break;
            }
        }
        return new Acf(instructions);
    }

    /// <summary>
    /// The frames of a <c>BMA </c> resource: one image (in the <c>BMP </c>
    /// format) per chunk, back to back, which DSUN.EXE streams one chunk at a
    /// time (<c>985F:0852</c>). With no chunk table the whole resource is one image.
    /// </summary>
    public static IReadOnlyList<ImageFrame> ReadBma(ReadOnlySpan<byte> data, IReadOnlyList<BmaChunk> chunks)
    {
        if (chunks.Count == 0)
        {
            return ImageDecoder.ReadFrames(data);
        }
        var frames = new List<ImageFrame>();
        int at = 0;
        foreach (BmaChunk chunk in chunks)
        {
            if (at + chunk.Bytes > data.Length)
            {
                throw new InvalidDataException($"BMA chunk at {at} ({chunk.Bytes} bytes) runs past the end ({data.Length})");
            }
            frames.AddRange(ImageDecoder.ReadFrames(data.Slice(at, chunk.Bytes)));
            at += chunk.Bytes;
        }
        return frames;
    }
}
