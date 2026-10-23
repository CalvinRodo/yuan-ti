using Godot;

namespace DarkSun.Engine;

/// <summary>
/// What the map draws on its floor, under every wall and figure (between the
/// ground and the sorted sprites), none of it in DSUN.EXE: the figures'
/// shadows, dust raised by walking on sand or dirt, and in a fight red rings
/// under the enemies. After the Obsidian Edition's
/// (<c>github.com/daaki85/darksun-obsidian-edition</c>, FEATURES.md, Graphics
/// and Controls), drawn here with Godot rather than into the game's floor.
/// </summary>
public partial class FloorEffects : Node2D
{
    /// <summary>A shadow lies toward the lower right (the maps are lit from the upper left): where a figure's top lands, per pixel of its height.</summary>
    private static readonly Vector2 ShadowLean = new(0.75f, 0.4f);

    private static readonly Color ShadowTint = new(0.12f, 0.06f, 0.02f);
    private const float ShadowAlpha = 0.45f;

    /// <summary>A puff every this many pixels walked, the feet in turn.</summary>
    private const float DustStep = 6;
    private const float DustLife = 1.0f, DustStartRadius = 2.5f, DustEndRadius = 7f, DustRise = 4f;
    private const int MaxPuffs = 240;

    /// <summary>Farther than this in a frame isn't a step but a figure put somewhere (a region entered, a load).</summary>
    private const float Jump = 24;

    private static readonly Color RingColor = new(0.85f, 0.15f, 0.1f, 0.85f);
    private static readonly Color ChosenRingColor = new(1f, 0.1f, 0.05f, 1f);

    private CanvasGroup _shadowGroup = null!;
    private DustLayer _dust = null!;
    private RingLayer _rings = null!;
    private readonly Dictionary<Sprite2D, Sprite2D> _shadows = [];
    private readonly Dictionary<Sprite2D, Stride> _strides = [];
    private Image? _ground;
    private readonly Random _random = new();

    public override void _Ready()
    {
        // One group, so where shadows overlap the floor is darkened once, not twice.
        _shadowGroup = new CanvasGroup { Modulate = new Color(1, 1, 1, ShadowAlpha) };
        AddChild(_shadowGroup);
        _dust = new DustLayer();
        AddChild(_dust);
        _rings = new RingLayer();
        AddChild(_rings);
    }

    /// <summary>Living figures cast shadows (<c>[display] shadows</c>).</summary>
    public bool Shadows { get; set; } = true;

    /// <summary>Walking on sand or dirt raises dust (<c>[display] dust</c>).</summary>
    public bool Dust { get; set; } = true;

    /// <summary>Figures casting a shadow now.</summary>
    public int ShadowCount => _shadows.Count;

    /// <summary>Puffs of dust on the floor now.</summary>
    public int PuffCount => _dust.Puffs.Count;

    /// <summary>Rings on the floor now, and whether one is the chosen enemy's.</summary>
    public (int Count, bool Chosen) RingCount => (_rings.Rings.Count, _rings.Rings.Any(r => r.Chosen));

    /// <summary>A new region: its ground, whose colours say where dust rises; nothing of the last one is kept.</summary>
    public void SetGround(Image ground)
    {
        _ground = ground;
        foreach (Sprite2D shadow in _shadows.Values)
        {
            shadow.QueueFree();
        }
        _shadows.Clear();
        _strides.Clear();
        _dust.Puffs.Clear();
        _dust.QueueRedraw();
        ShowRings([]);
    }

    /// <summary>
    /// Follow the figures as they are drawn this frame: each living one casts its
    /// picture's shadow, and those that walked raise dust.
    /// </summary>
    /// <param name="figures">The figures' sprites (hot spot on the feet) that are alive and shown.</param>
    public void Follow(double delta, IReadOnlyCollection<Sprite2D> figures)
    {
        _shadowGroup.Visible = Shadows;
        foreach (Sprite2D gone in _shadows.Keys.Where(f => !figures.Contains(f) || !IsInstanceValid(f)).ToList())
        {
            _shadows[gone].QueueFree();
            _shadows.Remove(gone);
        }
        foreach (Sprite2D gone in _strides.Keys.Where(f => !figures.Contains(f)).ToList())
        {
            _strides.Remove(gone);
        }
        foreach (Sprite2D figure in figures)
        {
            if (Shadows)
            {
                CastShadow(figure);
            }
            if (Dust)
            {
                Walked(figure);
            }
        }
        _dust.Age((float)delta);
    }

    /// <summary>The figure's picture laid flat on the floor from its feet, its top toward the lower right.</summary>
    private void CastShadow(Sprite2D figure)
    {
        if (!_shadows.TryGetValue(figure, out Sprite2D? shadow))
        {
            shadow = new Sprite2D { Modulate = ShadowTint, TextureFilter = figure.TextureFilter };
            _shadowGroup.AddChild(shadow);
            _shadows[figure] = shadow;
        }
        // The picture's y axis (up is −y) turned to lie along −ShadowLean, flattened.
        shadow.Transform = new Transform2D(new Vector2(1, 0), -ShadowLean, figure.Position);
        (shadow.Texture, shadow.FlipH, shadow.Offset, shadow.Centered) = (figure.Texture, figure.FlipH, figure.Offset, figure.Centered);
    }

