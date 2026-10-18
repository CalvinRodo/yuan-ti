using DarkSun.Data.Images;

namespace DarkSun.Data.Regions;

[Flags]
public enum RegionLayers
{
    Ground = 1,
    Objects = 2,
    Walls = 4,
    All = Ground | Objects | Walls,
}

/// <summary>
/// Renders a whole region to one RGB image in software, as
/// <c>region_renderer.py</c> does. Used to check the engine against the Python
/// tools and for map exports; the game itself draws with Godot nodes.
/// </summary>
public static class RegionRenderer
{
    /// <summary>Colour where no tile is drawn.</summary>
    public static readonly Rgb Background = new(13, 12, 20);

    /// <summary>RGB8 pixels, (Width × 16) × (Height × 16), of the chosen layers.</summary>
    public static byte[] RenderRgb(Region region, Palette palette, RegionLayers layers = RegionLayers.All)
    {
        int width = region.Width * Region.TileSize;
        int height = region.Height * Region.TileSize;
        var rgb = new byte[width * height * 3];
        for (int i = 0; i < rgb.Length; i += 3)
        {
            rgb[i] = Background.R;
            rgb[i + 1] = Background.G;
            rgb[i + 2] = Background.B;
        }

        for (int cell = 0; layers.HasFlag(RegionLayers.Ground) && cell < region.Width * region.Height; cell++)
        {
            // A short last row has no tiles past the end of the map.
            if (cell < region.TileMap.Length && region.Tiles.TryGetValue(region.TileMap[cell], out ImageFrame? tile))
            {
                Draw(rgb, width, height, new PlacedSprite(cell % region.Width * Region.TileSize, cell / region.Width * Region.TileSize, tile), palette);
            }
        }
        var sprites = (layers.HasFlag(RegionLayers.Objects) ? region.Objects : [])
            .Concat(layers.HasFlag(RegionLayers.Walls) ? region.Walls : []);
        foreach (PlacedSprite sprite in sprites)
        {
            Draw(rgb, width, height, sprite, palette);
        }
        return rgb;
    }

    /// <summary>Copy the sprite's opaque pixels, clipped to the image.</summary>
    private static void Draw(byte[] rgb, int width, int height, PlacedSprite sprite, Palette palette)
    {
        ImageFrame f = sprite.Frame;
        for (int y = 0; y < f.Height; y++)
        {
            int dy = sprite.Y + y;
            if (dy < 0 || dy >= height)
            {
                continue;
            }
            for (int x = 0; x < f.Width; x++)
            {
                int dx = sprite.X + x;
                int source = y * f.Width + (sprite.Mirrored ? f.Width - 1 - x : x);
                if (dx < 0 || dx >= width || !f.Opaque[source])
                {
                    continue;
                }
                Rgb c = palette[f.Pixels[source]];
                int at = (dy * width + dx) * 3;
                rgb[at] = c.R;
                rgb[at + 1] = c.G;
                rgb[at + 2] = c.B;
            }
        }
    }
}
