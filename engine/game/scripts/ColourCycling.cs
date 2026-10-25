using DarkSun.Data.Images;
using DarkSun.Data.Regions;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// DSUN.EXE's colour cycling (an autoload): ranges of palette colours turn, each
/// colour taking the one before it and the last going to the front
/// (<c>22F8:047C</c>), which makes fire, the menu button's lava, the spell and
/// effect icons and enchanted items flicker. While the map runs, the 96 Hz timer
/// asks for a step every 8 ticks, 12 a second (<c>1DF3:29E8</c>); a range turns
/// every <c>speed</c> steps. Entering the game starts four ranges for good
/// (<c>1587:1F83</c>): colours 1–5 and 6–10 every 2 steps, 11–15 every step,
/// 240–248 every 2; entering a region adds its <c>RDAT</c> two
/// (<see cref="RegionInfo"/>, <c>6BB8:030E</c>, a speed of 0 never turning).
/// <para>
/// The engine draws in true colour, so the turning is done on the screen: every
/// texture draws a colour that can turn with its two low bits per channel set to
/// a code for the palette index (<see cref="Keyed"/>; a VGA palette's colours, 6-bit
/// values × 4, never have them, so the change is at most 3 / 255 and can't be
/// seen), and a shader over everything puts the colour the index has now in its
/// place, for each of the palettes it is given (the region's and the screens').
/// </para>
/// </summary>
public partial class ColourCycling : CanvasLayer
{
    /// <summary>The palette indexes that can turn: every range DSUN.EXE starts lies in them.</summary>
    private static readonly int[] Cycled = [.. Enumerable.Range(1, 15), .. Enumerable.Range(224, 25)];

    private const int StepsPerSecond = 12, MaxPalettes = 4;

    /// <summary>The ranges started on entering the game (<c>1587:1F83</c>): first colour, count, steps a turn.</summary>
    private static readonly (int First, int Count, int Speed)[] GameRanges = [(1, 5, 2), (6, 5, 2), (11, 5, 1), (240, 9, 2)];

    private static readonly int[] CodeOf = BuildCodes();

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Palette, Palette> KeyedPalettes = new();

    public static ColourCycling? Instance { get; private set; }

    private sealed class Range(int first, int count, int speed)
    {
        public int First { get; } = first;
        public int Count { get; } = count;
        public int Speed { get; } = speed;
        public int Wait { get; set; } = speed;
        public int Turns { get; set; }
    }

    private readonly List<Range> _ranges = [];
    private readonly List<Palette> _palettes = [];
    private ShaderMaterial _material = null!;
    private ColorRect _screen = null!;
    private double _sinceStep;

    /// <summary>Steps taken since <see cref="Start"/> (for the checks).</summary>
    public int Steps { get; private set; }

    private const string ShaderCode = """
        shader_type canvas_item;
        uniform sampler2D screen : hint_screen_texture, filter_nearest;
        uniform vec3 base_colours[160];
        uniform vec3 now_colours[160];
        uniform int palettes = 0;

        void fragment() {
            vec4 c = texture(screen, SCREEN_UV);
            ivec3 v = ivec3(round(c.rgb * 255.0));
            ivec3 low = v & ivec3(3);
            int code = low.r | (low.g << 2) | (low.b << 4);
            COLOR = c;
            if (code > 0 && code <= 40) {
                vec3 base = vec3(v - low) / 255.0;
                for (int p = 0; p < palettes; p++) {
                    int i = p * 40 + code - 1;
                    if (all(lessThan(abs(base_colours[i] - base), vec3(0.5 / 255.0)))) {
                        COLOR = vec4(now_colours[i], c.a);
                        break;
                    }
                }
            }
        }
        """;

