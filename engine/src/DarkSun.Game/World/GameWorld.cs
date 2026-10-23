using DarkSun.Data.Animation;
using DarkSun.Data.Gff;
using DarkSun.Data.Items;
using DarkSun.Data.Regions;
using DarkSun.Game.Movement;
using DarkSun.Game.Scripting;

namespace DarkSun.Game.World;

public enum ObjectKind { Party, Creature, Thing }

/// <summary>
/// A door's flags (its item record's byte +0x0F; <c>6BB8:0D13</c>, <c>0B56</c>,
/// <c>0FF8</c>). In the shipped data 48 doors start closed, 38 closed and
/// locked, 72 closed and stuck, 6 open and stuck, 2 open.
/// </summary>
public static class Doors
{
    /// <summary>Closed: its cells block movement and sight.</summary>
    public const int Closed = 0x01;

    /// <summary>Locked ("THE DOOR IS LOCKED" when it doesn't move). Using it still opens it, as DSUN.EXE's code does; scripts lock and unlock.</summary>
    public const int Locked = 0x02;

    /// <summary>Stuck: using it does nothing ("THE DOOR WON'T OPEN"); scripts can still move it.</summary>
    public const int Stuck = 0x04;

    /// <summary>A secret door: using it reveals it ("THERE'S A SECRET DOOR!") and clears this flag.</summary>
    public const int Secret = 0x08;
}

/// <summary>Using an object: whether a door was closed before, and whether anything moved or toggled.</summary>
public sealed record DoorUse(WorldObject Object, bool WasClosed, bool Toggled, bool Rested = false);

/// <summary>
/// A creature's order (<c>1A0A:03CA</c>, table <c>3972:08AA</c>): what it is
/// doing, set by HUNT, GO, GOXY, FOLLOW, FLEE, FETCH and WAIT.
/// </summary>
public enum OrderMode
{
    None = 0x00,
    Flee = 0x01,
    Fetch = 0x0A,
    Follow = 0x0B,
    Go = 0x0D,
    GoXY = 0x0F,
    Wait = 0x11,
    Hunt = 0x12,
}

/// <param name="Target">GO / FOLLOW / HUNT / FETCH: the object index; GOXY: x.</param>
/// <param name="Extra">GOXY: y.</param>
public readonly record struct Order(OrderMode Mode, int Target, int Extra);

/// <summary>An object in the region: a party member, a creature or a thing.</summary>
public sealed class WorldObject
{
    private readonly Dictionary<int, int> _fields = [];

    public WorldObject(int index, ObjectKind kind, int id)
    {
        Index = index;
        Kind = kind;
        Id = id;
    }

    /// <summary>Index in the object table (0–319); 0–3 are the party, 4–47 can take orders.</summary>
    public int Index { get; }

    public ObjectKind Kind { get; }

    /// <summary>Field 72 (0x48): −<c>OJFF</c> number for region objects, which is what <c>name(-n)</c> matches.</summary>
    public int Id { get; set; }

    /// <summary>Field 19.</summary>
    public string Name { get; set; } = "";

    /// <summary>The region object this came from, if any.</summary>
    public RegionObject? Source { get; init; }

    /// <summary>Made at run time, drawn on its own: a CLONE copy of the object that has <see cref="Source"/>, or a new object (a dropped item, <c>REQUEST(9)</c>) whose <see cref="Source"/> has <c>EtabIndex</c> −1.</summary>
    public bool IsClone { get; internal set; }

    public CombatRecord? Record { get; init; }

    /// <summary>A creature's character record (its size, …), from its <c>RDFF</c>.</summary>
    public CharacterRecord? Character { get; init; }

    /// <summary>For an item (an object whose <c>RDFF</c> is an item record): that record.</summary>
    public ItemRecord? Item { get; init; }

    public Walker? Walker { get; set; }

    /// <summary>Cell for objects without a walker.</summary>
    public Cell FixedCell { get; set; }

    public Cell Cell => Walker?.Cell ?? FixedCell;

    /// <summary>Taken off the map (TELEPORT to another region).</summary>
    public bool Hidden { get; set; }

    /// <summary>Travelling inside the party leader; still present for scripts and character sheets.</summary>
    public bool Collapsed { get; internal set; }

    public Order Order { get; set; }

    /// <summary>Object flag 0x40, toggled by <c>REQUEST(5)</c> and by using a lever-like object.</summary>
    public bool SecondState { get; set; }

    /// <summary>
    /// A door: an item whose <c>OJFF</c> number is 0x46A–0x49C or 0x803–0x898
    /// (<c>6BB8:0B33</c>). Its state is <see cref="DoorFlags"/>.
    /// </summary>
    public bool IsDoor => Item is not null && Source is { } s && GameWorld.IsDoorNumber(s.Ojff);

    /// <summary>A door's state: its item record's flags (+0x0F), changed by opening and closing (<see cref="Doors"/>).</summary>
    public int DoorFlags { get; set; }

    /// <summary>Where its animation starts when the region is loaded (an <c>SCMD</c> record), e.g. a door's closed or open state.</summary>
    public int AnimationStart { get; set; }

    /// <summary>Combat record allegiance (<c>+0x1D</c>), set by <c>REQUEST(18)</c>; field 74 (<c>GPLX</c>).</summary>
    public int Allegiance { get; set; }

    /// <summary>Combat record status (<c>+0x1C</c>), read by GET_STATUS (see <see cref="Vitals"/>).</summary>
    public int Status
    {
        get => Vitals.Status;
        set => Vitals.Status = value;
    }

    /// <summary>Hit points and status; a party member's are kept for the whole game (<see cref="PartyMemberState"/>).</summary>
    public Vitals Vitals { get; set; } = new();

    /// <summary>What it carries (a creature, a party member, a container); a party member's list is kept for the whole game.</summary>
    public List<Item> Items { get; set; } = [];

    /// <summary>Facing 0–7 clockwise from north (field 95).</summary>
    public int Facing => Walker is { } w ? (int)w.Facing : _fields.GetValueOrDefault(95);

    public bool CanTakeOrders => Index < GameWorld.MaxCreatures && Walker is not null && Kind != ObjectKind.Party;

    /// <summary>
    /// What the AI does when no condition overrides it (combat record +0x1E,
    /// field 75; <see cref="CombatRecord.Mind"/>): scripts set it to 2 (fight)
    /// after making a creature hostile with <c>REQUEST(18)</c>. Kept with the
    /// stored fields, so a region's state keeps it.
    /// </summary>
    public int Mind
    {
        get => _fields.TryGetValue(FieldMind, out int m) ? m : Record?.Mind ?? 0;
        set => _fields[FieldMind] = value;
    }

    /// <summary>The combat record fields this models (<c>GPLX</c> group 1): +0 hit points, +6 id, +0x1C status, +0x1D allegiance, +0x1E mind, +0x38 facing.</summary>
    public const int FieldHp = 67, FieldId = 72, FieldStatus = 73, FieldAllegiance = 74, FieldMind = 75, FieldFacing = 95;

    /// <summary>Field 0 of an item, record +0x0F: a door's state (<see cref="DoorFlags"/>).</summary>
    public const int FieldItemFlags = 0;

