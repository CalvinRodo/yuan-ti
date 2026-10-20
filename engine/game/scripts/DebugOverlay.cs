using System.Text;
using DarkSun.Data.Regions;
using DarkSun.Game.Movement;
using DarkSun.Game.Scripting;
using DarkSun.Game.World;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// The region view's debug overlay, in layers that turn on separately:
/// <list type="bullet">
/// <item><see cref="Flags"/> (F1): the <c>GMAP</c> flags, like DSUN.EXE's own
/// overlay (<c>gmap_debug_overlay</c>, <c>142F:0426</c>): red cells block
/// movement, blue outlines block line of sight, yellow cells are occupied by
/// creatures, magenta marks move triggers (tiles filled, boxes outlined).</item>
/// <item><see cref="Grid"/> (F3): cell lines, with coordinates every 8 cells.</item>
/// <item><see cref="Numbers"/> (F4): each object's index and <c>OJFF</c> number
/// (scripts name objects as −<c>OJFF</c>), coloured by kind: party green,
/// creatures yellow, doors cyan, other things white.</item>
/// </list>
/// With any layer on, the cell under the pointer is outlined and
/// <see cref="Describe"/> says what is in it.
/// </summary>
public partial class DebugOverlay : Node2D
{
    private const int Size = Region.TileSize;
    private const int FontSize = 6;

    private static readonly Color BlockedFill = new(1f, 0.15f, 0.1f, 0.35f);
    private static readonly Color SightOutline = new(0.3f, 0.6f, 1f, 0.9f);
    private static readonly Color OccupiedFill = new(1f, 0.9f, 0.1f, 0.45f);
    private static readonly Color TriggerColor = new(1f, 0.2f, 1f, 0.8f);
    private static readonly Color GridLine = new(1f, 1f, 1f, 0.12f);
    private static readonly Color GridMajor = new(1f, 1f, 1f, 0.3f);
    private static readonly Color HoverOutline = new(1f, 1f, 1f, 0.95f);

    private Region? _region;
    private Occupancy? _occupancy;
    private GplTriggers? _triggers;
    private GameWorld? _world;
    private bool _flags, _grid, _numbers;
    private Cell? _hovered;

    /// <summary>The <c>GMAP</c> flags and triggers layer (F1).</summary>
    public bool Flags
    {
        get => _flags;
        set => Set(ref _flags, value);
    }

    /// <summary>The cell grid (F3).</summary>
    public bool Grid
    {
        get => _grid;
        set => Set(ref _grid, value);
    }

    /// <summary>The objects' numbers (F4).</summary>
    public bool Numbers
    {
        get => _numbers;
        set => Set(ref _numbers, value);
    }

    /// <summary>Whether any layer is on.</summary>
    public bool Active => _flags || _grid || _numbers;

    /// <summary>The cell under the pointer (outlined while a layer is on).</summary>
    public Cell? Hovered
    {
        get => _hovered;
        set
        {
            if (_hovered != value)
            {
                _hovered = value;
                QueueRedraw();
            }
        }
    }

    public void Show(Region region, Occupancy occupancy, GplTriggers? triggers, GameWorld? world)
    {
        _region = region;
        _occupancy = occupancy;
        _triggers = triggers;
        _world = world;
        QueueRedraw();
    }

