using System.Buffers.Binary;

namespace DarkSun.Game.Scripting;

/// <summary>Trigger lists, by the offset of their head in segment <c>377E</c>.</summary>
public enum GplTriggerList
{
    MoveTile = 0x05,
    DoorTile = 0x07,
    MoveBox = 0x09,
    DoorBox = 0x0B,
    Pickup = 0x0D,
    TalkTo = 0x0F,
    Attack = 0x11,
    Look = 0x13,
    Use = 0x15,
    UseWith = 0x17,
}

/// <summary>A script to run: <c>GPL </c> (kind 1) or <c>MAS </c> (kind 2) file, from a byte address.</summary>
public readonly record struct GplEntry(int File, int Address, GplFileKind Kind = GplFileKind.Gpl);

public enum GplFileKind { Gpl = 1, Mas = 2 }

/// <summary>
/// The trigger table, ported byte for byte from DSUN.EXE
/// (<c>dsun-exe-reference.md</c> §4): 200 records of 13 bytes chained into ten
/// sorted lists plus a free list. Records hold <c>+0</c> address, <c>+2</c>
/// file, <c>+4</c>/<c>+6</c> keys, bytes <c>+8</c>–<c>+0A</c>, <c>+0B</c> next.
/// Keeping the raw layout keeps the original's quirks: tile triggers never
/// write byte <c>+0A</c>, inserts walk past a record when either key is
/// smaller, and a full table overwrites the record at the insertion point.
/// </summary>
public sealed class GplTriggers
{
    public const int Capacity = 200;
    public const int RecordSize = 13;
    private const int Next = 0x0B;

    private readonly byte[] _table = new byte[Capacity * RecordSize];
    private readonly Dictionary<GplTriggerList, int> _heads = [];
    private int _free;

    public GplTriggers() => Reset();

    /// <summary>The whole table, list heads and free list, for a saved game.</summary>
    public (byte[] Table, IReadOnlyDictionary<GplTriggerList, int> Heads, int Free) Save() =>
        ([.. _table], new Dictionary<GplTriggerList, int>(_heads), _free);

    /// <summary>Put back what <see cref="Save"/> returned.</summary>
    public void Load(byte[] table, IReadOnlyDictionary<GplTriggerList, int> heads, int free)
    {
        if (table.Length != _table.Length)
        {
            throw new InvalidDataException($"trigger table of {table.Length} bytes, expected {_table.Length}");
        }
        table.CopyTo(_table, 0);
        foreach (GplTriggerList list in Enum.GetValues<GplTriggerList>())
        {
            _heads[list] = heads.GetValueOrDefault(list, -1);
        }
        _free = free;
    }

    /// <summary>As a new game does (<c>main</c>): every byte 0xFF, all records free, all lists empty.</summary>
    public void Reset()
    {
        Array.Fill(_table, (byte)0xFF);
        for (int i = 0; i < Capacity; i++)
        {
            SetWord(i, Next, i + 1 < Capacity ? i + 1 : -1);
        }
        _free = 0;
        foreach (GplTriggerList list in Enum.GetValues<GplTriggerList>())
        {
            _heads[list] = -1;
        }
    }

    /// <summary>Records in a list, in order: (address, file, key A, key B, bytes +8..+0A).</summary>
    public IEnumerable<(int Address, int File, int A, int B, byte B8, byte B9, byte B10)> Records(GplTriggerList list)
    {
        for (int i = _heads[list]; i != -1; i = Word(i, Next))
        {
            yield return (Word(i, 0), Word(i, 2), Word(i, 4), Word(i, 6), Byte(i, 8), Byte(i, 9), Byte(i, 10));
        }
    }

    public int Count(GplTriggerList list) => Records(list).Count();

    // -- creating (043D:30CA..3605, called by the CREATE_*_TRIGGER commands) --------

