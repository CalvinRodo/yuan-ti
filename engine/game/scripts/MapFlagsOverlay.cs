using DarkSun.Data.Regions;
using DarkSun.Game.Movement;
using DarkSun.Game.Scripting;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// Debug overlay of the region's <c>GMAP</c> flags, like DSUN.EXE's own
/// (<c>gmap_debug_overlay</c>, <c>142F:0426</c>): red cells block movement,
/// blue outlines block line of sight, yellow cells are occupied by creatures,
/// magenta marks script triggers (tiles filled, boxes outlined).
/// </summary>
public partial class MapFlagsOverlay : Node2D
{
    private static readonly Color BlockedFill = new(1f, 0.15f, 0.1f, 0.35f);
    private static readonly Color SightOutline = new(0.3f, 0.6f, 1f, 0.9f);
    private static readonly Color OccupiedFill = new(1f, 0.9f, 0.1f, 0.45f);
    private static readonly Color TriggerColor = new(1f, 0.2f, 1f, 0.8f);

    private Region? _region;
    private Occupancy? _occupancy;
    private GplTriggers? _triggers;

    public void Show(Region region, Occupancy occupancy, GplTriggers? triggers)
    {
        _region = region;
        _occupancy = occupancy;
        _triggers = triggers;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_region is null)
        {
            return;
        }
        for (int y = 0; y < _region.Height; y++)
        {
            for (int x = 0; x < _region.Width; x++)
            {
                var cell = new Rect2(x * Region.TileSize, y * Region.TileSize, Region.TileSize, Region.TileSize);
                if (_region.IsBlocked(x, y))
                {
                    DrawRect(cell, BlockedFill);
                }
                if (_region.BlocksSight(x, y))
                {
                    DrawRect(cell.Grow(-1), SightOutline, filled: false);
                }
            }
        }
        const int size = Region.TileSize;
        foreach (var t in _triggers?.Records(GplTriggerList.MoveTile) ?? [])
        {
            DrawRect(new Rect2(t.A * size, t.B * size, size, size), TriggerColor with { A = 0.4f });
        }
        foreach (var t in _triggers?.Records(GplTriggerList.MoveBox) ?? [])
        {
            DrawRect(new Rect2(t.A * size, t.B * size, t.B8 * size, t.B9 * size), TriggerColor, filled: false, width: 2);
        }
        foreach (var (cell, _) in _occupancy?.Cells ?? [])
        {
            DrawRect(new Rect2(cell.X * Region.TileSize, cell.Y * Region.TileSize, Region.TileSize, Region.TileSize), OccupiedFill);
        }
    }
}