    /// <summary>
    /// Script-visible fields: those modelled (hit points, id, status,
    /// allegiance — a party member's 0 reads as 1, the party's side — mind,
    /// facing, a door's state), else what a script stored, else 0.
    /// <see cref="WorldGplHost"/> reads the rest from the records.
    /// </summary>
    public int ReadField(int field) => field switch
    {
        FieldHp when Kind != ObjectKind.Thing => Vitals.Hp,
        FieldId => Id,
        FieldStatus when Kind != ObjectKind.Thing => Status,
        FieldAllegiance when Kind != ObjectKind.Thing => Kind == ObjectKind.Party && Allegiance == 0 ? 1 : Allegiance,
        FieldFacing => Facing,
        FieldItemFlags when IsDoor => DoorFlags,
        _ => _fields.GetValueOrDefault(field),
    };

    /// <summary>Whether <see cref="ReadField"/> answers <paramref name="field"/> itself (modelled, or written by a script).</summary>
    public bool Knows(int field) => _fields.ContainsKey(field) || field is FieldId or FieldFacing
        || Kind != ObjectKind.Thing && field is FieldHp or FieldStatus or FieldAllegiance
        || IsDoor && field == FieldItemFlags;

    public void WriteField(int field, int value)
    {
        switch (field)
        {
            case FieldHp when Kind != ObjectKind.Thing: Vitals.Hp = value; break;
            case FieldId: Id = value; break;
            case FieldStatus when Kind != ObjectKind.Thing: Status = value; break;
            case FieldAllegiance when Kind != ObjectKind.Thing: Allegiance = value; break;
            case FieldItemFlags when IsDoor: DoorFlags = value; break;
            default: _fields[field] = value; break;
        }
    }

    /// <summary>Field 23 + i: ability i (Str … Cha) in the combat record (+0x22 + i).</summary>
    public const int FieldStr = 23;

    /// <summary>Character record fields: 30 Dexterity (+0x1C), 46 the base armour class (+0x27).</summary>
    public const int SheetDex = 30, SheetBaseArmorClass = 46;

    /// <summary>A combat record field a script (or <see cref="GameWorld.StageArenaBout"/>) wrote, or null.</summary>
    public int? StoredField(int field) => _fields.TryGetValue(field, out int v) ? v : null;

    /// <summary>A field of the creature's character record a script wrote (<c>obj.71.f</c>), kept with the stored fields under 1000 + f.</summary>
    public int? SheetField(int field) => _fields.TryGetValue(SheetFields + field, out int v) ? v : null;

    public void WriteSheetField(int field, int value) => _fields[SheetFields + field] = value;

    private const int SheetFields = 1000;

    /// <summary>The fields scripts wrote that aren't modelled (for <see cref="RegionState"/>).</summary>
    public IReadOnlyDictionary<int, int> StoredFields => _fields;

    internal void RestoreFields(IReadOnlyDictionary<int, int> fields)
    {
        _fields.Clear();
        foreach (var (field, value) in fields)
        {
            _fields[field] = value;
        }
    }
}

/// <summary>What the view needs to draw a walking character: its script and frame sizes.</summary>
/// <summary>What a walking character needs: its script, its frame sizes and the cells it covers.</summary>
public sealed record CharacterSpec(Scmd Script, IReadOnlyList<(int Width, int Height)> Frames, Footprint Footprint = default);

/// <summary>
/// The objects of the current region and what scripts do to them: the
/// engine's model of DSUN.EXE's object table (<c>DS:6694</c>, 320 entries),
/// creature table (<c>3972:08AA</c>) and the script engine variables.
/// <see cref="Update"/> carries out the creatures' orders.
/// </summary>
public sealed partial class GameWorld
{
    /// <summary>
    /// Creatures take the slots below this (the party 0–3), as in DSUN.EXE;
    /// things (items, bodies, scenery) take the slots from here on, as many as
    /// the region needs. DSUN.EXE's whole table holds 320 (<c>0x140</c>), but it
    /// makes a map entry an object only when it is drawn (<c>1DBD:0002</c> →
    /// <c>1DF3:1157</c>) and, when the table is full, gives back the slots of
    /// things off screen (<c>1DF3:0F7C</c>). Here every entry is an object for as
    /// long as the region is shown, so the things' part grows instead: regions
    /// have up to 645 entries (region 38), and dropped items and bodies are
    /// added to them.
    /// </summary>
    public const int MaxCreatures = 0x30;
    public const int PartySize = 4;

    /// <summary>DSUN.EXE's table size, which this world once kept to: regions saved then have no objects from the map past it (<see cref="Restore"/>).</summary>
    private const int OldTable = 0x140;
    public const int None = 9999;

    /// <summary>Ticks between a creature's re-plans while it follows a moving target (about 0.5 s).</summary>
    private const int ReplanTicks = 48;

    /// <summary>Ticks between checks for idle creatures and line-of-sight scripts (about 0.5 s).</summary>
    private const int ScriptCheckTicks = 48;

    /// <summary>The objects by slot (<see cref="MaxCreatures"/>); it grows as things are added.</summary>
    private readonly List<WorldObject?> _objects = [.. new WorldObject?[MaxCreatures]];
    private readonly int[] _engine;
    private readonly Dictionary<int, Scmd?> _scripts = [];
    private readonly IResourceSource _objectData;
    private readonly IReadOnlyList<PartyMemberState> _party;
    private readonly Dictionary<int, long> _nextPlan = [];
    private long _nextScriptCheck;

    /// <summary>A world for <paramref name="region"/>: its <c>ETAB</c> objects, creatures from their <c>RDFF</c> records.</summary>
    /// <param name="objectData"><c>SEGOBJEX.GFF</c> (and <c>GPLDATA.GFF</c>): <c>RDFF</c> and <c>SCMD</c> resources.</param>
    /// <param name="engine">The engine variables (<c>gname[0–14]</c>), kept across regions; a new set if null.</param>
    /// <param name="party">What the party members carry and how they are, kept across regions; new ones if null.</param>
    /// <param name="difficulty">GAME DIFFICULTY (<see cref="Difficulty"/>).</param>
    public GameWorld(Region region, IResourceSource objectData, int[]? engine = null, IReadOnlyList<PartyMemberState>? party = null, int difficulty = 1)
    {
        Region = region;
        Difficulty = Math.Clamp(difficulty, 0, 3);
        _party = party ?? [.. Enumerable.Range(0, PartySize).Select(_ => new PartyMemberState())];
        _objectData = objectData;
        _engine = engine ?? new int[15];
        _engine[4] = region.Number;
        Items = ItemCatalog.Load(objectData);
        int nextCreature = PartySize;
        int nextThing = MaxCreatures;

        foreach (RegionObject source in region.Objects)
        {
            bool creature = IsCreature(source.Ojff);
            int index = creature && nextCreature < MaxCreatures ? nextCreature++ : nextThing++;
            Put(index, MakeObject(index, source));
        }
        SetUpDoors();
    }

