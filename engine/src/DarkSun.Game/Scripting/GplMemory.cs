using System.Buffers.Binary;
using System.Text;

namespace DarkSun.Game.Scripting;

/// <summary>Where a value lives, for commands that modify it in place (DSUN.EXE keeps a far pointer).</summary>
public abstract record GplLocation;

/// <summary>A byte offset in one of the variable areas.</summary>
public sealed record GplMemoryLocation(GplArea Area, int Offset) : GplLocation;

/// <summary>An engine variable (<c>gname</c>), owned by the host.</summary>
public sealed record GplEngineLocation(int Index) : GplLocation;

/// <summary>An object field, owned by the host.</summary>
public sealed record GplFieldLocation(GplObjectRef Field) : GplLocation;

public enum GplArea { LocalFlags, LocalNums, LocalBigNums, LocalStrings, GlobalFlags, GlobalNums, GlobalBigNums, GlobalStrings }

/// <summary>
/// The GPL variable areas, laid out as in DSUN.EXE (sizes from <c>gpl_init</c>,
/// <c>5DAA:0000</c>, and the local reset <c>043D:38EE</c>): int16 and int32
/// little-endian, flags least significant bit first, strings in 42-byte
/// records. Reads outside an area give 0 and writes are dropped, where DSUN.EXE
/// would touch whatever memory follows.
/// </summary>
public sealed class GplMemory
{
    public const int StringSize = 42;

    private readonly Dictionary<GplArea, byte[]> _areas = new()
    {
        [GplArea.LocalFlags] = new byte[9],
        [GplArea.LocalNums] = new byte[0x40],
        [GplArea.LocalBigNums] = new byte[0x28],
        [GplArea.LocalStrings] = new byte[0x1A4],
        [GplArea.GlobalFlags] = new byte[0x65],
        [GplArea.GlobalNums] = new byte[400],
        [GplArea.GlobalBigNums] = new byte[0x50],
        [GplArea.GlobalStrings] = new byte[0x1A4],
    };

    /// <summary>Accesses that fell outside an area, for diagnostics.</summary>
    public int OutOfRange { get; private set; }

    public void ClearLocals()
    {
        foreach (GplArea area in new[] { GplArea.LocalFlags, GplArea.LocalNums, GplArea.LocalBigNums, GplArea.LocalStrings })
        {
            Array.Clear(_areas[area]);
        }
    }

    public void ClearGlobals()
    {
        foreach (GplArea area in new[] { GplArea.GlobalFlags, GplArea.GlobalNums, GplArea.GlobalBigNums, GplArea.GlobalStrings })
        {
            Array.Clear(_areas[area]);
        }
    }

    public byte[] Area(GplArea area) => _areas[area];

    /// <summary>The location of a numeric or string variable; null for engine variables.</summary>
    public static GplMemoryLocation? Locate(GplVariable v) => v.Type switch
    {
        GplVarType.LocalNum => new(GplArea.LocalNums, v.Index * 2),
        GplVarType.GlobalNum => new(GplArea.GlobalNums, v.Index * 2),
        GplVarType.LocalBigNum => new(GplArea.LocalBigNums, v.Index * 4),
        GplVarType.GlobalBigNum => new(GplArea.GlobalBigNums, v.Index * 4),
        GplVarType.LocalFlag => new(GplArea.LocalFlags, v.Index / 8),
        GplVarType.GlobalFlag => new(GplArea.GlobalFlags, v.Index / 8),
        GplVarType.LocalString => new(GplArea.LocalStrings, v.Index * StringSize),
        GplVarType.GlobalString => new(GplArea.GlobalStrings, v.Index * StringSize),
        _ => null,
    };

    /// <summary>A variable's value: int16 sign-extended, int32, a flag as 0 or 1 (<c>gpl_read_variable</c>).</summary>
    public int Read(GplVariable v) => v.Type switch
    {
        GplVarType.LocalNum or GplVarType.GlobalNum => ReadInt(Locate(v)!, 2),
        GplVarType.LocalBigNum or GplVarType.GlobalBigNum => ReadInt(Locate(v)!, 4),
        GplVarType.LocalFlag or GplVarType.GlobalFlag => (ReadInt(Locate(v)!, 1) >> (v.Index & 7)) & 1,
        _ => 0,
    };

    /// <summary>
    /// Assign as LOAD_VAR does (<c>043D:2A76</c>): numbers truncated to the
    /// variable's width; flags set when the value is non-zero. Strings and
    /// engine variables are not assignable this way.
    /// </summary>
    public void Assign(GplVariable v, int value)
    {
        switch (v.Type)
        {
            case GplVarType.LocalNum or GplVarType.GlobalNum:
                WriteInt(Locate(v)!, 2, value);
                break;
            case GplVarType.LocalBigNum or GplVarType.GlobalBigNum:
                WriteInt(Locate(v)!, 4, value);
                break;
            case GplVarType.LocalFlag or GplVarType.GlobalFlag:
                GplMemoryLocation at = Locate(v)!;
                int bits = ReadInt(at, 1) & 0xFF;
                int mask = 1 << (v.Index & 7);
                WriteInt(at, 1, value != 0 ? bits | mask : bits & ~mask);
                break;
        }
    }

    /// <summary>Read 1, 2 or 4 bytes, sign-extended.</summary>
    public int ReadInt(GplMemoryLocation at, int width)
    {
        byte[] area = _areas[at.Area];
        if (at.Offset < 0 || at.Offset + width > area.Length)
        {
            OutOfRange++;
            return 0;
        }
        return width switch
        {
            1 => (sbyte)area[at.Offset],
            2 => BinaryPrimitives.ReadInt16LittleEndian(area.AsSpan(at.Offset)),
            _ => BinaryPrimitives.ReadInt32LittleEndian(area.AsSpan(at.Offset)),
        };
    }

    public void WriteInt(GplMemoryLocation at, int width, int value)
    {
        byte[] area = _areas[at.Area];
        if (at.Offset < 0 || at.Offset + width > area.Length)
        {
            OutOfRange++;
            return;
        }
        switch (width)
        {
            case 1:
                area[at.Offset] = (byte)value;
                break;
            case 2:
                BinaryPrimitives.WriteInt16LittleEndian(area.AsSpan(at.Offset), (short)value);
                break;
            default:
                BinaryPrimitives.WriteInt32LittleEndian(area.AsSpan(at.Offset), value);
                break;
        }
    }

    /// <summary>A string record's text, up to its first 0.</summary>
    public string ReadString(GplMemoryLocation at)
    {
        byte[] area = _areas[at.Area];
        if (at.Offset < 0 || at.Offset + StringSize > area.Length)
        {
            OutOfRange++;
            return "";
        }
        ReadOnlySpan<byte> record = area.AsSpan(at.Offset, StringSize);
        int end = record.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? record : record[..end]);
    }

    /// <summary>Store text in a string record, cut to 41 characters plus the terminating 0.</summary>
    public void WriteString(GplMemoryLocation at, string text)
    {
        byte[] area = _areas[at.Area];
        if (at.Offset < 0 || at.Offset + StringSize > area.Length)
        {
            OutOfRange++;
            return;
        }
        Span<byte> record = area.AsSpan(at.Offset, StringSize);
        record.Clear();
        Encoding.Latin1.GetBytes(text.AsSpan(0, Math.Min(text.Length, StringSize - 1)), record);
    }
}
