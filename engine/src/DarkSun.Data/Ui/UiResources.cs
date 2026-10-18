using System.Buffers.Binary;
using System.Text;

namespace DarkSun.Data.Ui;

/// <summary>An item of a window: another UI resource (<c>BUTN</c>, <c>APFM</c>, <c>EBOX</c>, <c>ACCL</c>) at a position.</summary>
public readonly record struct WindowItem(string Tag, int Number, int X, int Y);

/// <summary>
/// A <c>WIND</c> resource (<c>RESOURCE.GFF</c>): a 0x105-byte header and
/// 30-byte items. DSUN.EXE loads it as is and keeps run-time pointers in it
/// (window list <c>2531:049E</c>). Layout, from the data and the loader:
/// <list type="bullet">
/// <item><c>+0x08</c> resource number; <c>+0xBE</c>, <c>+0xC0</c> width and height;
/// <c>+0xF3</c> item count.</item>
/// <item>item i at <c>0x105 + 30·i</c>: <c>+4</c> tag, <c>+8</c> number (uint32),
/// <c>+0x0C</c>, <c>+0x0E</c> position in the window.</item>
/// </list>
/// </summary>
public sealed record UiWindow(int Number, int Width, int Height, IReadOnlyList<WindowItem> Items)
{
    public const int HeaderSize = 0x105;
    public const int ItemSize = 30;

    /// <exception cref="InvalidDataException">Not a <c>WIND</c> resource, or truncated.</exception>
    public static UiWindow Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || !data[..4].SequenceEqual("WIND"u8))
        {
            throw new InvalidDataException("not a WIND resource");
        }
        int count = data[0xF3];
        if (data.Length < HeaderSize + count * ItemSize)
        {
            throw new InvalidDataException($"WIND has {count} items but only {data.Length} bytes");
        }
        var items = new WindowItem[count];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> item = data.Slice(HeaderSize + i * ItemSize, ItemSize);
            items[i] = new WindowItem(
                Encoding.ASCII.GetString(item.Slice(4, 4)),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(item[8..]),
                BinaryPrimitives.ReadInt16LittleEndian(item[0x0C..]),
                BinaryPrimitives.ReadInt16LittleEndian(item[0x0E..]));
        }
        return new UiWindow(
            (int)BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
            BinaryPrimitives.ReadInt16LittleEndian(data[0xBE..]),
            BinaryPrimitives.ReadInt16LittleEndian(data[0xC0..]),
            items);
    }
}

/// <summary>
/// A <c>BUTN</c> resource (110 bytes): <c>+0x08</c> number, <c>+0x28</c>,
/// <c>+0x2A</c> size, <c>+0x5A</c> the <c>ICON</c> it shows. The icon's
/// frames are the button's looks: 0 normal, 1 highlighted, 2 disabled (1 × 1
/// when there is none), 3 pressed (from the main menu's art).
/// </summary>
public sealed record UiButton(int Number, int Width, int Height, int Icon)
{
    public const int NormalFrame = 0, HighlightFrame = 1, DisabledFrame = 2, PressedFrame = 3;

    /// <exception cref="InvalidDataException">Not a <c>BUTN</c> resource.</exception>
    public static UiButton Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x5C || !data[..4].SequenceEqual("BUTN"u8))
        {
            throw new InvalidDataException("not a BUTN resource");
        }
        return new UiButton(
            (int)BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
            BinaryPrimitives.ReadInt16LittleEndian(data[0x28..]),
            BinaryPrimitives.ReadInt16LittleEndian(data[0x2A..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[0x5A..]));
    }
}

/// <summary>A key and the command it gives.</summary>
public readonly record struct Accelerator(int Flags, char Key, int Command);

/// <summary>
/// An <c>ACCL</c> resource: <c>+0x08</c> number, <c>+0x0C</c> count, then
/// 5-byte entries (flags byte, key, command). The main menu's (8100) gives
/// S, s, C, c, L, l, E, e commands 8100–8107.
/// </summary>
public static class AcceleratorTable
{
    /// <exception cref="InvalidDataException">Not an <c>ACCL</c> resource, or truncated.</exception>
    public static IReadOnlyList<Accelerator> Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x0E || !data[..4].SequenceEqual("ACCL"u8))
        {
            throw new InvalidDataException("not an ACCL resource");
        }
        int count = BinaryPrimitives.ReadUInt16LittleEndian(data[0x0C..]);
        if (data.Length < 0x0E + count * 5)
        {
            throw new InvalidDataException($"ACCL has {count} entries but only {data.Length} bytes");
        }
        var keys = new Accelerator[count];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = data.Slice(0x0E + i * 5, 5);
            keys[i] = new Accelerator(entry[0], (char)BinaryPrimitives.ReadUInt16LittleEndian(entry[1..]),
                BinaryPrimitives.ReadUInt16LittleEndian(entry[3..]));
        }
        return keys;
    }
}
