using DarkSun.Data.Items;
using DarkSun.Game.Movement;
using DarkSun.Game.World;

namespace DarkSun.Game.Combat;

/// <summary>
/// What a lingering area leaves on the map (<c>7D80:05C7</c>, <c>7D80:2782</c>):
/// the cells it covers, the effect it gives (<see cref="AreaTemplate.Effect"/>)
/// at the caster's level, until the round clock reaches <see cref="EndsAt"/>
/// (null: the fight). Who stands in it is kept (<c>DS:000A</c>, 3 bytes an entry)
/// so it can tell entering, staying and leaving apart (<c>7D80:0C6E</c>).
/// </summary>
public sealed class AreaField(SpellEffect effect, AreaTemplate template, Combatant caster, int level, IReadOnlySet<Cell> cells, int? endsAt)
{
    public SpellEffect Effect { get; } = effect;

    public AreaTemplate Template { get; } = template;

    public Combatant Caster { get; } = caster;

    public int Level { get; } = level;

    public IReadOnlySet<Cell> Cells { get; } = cells;

    public int? EndsAt { get; } = endsAt;

    internal HashSet<Combatant> Inside { get; } = [];

    /// <summary>Who stands in it now.</summary>
    public IReadOnlyCollection<Combatant> Occupants => Inside;
}

public sealed partial class Battle
{
    private readonly List<AreaField> _fields = [];

    /// <summary>The lingering areas on the map: fog, walls, clouds, webs.</summary>
    public IReadOnlyList<AreaField> Fields => _fields;

    // -- the original's geometry: sixteenths of a cell, angles in 256ths of a turn ---------------------

    /// <summary>A shape as <c>8BEB:0000</c> lays it out, in sixteenths of a cell.</summary>
    private sealed record Shape(int Kind, int X, int Y, int Radius, int Angle, int HalfAngle, int Width, int Length, Combatant? Without);

    /// <summary>A cell's centre in sixteenths (object coordinates; the cell is coordinate ÷ 16).</summary>
    public static (int X, int Y) Centre(Cell c) => (c.X * 16 + 8, c.Y * 16 + 8);

    private static Cell CellAt(int x, int y) => new(x >> 4, y >> 4);

    /// <summary>
    /// Distance in sixteenths (<c>1A0A:2DE6</c>): <c>0898:000C</c> on the
    /// differences, scaled down to 127 and back up when they are larger.
    /// </summary>
    public static int Distance16(int x1, int y1, int x2, int y2)
    {
        int dx = Math.Abs(x2 - x1), dy = Math.Abs(y2 - y1);
        if (dx <= 0x7F && dy <= 0x7F)
        {
            return GplGeometry.Distance(0, 0, dx, dy);
        }
        int big = Math.Max(dx, dy), small = Math.Min(dx, dy);
        long scale = ((long)big << 8) / 0x7F;
        small = (int)((long)small * (0x7F00 / big) >> 8);
        return (int)(GplGeometry.Distance(0, 0, 0x7F, small) * scale >> 8);
    }

    /// <summary>The direction from one point to another in 256ths of a turn, 0 north, clockwise (<c>69CB:09D8</c>).</summary>
    public static int Angle(int x1, int y1, int x2, int y2) =>
        x1 == x2 && y1 == y2 ? 0 : (int)Math.Round(Math.Atan2(x2 - x1, -(y2 - y1)) * 128 / Math.PI) & 0xFF;

    /// <summary><paramref name="r"/> sixteenths toward <paramref name="angle"/> (<c>69CB:0B3A</c>).</summary>
    private static (int X, int Y) Polar(int angle, int r)
    {
        double a = angle * Math.PI / 128;
        return ((int)Math.Round(Math.Sin(a) * r), (int)Math.Round(-Math.Cos(a) * r));
    }

