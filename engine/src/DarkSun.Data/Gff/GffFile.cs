using System.Buffers.Binary;
using System.Text;

namespace DarkSun.Data.Gff;

/// <summary>One resource in a GFF file: 4-character tag, number, and location.</summary>
public readonly record struct ResourceDescriptor(string Tag, int Number, int Offset, int Size);

/// <summary>
/// A GFF resource container, read whole into memory. Port of <c>src/gff.py</c>
/// (itself from John Glassmyer's GffFile.java); the format is described in
/// <c>.github/instructions/gff-data-reference.md</c> §2.
/// </summary>
/// <remarks>
/// Layout, all little-endian: the int32 at byte 12 points to the index block.
/// The index block skips 8 bytes, then holds an int16 tag count and, per tag,
/// either a primary table (int32 count, then 12-byte entries: number, offset,
/// size) or a reference to a secondary table: an index into the GFFI table,
/// whose entry locates 8-byte (offset, size) entries numbered by inline
/// (first number, count) segments.
/// </remarks>
public sealed class GffFile : IResourceSource
{
    private readonly byte[] _data;
    private readonly Dictionary<string, Table> _tables;

    private GffFile(byte[] data, Dictionary<string, Table> tables)
    {
        _data = data;
        _tables = tables;
    }

    /// <summary>Tags in index order (primary tables first, then secondary).</summary>
    public IEnumerable<string> Tags => _tables.Keys;

    public static GffFile Open(string path) => FromBytes(File.ReadAllBytes(path));

    public static GffFile FromBytes(byte[] data)
    {
        try
        {
            return new GffFile(data, ReadTables(data));
        }
        catch (ArgumentOutOfRangeException e)
        {
            throw new InvalidDataException("GFF index points outside the file", e);
        }
    }

    /// <summary>Every resource, sorted by file offset.</summary>
    public IReadOnlyList<ResourceDescriptor> DescribeResources() =>
        _tables.SelectMany(t => t.Value.Entries.Select(e => new ResourceDescriptor(t.Key, e.Number, e.Offset, e.Size)))
            .OrderBy(d => d.Offset)
            .ToList();

    public bool Contains(string tag, int number) =>
        _tables.TryGetValue(NormalizeTag(tag), out Table? table) && table.ByNumber.ContainsKey(number);

    public ReadOnlyMemory<byte> Read(string tag, int number) =>
        TryRead(tag, number, out ReadOnlyMemory<byte> data)
            ? data
            : throw new KeyNotFoundException($"no resource {NormalizeTag(tag)}-{number} in GFF file");

    public bool TryRead(string tag, int number, out ReadOnlyMemory<byte> data)
    {
        if (_tables.TryGetValue(NormalizeTag(tag), out Table? table)
            && table.ByNumber.TryGetValue(number, out Entry entry))
        {
            data = _data.AsMemory(entry.Offset, entry.Size);
            return true;
        }
        data = default;
        return false;
    }

    /// <summary>Pad a tag to 4 characters, as stored: <c>"PAL"</c> → <c>"PAL "</c>.</summary>
    public static string NormalizeTag(string tag) => tag.Length >= 4 ? tag : tag.PadRight(4);

    private static Dictionary<string, Table> ReadTables(byte[] data)
    {
        ReadOnlySpan<byte> span = data;
        int pos = Int32(span, 12) + 8;
        int tagCount = BinaryPrimitives.ReadUInt16LittleEndian(span[pos..]);
        pos += 2;

        var tables = new Dictionary<string, Table>();
        var secondary = new List<(string Tag, int GffiIndex, int NumberingOffset)>();

        for (int i = 0; i < tagCount; i++)
        {
            string tag = Encoding.ASCII.GetString(data, pos, 4);
            pos += 4;
            if (Int32(span, pos) > 0)
            {
                Table table = PrimaryTable(span, pos);
                tables[tag] = table; // a repeated tag replaces the earlier one, as in gff.py
                pos += 4 + table.Entries.Count * 12;
            }
            else
            {
                pos += 8;
                int gffiIndex = Int32(span, pos);
                pos += 4;
                secondary.Add((tag, gffiIndex, pos));
                pos += 4 + Int32(span, pos) * 8;
            }
        }

        if (tables.TryGetValue("GFFI", out Table? gffi))
        {
            foreach (var (tag, gffiIndex, numberingOffset) in secondary)
            {
                tables[tag] = SecondaryTable(span, gffi.Entries[gffiIndex].Offset, numberingOffset);
            }
        }
        return tables;
    }

    private static Table PrimaryTable(ReadOnlySpan<byte> data, int start)
    {
        int count = Int32(data, start);
        var entries = new List<Entry>(count);
        for (int i = 0; i < count; i++)
        {
            int at = start + 4 + i * 12;
            entries.Add(new Entry(Int32(data, at), Int32(data, at + 4), Int32(data, at + 8)));
        }
        return new Table(entries);
    }

    private static Table SecondaryTable(ReadOnlySpan<byte> data, int start, int numberingOffset)
    {
        // Numbering segments: (first resource number, count), applied to
        // consecutive entries. Entries past the last segment continue its
        // numbering, as in gff.py.
        int segments = Int32(data, numberingOffset);
        var firstIndex = new List<int>(segments);
        var firstNumber = new List<int>(segments);
        int index = 0;
        for (int s = 0; s < segments; s++)
        {
            int at = numberingOffset + 4 + s * 8;
            firstIndex.Add(index);
            firstNumber.Add(Int32(data, at));
            index += Int32(data, at + 4);
        }

        int count = Int32(data, start);
        var entries = new List<Entry>(count);
        int segment = 0;
        for (int i = 0; i < count; i++)
        {
            while (segment + 1 < firstIndex.Count && firstIndex[segment + 1] <= i)
            {
                segment++;
            }
            int at = start + 4 + i * 8;
            entries.Add(new Entry(firstNumber[segment] + (i - firstIndex[segment]), Int32(data, at), Int32(data, at + 4)));
        }
        return new Table(entries);
    }

    private static int Int32(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadInt32LittleEndian(data[at..]);

    private readonly record struct Entry(int Number, int Offset, int Size);

    private sealed class Table
    {
        public Table(List<Entry> entries)
        {
            Entries = entries;
            ByNumber = new Dictionary<int, Entry>(entries.Count);
            foreach (Entry e in entries)
            {
                ByNumber.TryAdd(e.Number, e); // first entry wins, as in gff.py
            }
        }

        public List<Entry> Entries { get; }
        public Dictionary<int, Entry> ByNumber { get; }
    }
}
