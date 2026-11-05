using DarkSun.Data.Images;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// The map's light (<c>[display] lighting</c>; none in DSUN.EXE): a layer over the map and under
/// what is drawn unlit (the damage splashes, the spells' particles, the overhead map's marks) and
/// the windows. Lights are drawn, added up, into a buffer with a texel for each map pixel over the
/// sky's light (<see cref="Frame"/>); a shader over the map's part of the screen first turns the
/// colour-cycling colours, as <see cref="ColourCycling"/> does (its codes would be lost once
/// lit), then multiplies each map pixel, all of it by one texel so the pixels stay square at
/// any scale; lava and fire (colours that turn and are hot) light themselves, showing as drawn. <c>smooth</c> keeps the result in VGA's 6-bit steps; <c>pixel</c> lights in
/// bands and puts each colour back to the nearest of the region's palette
/// (<see cref="SetPalette"/>), so the map stays in its own colours. The colours it writes have
/// no code bits, so the cycling over everything leaves them be.
/// </summary>
public partial class LightingLayer : CanvasLayer
{
    /// <summary>The most lights drawn in a frame, nearest the middle of the view first.</summary>
    public const int MaxLights = 160;

    /// <summary>The steps of light <c>pixel</c> lights in (from black to twice the colour).</summary>
    private const int Bands = 6;

    /// <summary>The palette lookup's steps per channel (32³ colours in a 1024 × 32 texture).</summary>
    private const int LutSteps = 32;

    private const string ShaderCode = """
        shader_type canvas_item;
        render_mode unshaded;
        uniform sampler2D screen : hint_screen_texture, filter_nearest;
        uniform sampler2D light_map : filter_nearest;
        uniform sampler2D lut : filter_nearest;
        uniform vec3 base_colours[160];
        uniform vec3 now_colours[160];
        uniform int palettes = 0;
        uniform vec2 light_size = vec2(1.0);
        uniform bool banded = false;

        bool hot(vec3 c) { return c.r >= 112.0 / 255.0 && c.g <= c.r * 0.85 && c.b <= c.r * 0.35; }

        // The colour of the moment of a colour that turns; glowing if it is hot (lava, fire), giving its own light.
        vec3 cycled(vec4 c, out bool glowing) {
            glowing = false;
            ivec3 v = ivec3(round(c.rgb * 255.0));
            ivec3 low = v & ivec3(3);
            int code = low.r | (low.g << 2) | (low.b << 4);
            if (code > 0 && code <= 40) {
                vec3 base = vec3(v - low) / 255.0;
                for (int p = 0; p < palettes; p++) {
                    int i = p * 40 + code - 1;
                    if (all(lessThan(abs(base_colours[i] - base), vec3(0.5 / 255.0)))) {
                        glowing = hot(now_colours[i]);
                        return now_colours[i];
                    }
                }
            }
            return c.rgb;
        }

        void fragment() {
            bool glowing;
            vec3 colour = cycled(texture(screen, SCREEN_UV), glowing);
            // One texel for the whole map pixel: its middle.
            vec2 cell = floor(UV * light_size) + 0.5;
            // The buffer holds the light × 128/255 in 8 bits (so 1, the map as drawn, is exactly 128; up to 2).
            vec3 light = round(texture(light_map, cell / light_size).rgb * 255.0) / 128.0;
            if (glowing) {
                light = vec3(1.0); // lava and fire show as drawn, by day or night
            }
            if (banded) {
                light = floor(light * float(BANDS) + 0.5) / float(BANDS);
            }
            if (all(equal(light, vec3(1.0)))) {
                COLOR = vec4(colour, 1.0); // as drawn (a turning colour as it is now)
            } else if (banded) {
                ivec3 q = ivec3(clamp(colour * light, 0.0, 1.0) * float(STEPS - 1) + 0.5);
                vec2 at = vec2(float(q.b * STEPS + q.r), float(q.g)) + 0.5;
                COLOR = vec4(texture(lut, at / vec2(float(STEPS * STEPS), float(STEPS))).rgb, 1.0);
            } else {
                // VGA's colours: a multiple of 4 a channel (0–252), with no code bits.
                COLOR = vec4(min(round(clamp(colour * light, 0.0, 1.0) * 255.0 / 4.0), vec3(63.0)) * 4.0 / 255.0, 1.0);
            }
        }
        """;

    private ShaderMaterial _material = null!;
    private ColorRect _screen = null!;
    private SubViewport _buffer = null!;
    private ColorRect _sky = null!;
    private Node2D _lights = null!;
    private readonly List<Sprite2D> _pool = [];
    private Texture2D _falloff = null!;

    /// <summary>A light to draw this frame: where (map pixels), its colour times its strength, how far it reaches.</summary>
    public readonly record struct Lit(Vector2 At, Vector3 Colour, float Radius);

    /// <summary>Lights drawn in the last frame (for the checks).</summary>
    public int Drawn { get; private set; }