    /// <summary>
    /// The shape of <paramref name="e"/> cast by <paramref name="caster"/> at
    /// <paramref name="at"/> (<c>8BEB:0000</c>, <c>8BEB:092D</c>, <c>8BEB:0393</c>).
    /// Its size is the area's (doubled for a circle), or for a wall the distance
    /// between its ends: the player gives both; cast at one cell (as the
    /// original's AI does, <c>6466:0000</c>) both are that cell and the wall is
    /// a point, unless <see cref="CombatFixes.WallsFromOneCell"/>. A circle has radius size ÷ 2 around the cell; a cone
    /// starts 4 sixteenths out from the caster toward the cell, size ÷ 2 long,
    /// as wide as the template's width and length make its angle, and leaves the
    /// caster out; a line is a box centred on the cell (a wall's middle), the
    /// template's length along the caster's line of fire (turned by its
    /// <see cref="AreaTemplate.Turn"/>; a wall's along its ends) and its width across.
    /// </summary>
    private Shape ShapeOf(Combatant caster, SpellEffect e, AreaTemplate t, int level, Cell at, Cell? end)
    {
        var (x, y) = Centre(at);
        var (cx, cy) = Centre(caster.Cell);
        int size;
        int x2 = x, y2 = y;
        if (e.Target == 2)
        {
            if (end is { } other)
            {
                (x2, y2) = Centre(other);
            }
            else if (Rules.Fixes.WallsFromOneCell)
            {
                // Across the caster's line of fire, centred on the cell, the area's size long.
                var (dx, dy) = Polar(Angle(cx, cy, x, y) + 64, Math.Max(16, e.AreaSize(level)) / 2);
                (x, y, x2, y2) = (x - dx, y - dy, x + dx, y + dy);
            }
            size = Math.Max(1, Distance16(x, y, x2, y2));
        }
        else
        {
            size = e.AreaSize(level) * (t.Shape == AreaTemplate.Circle ? 2 : 1);
        }
        var (width, length) = t.Proportions(size);
        switch (t.Shape)
        {
            case AreaTemplate.Circle:
                return new(AreaTemplate.Circle, x, y, size / 2, 0, 0, 0, 0, null);
            case AreaTemplate.Cone:
            {
                int direction = Angle(cx, cy, x, y);
                var (dx, dy) = Polar(direction, 4);
                return new(AreaTemplate.Cone, cx + dx, cy + dy, size / 2, direction, Angle(0, 0, width / 2, -length), 0, 0, caster);
            }
            default:
            {
                int angle = e.Target == 2 ? Angle(x, y, x2, y2) : Angle(cx, cy, x, y);
                if (e.Target == 2)
                {
                    (x, y) = ((x + x2) / 2, (y + y2) / 2);
                }
                return new(AreaTemplate.Line, x, y, 0, (angle + t.Turn * 16) & 0xFF, 0, width, length, null);
            }
        }
    }

    /// <summary>Is the point (sixteenths) in the shape, and in sight from its centre (<c>612E:2DCC</c>, <c>2982</c>, <c>2A99</c>)?</summary>
    private bool Covers(Shape s, int x, int y)
    {
        bool inside;
        switch (s.Kind)
        {
            case AreaTemplate.Circle:
                inside = Distance16(s.X, s.Y, x, y) <= s.Radius;
                break;
            case AreaTemplate.Cone:
                inside = Distance16(s.X, s.Y, x, y) <= s.Radius
                    && ((Angle(s.X, s.Y, x, y) - (s.Angle - s.HalfAngle)) & 0xFF) <= ((2 * s.HalfAngle) & 0xFF);
                break;
            default:
                double a = s.Angle * Math.PI / 128, dx = x - s.X, dy = y - s.Y;
                double along = dx * Math.Sin(a) - dy * Math.Cos(a), across = dx * Math.Cos(a) + dy * Math.Sin(a);
                inside = Math.Abs(along) <= s.Length / 2 && Math.Abs(across) <= s.Width / 2;
                break;
        }
        return inside && InSight(CellAt(s.X, s.Y), CellAt(x, y));
    }

    private bool Covers(Shape s, Combatant c)
    {
        var (x, y) = Centre(c.Cell);
        return c != s.Without && Covers(s, x, y);
    }

    /// <summary>The cells a shape covers: those whose centres it covers and that can be walked on.</summary>
    private HashSet<Cell> CellsOf(Shape s)
    {
        int reach = (Math.Max(s.Radius, Math.Max(s.Width, s.Length)) >> 4) + 2;
        Cell middle = CellAt(s.X, s.Y);
        HashSet<Cell> cells = [];
        for (int dy = -reach; dy <= reach; dy++)
        {
            for (int dx = -reach; dx <= reach; dx++)
            {
                var cell = new Cell(middle.X + dx, middle.Y + dy);
                var (x, y) = Centre(cell);
                if (Map.Walkable(cell) && Covers(s, x, y))
                {
                    cells.Add(cell);
                }
            }
        }
        return cells;
    }

    /// <summary>
    /// The cells <paramref name="e"/>'s area covers cast at <paramref name="at"/>
    /// (what <c>7D80:200D</c> draws a burst over); none for single targets.
    /// </summary>
    public IReadOnlySet<Cell> AreaCells(Combatant caster, SpellEffect e, int level, Cell at, Cell? end = null) =>
        Spells.TemplateOf(e) is { } t && !e.SingleTarget ? CellsOf(ShapeOf(caster, e, t, level, at, end)) : new HashSet<Cell>();

    // -- fields: lingering areas (7D80:0C6E) ------------------------------------------------------------