    /// <summary>Count how far a figure walked; every <see cref="DustStep"/> pixels on sand or dirt, a puff behind a foot.</summary>
    private void Walked(Sprite2D figure)
    {
        Vector2 feet = figure.Position + new Vector2(0, 1);
        if (!_strides.TryGetValue(figure, out Stride? stride))
        {
            _strides[figure] = new Stride(feet);
            return;
        }
        Vector2 moved = feet - stride.Last;
        stride.Last = feet;
        if (moved.Length() > Jump || moved == Vector2.Zero)
        {
            return;
        }
        stride.Walked += moved.Length();
        if (stride.Walked < DustStep)
        {
            return;
        }
        stride.Walked -= DustStep;
        stride.LeftFoot = !stride.LeftFoot;
        Vector2 way = moved.Normalized();
        Vector2 side = new Vector2(-way.Y, way.X) * (stride.LeftFoot ? 2 : -2);
        Vector2 at = feet - way * 3 + side;
        if (DustColour(at) is { } colour && _dust.Puffs.Count < MaxPuffs)
        {
            var drift = new Vector2((float)_random.NextDouble() - 0.5f, (float)_random.NextDouble() - 0.5f) * 3 - way * 2;
            _dust.Puffs.Add(new Puff(at, drift, colour));
        }
    }

    /// <summary>
    /// The colour of dust off the floor at <paramref name="at"/>: lighter than
    /// it, and only on warm middling colours (sand, dirt), not stone, water,
    /// lava or grass. Null where no dust rises.
    /// </summary>
    private Color? DustColour(Vector2 at)
    {
        if (_ground is null || at.X < 0 || at.Y < 0 || at.X >= _ground.GetWidth() || at.Y >= _ground.GetHeight())
        {
            return null;
        }
        Color floor = _ground.GetPixel((int)at.X, (int)at.Y);
        float hue = floor.H * 360;
        return hue is >= 12 and <= 55 && floor.S is >= 0.15f and <= 0.75f && floor.V is >= 0.3f and <= 0.95f
            ? floor.Lightened(0.7f)
            : null;
    }

    /// <summary>Rings under these feet (radius across, the chosen thicker and redder); none to put them away.</summary>
    public void ShowRings(IReadOnlyList<(Vector2 Feet, float Radius, bool Chosen)> rings)
    {
        _rings.Rings = rings;
        _rings.Visible = rings.Count > 0;
        _rings.QueueRedraw();
    }

    private sealed class Stride(Vector2 last)
    {
        public Vector2 Last { get; set; } = last;
        public float Walked { get; set; }
        public bool LeftFoot { get; set; }
    }

    private sealed class Puff(Vector2 at, Vector2 drift, Color colour)
    {
        public Vector2 At { get; } = at;
        public Vector2 Drift { get; } = drift;
        public Color Colour { get; } = colour;
        public float Age { get; set; }
    }

    /// <summary>
    /// The puffs: each spreads, rises a little and fades in <see cref="DustLife"/>,
    /// drawn as single pixels thinned toward its edge and as it fades by a
    /// pattern fixed to the map, so it doesn't shimmer as it moves.
    /// </summary>
    private sealed partial class DustLayer : Node2D
    {
        public List<Puff> Puffs { get; } = [];

        public void Age(float delta)
        {
            if (Puffs.Count == 0)
            {
                return;
            }
            foreach (Puff puff in Puffs)
            {
                puff.Age += delta;
            }
            Puffs.RemoveAll(p => p.Age >= DustLife);
            QueueRedraw();
        }

        public override void _Draw()
        {
            foreach (Puff puff in Puffs)
            {
                float t = puff.Age / DustLife;
                float radius = Mathf.Lerp(DustStartRadius, DustEndRadius, Mathf.Sqrt(t));
                Vector2 centre = puff.At + puff.Drift * t + new Vector2(0, -DustRise * t);
                float strength = Mathf.Pow(1 - t, 0.7f);
                int r = (int)Mathf.Ceil(radius);
                for (int y = -r; y <= r; y++)
                {
                    for (int x = -r; x <= r; x++)
                    {
                        // Flattened as the floor is seen: half as tall as wide.
                        float d = new Vector2(x, y * 2).Length() / radius;
                        int px = (int)centre.X + x, py = (int)centre.Y + y;
                        if (d <= 1 && Dither(px, py) < strength * (1 - d * d * d))
                        {
                            DrawRect(new Rect2(px, py, 1, 1), puff.Colour);
                        }
                    }
                }
            }
        }

        /// <summary>A threshold in [0, 1) for each map pixel, the same every frame.</summary>
        private static float Dither(int x, int y)
        {
            uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663);
            h ^= h >> 13;
            h *= 0x5bd1e995;
            h ^= h >> 15;
            return (h & 0xFFFF) / 65536f;
        }
    }

    /// <summary>Red rings on the floor under the enemies' feet; the chosen one's thicker and redder.</summary>
    private sealed partial class RingLayer : Node2D
    {
        public IReadOnlyList<(Vector2 Feet, float Radius, bool Chosen)> Rings { get; set; } = [];

        public override void _Draw()
        {
            foreach (var (feet, radius, chosen) in Rings)
            {
                DrawSetTransform(feet + new Vector2(0, -2), 0, new Vector2(1, 0.5f));
                DrawArc(Vector2.Zero, radius, 0, Mathf.Tau, 32, chosen ? ChosenRingColor : RingColor, chosen ? 3f : 1.5f);
            }
            DrawSetTransform(Vector2.Zero, 0, Vector2.One);
        }
    }
}
