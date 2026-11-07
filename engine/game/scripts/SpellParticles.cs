using DarkSun.Game.Animation;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// Spells drawn as particles (<c>[display] spell_effects</c>; none in DSUN.EXE), over the
/// figures, in place of the original's <c>OJFF</c> animations where a look gives them
/// (<see cref="ParticleLooks"/>). Each effect is a <see cref="ParticleEffect"/>, moved on
/// here and drawn in one of two styles:
/// <list type="bullet">
/// <item><see cref="ParticleStyle.Pixel"/>: whole map pixels, as the game draws, in the region's
/// palette (its colours carry colour cycling's codes, so fire still flickers), faded by an
/// ordered dither rather than see-through, never blended;</item>
/// <item><see cref="ParticleStyle.Modern"/>: at the screen's resolution, soft and glowing,
/// added onto what is under them, with light thrown on the ground (<see cref="Ground"/>), billowing
/// puffs, sharp shards and shock waves, and the view shaking (<see cref="Shaking"/>).</item>
/// </list>
/// An effect's layers are drawn over it, in order.
/// </summary>
public partial class SpellParticles : Node2D
{
    /// <summary>A 4 × 4 ordered dither: the share of pixels a fade keeps, the same pixels every frame.</summary>
    private static readonly int[] Bayer = [0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5];

    private readonly List<(ParticleEffect Effect, ParticleStyle Style)> _effects = [];
    private Layer _mix = null!, _add = null!;
    private Texture2D _soft = null!, _smoke = null!, _shock = null!;