    /// <summary>CREATE_MOVE_TILE/DOOR_TILE_TRIGGER (x, y, address, file, b): 9 bytes {address, file, x, y, b}.</summary>
    public void AddTile(GplTriggerList list, int x, int y, int address, int file, int b) =>
        InsertPair(list, Struct(address, file, x, y, b), 9, x, y, signed: false);

    /// <summary>
    /// CREATE_MOVE_BOX/DOOR_BOX_TRIGGER (x, y, width, height, address, file, who):
    /// 11 bytes {address, file, x, y, width, height, who}.
    /// </summary>
    public void AddBox(GplTriggerList list, int x, int y, int width, int height, int address, int file, int who) =>
        InsertPair(list, Struct(address, file, x, y, width, height, who), 11, x, y, signed: false);

    /// <summary>
    /// CREATE_PICKUP/TALKTO/ATTACK/LOOK/USE_TRIGGER (address, file, object): 7
    /// bytes {address, file, object, global}. <paramref name="global"/> marks
    /// triggers from <c>MAS 99</c>, which survive region changes.
    /// </summary>
    public void AddObject(GplTriggerList list, int address, int file, int obj, bool global)
    {
        byte[] s = Struct(address, file, obj, global ? 1 : 0);
        int link = Link(list);
        // Walk past records with a smaller object (signed), as 043D:3555 does.
        while (Get(link) is int r and not -1 && (short)Word(r, 4) < (short)obj)
        {
            link = NextLink(r);
        }
        if (Get(link) == -1 || (short)Word(Get(link), 4) != (short)obj)
        {
            Allocate(link);
        }
        CopyTo(link, s, 7);
    }

    /// <summary>CREATE_USEWITH_TRIGGER (object, object, address, file): 9 bytes {address, file, a, b, global}.</summary>
    public void AddUseWith(int a, int b, int address, int file, bool global) =>
        InsertPair(GplTriggerList.UseWith, Struct(address, file, a, b, global ? 1 : 0), 9, a, b, signed: true);

    /// <summary>Insert keyed by (+4, +6), as 043D:3605 / 36F0 / 34A1 do.</summary>
    private void InsertPair(GplTriggerList list, byte[] s, int copy, int a, int b, bool signed)
    {
        int link = Link(list);
        while (Get(link) is int r and not -1 && (Less(Word(r, 4), a, signed) || Less(Word(r, 6), b, signed)))
        {
            link = NextLink(r);
        }
        int at = Get(link);
        if (at == -1 || (ushort)Word(at, 4) != (ushort)a || (ushort)Word(at, 6) != (ushort)b)
        {
            Allocate(link);
        }
        CopyTo(link, s, copy);
    }

    private static bool Less(int recordKey, int key, bool signed) =>
        signed ? (short)recordKey < (short)key : (ushort)recordKey < (ushort)key;

    // -- region change (region_enter_scripts, 039D:0000) -------------------------------

    /// <summary>Drop the region's triggers: all tile and box triggers, and object triggers not from <c>MAS 99</c>.</summary>
    public void ClearRegion()
    {
        foreach (GplTriggerList list in new[] { GplTriggerList.MoveTile, GplTriggerList.DoorTile, GplTriggerList.MoveBox, GplTriggerList.DoorBox })
        {
            while (_heads[list] != -1)
            {
                FreeHead(Link(list));
            }
        }
        foreach (GplTriggerList list in new[] { GplTriggerList.Pickup, GplTriggerList.Use, GplTriggerList.Look, GplTriggerList.Attack, GplTriggerList.TalkTo })
        {
            RemoveLocal(Link(list), globalByte: 6);
        }
        RemoveLocal(Link(GplTriggerList.UseWith), globalByte: 8);
    }

    private void RemoveLocal(int link, int globalByte)
    {
        while (Get(link) is int r and not -1)
        {
            if (Byte(r, globalByte) == 0)
            {
                FreeHead(link);
            }
            else
            {
                link = NextLink(r);
            }
        }
    }

    // -- matching (039D:04E9, 0630, 07B9, 03BC) ------------------------------------------