    /// <summary>
    /// A lingering area cast (<c>7D80:05C7</c>): it stays on the cells its shape
    /// covers for its template's time and acts on whoever is in it now.
    /// </summary>
    private AreaField MakeField(Combatant caster, SpellEffect e, AreaTemplate t, int level, Cell at, Cell? end)
    {
        int? endsAt = t.DurationUnit == AreaTemplate.Permanent ? null
            : Clock + t.DurationUnit * (RollDice(t.DurationDice, t.DurationSides) + t.DurationPerLevel * Math.Min(level, SpellEffect.MaxLevel));
        var field = new AreaField(Spells.Effect(t.Effect) ?? e, t, caster, level, CellsOf(ShapeOf(caster, e, t, level, at, end)), endsAt);
        _fields.Add(field);
        Note(new FieldMade(field));
        foreach (Combatant c in _all.Where(c => c.Alive).ToList())
        {
            UpdateFields(c, moved: false, turn: false);
        }
        return field;
    }

    /// <summary>
    /// Where <paramref name="c"/> stands against the fields (<c>7D80:0C6E</c>,
    /// <c>7D80:0F9D</c>): entering one gives its effect; staying gives it again
    /// on each step in one on the ground and at the start of each turn in a
    /// cloud; leaving ends what it gave (<c>7D80:113A</c>). Fog does nothing to
    /// those in it.
    /// </summary>
    private void UpdateFields(Combatant c, bool moved, bool turn)
    {
        foreach (AreaField f in _fields.ToList())
        {
            bool now = c.Alive && c.Footprint.Any(f.Cells.Contains);
            bool was = f.Inside.Contains(c);
            if (now && !was)
            {
                f.Inside.Add(c);
                FieldActs(f, c);
            }
            else if (now && (f.Template.Kind == AreaTemplate.Ground && moved || f.Template.Kind == AreaTemplate.Cloud && turn))
            {
                FieldActs(f, c);
            }
            else if (!now && was)
            {
                f.Inside.Remove(c);
                LeaveField(f, c);
            }
        }
    }

    private void FieldActs(AreaField f, Combatant c)
    {
        if (f.Template.Kind != AreaTemplate.Fog && c.Alive)
        {
            Apply(f.Caster, c, f.Effect, f.Level, f);
        }
    }

    private void LeaveField(AreaField f, Combatant c)
    {
        foreach (ActiveEffect e in c.Effects.Where(e => e.Effect == f.Effect.Number).ToList())
        {
            EndEffect(c, e);
        }
    }

    /// <summary>A field going (its time is up, or it is dispelled or burnt away): what it gave its occupants ends.</summary>
    private void RemoveField(AreaField f)
    {
        if (!_fields.Remove(f))
        {
            return;
        }
        foreach (Combatant c in f.Inside.ToList())
        {
            LeaveField(f, c);
        }
        f.Inside.Clear();
        Note(new FieldEnded(f));
    }

    private void ExpireFields()
    {
        foreach (AreaField f in _fields.Where(f => f.EndsAt <= Clock).ToList())
        {
            RemoveField(f);
        }
    }

    /// <summary>
    /// After a cast (<c>7D80:003B</c>): Dispel Magic (both), effect 195 and any
    /// fire clear the fields they reach; fire only those that burn.
    /// </summary>
    private void ClearFields(Combatant caster, SpellEffect e, int level, Cell at, Cell? end)
    {
        bool fire = (e.Elements & DamageElement.Fire) != 0;
        if (_fields.Count == 0 || !fire && !Spells.Dispels.Contains(e.Number) && !Spells.AfflictionCures.Contains(e.Number))
        {
            return;
        }
        Shape? shape = Spells.TemplateOf(e) is { } t && !e.SingleTarget ? ShapeOf(caster, e, t, level, at, end) : null;
        bool reaches(Cell cell) => shape is null ? cell == at : Covers(shape, Centre(cell).X, Centre(cell).Y);
        foreach (AreaField f in _fields.Where(f => (!fire || f.Template.Burns || Spells.Dispels.Contains(e.Number)) && f.Cells.Any(reaches)).ToList())
        {
            RemoveField(f);
        }
    }

    /// <summary>The map's walkable cells less those a wall blocks (template flag 0x20, <c>142F:0182</c>).</summary>
    public bool Walkable(Cell cell) => Map.Walkable(cell) && !_fields.Any(f => f.Template.Blocks && f.Cells.Contains(cell));

    /// <summary>The map's sight-blocking cells and those a fog or wall hides (template flag 0x10, <c>142F:01B1</c>).</summary>
    public bool BlocksSight(Cell cell) => Map.BlocksSight(cell) || _fields.Count > 0 && _fields.Any(f => f.Template.Hides && f.Cells.Contains(cell));
}