    public override void _Ready()
    {
        Layer = 1; // over the map (the root canvas), under the unlit layer and the windows
        FollowViewportEnabled = true;
        _material = new ShaderMaterial
        {
            Shader = new Shader { Code = ShaderCode.Replace("BANDS", Bands.ToString()).Replace("STEPS", LutSteps.ToString()) },
        };
        _screen = new ColorRect { Material = _material, MouseFilter = Control.MouseFilterEnum.Ignore };
        AddChild(_screen);
        _buffer = new SubViewport
        {
            Disable3D = true,
            TransparentBg = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.Linear,
            Size = Vector2I.One,
        };
        AddChild(_buffer);
        _sky = new ColorRect { Size = new Vector2(1, 1) };
        _buffer.AddChild(_sky);
        _lights = new Node2D();
        _buffer.AddChild(_lights);
        _falloff = Falloff();
        _material.SetShaderParameter("light_map", _buffer.GetTexture());
        ColourCycling.Instance?.Share(_material);
        Visible = false;
    }

    public override void _ExitTree() => ColourCycling.Instance?.Unshare(_material);

    /// <summary>A light's shape: white, fading out to its edge, smoothly.</summary>
    private static ImageTexture Falloff()
    {
        const int size = 64;
        var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f) / size * 2 - 1, dy = (y + 0.5f) / size * 2 - 1;
                float r2 = Math.Min(1, dx * dx + dy * dy), a = (1 - r2) * (1 - r2);
                image.SetPixel(x, y, new Color(1, 1, 1, a));
            }
        }
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>
    /// The region's palette, for <c>pixel</c>: for each of 32³ colours the nearest of its
    /// colours (each channel 0–255; the palette's own, which carry no code bits).
    /// </summary>
    public void SetPalette(Palette palette)
    {
        var colours = Enumerable.Range(0, palette.Count).Select(i => palette[i]).Distinct().ToArray();
        var data = new byte[LutSteps * LutSteps * LutSteps * 3];
        for (int g = 0; g < LutSteps; g++)
        {
            for (int b = 0; b < LutSteps; b++)
            {
                for (int r = 0; r < LutSteps; r++)
                {
                    int cr = r * 255 / (LutSteps - 1), cg = g * 255 / (LutSteps - 1), cb = b * 255 / (LutSteps - 1);
                    Rgb best = colours[0];
                    int bestDistance = int.MaxValue;
                    foreach (Rgb c in colours)
                    {
                        // Weighted as the eye sees: green most, blue least.
                        int dr = c.R - cr, dg = c.G - cg, db = c.B - cb, d = 3 * dr * dr + 4 * dg * dg + 2 * db * db;
                        if (d < bestDistance)
                        {
                            (best, bestDistance) = (c, d);
                        }
                    }
                    int at = ((g * LutSteps * LutSteps) + b * LutSteps + r) * 3;
                    (data[at], data[at + 1], data[at + 2]) = (best.R, best.G, best.B);
                }
            }
        }
        _material.SetShaderParameter("lut", ImageTexture.CreateFromImage(Image.CreateFromData(LutSteps * LutSteps, LutSteps, false, Image.Format.Rgb8, data)));
    }

    /// <summary>
    /// Light the map's view this frame: the map pixels from <paramref name="topLeft"/> (whole pixels)
    /// <paramref name="size"/> across, under <paramref name="sky"/> (each channel 0–2) and
    /// <paramref name="lights"/>, banded or smooth.
    /// </summary>
    public void Frame(Vector2I topLeft, Vector2I size, Vector3 sky, IReadOnlyList<Lit> lights, bool banded)
    {
        Visible = true;
        size = new Vector2I(Math.Max(1, size.X), Math.Max(1, size.Y));
        if (_buffer.Size != size)
        {
            _buffer.Size = size;
        }
        (_screen.Position, _screen.Size) = (topLeft, size);
        _buffer.CanvasTransform = new Transform2D(0, -(Vector2)topLeft);
        (_sky.Position, _sky.Size, _sky.Color) = (topLeft, size, Encoded(sky));
        _material.SetShaderParameter("light_size", (Vector2)size);
        _material.SetShaderParameter("banded", banded);
        Vector2 middle = topLeft + size / 2;
        var shown = lights.Where(l => Math.Abs(l.At.X - middle.X) < size.X / 2 + l.Radius && Math.Abs(l.At.Y - middle.Y) < size.Y / 2 + l.Radius)
            .OrderBy(l => l.At.DistanceSquaredTo(middle)).Take(MaxLights).ToList();
        while (_pool.Count < shown.Count)
        {
            var sprite = new Sprite2D { Texture = _falloff, Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add } };
            _lights.AddChild(sprite);
            _pool.Add(sprite);
        }
        for (int i = 0; i < _pool.Count; i++)
        {
            Sprite2D sprite = _pool[i];
            sprite.Visible = i < shown.Count;
            if (sprite.Visible)
            {
                Lit l = shown[i];
                sprite.Position = l.At;
                sprite.Scale = Vector2.One * (l.Radius * 2 / _falloff.GetWidth());
                sprite.Modulate = Encoded(l.Colour);
            }
        }
        Drawn = shown.Count;
    }

    /// <summary>A light as the buffer holds it: × 128/255, so 1 is exactly 128 of 255.</summary>
    private static Color Encoded(Vector3 light) => new(light.X * 128 / 255, light.Y * 128 / 255, light.Z * 128 / 255);

    /// <summary>No light: the map as drawn (the colour cycling does its own turning).</summary>
    public void Off()
    {
        Visible = false;
        Drawn = 0;
    }
}
