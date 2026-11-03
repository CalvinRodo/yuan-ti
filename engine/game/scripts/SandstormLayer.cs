using Godot;

namespace DarkSun.Engine;

/// <summary>
/// A sandstorm blowing over the map (a mod's weather, <see cref="DarkSun.Game.World.Sandstorm"/>;
/// none in DSUN.EXE): drifting streaks of sand over the floor and every figure, in the
/// region's own ground colours, a whole map pixel at a time and dithered as the original
/// draws, moving in steps like its palette cycling rather than smoothly. It fades in and out.
/// </summary>
public partial class SandstormLayer : Node2D
{
    /// <summary>How many of the ground's commonest colours the sand is drawn in.</summary>
    private const int Shades = 4;

    /// <summary>Seconds to blow up or die down.</summary>
    private const float Fade = 2.5f;

    private const string ShaderCode = """
        shader_type canvas_item;
        uniform vec3 sand[4];
        uniform vec2 size = vec2(1.0);
        uniform vec2 wind = vec2(46.0, 11.0);
        uniform float strength = 0.0;
        uniform float steps = 12.0;

        float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }

        float noise(vec2 p) {
            vec2 i = floor(p), f = fract(p);
            f = f * f * (3.0 - 2.0 * f);
            return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), f.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), f.x), f.y);
        }

        // A 4 × 4 ordered dither, as the floor effects use.
        float bayer(vec2 px) {
            int x = int(mod(px.x, 4.0)), y = int(mod(px.y, 4.0));
            int m[16] = int[](0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5);
            return (float(m[y * 4 + x]) + 0.5) / 16.0;
        }

        void fragment() {
            vec2 px = floor(UV * size);
            float t = floor(TIME * steps) / steps;
            vec2 p = px - floor(wind * t);
            // Long thin streaks along the wind, and gusts that come and go across them.
            float streak = noise(vec2(p.x / 48.0, p.y / 3.0)) * 0.65 + noise(vec2(p.x / 15.0, p.y / 1.5)) * 0.35;
            float gust = noise(vec2(p.x / 160.0 - t * 0.3, p.y / 90.0));
            float density = clamp((streak - 0.42) * 1.3 + gust * 0.3, 0.0, 0.6) * strength;
            if (bayer(px) >= density) {
                discard;
            }
            int shade = int(clamp(noise(vec2(p.x / 9.0, p.y / 2.0)) * 4.0, 0.0, 3.0));
            COLOR = vec4(sand[shade], 1.0);
        }
        """;

    private ShaderMaterial _material = null!;
    private ColorRect _rect = null!;
    private float _strength;

    /// <summary>Is a storm blowing (it fades toward this)?</summary>
    public bool Blowing { get; set; }

    /// <summary>How strongly it shows now, 0–1.</summary>
    public float Strength => _strength;

    public override void _Ready()
    {
        _material = new ShaderMaterial { Shader = new Shader { Code = ShaderCode } };
        _rect = new ColorRect { Material = _material, MouseFilter = Control.MouseFilterEnum.Ignore, Color = Colors.White };
        AddChild(_rect);
        Visible = false;
    }

    /// <summary>A new region: the map's size and its ground, whose commonest colours the sand takes; calm until told.</summary>
    public void SetGround(Image ground)
    {
        _rect.Size = new Vector2(ground.GetWidth(), ground.GetHeight());
        _material.SetShaderParameter("size", _rect.Size);
        _material.SetShaderParameter("sand", SandColours(ground));
        Blowing = false;
        _strength = 0;
        Visible = false;
    }

    public override void _Process(double delta)
    {
        float target = Blowing ? 1 : 0;
        if (_strength == target)
        {
            return;
        }
        _strength = Mathf.MoveToward(_strength, target, (float)delta / Fade);
        _material.SetShaderParameter("strength", _strength);
        Visible = _strength > 0;
    }

    /// <summary>
    /// The sand the ground is made of (every 4th pixel counted): its commonest colour and the
    /// common ones of its family, near it in hue and greyness and not much darker (so not the
    /// rock, the shadows or the plants), lightest first.
    /// </summary>
    private static Vector3[] SandColours(Image ground)
    {
        var counts = new Dictionary<Color, int>();
        for (int y = 0; y < ground.GetHeight(); y += 4)
        {
            for (int x = 0; x < ground.GetWidth(); x += 4)
            {
                // Without the low bits colour cycling reads (ColourCycling), so the sand doesn't cycle as water would.
                Color c = ground.GetPixel(x, y);
                c = new Color((c.R8 & ~3) / 255f, (c.G8 & ~3) / 255f, (c.B8 & ~3) / 255f, c.A);
                if (c.A > 0.5f)
                {
                    counts[c] = counts.GetValueOrDefault(c) + 1;
                }
            }
        }
        List<Color> common = [];
        if (counts.Count > 0)
        {
            Color sand = counts.MaxBy(p => p.Value).Key;
            bool Kin(Color c)
            {
                float hue = Math.Abs(c.H - sand.H);
                return Math.Min(hue, 1 - hue) <= 0.06f && Math.Abs(c.S - sand.S) <= 0.15f && c.V >= sand.V * 0.8f;
            }
            common = [.. counts.Where(p => Kin(p.Key)).OrderByDescending(p => p.Value).Take(Shades).Select(p => p.Key).OrderByDescending(c => c.Luminance)];
        }
        while (common.Count < Shades)
        {
            common.Add(common.Count > 0 ? common[^1] : new Color(0.75f, 0.6f, 0.4f));
        }
        return [.. common.Select(c => new Vector3(c.R, c.G, c.B))];
    }
}
