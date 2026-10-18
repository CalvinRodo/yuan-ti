using DarkSun.Data.Regions;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// Debug overlay of the region's <c>GMAP</c> flags, like DSUN.EXE's own
/// (<c>gmap_debug_overlay</c>, <c>142F:0426</c>): red cells block movement,
/// blue outlines block line of sight.
/// </summary>
public partial class MapFlagsOverlay : Node2D
{
    private static readonly Color BlockedFill = new(1f, 0.15f, 0.1f, 0.35f);
    private static readonly Color SightOutline = new(0.3f, 0.6f, 1f, 0.9f);

    private Region? _region;

    public void Show(Region region)
    {
        _region = region;
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
    }
}