    /// <summary>Has <c>OJFF</c> <paramref name="ojff"/> a combat record (a creature, which takes an index below 48)?</summary>
    private bool IsCreature(int ojff) =>
        _objectData.TryRead("RDFF", ojff, out var rdff) && CombatRecord.Parse(rdff.Span) is not null;

    /// <summary>
    /// The object for <paramref name="source"/> at <paramref name="index"/>:
    /// a creature (from its combat and character records; with a walker if its
    /// <c>SCMD</c> can walk) or a thing (an item if its <c>RDFF</c> is an item record).
    /// </summary>
    private WorldObject MakeObject(int index, RegionObject source)
    {
        ReadOnlyMemory<byte> rdff = _objectData.TryRead("RDFF", source.Ojff, out var data) ? data : default;
        CreatureDefinition? definition = rdff.IsEmpty ? null : CreatureDefinition.FromRdff(rdff);
        CombatRecord? record = definition?.Combat;
        ItemRecord? item = record is null && !rdff.IsEmpty ? ItemRecord.FromRdff(rdff) : null;
        CharacterRecord? character = definition?.Character;
        bool creature = record is not null;
        // DSUN.EXE gives a creature its character record's hit points, scaled by difficulty (7155:0000).
        int hp = Scaled(definition?.HitPoints ?? 0);
        var obj = new WorldObject(index, creature ? ObjectKind.Creature : ObjectKind.Thing, -source.Ojff)
        {
            Source = source,
            Record = record,
            Item = item,
            Character = character,
            Name = record?.Name ?? (item is not null ? Items.NameOf(item) : ""),
            Allegiance = record?.Allegiance ?? 0,
            Vitals = new Vitals { Hp = hp, MaxHp = hp, Status = record?.Status ?? 0 },
            FixedCell = new Cell(source.EtabX / Region.TileSize, Math.Max(0, source.EtabY - 1) / Region.TileSize),
            // A container thing (a chest, a coffin) links its contents the way a creature links what it carries.
            Items = [.. (definition?.Carried ?? (item is not null ? CreatureDefinition.CarriedIn(Rdff.Entries(rdff)) : [])).Select(CarriedItem)],
        };

        // Creatures whose animation can walk get a walker, so orders can move them.
        if (creature && source.Scmd > 0 && ScriptOf(source.Scmd) is { CanWalk: true } scmd)
        {
            var spec = new CharacterSpec(scmd, [.. source.Frames.Select(f => (f.Width, f.Height))], Footprint.Of(character));
            if (NearestCell(obj.FixedCell, c => Fits(c, spec.Footprint)) is { } cell)
            {
                obj.Walker = new Walker(index, spec.Script, spec.Frames, cell, spec.Footprint, Occupancy);
            }
        }
        return obj;
    }

    /// <summary>
    /// An item a creature's <c>RDFF</c> gives it: the item record's own
    /// <c>RDFF</c> (−id), worn in its slot or in the pack; a quantity only for a
    /// type that stacks (otherwise the quantity is the spell a magic item casts).
    /// </summary>
    private Item CarriedItem(ItemRecord record) =>
        new(Math.Abs(record.Id), record, Stacks(record) ? Math.Max(1, (int)record.Quantity) : 1,
            record.Slot < Equipment.Slots ? record.Slot : Equipment.Pack);

    public Region Region { get; }

    /// <summary>
    /// GAME DIFFICULTY, 0–3 (easy, balanced, hard, hideous). DSUN.EXE scales a
    /// word of a creature's character record by (difficulty − 1) × 50 + 100 %
    /// when it loads one (<see cref="CharacterRecord"/>, likely its hit points);
    /// here creatures' hit points get 50, 100, 150 or 200 %.
    /// </summary>
    public int Difficulty { get; }

    private int Scaled(int hp) => Math.Max(hp > 0 ? 1 : 0, hp * (Difficulty * 50 + 50) / 100);

    /// <summary>Item types and names (<c>IT1R</c>, <c>NAME</c>), for naming and describing items.</summary>
    public ItemCatalog Items { get; }

    /// <summary>The engine variables (<c>gname[0–14]</c>) this world reads and writes.</summary>
    public int[] EngineVariables => _engine;

    public Occupancy Occupancy { get; } = new();

    public IEnumerable<WorldObject> Objects => _objects.OfType<WorldObject>();

    public IEnumerable<WorldObject> Party => Enumerable.Range(0, PartySize).Select(i => _objects[i]).OfType<WorldObject>();

    public WorldObject? this[int index] => index >= 0 && index < _objects.Count ? _objects[index] : null;

    /// <summary>Put <paramref name="obj"/> in slot <paramref name="index"/>, growing the table to it.</summary>
    private void Put(int index, WorldObject? obj)
    {
        while (_objects.Count <= index)
        {
            _objects.Add(null);
        }
        _objects[index] = obj;
    }

    /// <summary>
    /// A free slot: for a creature the first free below <see cref="MaxCreatures"/>
    /// (−1 if none), for a thing the first free from there on, else a new one.
    /// </summary>
    private int FreeSlot(bool creature)
    {
        int first = creature ? PartySize : MaxCreatures, last = creature ? MaxCreatures : _objects.Count;
        int index = Enumerable.Range(first, last - first).FirstOrDefault(n => _objects[n] is null, -1);
        return index >= 0 || creature ? index : _objects.Count;
    }

    /// <summary>The party's active member (<c>gname[5]</c>, <c>3781:0369</c>).</summary>
    public int ActiveMember
    {
        get => _engine[5];
        set => _engine[5] = value;
    }

    /// <summary>Scripts the world wants run: idle creatures' no-orders scripts and line-of-sight scripts.</summary>
    public Queue<(GplEntry Entry, int Actor, string Why)> PendingScripts { get; } = new();

    /// <summary>Region change a script asked for (TELEPORT of the party to another region).</summary>
    public (int Region, int X, int Y)? PendingRegion { get; set; }

    /// <summary>Objects whose appearance changed (moved, hidden, animation restarted), for the view.</summary>
    public event Action<WorldObject, int?>? Changed;

    // -- party -------------------------------------------------------------------------

    /// <summary>Put a party member (0–3) at the free cell nearest <paramref name="near"/>, facing <paramref name="facing"/>.</summary>
    public WorldObject? AddPartyMember(int slot, CharacterSpec spec, Cell near, Direction facing = Direction.South)
    {
        ExpandParty();
        if (slot is < 0 or >= PartySize || NearestCell(near, c => Fits(c, spec.Footprint)) is not { } cell)
        {
            return null;
        }
        _objects[slot]?.Walker?.Remove();
        var obj = new WorldObject(slot, ObjectKind.Party, slot)
        {
            Name = $"Character {slot + 1}",
            Walker = new Walker(slot, spec.Script, spec.Frames, cell, spec.Footprint, Occupancy, facing),
            Items = _party[slot].Items,
            Vitals = _party[slot].Vitals,
        };
        _objects[slot] = obj;
        return obj;
    }

    public void RemovePartyMember(int slot)
    {
        ExpandParty();
        _objects[slot]?.Walker?.Remove();
        if (slot is >= 0 and < PartySize)
        {
            _objects[slot] = null;
        }
    }

