using System.Numerics;
using DarkSun.Data.Images;
using DarkSun.Data.Regions;

namespace DarkSun.Game.Lighting;

/// <summary>A light on the map that doesn't move: where, which, and its own flicker (<see cref="Light.FlickerAt"/>).</summary>
public readonly record struct MapLight(Vector2 At, Light Light, int Seed);

/// <summary>
/// The lights a region gives of itself (<see cref="LightRules"/>), found once on entering it:
/// the objects <see cref="LightRules.Objects"/> names, each at the middle of its picture; the
/// pixels of the ground, the other objects and the walls drawn in a glowing palette colour
/// (<see cref="LightRules.Palette"/>: lava, fire pits, braziers, torches), a light for each
/// <see cref="Block"/>-pixel square holding enough of them, at their middle, brighter the more
/// there are; and the lights put by hand in <see cref="LightRules.Regions"/>.
/// </summary>
public static class GlowScanner
{
    /// <summary>The squares glowing pixels are gathered in, map pixels.</summary>
    public const int Block = 48;

    /// <summary>Glowing pixels in a square that make its light full strength.</summary>
    public const int FullAt = 240;

    public static List<MapLight> Scan(Region region, Palette palette, LightRules rules)
    {
        // Each palette index's glow (index into rules.Palette), or −1.
        var glowOf = new int[256];
        for (int i = 0; i < 256; i++)
        {
            glowOf[i] = rules.Palette.ToList().FindIndex(p => i >= p.From && i <= p.To
                && (!p.HotOnly || i < palette.Count && palette[i] is var c && LightRules.Hot(c.R, c.G, c.B)));
        }
        var blocks = new Dictionary<(int Bx, int By, int Glow), (int Count, long X, long Y)>();
        void Add(int x, int y, int glow)
        {
            var key = (Math.Max(0, x) / Block, Math.Max(0, y) / Block, glow);
            var (count, sx, sy) = blocks.GetValueOrDefault(key);
            blocks[key] = (count + 1, sx + x, sy + y);
        }
        void AddFrame(ImageFrame frame, int left, int top, bool mirrored)
        {
            if (frame.Rgba is not null)
            {
                return; // a mod's true-colour picture has no palette colours to glow
            }
            for (int y = 0; y < frame.Height; y++)
            {
                for (int x = 0; x < frame.Width; x++)
                {
                    int i = y * frame.Width + x;
                    if (frame.Opaque[i] && glowOf[frame.Pixels[i]] is >= 0 and var glow)
                    {
                        Add(left + (mirrored ? frame.Width - 1 - x : x), top + y, glow);
                    }
                }
            }
        }

        // The ground: each tile's glowing pixels counted once, then laid where the tile is.
        var tilePixels = new Dictionary<int, List<(int X, int Y, int Glow)>>();
        for (int cell = 0; cell < region.TileMap.Length; cell++)
        {
            int tile = region.TileMap[cell];
            if (!tilePixels.TryGetValue(tile, out var pixels))
            {
                pixels = [];
                if (region.Tiles.TryGetValue(tile, out ImageFrame? frame) && frame.Rgba is null)
                {
                    for (int i = 0; i < frame.Pixels.Length; i++)
                    {
                        if (frame.Opaque[i] && glowOf[frame.Pixels[i]] is >= 0 and var glow)
                        {
                            pixels.Add((i % frame.Width, i / frame.Width, glow));
                        }
                    }
                }
                tilePixels[tile] = pixels;
            }
            int left = cell % region.Width * Region.TileSize, top = cell / region.Width * Region.TileSize;
            foreach (var (x, y, glow) in pixels)
            {
                Add(left + x, top + y, glow);
            }
        }

        var lights = new List<MapLight>();
        foreach (RegionObject o in region.Objects)
        {
            if (rules.Objects.TryGetValue(o.Ojff.ToString(System.Globalization.CultureInfo.InvariantCulture), out Light? light))
            {
                var middle = new Vector2(o.X + o.Frame.Width / 2f, o.Y + o.Frame.Height / 2f);
                lights.Add(new MapLight(middle + light.At, light, o.EtabIndex));
            }
            else
            {
                AddFrame(o.Frame, o.X, o.Y, o.Mirrored);
            }
        }
        foreach (PlacedSprite wall in region.Walls)
        {
            AddFrame(wall.Frame, wall.X, wall.Y, wall.Mirrored);
        }

        foreach (var ((bx, by, glow), (count, sx, sy)) in blocks.OrderBy(b => b.Key.By).ThenBy(b => b.Key.Bx))
        {
            PaletteGlow p = rules.Palette[glow];
            if (count < p.MinPixels)
            {
                continue;
            }
            Light light = p.Light with { Strength = p.Light.Strength * Math.Clamp(count / (float)FullAt, 0.35f, 1f) };
            lights.Add(new MapLight(new Vector2(sx / (float)count, sy / (float)count) + light.At, light, by * 1000 + bx));
        }
        if (rules.Regions.TryGetValue(region.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), out var placed))
        {
            lights.AddRange(placed.Select((l, i) => new MapLight(new Vector2(l.X, l.Y) + l.Light.At, l.Light, 90_000 + i)));
        }
        return lights;
    }
}
