using System.Buffers.Binary;
using System.Text;
using DarkSun.Data.Gff;

namespace DarkSun.Data.Tests;

public class GffFileTests
{
    /// <summary>
    /// A GFF with a primary table (TEXT 7 and 9), a GFFI table, and a
    /// secondary table (TILE) numbered by two segments: 100, then 200–201.
    /// </summary>
    internal static byte[] BuildGff()
    {
        var b = new Bytes();
        b.Zero(16);
        int text7 = b.Ascii("seven");
        int text9 = b.Ascii("nine!");
        int tile0 = b.Ascii("t100");
        int tile1 = b.Ascii("t200");
        int tile2 = b.Ascii("t201");

        int secondaryTable = b.Count;
        b.Int(3).Int(tile0).Int(4).Int(tile1).Int(4).Int(tile2).Int(4);

        int index = b.Count;
        b.SetInt(12, index);
        b.Zero(8).Short(3);
        b.Text("TEXT").Int(2).Int(7).Int(text7).Int(5).Int(9).Int(text9).Int(5);
        b.Text("GFFI").Int(1).Int(0).Int(secondaryTable).Int(28);
        b.Text("TILE").Int(0).Int(0).Int(0).Int(2).Int(100).Int(1).Int(200).Int(2);
        return b.ToArray();
    }

    [Fact]
    public void ReadsPrimaryAndSecondaryTables()
    {
        GffFile gff = GffFile.FromBytes(BuildGff());

        Assert.Equal(["TEXT", "GFFI", "TILE"], gff.Tags);
        Assert.Equal("nine!", Encoding.ASCII.GetString(gff.Read("TEXT", 9).Span));
        Assert.Equal("t100", Encoding.ASCII.GetString(gff.Read("TILE", 100).Span));
        Assert.Equal("t201", Encoding.ASCII.GetString(gff.Read("TILE", 201).Span));
        Assert.False(gff.Contains("TILE", 101));
    }

    [Fact]
    public void DescribesResourcesInFileOrder()
    {
        GffFile gff = GffFile.FromBytes(BuildGff());

        Assert.Equal(["TEXT-7", "TEXT-9", "TILE-100", "TILE-200", "TILE-201", "GFFI-0"],
            gff.DescribeResources().Select(r => $"{r.Tag}-{r.Number}"));
    }

    [Fact]
    public void PadsShortTags()
    {
        Assert.Equal("PAL ", GffFile.NormalizeTag("PAL"));
        Assert.Equal("TILE", GffFile.NormalizeTag("TILE"));
    }

    [Fact]
    public void MissingResourceThrows()
    {
        GffFile gff = GffFile.FromBytes(BuildGff());
        Assert.Throws<KeyNotFoundException>(() => gff.Read("TEXT", 8));
        Assert.Throws<KeyNotFoundException>(() => gff.Read("PAL", 0));
    }

    [Fact]
    public void SetSearchesSourcesInOrder()
    {
        GffFile gff = GffFile.FromBytes(BuildGff());
        var set = new GffSet([new SingleResource("TEXT", 7, "override"u8.ToArray()), gff]);

        Assert.Equal("override", Encoding.ASCII.GetString(set.Read("TEXT", 7).Span));
        Assert.Equal("nine!", Encoding.ASCII.GetString(set.Read("TEXT", 9).Span));
    }

    [Fact]
    public void TruncatedIndexIsInvalidData()
    {
        byte[] data = BuildGff()[..^10];
        Assert.Throws<InvalidDataException>(() => GffFile.FromBytes(data));
    }

    private sealed class SingleResource(string tag, int number, byte[] data) : IResourceSource
    {
        public bool Contains(string t, int n) => t == tag && n == number;

        public bool TryRead(string t, int n, out ReadOnlyMemory<byte> result)
        {
            result = Contains(t, n) ? data : default;
            return Contains(t, n);
        }
    }
}

/// <summary>Little-endian byte builder for test data.</summary>
internal sealed class Bytes
{
    private readonly List<byte> _bytes = [];

    public int Count => _bytes.Count;

    public Bytes Zero(int n)
    {
        _bytes.AddRange(new byte[n]);
        return this;
    }

    public Bytes Byte(params byte[] values)
    {
        _bytes.AddRange(values);
        return this;
    }

    public Bytes Short(int value)
    {
        _bytes.Add((byte)value);
        _bytes.Add((byte)(value >> 8));
        return this;
    }

    public Bytes Int(int value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buf, value);
        _bytes.AddRange(buf.ToArray());
        return this;
    }

    public Bytes Text(string text)
    {
        _bytes.AddRange(Encoding.ASCII.GetBytes(text));
        return this;
    }

    /// <summary>Append ASCII text; returns its offset.</summary>
    public int Ascii(string text)
    {
        int at = _bytes.Count;
        _bytes.AddRange(Encoding.ASCII.GetBytes(text));
        return at;
    }

    public void SetInt(int at, int value)
    {
        for (int i = 0; i < 4; i++)
        {
            _bytes[at + i] = (byte)(value >> (8 * i));
        }
    }

    public byte[] ToArray() => [.. _bytes];
}
