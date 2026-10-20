using System.Text;
using DarkSun.Data.Gff;

namespace DarkSun.Data.Text;

/// <summary>
/// The text resources: <c>TEXT</c> (60 in <c>RESOURCE.GFF</c>: names, area
/// names, item descriptions), <c>MERR</c> (error messages, in
/// <c>RESOURCE.GFF</c> and <c>CINE.GFF</c>) and <c>SPIN</c> (180 spell
/// descriptions). Each is Latin-1 text, ended by CR LF in the shipped files
/// (or a NUL). Port of <c>decode_text</c> in <c>src/text_parsers.py</c>.
/// </summary>
public static class GameText
{
    /// <summary>Tags holding plain text.</summary>
    public static readonly IReadOnlyList<string> TextTags = ["TEXT", "MERR", "SPIN"];

    /// <summary>The text up to the first NUL, with CR LF and lone CR turned into LF.</summary>
    public static string Decode(ReadOnlySpan<byte> raw)
    {
        int end = raw.IndexOf((byte)0);
        string text = Encoding.Latin1.GetString(end < 0 ? raw : raw[..end]);
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    /// <summary>Text resource <paramref name="tag"/> <paramref name="number"/>, or null if there is none.</summary>
    public static string? Read(IResourceSource source, string tag, int number) =>
        source.TryRead(tag, number, out var data) ? Decode(data.Span) : null;
}

/// <summary>
/// A spell's description (<c>SPIN</c> n): <c>"NAME: description"</c>. A
/// spell's icon is <c>ICON</c> 20999 + n (21000 is spell 1, Armor; 21008
/// Magic Missile; to 21137, spell 138); a psionic power's (139–172) is
/// <c>ICON</c> 3000 + n − 139 (34 icons, as the repository's Python viewer has it).
/// </summary>
public sealed record SpellInfo(int Number, string Name, string Description)
{
    public const int IconBase = 20999, PowerIconBase = 3000, FirstPower = 139;

    /// <summary>The spell's or power's <c>ICON</c>, or null (the potions' and others past 172 have none).</summary>
    public int? Icon => Number switch
    {
        >= 1 and < FirstPower => IconBase + Number,
        >= FirstPower and <= 172 => PowerIconBase + Number - FirstPower,
        _ => null,
    };

    /// <summary>Split at the first <c>": "</c> (no separator: all name).</summary>
    public static SpellInfo Parse(int number, ReadOnlySpan<byte> raw)
    {
        string text = GameText.Decode(raw).Trim();
        int colon = text.IndexOf(": ", StringComparison.Ordinal);
        return colon < 0
            ? new SpellInfo(number, text, "")
            : new SpellInfo(number, text[..colon], text[(colon + 2)..]);
    }

    /// <summary>Every <c>SPIN</c> in <paramref name="file"/>, by number.</summary>
    public static IReadOnlyList<SpellInfo> ReadAll(GffFile file) =>
        [.. file.DescribeResources().Where(r => r.Tag == "SPIN").OrderBy(r => r.Number)
            .Select(r => Parse(r.Number, file.Read(r.Tag, r.Number).Span))];
}

/// <summary>
/// The <c>NAME</c> table (<c>GPLDATA.GFF</c> <c>NAME</c> 1): 25-byte records,
/// each a NUL-terminated name. DSUN.EXE loads it whole (<c>60B9:0165</c>,
/// pointer at <c>DS:166D</c>) and copies a name from <c>table + 25·n</c>
/// (<c>612E</c>, <c>78A9</c>, <c>88F4</c>); an item's name number is the word
/// at +0x12 of its 0x15-byte record (<c>DS:165D</c>). The bytes after a name's
/// NUL are leftovers from the tool that wrote the file (fragments such as
/// <c>\NAMEIX\*.oda</c>), not data; <c>src/text_parsers.py</c> used to read
/// the last three as type bytes.
/// </summary>
public sealed class NameTable
{
    public const int RecordSize = 25;
    public const int Resource = 1;

    private readonly string[] _names;

    private NameTable(string[] names) => _names = names;

    public int Count => _names.Length;

    /// <summary>Name <paramref name="index"/>, or null outside the table.</summary>
    public string? this[int index] => index >= 0 && index < _names.Length ? _names[index] : null;

    public IReadOnlyList<string> Names => _names;

    /// <summary>The table's records; a short last record is read as far as it goes.</summary>
    public static NameTable Parse(ReadOnlySpan<byte> data)
    {
        var names = new string[(data.Length + RecordSize - 1) / RecordSize];
        for (int i = 0; i < names.Length; i++)
        {
            ReadOnlySpan<byte> record = data.Slice(i * RecordSize, Math.Min(RecordSize, data.Length - i * RecordSize));
            int end = record.IndexOf((byte)0);
            names[i] = Encoding.Latin1.GetString(end < 0 ? record : record[..end]);
        }
        return new NameTable(names);
    }

    /// <summary><c>NAME</c> 1 from <paramref name="source"/> (<c>GPLDATA.GFF</c>), or an empty table.</summary>
    public static NameTable Read(IResourceSource source) =>
        source.TryRead("NAME", Resource, out var data) ? Parse(data.Span) : new NameTable([]);
}