    // -- selection (1A0A:0922) ----------------------------------------------------------

    /// <summary>
    /// The objects a script value refers to: a negative value is a name (every
    /// object whose id is that value), 0 or more an object index, 32766 the
    /// party, 9999 none.
    /// </summary>
    public IEnumerable<WorldObject> Select(int value)
    {
        if (value == None)
        {
            yield break;
        }
        if (value == DialogueGplHost.WholeParty)
        {
            // 32766: the party members (1A0A:0922 walks them as 1A0A:02E5 does).
            foreach (WorldObject member in Party.Where(p => !p.Hidden))
            {
                yield return member;
            }
            yield break;
        }
        if (value >= 0)
        {
            if (this[value] is { } obj)
            {
                yield return obj;
            }
            yield break;
        }
        foreach (WorldObject obj in Objects)
        {
            if (obj.Id == value && !obj.Hidden)
            {
                yield return obj;
            }
        }
    }

    public WorldObject? First(int value) => Select(value).FirstOrDefault();

    // -- engine variables (gname, 3781:0000 pointer table) -----------------------------

    /// <summary>Engine variable by raw index 0x20–0x2C.</summary>
    public int EngineVariable(int raw) => raw is >= 0x20 and < 0x2F ? _engine[raw - 0x20] : 0;

    public void SetEngineVariable(int raw, int value)
    {
        if (raw is >= 0x20 and < 0x2F)
        {
            _engine[raw - 0x20] = value;
        }
    }

    /// <summary>The object a special reference names: <c>obj_special(n)</c> is the object whose index engine variable n holds.</summary>
    public WorldObject? Special(int n) => this[EngineVariable(n)];

    // -- orders and queries (1A0A) -------------------------------------------------------

    /// <summary>
    /// Give every creature matching <paramref name="who"/> an order
    /// (<c>1A0A:03CA</c>): GO / FOLLOW / HUNT / FETCH need their target to
    /// exist and keep its first match's index.
    /// </summary>
    public void SetOrder(int who, OrderMode mode, int target, int extra)
    {
        int resolved = target;
        if (mode is not (OrderMode.GoXY or OrderMode.Wait))
        {
            if (First(target) is not { } t)
            {
                return;
            }
            resolved = t.Index;
        }
        foreach (WorldObject obj in Select(who).Where(o => o.Index < MaxCreatures))
        {
            obj.Order = new Order(mode, resolved, extra);
            _nextPlan[obj.Index] = 0;
        }
    }

    public int ReadOrders(int who) => this[who] is { Index: < MaxCreatures } obj ? (int)obj.Order.Mode : 0;

    /// <summary>
    /// RANGE (<c>1A0A:01A0</c>): the distance from object <paramref name="from"/>
    /// to the nearest other object matching <paramref name="to"/>; that object
    /// becomes "other" (<c>gname[8]</c>). 999 if none.
    /// </summary>
    public int Range(int from, int to)
    {
        if (this[from] is not { } a)
        {
            return GplGeometry.Far;
        }
        int best = GplGeometry.Far;
        foreach (WorldObject b in Select(to).Where(b => b != a && !b.Hidden))
        {
            int d = GplGeometry.Distance(a.Cell.X, a.Cell.Y, b.Cell.X, b.Cell.Y);
            if (d < best)
            {
                best = d;
                SetEngineVariable(0x28, b.Index);
            }
        }
        return best;
    }

    /// <summary>GET_LOS (<c>1A0A:00B3</c>): is any object matching <paramref name="to"/> in sight of <paramref name="from"/> within <paramref name="range"/>?</summary>
    public bool InSight(int from, int to, int range)
    {
        if (this[from] is not { } a)
        {
            return false;
        }
        bool seen = false;
        foreach (WorldObject b in Select(to).Where(b => b != a && !b.Hidden))
        {
            if (GplGeometry.LineOfSight(Region, a.Cell.X, a.Cell.Y, b.Cell.X, b.Cell.Y)
                && GplGeometry.Distance(a.Cell.X, a.Cell.Y, b.Cell.X, b.Cell.Y) <= (range & 0xFF))
            {
                seen = true;
                SetEngineVariable(0x28, b.Index);
            }
        }
        return seen;
    }

    /// <summary>GETXY (<c>1A0A:0256</c>): the cell of the (last) matching object into <c>gname[1]</c>, <c>gname[2]</c>; <c>gname[3]</c> = 0.</summary>
    public void GetXY(int who)
    {
        foreach (WorldObject obj in Select(who))
        {
            SetEngineVariable(0x21, obj.Cell.X);
            SetEngineVariable(0x22, obj.Cell.Y);
            SetEngineVariable(0x23, 0);
        }
    }

    /// <summary>GET_STATUS: the named creature's status, 8 if there is none (<c>1A0A:02B6</c>).</summary>
    public int Status(int who) => First(who) is { Record: not null } c ? c.Status : 8;

    /// <summary><c>REQUEST(18)</c> (<c>87B3:12DC</c>): set the allegiance of matching creatures outside the party.</summary>
    public void SetAllegiance(int who, int value)
    {
        foreach (WorldObject obj in Select(who).Where(o => o.Kind == ObjectKind.Creature))
        {
            obj.Allegiance = value & 0xFF;
        }
    }

    // -- placing objects ---------------------------------------------------------------------

    /// <summary>
    /// TELEPORT objects (<c>7372:06DF</c>): to another region (255: off the
    /// map) they leave this one; here they go to the free cell nearest (x, y).
    /// The party (who 32766) to another region becomes <see cref="PendingRegion"/>.
    /// </summary>
    public void Teleport(int who, int region, int x, int y)
    {
        IEnumerable<WorldObject> targets = who == DialogueGplHost.WholeParty ? Party : Select(who);
        if (who == DialogueGplHost.WholeParty && region != Region.Number)
        {
            PendingRegion = (region, x, y);
            return;
        }
        int? collapsedLeader = who == DialogueGplHost.WholeParty && region == Region.Number ? CollapsedLeader : null;
        List<int> collapsed = [.. Party.Where(p => p.Collapsed).Select(p => p.Index)];
        if (targets.Any(p => p.Kind == ObjectKind.Party))
        {
            ExpandParty();
        }
        foreach (WorldObject obj in targets.ToList())
        {
            if (region != Region.Number)
            {
                obj.Hidden = true;
                obj.Walker?.Remove();
            }
            else
            {
                obj.Hidden = false;
                Place(obj, new Cell(x, y));
            }
            Changed?.Invoke(obj, null);
        }
        if (collapsedLeader is { } leader)
        {
            RestoreCollapsedParty(leader, collapsed);
        }
    }