    /// <summary>
    /// A move to cell (x, y): the first tile trigger at the cell, else the first
    /// box containing it, whose byte <c>+0A</c> is at least <paramref name="who"/>
    /// (0 for the party's active member, 1 for the others).
    /// </summary>
    public GplEntry? MatchMove(int x, int y, int who) =>
        MatchTile(GplTriggerList.MoveTile, x, y, who) ?? MatchBox(GplTriggerList.MoveBox, x, y, who);

    public GplEntry? MatchTile(GplTriggerList list, int x, int y, int who)
    {
        for (int r = _heads[list]; r != -1; r = Word(r, Next))
        {
            if ((short)Word(r, 4) == x && (short)Word(r, 6) == y && Byte(r, 10) >= who)
            {
                return Entry(r);
            }
        }
        return null;
    }

    public GplEntry? MatchBox(GplTriggerList list, int x, int y, int who)
    {
        for (int r = _heads[list]; r != -1; r = Word(r, Next))
        {
            int left = (short)Word(r, 4);
            int top = (short)Word(r, 6);
            if (x >= left && x <= left + Byte(r, 8) - 1 && y >= top && y <= top + Byte(r, 9) - 1 && Byte(r, 10) >= who)
            {
                return Entry(r);
            }
        }
        return null;
    }

    /// <summary>Pick up, talk to, attack, look, use: the first trigger for the target's id.</summary>
    public GplEntry? MatchObject(GplTriggerList list, int targetId)
    {
        for (int r = _heads[list]; r != -1; r = Word(r, Next))
        {
            if ((short)Word(r, 4) == (short)targetId)
            {
                return Entry(r);
            }
        }
        return null;
    }

    /// <summary>Use with: the two ids in either order.</summary>
    public GplEntry? MatchUseWith(int a, int b)
    {
        for (int r = _heads[GplTriggerList.UseWith]; r != -1; r = Word(r, Next))
        {
            int ra = (short)Word(r, 4);
            int rb = (short)Word(r, 6);
            if ((ra == (short)a && rb == (short)b) || (ra == (short)b && rb == (short)a))
            {
                return Entry(r);
            }
        }
        return null;
    }

    private GplEntry Entry(int r) => new((short)Word(r, 2), (ushort)Word(r, 0));

    // -- table plumbing ----------------------------------------------------------------

    // A link is where a record index is stored: a list head (encoded as −2 − list)
    // or a record's next field (its record index).
    private static int Link(GplTriggerList list) => -2 - (int)list;

    private static int NextLink(int record) => record;

    private int Get(int link) => link < -1 ? _heads[(GplTriggerList)(-2 - link)] : (short)Word(link, Next);

    private void Set(int link, int value)
    {
        if (link < -1)
        {
            _heads[(GplTriggerList)(-2 - link)] = value;
        }
        else
        {
            SetWord(link, Next, value);
        }
    }

    /// <summary>043D:37DB: take a free record and link it in at <paramref name="link"/>.</summary>
    private void Allocate(int link)
    {
        int old = Get(link);
        int fresh = TakeFree();
        if (old == -1)
        {
            Set(link, fresh);
        }
        else if (fresh != -1)
        {
            SetWord(fresh, Next, old);
            Set(link, fresh);
        }
        // A full table leaves the link alone: the struct then overwrites that record.
    }

    /// <summary>043D:382F.</summary>
    private int TakeFree()
    {
        if (_free == -1)
        {
            return -1;
        }
        int r = _free;
        _free = (short)Word(r, Next);
        SetWord(r, Next, -1);
        return r;
    }

    /// <summary>039D:0846: unlink the record at <paramref name="link"/> and free it.</summary>
    private void FreeHead(int link)
    {
        int r = Get(link);
        if (r == -1)
        {
            return;
        }
        Set(link, (short)Word(r, Next));
        SetWord(r, Next, _free);
        _free = r;
    }

    private void CopyTo(int link, byte[] s, int count)
    {
        int r = Get(link);
        if (r != -1)
        {
            s.AsSpan(0, count).CopyTo(_table.AsSpan(r * RecordSize));
        }
    }