    public override void _Ready()
    {
        Instance = this;
        Layer = 128; // over every screen
        _material = new ShaderMaterial { Shader = new Shader { Code = ShaderCode } };
        _screen = new ColorRect { Material = _material, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _screen.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(_screen);
        SetProcess(false);
    }

    private static int[] BuildCodes()
    {
        var codes = new int[256];
        for (int i = 0; i < Cycled.Length; i++)
        {
            codes[Cycled[i]] = i + 1;
        }
        return codes;
    }

    /// <summary>A VGA palette: every channel a 6-bit value × 4, so the low bits are free for the codes.</summary>
    private static bool Vga(Palette p) =>
        p.Count >= 256 && Enumerable.Range(0, p.Count).All(i => p[i] is { } c && (c.R & 3) == 0 && (c.G & 3) == 0 && (c.B & 3) == 0);

    /// <summary>
    /// <paramref name="palette"/> with each colour that can turn carrying its code in
    /// its low bits (the code's bits 0–1 in red, 2–3 in green, 4–5 in blue); a
    /// palette that isn't VGA's comes back as it is.
    /// </summary>
    public static Palette Keyed(Palette palette) => KeyedPalettes.GetValue(palette, p =>
    {
        if (!Vga(p))
        {
            return p;
        }
        var colours = Enumerable.Range(0, p.Count).Select(i => p[i]).ToArray();
        foreach (int i in Cycled)
        {
            int code = CodeOf[i];
            Rgb c = colours[i];
            colours[i] = new Rgb((byte)(c.R | code & 3), (byte)(c.G | code >> 2 & 3), (byte)(c.B | code >> 4 & 3));
        }
        return new Palette(colours);
    });

    /// <summary>
    /// Start turning, as on entering a region: the game's four ranges and
    /// <paramref name="region"/>'s two, over textures drawn in any of
    /// <paramref name="palettes"/> (the first four).
    /// </summary>
    public void Start(RegionInfo? region, IEnumerable<Palette> palettes)
    {
        _ranges.Clear();
        foreach (var (first, count, speed) in GameRanges)
        {
            _ranges.Add(new Range(first, count, speed));
        }
        if (region is not null)
        {
            foreach (var (first, count, speed) in new[] { region.Cycle1, region.Cycle2 })
            {
                if (count > 1 && speed > 0 && first + count <= 256)
                {
                    _ranges.Add(new Range(first, count, speed));
                }
            }
        }
        _palettes.Clear();
        _palettes.AddRange(palettes.Where(Vga).Take(MaxPalettes));
        (_sinceStep, Steps) = (0, 0);
        _material.SetShaderParameter("palettes", _palettes.Count);
        _material.SetShaderParameter("base_colours", Colours(_ => 0));
        Apply();
        _screen.Visible = _palettes.Count > 0;
        SetProcess(_screen.Visible);
    }

    /// <summary>Stop turning (leaving the map): everything shows as drawn.</summary>
    public void Stop()
    {
        _ranges.Clear();
        _screen.Visible = false;
        SetProcess(false);
    }

    public bool Running => _screen.Visible;

    public override void _Process(double delta)
    {
        _sinceStep += delta;
        bool turned = false;
        while (_sinceStep >= 1.0 / StepsPerSecond)
        {
            _sinceStep -= 1.0 / StepsPerSecond;
            turned |= Step();
        }
        if (turned)
        {
            Apply();
        }
    }

    /// <summary>One step of the 12 a second: each range whose wait runs out turns. Whether any did.</summary>
    public bool Step()
    {
        Steps++;
        bool turned = false;
        foreach (Range r in _ranges)
        {
            if (--r.Wait == 0)
            {
                r.Wait = r.Speed;
                r.Turns = (r.Turns + 1) % r.Count;
                turned = true;
            }
        }
        return turned;
    }

    /// <summary>The palette index whose colour <paramref name="index"/> shows now.</summary>
    public int ShownAt(int index)
    {
        foreach (Range r in _ranges)
        {
            if (index >= r.First && index < r.First + r.Count)
            {
                // Each turn the colours move up one: after t turns, position k holds what was at k − t.
                return r.First + ((index - r.First - r.Turns) % r.Count + r.Count) % r.Count;
            }
        }
        return index;
    }

    /// <summary>
    /// Is <paramref name="c"/> a turning colour as a texture draws it, its code still showing
    /// (the shader should have put the colour of the moment in its place)? A blend, a shadow
    /// say, can have the low bits too, but not a palette colour under them.
    /// </summary>
    public bool ShowsCode(Color c)
    {
        int code = (c.R8 & 3) | (c.G8 & 3) << 2 | (c.B8 & 3) << 4;
        if (code is 0 or > 40)
        {
            return false;
        }
        var b = new Rgb((byte)(c.R8 & ~3), (byte)(c.G8 & ~3), (byte)(c.B8 & ~3));
        return _palettes.Any(p => p[Cycled[code - 1]] == b);
    }

    private void Apply() => _material.SetShaderParameter("now_colours", Colours(ShownAt));

    /// <summary>For each palette and each index that can turn, the colour of <paramref name="from"/>(index) (0: the index itself).</summary>
    private Vector3[] Colours(Func<int, int> from)
    {
        var colours = new Vector3[MaxPalettes * Cycled.Length];
        for (int p = 0; p < _palettes.Count; p++)
        {
            for (int k = 0; k < Cycled.Length; k++)
            {
                int index = from(Cycled[k]);
                Rgb c = _palettes[p][index == 0 ? Cycled[k] : index];
                colours[p * Cycled.Length + k] = new Vector3(c.R / 255f, c.G / 255f, c.B / 255f);
            }
        }
        return colours;
    }
}