    private void Set(ref bool layer, bool on)
    {
        layer = on;
        Visible = Active;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_region is null)
        {
            return;
        }
        if (_flags)
        {
            DrawFlags(_region);
        }
        if (_grid)
        {
            DrawGrid(_region);
        }
        if (_numbers && _world is not null)
        {
            DrawNumbers(_world);
        }
        if (_hovered is { } h && Active)
        {
            DrawRect(new Rect2(h.X * Size, h.Y * Size, Size, Size), HoverOutline, filled: false, width: 1);
        }
    }

    private void DrawFlags(Region region)
    {
        for (int y = 0; y < region.Height; y++)
        {
            for (int x = 0; x < region.Width; x++)
            {
                var cell = new Rect2(x * Size, y * Size, Size, Size);
                if (region.IsBlocked(x, y))
                {
                    DrawRect(cell, BlockedFill);
                }
                if (region.BlocksSight(x, y))
                {
                    DrawRect(cell.Grow(-1), SightOutline, filled: false);
                }
            }
        }
        foreach (var t in _triggers?.Records(GplTriggerList.MoveTile) ?? [])
        {
            DrawRect(new Rect2(t.A * Size, t.B * Size, Size, Size), TriggerColor with { A = 0.4f });
        }
        foreach (var t in _triggers?.Records(GplTriggerList.MoveBox) ?? [])
        {
            DrawRect(new Rect2(t.A * Size, t.B * Size, t.B8 * Size, t.B9 * Size), TriggerColor, filled: false, width: 2);
        }
        foreach (var (cell, _) in _occupancy?.Cells ?? [])
        {
            DrawRect(new Rect2(cell.X * Size, cell.Y * Size, Size, Size), OccupiedFill);
        }
    }

    private void DrawGrid(Region region)
    {
        float width = region.Width * Size, height = region.Height * Size;
        for (int x = 0; x <= region.Width; x++)
        {
            DrawLine(new Vector2(x * Size, 0), new Vector2(x * Size, height), x % 8 == 0 ? GridMajor : GridLine);
        }
        for (int y = 0; y <= region.Height; y++)
        {
            DrawLine(new Vector2(0, y * Size), new Vector2(width, y * Size), y % 8 == 0 ? GridMajor : GridLine);
        }
        Font font = ThemeDB.FallbackFont;
        for (int y = 0; y < region.Height; y += 8)
        {
            for (int x = 0; x < region.Width; x += 8)
            {
                DrawString(font, new Vector2(x * Size + 1, y * Size + FontSize), $"{x},{y}", fontSize: FontSize, modulate: GridMajor with { A = 0.7f });
            }
        }
    }

    private void DrawNumbers(GameWorld world)
    {
        Font font = ThemeDB.FallbackFont;
        var placed = new List<Rect2>();
        foreach (WorldObject obj in world.Objects.Where(o => !o.Hidden).OrderBy(o => o.Cell.Y).ThenBy(o => o.Cell.X))
        {
            if (world.CellsOf(obj) is not [var cell, ..])
            {
                continue;
            }
            string text = obj.Kind == ObjectKind.Party ? $"P{obj.Index}" : $"{obj.Index}:{-obj.Id}";
            Color color = obj.Kind switch
            {
                ObjectKind.Party => new Color(0.4f, 1f, 0.4f),
                ObjectKind.Creature => new Color(1f, 0.9f, 0.3f),
                _ when obj.IsDoor => new Color(0.4f, 0.9f, 1f),
                _ => Colors.White,
            };
            var at = new Vector2(cell.X * Size + 1, cell.Y * Size + Size - 2);
            // Stack labels that would overprint a neighbour's.
            Vector2 size = font.GetStringSize(text, fontSize: FontSize);
            var box = new Rect2(at - new Vector2(0, size.Y), size);
            for (int n = 0; n < 6 && placed.Any(p => p.Intersects(box)); n++)
            {
                at.Y += FontSize + 1;
                box.Position += new Vector2(0, FontSize + 1);
            }
            placed.Add(box);
            DrawString(font, at + Vector2.One, text, fontSize: FontSize, modulate: Colors.Black);
            DrawString(font, at, text, fontSize: FontSize, modulate: color);
        }
    }

    /// <summary>What cell <paramref name="c"/> holds: flags, occupant, objects and triggers.</summary>
    public string Describe(Cell c)
    {
        if (_region is not { } region || c.X < 0 || c.Y < 0 || c.X >= region.Width || c.Y >= region.Height)
        {
            return "";
        }
        var b = new StringBuilder();
        b.Append($"Cell ({c.X},{c.Y}) · pixel ({c.X * Size},{c.Y * Size}) · region {region.Number}\n");
        CellFlags flags = region.FlagsAt(c.X, c.Y);
        var named = new List<string>();
        if (flags.HasFlag(CellFlags.Blocked))
        {
            named.Add("blocked");
        }
        if (flags.HasFlag(CellFlags.BlocksSight))
        {
            named.Add("blocks sight");
        }
        if (flags.HasFlag(CellFlags.Occupied))
        {
            named.Add("occupied");
        }
        if (((int)flags & (int)CellFlags.WallMask) is int wall and > 0)
        {
            named.Add($"wall {wall}");
        }
        b.Append($"GMAP 0x{(byte)flags:x2}{(named.Count > 0 ? ": " + string.Join(", ", named) : ": open")}\n");
        foreach (var (cell, owner) in _occupancy?.Cells ?? [])
        {
            if (cell == c)
            {
                b.Append($"Occupied by walker {owner}{(_world?[owner] is { } o ? $" ({Label(o)})" : "")}\n");
            }
        }
        if (_world is { } world)
        {
            foreach (WorldObject obj in world.Objects.Where(o => !o.Hidden && world.CellsOf(o).Contains(c)))
            {
                string verbs = obj.Kind == ObjectKind.Party || _triggers is null ? ""
                    : " · " + string.Join("/", ObjectVerbs.For(obj, world.Items, _triggers, world.IsContainer(obj)));
                b.Append($"Object {Label(obj)}{verbs}\n");
            }
        }
        foreach (var t in _triggers?.Records(GplTriggerList.MoveTile) ?? [])
        {
            if (t.A == c.X && t.B == c.Y)
            {
                b.Append($"Move tile trigger → GPL {t.File} @{t.Address:x4}\n");
            }
        }
        foreach (var t in _triggers?.Records(GplTriggerList.MoveBox) ?? [])
        {
            if (c.X >= t.A && c.X < t.A + t.B8 && c.Y >= t.B && c.Y < t.B + t.B9)
            {
                b.Append($"Move box ({t.A},{t.B}) {t.B8}×{t.B9}{(t.B10 == 0 ? ", active member only" : "")} → GPL {t.File} @{t.Address:x4}\n");
            }
        }
        return b.ToString().TrimEnd();
    }

    private static string Label(WorldObject obj)
    {
        string kind = obj.Kind switch
        {
            ObjectKind.Party => "party",
            ObjectKind.Creature => "creature",
            _ when obj.IsDoor => "door",
            _ => "thing",
        };
        string name = obj.Name.Length > 0 ? $" {obj.Name}" : "";
        return obj.Kind == ObjectKind.Party ? $"#{obj.Index}{name} ({kind})" : $"#{obj.Index} OJFF {-obj.Id}{name} ({kind})";
    }
}