    public SpellParticles()
    {
        Ground = new Layer { Particles = this, Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add } };
    }

    /// <summary>The modern style's light on the ground: put under the figures.</summary>
    public Node2D Ground { get; }

    /// <summary>The region's palette as drawn (<see cref="ColourCycling.Keyed"/>), for the pixel style.</summary>
    public IReadOnlyList<Color> Palette { get; set; } = [];

    public override void _Ready()
    {
        _soft = new GradientTexture2D
        {
            Width = 64,
            Height = 64,
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f),
            FillTo = new Vector2(0.5f, 0),
            Gradient = new Gradient { Colors = [Colors.White, new Color(1, 1, 1, 0.35f), new Color(1, 1, 1, 0)], Offsets = [0, 0.35f, 1] },
        };
        _smoke = SmokeTexture();
        _shock = new GradientTexture2D
        {
            Width = 128,
            Height = 128,
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f),
            FillTo = new Vector2(0.5f, 0),
            Gradient = new Gradient
            {
                Colors = [new Color(1, 1, 1, 0), new Color(1, 1, 1, 0.15f), Colors.White, new Color(1, 1, 1, 0)],
                Offsets = [0, 0.6f, 0.9f, 1],
            },
        };
        _mix = new Layer { Particles = this, Blend = ParticleBlend.Mix };
        _add = new Layer { Particles = this, Blend = ParticleBlend.Add, Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add } };
        AddChild(_mix);
        AddChild(_add);
    }

    /// <summary>Effects playing now.</summary>
    public IEnumerable<ParticleEffect> Effects => _effects.Select(e => e.Effect);

    /// <summary>Particles alive now.</summary>
    public int ParticleCount => _effects.Sum(e => e.Effect.WithLayers.Sum(l => l.Particles.Count));

    /// <summary>How far the view shakes now, in map pixels: the most of the effects'.</summary>
    public float Shaking => _effects.Count == 0 ? 0 : _effects.Max(e => e.Effect.Shaking);

    /// <summary>
    /// A lumpy puff: a soft disc whose edge and inside are broken up by noise, so a cloud of them turned
    /// every way billows rather than reading as dots.
    /// </summary>
    private static ImageTexture SmokeTexture()
    {
        const int size = 64;
        var noise = new FastNoiseLite { Seed = 11, NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth, Frequency = 0.07f, FractalOctaves = 3 };
        Image image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f) / size * 2 - 1, dy = (y + 0.5f) / size * 2 - 1;
                float n = noise.GetNoise2D(x, y); // about -1 to 1
                float edge = 0.78f + 0.22f * n;
                float r = MathF.Sqrt(dx * dx + dy * dy) / edge;
                float a = Math.Clamp(1 - r, 0, 1);
                a = a * a * (3 - 2 * a) * (0.7f + 0.3f * noise.GetNoise2D(x * 2.3f + 100, y * 2.3f));
                float shade = 0.82f + 0.18f * n;
                image.SetPixel(x, y, new Color(shade, shade, shade, Math.Clamp(a * 1.4f, 0, 1)));
            }
        }
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>The playback waits for one of them (a flight in the air, a burst that blocks).</summary>
    public bool Blocking => _effects.Any(e => e.Effect.Blocks);

    public ParticleEffect Add(ParticleEffect effect, ParticleStyle style)
    {
        _effects.Add((effect, style));
        return effect;
    }

    public void Clear() => _effects.Clear();

    /// <summary>Each frame: every effect moves on; those done go.</summary>
    public void Advance(double delta)
    {
        foreach (var (effect, _) in _effects)
        {
            effect.Update(delta);
        }
        _effects.RemoveAll(e => e.Effect.Done);
        _mix.QueueRedraw();
        _add.QueueRedraw();
        Ground.QueueRedraw();
    }

    private static Vector2 V(System.Numerics.Vector2 v) => new(v.X, v.Y);

    /// <summary>A particle's colour: the one picked for it, else its colour at that point of its life.</summary>
    private Color ColourOf(ParticleDefinition d, Particle p) => ColourAt(d, d.RandomColor ? p.Pick : p.T);

    /// <summary>The colour at <paramref name="t"/> (0–1) of the palette's indexes, stepped, else of its colours, blended.</summary>
    private Color ColourAt(ParticleDefinition d, float t)
    {
        if (d.Palette.Length > 0)
        {
            int index = ParticleEffect.Stepped(d.Palette, t);
            return index < Palette.Count ? Palette[index] : Colors.White;
        }
        var keys = d.Rgba;
        float at = Math.Clamp(t, 0, 1) * (keys.Count - 1);
        int i = Math.Min((int)at, Math.Max(0, keys.Count - 2));
        var (a, b) = (keys[i], keys[Math.Min(i + 1, keys.Count - 1)]);
        return new Color(a.R, a.G, a.B, a.A).Lerp(new Color(b.R, b.G, b.B, b.A), at - i);
    }

    private void DrawLayer(Layer layer)
    {
        foreach (var (effect, style) in _effects.SelectMany(e => e.Effect.WithLayers.Select(l => (l, e.Style))))
        {
            ParticleDefinition d = effect.Definition;
            if (layer.Blend is null)
            {
                if (style == ParticleStyle.Modern && d.Glow is { } glow)
                {
                    DrawLight(layer, effect, glow);
                }
                continue;
            }
            // The pixel style is never blended: everything is drawn as the original draws, in the mixing layer.
            if ((style == ParticleStyle.Pixel ? ParticleBlend.Mix : d.Blend) != layer.Blend)
            {
                continue;
            }
            foreach (Particle p in effect.Particles)
            {
                float size = ParticleEffect.Over(d.Size, p.T) * p.Scale;
                if (style == ParticleStyle.Pixel)
                {
                    DrawPixels(layer, d, V(p.Position), V(p.Velocity), size, ColourOf(d, p), ParticleEffect.Over(d.Alpha, p.T));
                }
                else
                {
                    DrawSoft(layer, d, V(p.Position), V(p.Velocity), size, ColourOf(d, p), ParticleEffect.Over(d.Alpha, p.T), p.Rotation);
                }
            }
            if (d.HeadSize > 0)
            {
                foreach (System.Numerics.Vector2 head in effect.Heads())
                {
                    if (style == ParticleStyle.Pixel)
                    {
                        DrawPixels(layer, d with { Texture = ParticleTexture.Glow }, V(head), Vector2.Zero, d.HeadSize, ColourAt(d, 0), 1);
                    }
                    else
                    {
                        DrawSoft(layer, d with { Texture = ParticleTexture.Glow }, V(head), Vector2.Zero, d.HeadSize, ColourAt(d, 0), 1, 0);
                    }
                }
            }
        }
    }

    /// <summary>The pixel style: whole map pixels, kept or not by the dither where it fades.</summary>
    private static void DrawPixels(Layer layer, ParticleDefinition d, Vector2 at, Vector2 velocity, float size, Color colour, float alpha)
    {
        alpha *= colour.A;
        colour.A = 1;
        int s = Math.Max(1, (int)MathF.Round(size)), x0 = (int)MathF.Floor(at.X), y0 = (int)MathF.Floor(at.Y);
        void Pixel(int x, int y, float keep)
        {
            if ((Bayer[(y & 3) * 4 + (x & 3)] + 0.5f) / 16 < keep)
            {
                layer.DrawRect(new Rect2(x, y, 1, 1), colour);
            }
        }
        switch (d.Texture)
        {
            case ParticleTexture.Shock when s > 2:
                // A ring on the floor: its rim's pixels, flattened as the floor is seen.
                float rx = s / 2f;
                for (int i = 0, steps = Math.Max(12, s * 2); i < steps; i++)
                {
                    float a = i * MathF.Tau / steps;
                    Pixel((int)MathF.Floor(at.X + MathF.Cos(a) * rx), (int)MathF.Floor(at.Y + MathF.Sin(a) * rx / 2), alpha);
                }
                break;
            case ParticleTexture.Spark or ParticleTexture.Shard when velocity.LengthSquared() > 0:
                Vector2 back = -velocity.Normalized();
                for (int i = 0; i < s * 2; i++)
                {
                    Pixel((int)MathF.Floor(at.X + back.X * i), (int)MathF.Floor(at.Y + back.Y * i), alpha * (1 - i / (s * 2f)));
                }
                break;
            case ParticleTexture.Glow or ParticleTexture.Smoke when s > 1:
                float r = s / 2f;
                for (int y = -(int)r; y <= (int)r; y++)
                {
                    for (int x = -(int)r; x <= (int)r; x++)
                    {
                        float dd = (x * x + y * y) / (r * r);
                        if (dd <= 1)
                        {
                            Pixel(x0 + x, y0 + y, alpha * (1 - dd * dd));
                        }
                    }
                }
                break;
            default:
                for (int y = 0; y < s; y++)
                {
                    for (int x = 0; x < s; x++)
                    {
                        Pixel(x0 - s / 2 + x, y0 - s / 2 + y, alpha);
                    }
                }
                break;
        }
    }

    /// <summary>The modern style: where the particle is to the screen's pixel, soft-edged, glowing if it is added, turned by <paramref name="rotation"/> (radians).</summary>
    private void DrawSoft(Layer layer, ParticleDefinition d, Vector2 at, Vector2 velocity, float size, Color colour, float alpha, float rotation)
    {
        colour.A *= alpha;
        if (size <= 0 || colour.A <= 0)
        {
            return;
        }
        switch (d.Texture)
        {
            case ParticleTexture.Pixel:
                layer.DrawRect(new Rect2(at - Vector2.One * size / 2, Vector2.One * size), colour);
                break;
            case ParticleTexture.Spark when velocity.LengthSquared() > 0:
                Vector2 tail = at - velocity * 0.05f - velocity.Normalized() * size;
                layer.DrawLine(tail, at, colour, Math.Max(0.5f, size * 0.5f), true);
                layer.DrawTextureRect(_soft, new Rect2(at - Vector2.One * size, Vector2.One * size * 2), false, colour with { A = colour.A * 0.5f });
                break;
            case ParticleTexture.Smoke:
                layer.DrawSetTransform(at, rotation, Vector2.One);
                layer.DrawTextureRect(_smoke, new Rect2(-Vector2.One * size, Vector2.One * size * 2), false, colour);
                layer.DrawSetTransform(Vector2.Zero, 0, Vector2.One);
                break;
            case ParticleTexture.Shard:
                // A sliver: a long point ahead, a short one behind, narrow across; the way it flies, else as it turns.
                Vector2 ahead = velocity.LengthSquared() > 1 ? velocity.Normalized() : Vector2.FromAngle(rotation);
                Vector2 across = new Vector2(-ahead.Y, ahead.X) * size * 0.3f;
                layer.DrawColoredPolygon([at + ahead * size * 1.6f, at + across, at - ahead * size * 0.8f, at - across], colour);
                if (d.Blend == ParticleBlend.Add)
                {
                    layer.DrawTextureRect(_soft, new Rect2(at - Vector2.One * size, Vector2.One * size * 2), false, colour with { A = colour.A * 0.35f });
                }
                break;
            case ParticleTexture.Shock:
                // A ring on the floor: its size across, flattened as the floor is seen.
                layer.DrawTextureRect(_shock, new Rect2(at - new Vector2(size / 2, size / 4), new Vector2(size, size / 2)), false, colour);
                break;
            default:
                layer.DrawTextureRect(_soft, new Rect2(at - Vector2.One * size, Vector2.One * size * 2), false, colour);
                break;
        }
    }

    /// <summary>Light on the ground: a flattened glow under each head in flight, else where it shows, fading as its particles die.</summary>
    private void DrawLight(Layer layer, ParticleEffect effect, ParticleGlow glow)
    {
        var (r, g, b, a) = ParticleDefinition.ParseColour(glow.Color);
        float strength = Math.Min(1, effect.Particles.Count / 24f) * glow.Alpha * a;
        List<System.Numerics.Vector2> at = [.. effect.Heads()];
        if (at.Count == 0)
        {
            at.Add(effect.Definition.Shape is ParticleShape.Cone or ParticleShape.Line ? (effect.From + effect.To) / 2 : effect.To);
        }
        foreach (System.Numerics.Vector2 p in at)
        {
            var size = new Vector2(glow.Radius * 2, glow.Radius);
            layer.DrawTextureRect(_soft, new Rect2(V(p) - size / 2, size), false, new Color(r, g, b, strength));
        }
    }

    /// <summary>A canvas the particles of one blend are drawn on (null: the ground's light).</summary>
    private sealed partial class Layer : Node2D
    {
        public SpellParticles Particles { get; init; } = null!;

        public ParticleBlend? Blend { get; init; }

        public override void _Draw() => Particles.DrawLayer(this);
    }
}