    /// <summary>
    /// REQUEST(14) (<c>87B3:0FE9</c>), the arena's opening bout, which the party
    /// watches: from the active member (<c>gname[5]</c>; the request's object,
    /// region 42's gate, isn't read) at (x, y), the mage Celgor (<c>OJFF</c> 296) at (x − 3, y + 18) on the party's side
    /// (allegiance 1), the rampager (171) at (x + 3, y + 18) and two more
    /// (269) at (x + 3, y + 16) and (x + 3, y + 20), each made as CLONE makes
    /// one (<c>7372:0A44</c>, within 6 cells) with field 0x4A 1 or 2; the
    /// party's allegiance becomes 4, which keeps it out of the fight. Returns
    /// Celgor, where FIGHT starts (<c>7372:030B</c>) and the view centres
    /// (<c>191F:01FF</c>), or null with no active member. Celgor also gets
    /// Dexterity 25 (combat record +0x23 and character record +0x1C = 0x19) and
    /// base armour class −3 (character record +0x27 = 0xFD): armour class −9 in
    /// all, so the rampager and the sligs seldom hit him.
    /// </summary>
    public WorldObject? StageArenaBout()
    {
        if (this[ActiveMember] is not { } gate)
        {
            return null;
        }
        WorldObject? Make(int ojff, int dx, int dy, int field74)
        {
            Cell at = new(gate.Cell.X + dx, gate.Cell.Y + dy);
            if (NearestCell(at, c => Fits(c, Footprint.OneCell) && GplGeometry.Distance(c.X, c.Y, at.X, at.Y) <= 6) is not { } cell
                || Spawn(ojff, cell) is not { } made)
            {
                return null;
            }
            made.WriteField(0x4A, field74);
            return made;
        }
        WorldObject? celgor = Make(296, -3, 18, 1);
        if (celgor is { Kind: ObjectKind.Creature })
        {
            // Allegiance 1, Dexterity 25 in both records, base armour class −3: a mage who lasts.
            celgor.Allegiance = 1;
            celgor.WriteField(WorldObject.FieldStr + 1, 25);
            celgor.WriteSheetField(WorldObject.SheetDex, 25);
            celgor.WriteSheetField(WorldObject.SheetBaseArmorClass, -3);
        }
        Make(171, 3, 18, 2);
        Make(269, 3, 16, 2);
        Make(269, 3, 20, 2);
        foreach (WorldObject member in Party)
        {
            member.Allegiance = WatchingAllegiance;
        }
        return celgor;
    }

    /// <summary>
    /// The game clock (<c>gname[9]</c>, <c>3781:035B</c>), in seconds: one more
    /// every <see cref="ClockTicks"/> ticks of the 96 Hz timer while the map
    /// runs (<c>1DF3:29E8</c>, not in combat), a minute a combat round after
    /// the first (<c>612E:0754</c>), PASS_TIME's minutes (<c>8053:0A02</c>).
    /// </summary>
    public int Clock
    {
        get => _engine[9];
        set => _engine[9] = value;
    }

    public const int ClockTicks = 64;

    private long _clockFrom = -1;

    public void PassTime(int seconds) => Clock = unchecked(Clock + seconds);

    /// <summary>A party member with this allegiance (no fighting bits, <c>&amp; 3</c>) sits a fight out: REQUEST(14)'s.</summary>
    public const int WatchingAllegiance = 4;

    /// <summary>
    /// CLONE (<c>043D:3457</c>, <c>7372:0A44</c>): <paramref name="count"/>
    /// copies of the object named <paramref name="name"/> near (x, y). Returns
    /// how many were made. DSUN.EXE loads each copy from the <c>RDFF</c>
    /// (−name), so one need not be on the map: then each is made afresh
    /// (<see cref="Spawn"/>). Each copy becomes "other" (<c>gname[8]</c>,
    /// <c>7372:0A44</c> stores it in <c>3781:0363</c>), which the arena's
    /// monster release then orders: <c>HUNT(obj_special(0x28).72)</c>.
    /// </summary>
    public int Clone(int name, int count, int x, int y)
    {
        if (First(name) is not { } original)
        {
            int spawned = 0;
            for (int i = 0; name < 0 && i < count; i++)
            {
                if (NearestCell(new Cell(x, y), c => Fits(c, Footprint.OneCell)) is { } cell && Spawn(-name, cell) is { } spawn)
                {
                    SetEngineVariable(0x28, spawn.Index);
                    spawned++;
                }
            }
            return spawned;
        }
        int made = 0;
        for (int i = 0; i < count; i++)
        {
            int index = Enumerable.Range(PartySize, _objects.Count - PartySize).FirstOrDefault(n => _objects[n] is null, _objects.Count);
            var copy = new WorldObject(index, original.Kind, original.Id)
            {
                Source = original.Source,
                IsClone = true,
                Record = original.Record,
                Item = original.Item,
                Character = original.Character,
                Name = original.Name,
                Allegiance = original.Allegiance,
                Vitals = new Vitals { Hp = original.Vitals.MaxHp, MaxHp = original.Vitals.MaxHp, Status = original.Status },
                FixedCell = new Cell(x, y),
            };
            if (original.Walker is { } w && index < MaxCreatures)
            {
                var spec = new CharacterSpec(w.Script, w.Frames, w.Footprint);
                if (NearestCell(new Cell(x, y), c => Fits(c, spec.Footprint)) is not { } cell)
                {
                    continue;
                }
                copy.Walker = new Walker(index, spec.Script, spec.Frames, cell, spec.Footprint, Occupancy);
            }
            Put(index, copy);
            made++;
            SetEngineVariable(0x28, index);
            Changed?.Invoke(copy, null);
        }
        return made;
    }

    /// <summary><c>REQUEST(17)</c>: show the object named <paramref name="name"/> at cell (x, y).</summary>
    public void Show(int name, int x, int y)
    {
        foreach (WorldObject obj in Select(name).Concat(Objects.Where(o => o.Id == name && o.Hidden)).Distinct().ToList())
        {
            obj.Hidden = false;
            Place(obj, new Cell(x, y));
            Changed?.Invoke(obj, null);
        }
    }

    /// <summary>
    /// <c>REQUEST(5)</c> (<c>87B3:019F</c>): toggle the objects' second state
    /// (e.g. open / closed), playing the animation that starts at <c>SCMD</c>
    /// header byte 0x44, or the normal one when switching back.
    /// </summary>
    public void ToggleState(int who)
    {
        foreach (WorldObject obj in Select(who))
        {
            ToggleState(obj);
        }
    }

    private void ToggleState(WorldObject obj)
    {
        if (obj.Source is not { Scmd: > 0 } source || ScriptOf(source.Scmd) is not { } scmd)
        {
            return;
        }
        obj.SecondState = !obj.SecondState;
        int start = obj.SecondState ? scmd.Extra[4] | (scmd.Extra[5] << 8) : scmd.Slots[0];
        Changed?.Invoke(obj, start);
    }

    // -- doors and using objects (6BB8) -------------------------------------------------

    public static bool IsDoorNumber(int ojff) => ojff is >= 0x46A and <= 0x49C or >= 0x803 and <= 0x898;

