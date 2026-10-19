using System.Buffers.Binary;

namespace DarkSun.Data.Regions;

/// <summary>
/// An <c>OJFF</c> record (16 bytes, <c>SEGOBJEX.GFF</c>): how to draw an
/// object. Layout from <c>object_from_ojff</c> (<c>1DF3:1073</c>), see
/// <c>dsun-exe-reference.md</c> §3.
/// </summary>
/// <param name="AnchorX">Pixels from the image's left edge to the <c>ETAB</c> position.</param>
/// <param name="AnchorY">Pixels from the image's top edge to the <c>ETAB</c> position (+4 plus the int8 at +0x0A).</param>
/// <param name="YAdjust">The int8 at +0x0A, included in <paramref name="AnchorY"/>; it stays when an
/// <c>SCMD</c> record replaces the anchor with its hot spot (*unverified*: DSUN.EXE subtracts object
/// byte +0x0D then, which is assumed to hold it).</param>
/// <param name="Bmp">The <c>BMP </c> resource with the object's frames.</param>
/// <param name="Scmd">The <c>SCMD</c> animation, or 0.</param>
public sealed record ObjectDefinition(byte Flags, int AnchorX, int AnchorY, int Bmp, int Scmd, int YAdjust = 0)
{
    public const int Size = 0x10;

    public static ObjectDefinition Parse(ReadOnlySpan<byte> r)
    {
        if (r.Length < Size)
        {
            throw new InvalidDataException($"OJFF is {r.Length} bytes, expected {Size}");
        }
        return new ObjectDefinition(
            r[0],
            BinaryPrimitives.ReadInt16LittleEndian(r[2..]),
            BinaryPrimitives.ReadInt16LittleEndian(r[4..]) + (sbyte)r[0x0A],
            BinaryPrimitives.ReadUInt16LittleEndian(r[0x0C..]),
            BinaryPrimitives.ReadInt16LittleEndian(r[0x0E..]),
            (sbyte)r[0x0A]);
    }
}