    private static byte[] Struct(int address, int file, int a, int b, params int[] bytes)
    {
        var s = new byte[8 + bytes.Length];
        BinaryPrimitives.WriteInt16LittleEndian(s, (short)address);
        BinaryPrimitives.WriteInt16LittleEndian(s.AsSpan(2), (short)file);
        BinaryPrimitives.WriteInt16LittleEndian(s.AsSpan(4), (short)a);
        BinaryPrimitives.WriteInt16LittleEndian(s.AsSpan(6), (short)b);
        for (int i = 0; i < bytes.Length; i++)
        {
            s[8 + i] = (byte)bytes[i];
        }
        return s;
    }

    private int Word(int record, int offset) => BinaryPrimitives.ReadInt16LittleEndian(_table.AsSpan(record * RecordSize + offset));

    private void SetWord(int record, int offset, int value) =>
        BinaryPrimitives.WriteInt16LittleEndian(_table.AsSpan(record * RecordSize + offset), (short)value);

    private byte Byte(int record, int offset) => _table[record * RecordSize + offset];
}

/// <summary>
/// Per-creature scripts (table <c>3972:08AA</c>), keyed by object index
/// (<see cref="IGplHost.ScriptObjects"/>), or by a name (e.g. −248 for
/// <c>name(-248)</c>) that matched no object when it was set, for any object
/// that has it later. An object's own entry comes first (<see cref="NoOrdersFor"/>,
/// <see cref="SightFor"/>). Cleared when a region is entered.
/// </summary>
public sealed class GplCreatureScripts
{
    private readonly Dictionary<int, GplEntry> _noOrders = [];
    private readonly Dictionary<int, (int Kind, GplEntry Entry, int Range)> _sight = [];

    public IReadOnlyDictionary<int, GplEntry> NoOrders => _noOrders;

    /// <summary>Kind 1: runs when the creature can see the active party member within range; kind 2: when it can't.</summary>
    public IReadOnlyDictionary<int, (int Kind, GplEntry Entry, int Range)> Sight => _sight;

    public void SetNoOrders(int obj, GplEntry entry) => _noOrders[obj] = entry;

    public void SetSight(int obj, int kind, GplEntry entry, int range) => _sight[obj] = (kind, entry, range);

    /// <summary>
    /// CLEAR_LOS: an object's script is cleared, overriding a name's
    /// (<see cref="SightFor"/>); a name's is removed.
    /// </summary>
    public void ClearSight(int obj)
    {
        if (obj >= 0)
        {
            _sight[obj] = (0, default, 0);
        }
        else
        {
            _sight.Remove(obj);
        }
    }

    /// <summary>The no-orders script of object <paramref name="index"/> with id <paramref name="id"/>: its own, else its name's.</summary>
    public GplEntry? NoOrdersFor(int index, int id) =>
        _noOrders.TryGetValue(index, out GplEntry own) ? own : _noOrders.TryGetValue(id, out GplEntry named) ? named : null;

    /// <summary>The line-of-sight script of object <paramref name="index"/> with id <paramref name="id"/>: its own, else its name's.</summary>
    public (int Kind, GplEntry Entry, int Range)? SightFor(int index, int id) =>
        _sight.TryGetValue(index, out var own) ? own : _sight.TryGetValue(id, out var named) ? named : null;

    public void Clear()
    {
        _noOrders.Clear();
        _sight.Clear();
    }

    /// <summary>Replace every script with <paramref name="noOrders"/> and <paramref name="sight"/> (a saved game's).</summary>
    public void Load(IEnumerable<KeyValuePair<int, GplEntry>> noOrders, IEnumerable<KeyValuePair<int, (int Kind, GplEntry Entry, int Range)>> sight)
    {
        Clear();
        foreach (var (obj, entry) in noOrders)
        {
            _noOrders[obj] = entry;
        }
        foreach (var (obj, s) in sight)
        {
            _sight[obj] = s;
        }
    }
}