    /// <summary>
    /// <c>REQUEST(2)</c> and using a door (<c>6BB8:0D13</c>): set the door,
    /// and the other leaf of a double door (<c>OJFF</c> ± 1), to
    /// <paramref name="state"/> (<see cref="Doors.Closed"/> set: closed; with
    /// <see cref="Doors.Locked"/>: closed and locked; 0: open). A door that is
    /// stuck or secret (<see cref="Doors.Stuck"/>, <see cref="Doors.Secret"/>)
    /// only moves when <paramref name="force"/>d. When it moves it plays
    /// <c>SCMD</c> slot 2 (opening) or 3 (closing) and blocks or frees its
    /// cells. Returns whether it moved.
    /// </summary>
    public bool SetDoor(WorldObject door, int state, bool force)
    {
        if (!door.IsDoor)
        {
            return false;
        }
        int before = door.DoorFlags;
        if ((before & (Doors.Stuck | Doors.Secret)) != 0 && !force)
        {
            return false;
        }
        int after = (state & Doors.Locked) != 0 ? before | Doors.Closed | Doors.Locked
            : state == 0 ? before & ~(Doors.Closed | Doors.Locked)
            : before | Doors.Closed;
        WorldObject? pair = PairOf(door);
        door.DoorFlags = after;
        if (pair is not null)
        {
            pair.DoorFlags = after;
        }
        if (((state & Doors.Closed) != 0) == ((before & Doors.Closed) != 0))
        {
            return false;
        }
        int slot = (state & (Doors.Closed | Doors.Locked)) == 0 ? 2 : 3;
        foreach (WorldObject leaf in pair is null ? [door] : new[] { door, pair })
        {
            SetDoorCells(leaf, state);
            if (leaf.Source is { Scmd: > 0 } source && ScriptOf(source.Scmd) is { } scmd)
            {
                Changed?.Invoke(leaf, scmd.Slots[slot]);
            }
        }
        return true;
    }

    /// <summary>The fire pits (<c>OJFF</c> 1307, 1310): using one rests the party (<c>6BB8:0B56</c>).</summary>
    public static bool IsRestingPlace(WorldObject obj) => obj.Source?.Ojff is 1307 or 1310;

    /// <summary>The time a rest at a fire pit takes (<c>87B3:073D(0, 8)</c>), in PASS_TIME's units.</summary>
    public const int FirePitRest = 8;

    /// <summary>
    /// Use an object (<c>6BB8:0B56</c>): a fire pit rests the party
    /// (<see cref="IsRestingPlace"/>); a door opens or closes; an object
    /// whose item type has flag 1 (<c>IT1R</c> +0x0F) toggles its second state,
    /// as <c>REQUEST(5)</c>. Returns what the game would say when a door stays
    /// as it was (check it after the object's use trigger has run, as DSUN.EXE
    /// does, with <see cref="DoorRefusal"/>), or null.
    /// </summary>
    public DoorUse UseObject(WorldObject obj)
    {
        if (!obj.IsDoor)
        {
            bool rested = IsRestingPlace(obj);
            if (rested)
            {
                Rest(raiseDead: false, FirePitRest);
            }
            if (obj.Item is { } item && Items.TypeOf(item) is { } type && (type.Kind & ItemTypeFlags.Usable) != 0)
            {
                ToggleState(obj);
                return new DoorUse(obj, false, Toggled: true, rested);
            }
            return new DoorUse(obj, false, Toggled: false, rested);
        }
        bool wasClosed = (obj.DoorFlags & Doors.Closed) != 0;
        bool moved = SetDoor(obj, wasClosed ? 0 : Doors.Closed, force: false);
        return new DoorUse(obj, wasClosed, Toggled: moved);
    }

    /// <summary>
    /// After using a door and running its use trigger: the message DSUN.EXE
    /// shows if it stayed as it was (<c>6BB8:0B56</c>). Revealing a secret
    /// door clears its secret flag.
    /// </summary>
    public string? DoorRefusal(DoorUse use)
    {
        WorldObject door = use.Object;
        if (!door.IsDoor || ((door.DoorFlags & Doors.Closed) != 0) != use.WasClosed)
        {
            return null;
        }
        if ((door.DoorFlags & Doors.Secret) != 0)
        {
            door.DoorFlags &= ~Doors.Secret;
            return "THERE'S A SECRET DOOR!";
        }
        return (door.DoorFlags & Doors.Locked) != 0 ? "THE DOOR IS LOCKED"
            : (door.DoorFlags & Doors.Closed) != 0 ? "THE DOOR WON'T OPEN"
            : null;
    }

    /// <summary>The other leaf of a double door: the door numbered one more (odd) or one less (even).</summary>
    private WorldObject? PairOf(WorldObject door)
    {
        int ojff = door.Source!.Ojff;
        int other = ojff % 2 == 0 ? ojff - 1 : ojff + 1;
        return Objects.FirstOrDefault(o => o.Index >= 5 && o != door && o.IsDoor && o.Source!.Ojff == other);
    }

    /// <summary>
    /// <c>6BB8:0FF8</c>: a closed door blocks movement and sight (<c>GMAP</c>
    /// 0xC0) on its cells, an open one frees them. The cells start at its
    /// position ÷ 16 and run east for a width of 0–9 (item record +0x0E), or
    /// south for 10 and more (n − 10). An open door keeps its first cell
    /// blocked when the cell before it (west, or north) is free, or when it runs
    /// south.
    /// </summary>
    /// <summary>A door's cells (<c>6BB8:0FF8</c>): from its position ÷ 16, item +0x0E more east (0–9) or south (10 and up, less 10).</summary>
    private static IEnumerable<Cell> DoorCells(WorldObject door)
    {
        if (door.Source is not { } source || door.Item is not { } item)
        {
            yield break;
        }
        int size = (sbyte)item.Charges;
        (int across, int down) = size > 9 ? (0, size - 10) : (size, 0);
        for (int dx = 0; dx <= across; dx++)
        {
            for (int dy = 0; dy <= down; dy++)
            {
                yield return new Cell((source.EtabX >> 4) + dx, (source.EtabY >> 4) + dy);
            }
        }
    }

    /// <summary>
    /// The cells an object stands on, for walking up to it: a creature's
    /// footprint, a door's width, or the cell of anything else.
    /// </summary>
    public IReadOnlyList<Cell> CellsOf(WorldObject obj) =>
        obj.Walker is { } walker ? [.. walker.Footprint.Cells(walker.Cell)]
        : obj.IsDoor ? [.. DoorCells(obj)]
        : [obj.Cell];

    private void SetDoorCells(WorldObject door, int state)
    {
        if (door.Source is not { } source || door.Item is not { } item)
        {
            return;
        }
        int x = source.EtabX >> 4, y = source.EtabY >> 4;
        int size = (sbyte)item.Charges;
        bool closed = (state & Doors.Closed) != 0;
        foreach (Cell cell in DoorCells(door))
        {
            SetCellFlags(cell.X, cell.Y, CellFlags.Blocked | CellFlags.BlocksSight, closed);
        }
        if (!closed)
        {
            bool beforeBlocked = size > 9 ? Region.IsBlocked(x, y - 1) : Region.IsBlocked(x - 1, y);
            if (!beforeBlocked || (size > 9 ? size - 10 : size) != 0)
            {
                SetCellFlags(x, y, CellFlags.Blocked | CellFlags.BlocksSight, true);
            }
        }
    }

