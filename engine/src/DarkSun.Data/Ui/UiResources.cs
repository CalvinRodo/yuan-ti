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
/// <c>+0xC2</c> the <c>BMP</c> behind it, 0 for none (15503, the spell
/// information window: 15002; 3012 / 3013: 20087; 17500–17502: 17000–17002);
/// <c>+0xF3</c> item count.</item>
/// <item>item i at <c>0x105 + 30·i</c>: <c>+4</c> tag, <c>+8</c> number (uint32),
/// <c>+0x0C</c>, <c>+0x0E</c> position in the window.</item>
/// </list>
/// </summary>
public sealed record UiWindow(int Number, int Width, int Height, IReadOnlyList<WindowItem> Items, int Backdrop = 0)
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
            items,
            BinaryPrimitives.ReadUInt16LittleEndian(data[0xC2..]));
    }
}

/// <summary>
/// A <c>BUTN</c> resource (110 bytes, 130 or 163 with a text): <c>+0x08</c>
/// number, <c>+0x28</c>, <c>+0x2A</c> size, <c>+0x5A</c> its own number
/// again, <c>+0x64</c> the <c>ICON</c> it shows (0: none, e.g. the invisible
/// button over a whole text window). The icon's frames are the button's
/// looks: 0 normal, 1 highlighted, 2 disabled (1 × 1 when there is none),
/// 3 pressed (from the main menu's art). See <see cref="UiControl"/>.
/// </summary>
public sealed record UiButton(int Number, int Width, int Height, int Icon)
{
    public const int NormalFrame = 0, HighlightFrame = 1, DisabledFrame = 2, PressedFrame = 3;

    /// <exception cref="InvalidDataException">Not a <c>BUTN</c> resource.</exception>
    public static UiButton Parse(ReadOnlySpan<byte> data)
    {
        UiControl c = UiControl.Parse(data);
        return c.Tag == "BUTN" ? new UiButton(c.Number, c.Width, c.Height, c.Icon) : throw new InvalidDataException("not a BUTN resource");
    }
}

/// <summary>
/// Where a control puts its icon and its text (the word at <c>+0x58</c>, read
/// by the layout routine <c>2A1D</c>): bits 8–9 the icon across and 10–11
/// down, 4–5 the text across and 6–7 down, each 0 at the start, 1 inset (by
/// the border), 2 centred, 3 at the end; bits 0–3 flags (not traced).
/// </summary>
public readonly record struct UiAlignment(int IconX, int IconY, int TextX, int TextY, int Flags)
{
    public const int Start = 0, Inset = 1, Centre = 2, End = 3;

    public static UiAlignment Parse(int word) =>
        new(word >> 8 & 3, word >> 10 & 3, word >> 4 & 3, word >> 6 & 3, word & 0xF);

    public int Word => IconX << 8 | IconY << 10 | TextX << 4 | TextY << 6 | Flags;
}

/// <summary>
/// One control of a window, whatever its tag: they share a header (tag,
/// length, number at <c>+0x08</c>). <c>BUTN</c> and <c>APFM</c> have their size
/// at <c>+0x28</c>, <c>+0x2A</c>, the alignment at <c>+0x58</c> and an icon at
/// <c>+0x64</c>; a long <c>BUTN</c> has a text at <c>+0x6E</c> (a placeholder
/// such as "XXXXX" the game replaces). An <c>APFM</c> (116 bytes, everything
/// else 0) is a frame or a slot the code draws into or fills at run time:
/// character boxes, item slots, bars. An <c>EBOX</c> (168 bytes) is a text
/// box: size at <c>+0x22</c>, <c>+0x24</c>, <c>+0x5A</c> a number (2081, 2099;
/// not a <c>FONT</c>: there is only one), <c>+0x7E</c> its background colour
/// (229, the transparent key) and <c>+0x80</c> its text colour (199), 0 for
/// the defaults.
/// </summary>
public sealed record UiControl(string Tag, int Number, int Width, int Height, UiAlignment Align, int Icon, string Text,
    int TextColour, int Background)
{
    /// <exception cref="InvalidDataException">Not a control resource.</exception>
    public static UiControl Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x2C)
        {
            throw new InvalidDataException("too short for a control");
        }
        string tag = Encoding.ASCII.GetString(data[..4]);
        byte[] bytes = data.ToArray();
        int Word(int at) => at + 2 <= bytes.Length ? BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(at)) : 0;
        int number = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        switch (tag)
        {
            case "EBOX":
                return new UiControl(tag, number, Word(0x22), Word(0x24), default, 0, "", Word(0x80), Word(0x7E));
            case "BUTN" or "APFM":
                string text = data.Length > 0x6E ? Encoding.Latin1.GetString(data[0x6E..]).Split('\0')[0] : "";
                return new UiControl(tag, number, Word(0x28), Word(0x2A), UiAlignment.Parse(Word(0x58)), (ushort)Word(0x64), text, 0, 0);
            default:
                throw new InvalidDataException($"not a control resource ({tag})");
        }
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
