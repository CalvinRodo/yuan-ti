using DarkSun.Data.Regions;
using Godot;

namespace DarkSun.Engine;

/// <summary>A floor type painted on map cells in the GFF viewer (<c>data/map_terrain.json</c>).</summary>
public enum FloorTerrain
{
    Water,
    Sand,
}

/// <summary>
/// What the map draws on its floor, under every wall and figure (between the
/// ground and the sorted sprites), none of it in DSUN.EXE: the figures'
/// shadows, dust raised by walking on sand or dirt, splashes on water, and in a fight red rings
/// under the enemies; and, with defiling (<see cref="DarkSun.Game.Combat.DefilingRules"/>),
/// the ash a defiler leaves. After the Obsidian Edition's
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
    private static readonly Color SplashTint = new(0.85f, 0.93f, 1f);

    /// <summary>Farther than this in a frame isn't a step but a figure put somewhere (a region entered, a load).</summary>
    private const float Jump = 24;

    private static readonly Color RingColor = new(0.85f, 0.15f, 0.1f, 0.85f);
    private static readonly Color ChosenRingColor = new(1f, 0.1f, 0.05f, 1f);

    private AshLayer _ash = null!;
    private CanvasGroup _shadowGroup = null!;
    private DustLayer _dust = null!;
    private RingLayer _rings = null!;
    private readonly Dictionary<Sprite2D, Sprite2D> _shadows = [];
    private readonly Dictionary<Sprite2D, Stride> _strides = [];
    private Image? _ground;
    private readonly Random _random = new();

    public override void _Ready()
    {
        _ash = new AshLayer();
        AddChild(_ash);
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

    /// <summary>Walking on sand or dirt raises dust, and on water splashes (<c>[display] dust</c>).</summary>
    public bool Dust { get; set; } = true;

    /// <summary>An indoor map: only cells painted as sand raise dust.</summary>
    public bool Indoors { get; set; }

    /// <summary>The cells painted with a floor type; they decide over the floor's colour.</summary>
    public IReadOnlyDictionary<Vector2I, FloorTerrain> Terrain { get; set; } = new Dictionary<Vector2I, FloorTerrain>();

    /// <summary>Figures casting a shadow now.</summary>
    public int ShadowCount => _shadows.Count;

    /// <summary>Puffs of dust and splashes on the floor now.</summary>
    public int PuffCount => _dust.Puffs.Count;

    /// <summary>Splashes on the floor now.</summary>
    public int SplashCount => _dust.Puffs.Count(p => p.Splash);

    /// <summary>Cells of ash on the floor now.</summary>
    public int AshCount => _ash.Cells.Count;

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
        _ash.Ground = ground;
        ShowAsh([], []);
    }

    /// <summary>
    /// Ash on these cells (<see cref="AshLayer"/>), in <paramref name="palette"/>'s
    /// colours (the region's); none to clear it.
    /// </summary>
    public void ShowAsh(IReadOnlyCollection<Vector2I> cells, IReadOnlyList<Color> palette)
    {
        _ash.Cells = [.. cells];
        if (palette.Count > 0 && !palette.SequenceEqual(_ash.Palette))
        {
            _ash.Palette = palette;
        }
        _ash.QueueRedraw();
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

    /// <summary>Count how far a figure walked; every <see cref="DustStep"/> pixels on sand, dirt or water, a puff or splash behind a foot.</summary>
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
        if (StepEffect(at) is var (colour, splash) && _dust.Puffs.Count < MaxPuffs)
        {
            // Splashes stay where the foot fell; dust drifts back from the walk.
            var drift = splash ? Vector2.Zero : new Vector2((float)_random.NextDouble() - 0.5f, (float)_random.NextDouble() - 0.5f) * 3 - way * 2;
            _dust.Puffs.Add(new Puff(at, drift, colour, splash));
        }
    }

    /// <summary>
    /// What a step at <paramref name="at"/> raises, in a colour of the floor
    /// there: a splash on a cell painted as water; dust on one painted as sand,
    /// or, on an unpainted cell outdoors, on warm middling colours (sand, dirt),
    /// not stone, water, lava or grass. Null where nothing rises.
    /// </summary>
    private (Color Colour, bool Splash)? StepEffect(Vector2 at)
    {
        if (_ground is null || at.X < 0 || at.Y < 0 || at.X >= _ground.GetWidth() || at.Y >= _ground.GetHeight())
        {
            return null;
        }
        Color floor = _ground.GetPixel((int)at.X, (int)at.Y);
        var cell = new Vector2I((int)at.X / Region.TileSize, (int)at.Y / Region.TileSize);
        if (Terrain.TryGetValue(cell, out FloorTerrain terrain))
        {
            return terrain == FloorTerrain.Water ? (floor.Lerp(SplashTint, 0.7f), true) : (floor.Lightened(0.7f), false);
        }
        float hue = floor.H * 360;
        return !Indoors && hue is >= 12 and <= 55 && floor.S is >= 0.15f and <= 0.75f && floor.V is >= 0.3f and <= 0.95f
            ? (floor.Lightened(0.7f), false)
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

    private sealed class Puff(Vector2 at, Vector2 drift, Color colour, bool splash)
    {
        public Vector2 At { get; } = at;
        public Vector2 Drift { get; } = drift;
        public Color Colour { get; } = colour;
        public bool Splash { get; } = splash;
        public float Age { get; set; }
    }

    /// <summary>
    /// The puffs: each spreads, rises a little and fades in <see cref="DustLife"/>,
    /// drawn as single pixels thinned toward its edge and as it fades by a
    /// pattern fixed to the map, so it doesn't shimmer as it moves. A splash
    /// is a ripple instead: a ring that widens on the floor and fades.
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
                Vector2 centre = puff.At + puff.Drift * t + (puff.Splash ? Vector2.Zero : new Vector2(0, -DustRise * t));
                float strength = Mathf.Pow(1 - t, 0.7f);
                int r = (int)Mathf.Ceil(radius);
                for (int y = -r; y <= r; y++)
                {
                    for (int x = -r; x <= r; x++)
                    {
                        // Flattened as the floor is seen: half as tall as wide.
                        float d = new Vector2(x, y * 2).Length() / radius;
                        int px = (int)centre.X + x, py = (int)centre.Y + y;
                        // A ripple's pixels lie on its rim; a puff's thin out toward its edge.
                        float density = puff.Splash ? (d is >= 0.7f and <= 1 ? 1 : 0) : 1 - d * d * d;
                        if (d <= 1 && Dither(px, py) < strength * density)
                        {
                            DrawRect(new Rect2(px, py, 1, 1), puff.Colour);
                        }
                    }
                }
            }
        }

        /// <summary>A threshold in [0, 1) for each map pixel, the same every frame.</summary>
        public static float Dither(int x, int y)
        {
            uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663);
            h ^= h >> 13;
            h *= 0x5bd1e995;
            h ^= h >> 15;
            return (h & 0xFFFF) / 65536f;
        }
    }

    /// <summary>
    /// Ash: the ground itself burnt out. Each pixel of an ash cell is the floor's
    /// own colour there, drained of colour and darkened, then the nearest colour
    /// of the region's palette, so the ground keeps its pattern (sand's ripples,
    /// a flagstone's cracks) in the palette's dull greys and browns, as the
    /// original's art is drawn. Where the ash ends it thins out in clumps of
    /// pixels over a few pixels, not along a straight line. Opaque, so colour
    /// cycling still reads the floor around it.
    /// </summary>
    private sealed partial class AshLayer : Node2D
    {
        /// <summary>How far in from bare ground the ash thins, in pixels.</summary>
        private const float Fade = 7;

        /// <summary>How much colour the ash keeps, and how bright it is beside the ground.</summary>
        private const float Colour = 0.35f, Darkness = 0.5f;

        private readonly Dictionary<Color, Color> _nearest = [];

        public HashSet<Vector2I> Cells { get; set; } = [];

        public Image? Ground { get; set; }

        public IReadOnlyList<Color> Palette
        {
            get;
            set
            {
                field = value;
                _nearest.Clear();
            }
        } = [];

        public override void _Draw()
        {
            if (Ground is null || Palette.Count == 0)
            {
                return;
            }
            int size = Region.TileSize;
            foreach (Vector2I cell in Cells)
            {
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        int px = cell.X * size + x, py = cell.Y * size + y;
                        if (px >= Ground.GetWidth() || py >= Ground.GetHeight())
                        {
                            continue;
                        }
                        // Clumps of 2 × 2 pixels thin out toward bare ground.
                        float clump = DustLayer.Dither(px / 2, py / 2);
                        if (Inside(cell, x, y) / Fade < 0.15f + clump * 0.85f)
                        {
                            continue;
                        }
                        float mottle = 0.92f + 0.16f * DustLayer.Dither(px / 2 + 7919, py / 2 + 104729);
                        DrawRect(new Rect2(px, py, 1, 1), Ash(Ground.GetPixel(px, py), mottle));
                    }
                }
            }
        }

        /// <summary>How far pixel (x, y) of <paramref name="cell"/> is from the nearest bare cell's edge or corner (<see cref="Fade"/> or more: deep in the ash).</summary>
        private float Inside(Vector2I cell, int x, int y)
        {
            int size = Region.TileSize;
            float nearest = Fade;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if ((dx, dy) == (0, 0) || Cells.Contains(cell + new Vector2I(dx, dy)))
                    {
                        continue;
                    }
                    // Distance to that neighbour's square, along each axis it lies off.
                    float ax = dx < 0 ? x + 0.5f : dx > 0 ? size - x - 0.5f : 0;
                    float ay = dy < 0 ? y + 0.5f : dy > 0 ? size - y - 0.5f : 0;
                    nearest = Math.Min(nearest, MathF.Sqrt(ax * ax + ay * ay));
                }
            }
            return nearest;
        }

        /// <summary>The ash a floor colour turns to: greyed, darkened, mottled, then the palette's nearest.</summary>
        private Color Ash(Color floor, float mottle)
        {
            float grey = floor.R * 0.3f + floor.G * 0.59f + floor.B * 0.11f;
            var ash = new Color(grey, grey, grey).Lerp(floor, Colour) * (Darkness * mottle);
            ash = new Color(MathF.Round(ash.R * 63) / 63, MathF.Round(ash.G * 63) / 63, MathF.Round(ash.B * 63) / 63);
            if (!_nearest.TryGetValue(ash, out Color found))
            {
                found = Palette.MinBy(p => (p.R - ash.R) * (p.R - ash.R) * 0.3f + (p.G - ash.G) * (p.G - ash.G) * 0.59f + (p.B - ash.B) * (p.B - ash.B) * 0.11f);
                _nearest[ash] = found;
            }
            return found;
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