    /// <summary>On loading the region (<c>6BB8</c> setup): each door's cells as its state says, and its animation at slot 1 (closed) or 2 (opening, to open).</summary>
    private void SetUpDoors()
    {
        foreach (WorldObject door in Objects.Where(o => o.IsDoor))
        {
            int flags = door.Item!.Flags;
            SetUpDoor(door, (flags & Doors.Locked) != 0 ? flags | Doors.Closed : flags);
        }
    }

    private void SetUpDoor(WorldObject door, int flags)
    {
        door.DoorFlags = flags;
        bool closed = (flags & Doors.Closed) != 0;
        SetDoorCells(door, flags);
        if (door.Source!.Scmd > 0 && ScriptOf(door.Source.Scmd) is { } scmd)
        {
            door.AnimationStart = scmd.Slots[closed ? 1 : 2];
        }
    }

    // -- remembering the region ---------------------------------------------------------

    /// <summary>
    /// What this region is like now, to restore on the next visit
    /// (DSUN.EXE saves the region it leaves, <c>7155:0C64</c>): the <c>GMAP</c>
    /// flags (opened doors, cells scripts blocked) and every object but the
    /// party, clones included.
    /// </summary>
    public RegionState Snapshot()
    {
        byte[] flags = [.. Region.Flags];
        for (int i = 0; i < flags.Length; i++)
        {
            flags[i] &= unchecked((byte)~(byte)CellFlags.Occupied);
        }
        var objects = Objects.Where(o => o.Kind != ObjectKind.Party).Select(o => new ObjectState(
            o.Index, o.Source?.EtabIndex, o.Id, o.Name, o.Hidden, o.Cell, o.Allegiance, o.Status,
            o.DoorFlags, o.SecondState, new Dictionary<int, int>(o.StoredFields),
            o.Items.Count > 0 ? [.. o.Items.Select(i => i.Save())] : null, o.Vitals.Save(),
            o.Source is { EtabIndex: < 0 } made ? made.Ojff : null)).ToList();
        return new RegionState(Region.Number, flags, objects);
    }

    /// <summary>
    /// Make this freshly loaded region as <paramref name="state"/> left it:
    /// objects it no longer had are removed, clones it had are made again,
    /// and the others take back their place and state. Call before the
    /// region's script runs (DSUN.EXE runs it on a saved region too). Returns
    /// the objects whose picture may differ from the files', for the view.
    /// </summary>
    public IReadOnlyList<WorldObject> Restore(RegionState state)
    {
        if (state.Region != Region.Number)
        {
            throw new ArgumentException($"state of region {state.Region}, not {Region.Number}");
        }
        int keepFrom = int.MaxValue;
        if (_objects.Count > OldTable && !state.Objects.Any(o => o.Index >= OldTable && o.Etab is >= 0 && this[o.Index]?.Source?.EtabIndex == o.Etab))
        {
            // Saved when the world held DSUN.EXE's 320 objects and no more: the
            // map entries past them weren't objects then (drawn as scenery). They
            // are kept, and what was made at run time in their slots moves after them.
            int next = _objects.Count;
            state = state with { Objects = [.. state.Objects.Select(o => o.Index < OldTable ? o : o with { Index = next++ })] };
            keepFrom = OldTable;
        }
        var saved = state.Objects.ToDictionary(o => o.Index);
        // Gone since the files: removed (the party keeps its slots).
        for (int i = PartySize; i < Math.Min(_objects.Count, keepFrom); i++)
        {
            if (_objects[i] is { } obj && !saved.ContainsKey(i))
            {
                obj.Walker?.Remove();
                _objects[i] = null;
            }
        }
        var bySource = Objects.Where(o => o.Source is not null && !o.IsClone).GroupBy(o => o.Source!.EtabIndex).ToDictionary(g => g.Key, g => g.First());
        var changed = new List<WorldObject>();
        foreach (ObjectState o in state.Objects)
        {
            WorldObject? obj = this[o.Index];
            if ((obj is null || obj.Source?.EtabIndex != o.Etab) && o.Ojff is int ojff)
            {
                // Made at run time from nothing: made again.
                obj?.Walker?.Remove();
                Put(o.Index, null);
                if (Region.MakeObject(_objectData, ojff, 0, 0) is not { } made)
                {
                    continue;
                }
                obj = MakeObject(o.Index, made);
                obj.IsClone = true;
                Put(o.Index, obj);
            }
            else if (obj is null || obj.Source?.EtabIndex != o.Etab)
            {
                // A clone: made again from the object it copied.
                if (o.Etab is not int etab || !bySource.TryGetValue(etab, out WorldObject? original))
                {
                    continue;
                }
                obj?.Walker?.Remove();
                obj = new WorldObject(o.Index, original.Kind, o.Id)
                {
                    Source = original.Source,
                    IsClone = true,
                    Record = original.Record,
                    Item = original.Item,
                    Character = original.Character,
                    FixedCell = o.Cell,
                };
                if (original.Walker is { } w && o.Index < MaxCreatures)
                {
                    obj.Walker = new Walker(o.Index, w.Script, w.Frames, o.Cell, w.Footprint, Occupancy);
                }
                Put(o.Index, obj);
            }
            obj.Id = o.Id;
            obj.Name = o.Name;
            obj.Hidden = o.Hidden;
            obj.Allegiance = o.Allegiance;
            if (o.Vitals is { } vitals)
            {
                obj.Vitals.Load(vitals);
            }
            obj.Status = o.Status;
            obj.Items = [.. (o.Items ?? []).Select(i => NewItem(i)).OfType<Item>()];
            obj.SecondState = o.SecondState;
            obj.RestoreFields(o.Fields);
            if (obj.Walker is { } walker)
            {
                if (o.Hidden)
                {
                    walker.Remove();
                }
                else if (walker.Cell != o.Cell && !walker.Teleport(o.Cell, Region.IsBlocked))
                {
                    walker.Teleport(o.Cell, (_, _) => false);
                }
            }
            else
            {
                obj.FixedCell = o.Cell;
            }
            if (obj.IsDoor)
            {
                SetUpDoor(obj, o.DoorFlags);
            }
            changed.Add(obj);
        }
        Array.Copy(state.Flags, Region.Flags, Math.Min(state.Flags.Length, Region.Flags.Length));
        return changed;
    }

    /// <summary>Where the animation of <paramref name="obj"/> should start for its second state (<c>REQUEST(5)</c>), or null for its normal one.</summary>
    public int? SecondStateAnimation(WorldObject obj) =>
        obj.SecondState && obj.Source is { Scmd: > 0 } source && ScriptOf(source.Scmd) is { } scmd
            ? scmd.Extra[4] | (scmd.Extra[5] << 8)
            : null;

    /// <summary>
    /// <c>REQUEST(10–13)</c>: set or clear <c>GMAP</c> bits at cell (x, y)
    /// (<c>gmap_set_bits</c> / <c>gmap_clear_bits</c>): 0x40 blocks movement,
    /// 0x80 sight (doors, portcullises).
    /// </summary>
    public void SetCellFlags(int x, int y, CellFlags flags, bool on)
    {
        if (x < 0 || y < 0 || x >= Region.Width || y >= Region.Height || y * Region.Width + x >= Region.Flags.Length)
        {
            return;
        }
        ref byte cell = ref Region.Flags[y * Region.Width + x];
        cell = on ? (byte)(cell | (byte)flags) : (byte)(cell & ~(byte)flags);
    }

    /// <summary>The object a script's <c>obj(id)</c> or <c>obj_special(n)</c> names, before its field path.</summary>
    public WorldObject? Resolve(int id) => id >= 0x8000 ? Special(id & 0x7FFF) : First(-id);

    private Scmd? ScriptOf(int number)
    {
        if (!_scripts.TryGetValue(number, out Scmd? scmd))
        {
            scmd = _objectData.TryRead("SCMD", number, out var data) ? Scmd.Parse(data.Span) : null;
            _scripts[number] = scmd;
        }
        return scmd;
    }

    private void Place(WorldObject obj, Cell near)
    {
        if (obj.Walker is { } w)
        {
            if (NearestCell(near, c => w.CanStand(c, Region.IsBlocked)) is { } cell)
            {
                if (!w.Teleport(cell, Region.IsBlocked))
                {
                    w.Teleport(cell, (_, _) => false);
                }
            }
        }
        else
        {
            obj.FixedCell = near;
        }
    }

    // -- the passage of time ----------------------------------------------------------------

    /// <summary>
    /// Carry out orders, step the creatures (not the party) to tick
    /// <paramref name="now"/>, and queue the scripts creatures should
    /// run: no-orders scripts for idle objects, and line-of-sight scripts
    /// (kind 1 in sight of the active member within range, kind 2 not).
    /// Returns the objects whose picture changed.
    /// </summary>
    public List<WorldObject> Update(long now, GplCreatureScripts creatureScripts)
    {
        var changed = new List<WorldObject>();
        // Party members are stepped by the caller, which also fires their move triggers.
        foreach (WorldObject obj in Objects.Where(o => o.Walker is not null && !o.Hidden && o.Kind != ObjectKind.Party).ToList())
        {
            if (obj.CanTakeOrders)
            {
                FollowOrder(obj, now);
            }
            if (obj.Walker!.Update(now, Region.IsBlocked))
            {
                changed.Add(obj);
            }
        }
        if (_clockFrom < 0 || now < _clockFrom)
        {
            _clockFrom = now;
        }
        for (; now - _clockFrom >= ClockTicks; _clockFrom += ClockTicks)
        {
            PassTime(1);
        }
        if (now >= _nextScriptCheck)
        {
            _nextScriptCheck = now + ScriptCheckTicks;
            QueueCreatureScripts(creatureScripts);
        }
        return changed;
    }

    private void FollowOrder(WorldObject obj, long now)
    {
        Walker w = obj.Walker!;
        Order order = obj.Order;
        if (order.Mode is OrderMode.None or OrderMode.Wait || now < _nextPlan.GetValueOrDefault(obj.Index))
        {
            return;
        }
        _nextPlan[obj.Index] = now + ReplanTicks;
        WorldObject? target = order.Mode is OrderMode.GoXY ? null : this[order.Target];
        switch (order.Mode)
        {
            case OrderMode.GoXY:
                if (w.Cell == new Cell(order.Target, order.Extra))
                {
                    obj.Order = default;
                }
                else if (!w.IsWalking)
                {
                    w.WalkTo(new Cell(order.Target, order.Extra), Region.IsBlocked);
                }
                break;
            case OrderMode.Go or OrderMode.Fetch or OrderMode.Follow or OrderMode.Hunt:
                if (target is null || target.Hidden)
                {
                    obj.Order = default;
                    break;
                }
                int distance = GplGeometry.Distance(w.Cell.X, w.Cell.Y, target.Cell.X, target.Cell.Y);
                int near = order.Mode == OrderMode.Follow ? 2 : 1;
                if (distance <= near)
                {
                    w.Stop();
                    if (order.Mode is OrderMode.Go or OrderMode.Fetch)
                    {
                        obj.Order = default;
                    }
                }
                else
                {
                    w.WalkTo(target.Cell, Region.IsBlocked);
                }
                break;
            case OrderMode.Flee:
                if (this[ActiveMember] is { } from && !w.IsWalking)
                {
                    int dx = Math.Sign(w.Cell.X - from.Cell.X), dy = Math.Sign(w.Cell.Y - from.Cell.Y);
                    w.WalkTo(new Cell(w.Cell.X + 6 * (dx == 0 ? 1 : dx), w.Cell.Y + 6 * dy), Region.IsBlocked);
                }
                break;
        }
    }

    /// <summary>
    /// The creature scripts due (<c>1A0A:0A77</c>). Any object a script gave
    /// one to takes part, not only creatures: DSUN.EXE moves it into the
    /// creature slots (<c>1A0A:273E</c>, from <c>1A0A:0554</c>) and runs its
    /// no-orders script whenever it has no order. Region 42's door
    /// (<c>name(-2059)</c>) is how the arena's opening scene starts.
    /// </summary>
    private void QueueCreatureScripts(GplCreatureScripts scripts)
    {
        WorldObject? active = this[ActiveMember];
        foreach (WorldObject obj in Objects.Where(o => o.Kind != ObjectKind.Party && !o.Hidden && o.Index >= PartySize))
        {
            if (obj.Order.Mode == OrderMode.None && obj.Walker is not { IsWalking: true }
                && scripts.NoOrdersFor(obj.Index, obj.Id) is { File: not 0 } idle)
            {
                PendingScripts.Enqueue((idle, obj.Index, $"{obj.Name} has no orders"));
            }
            if (active is not null && scripts.SightFor(obj.Index, obj.Id) is { Entry.File: not 0 } sight)
            {
                bool seen = GplGeometry.LineOfSight(Region, obj.Cell.X, obj.Cell.Y, active.Cell.X, active.Cell.Y)
                    && GplGeometry.Distance(obj.Cell.X, obj.Cell.Y, active.Cell.X, active.Cell.Y) <= (sight.Range & 0x7F);
                if (seen == (sight.Kind == 1))
                {
                    PendingScripts.Enqueue((sight.Entry, obj.Index, $"{obj.Name} {(seen ? "sees" : "lost sight of")} {active.Name}"));
                }
            }
        }
    }

    // -- cells --------------------------------------------------------------------------------

    /// <summary>Whether a footprint fits at a cell right now.</summary>
    public bool Fits(Cell cell, Footprint footprint) =>
        footprint.Cells(cell).All(c => !Region.IsBlocked(c.X, c.Y) && Occupancy.OwnerOf(c) is null);

    /// <summary>The cell nearest <paramref name="start"/> that satisfies <paramref name="ok"/>, searching outwards.</summary>
    public Cell? NearestCell(Cell start, Func<Cell, bool> ok)
    {
        for (int radius = 0; radius < Math.Max(Region.Width, Region.Height); radius++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    var cell = new Cell(start.X + dx, start.Y + dy);
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == radius && ok(cell))
                    {
                        return cell;
                    }
                }
            }
        }
        return null;
    }
}
