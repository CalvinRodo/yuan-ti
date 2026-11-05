using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DarkSun.Game.Lighting;

/// <summary>
/// One light (none in DSUN.EXE): its colour (<c>#rrggbb</c>), how far it reaches
/// (<paramref name="Radius"/>, map pixels), how bright it is at its middle
/// (<paramref name="Strength"/>, 1 lighting a black night to the colour), how much it
/// flickers (<paramref name="Flicker"/>, 0–1 of its strength) and where it sits from
/// what gives it (<paramref name="Offset"/>, map pixels). A spell's light lasts
/// <paramref name="Seconds"/>, fading.
/// </summary>
public sealed record Light(string Color = "#ffffff", float Radius = 48, float Strength = 1, float Flicker = 0,
    float[]? Offset = null, float Seconds = 0)
{
    /// <summary>The colour, 0–1 a channel.</summary>
    [JsonIgnore]
    public Vector3 Rgb => LightRules.ParseColour(Color);

    [JsonIgnore]
    public Vector2 At => Offset is [var x, var y] ? new Vector2(x, y) : Vector2.Zero;

    /// <summary>
    /// How bright the light is <paramref name="seconds"/> into the game (0–1 of its strength):
    /// fires and torches waver, each to its own <paramref name="seed"/>.
    /// </summary>
    public float FlickerAt(double seconds, int seed)
    {
        if (Flicker <= 0)
        {
            return 1;
        }
        double s = seed * 1.618;
        double wave = 0.5 + 0.3 * Math.Sin(seconds * 7.3 + s) + 0.2 * Math.Sin(seconds * 13.7 + s * 2.3);
        return (float)(1 - Flicker * Math.Clamp(wave, 0, 1));
    }

    internal void Validate(string what)
    {
        _ = LightRules.ParseColour(Color, what);
        if (Radius <= 0 || Strength < 0 || Flicker is < 0 or > 1 || Seconds < 0 || Offset is { Length: not 2 })
        {
            throw new InvalidDataException($"{what}: needs radius > 0, strength ≥ 0, flicker 0–1, seconds ≥ 0, offset [x, y]");
        }
    }
}

/// <summary>A point of the day's light outdoors: from <paramref name="Hour"/> (0–24), the sky's colour and its strength.</summary>
public sealed record DayKey(float Hour, string Color, float Strength = 1);

/// <summary>
/// Pixels drawn in palette colours <paramref name="From"/>–<paramref name="To"/> give light
/// (lava, fire): the map's ground, objects and walls are looked at when a region is entered
/// (<see cref="GlowScanner"/>). With <paramref name="HotOnly"/>, only those colours of the
/// region's palette that are hot (red to yellow, bright) do: a region's own cycling colours
/// are water in one region and lava in the next. Groups of at least <paramref name="MinPixels"/>.
/// </summary>
public sealed record PaletteGlow(int From, int To, Light Light, bool HotOnly = false, int MinPixels = 12);

/// <summary>A light put by hand (<c>Regions</c>): at map pixel (<paramref name="X"/>, <paramref name="Y"/>).</summary>
public sealed record PlacedLight(int X, int Y, Light Light);

/// <summary>
/// The lighting's data (none in DSUN.EXE; <c>[display] lighting</c>): the engine's
/// <c>data/lights.json</c>, with each mod's <c>rules/lights.json</c> added over it
/// (<see cref="Over"/>). <see cref="Day"/> is the sky outdoors over the day,
/// <see cref="Indoors"/> the fixed light inside, <see cref="LeastOnFigures"/> the least light
/// a figure stands in, so night never hides who is where. Lights come from the palette
/// (<see cref="Palette"/>), from objects (<see cref="Objects"/>, by <c>OJFF</c> number), from
/// what a figure carries (<see cref="Items"/>, by <c>RDFF</c>) or has (<see cref="Conditions"/>,
/// by name), and by hand (<see cref="Regions"/>, by region number). Looks only: no rule sees them.
/// </summary>
public sealed record LightRules
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
    };

    public IReadOnlyList<DayKey> Day { get; init; } = [];

    public DayKey? Indoors { get; init; }

    /// <summary>Given in the file, or null: <see cref="LeastOnFigures"/>.</summary>
    public float? FigureMinimum { get; init; }

    /// <summary>The least light a figure stands in, 0–1.</summary>
    [JsonIgnore]
    public float LeastOnFigures => FigureMinimum ?? 0.5f;

    public IReadOnlyList<PaletteGlow> Palette { get; init; } = [];

    public IReadOnlyDictionary<string, Light> Objects { get; init; } = new Dictionary<string, Light>();

    public IReadOnlyDictionary<string, Light> Items { get; init; } = new Dictionary<string, Light>();

    public IReadOnlyDictionary<string, Light> Conditions { get; init; } = new Dictionary<string, Light>();

    public IReadOnlyDictionary<string, IReadOnlyList<PlacedLight>> Regions { get; init; } = new Dictionary<string, IReadOnlyList<PlacedLight>>();

    public static LightRules Default { get; } = new();

    public const int SecondsADay = 24 * 3600;

    /// <summary>The hour the game clock's 0 is (the clock counts seconds from the start of a new game), or null: midnight.</summary>
    public float? HourAtZero { get; init; }

    /// <summary>The hour (0–24) at game clock <paramref name="clock"/> (seconds).</summary>
    public double HourOf(int clock) => ((clock % SecondsADay / 3600.0 + (HourAtZero ?? 0)) % 24 + 24) % 24;

    /// <summary>The light over the map: indoors the fixed one, outdoors the sky's at <paramref name="hour"/>, each channel 0–2.</summary>
    public Vector3 Ambient(double hour, bool indoors)
    {
        if (indoors)
        {
            return Indoors is { } i ? ParseColour(i.Color) * i.Strength : Vector3.One;
        }
        if (Day.Count == 0)
        {
            return Vector3.One;
        }
        var keys = Day.OrderBy(k => k.Hour).ToList();
        hour = (hour % 24 + 24) % 24;
        int next = keys.FindIndex(k => k.Hour > hour);
        DayKey after = keys[next < 0 ? 0 : next], before = keys[next <= 0 ? keys.Count - 1 : next - 1];
        double span = (after.Hour - before.Hour + 24) % 24, into = (hour - before.Hour + 24) % 24;
        float t = span == 0 ? 0 : (float)(into / span);
        return Vector3.Lerp(ParseColour(before.Color) * before.Strength, ParseColour(after.Color) * after.Strength, t);
    }

    /// <summary>
    /// The sun at <paramref name="hour"/>, for the figures' shadows: where a figure's top lands per
    /// pixel of its height (the shadow falls west in the morning, short at noon, east in the
    /// evening) and how dark the shadow is (0 at night, 1 by day).
    /// </summary>
    public static (Vector2 Lean, float Strength) Sun(double hour)
    {
        const double rise = 6, set = 19;
        double t = (hour - rise) / (set - rise);
        if (t is <= 0 or >= 1)
        {
            return (new Vector2(0.75f, 0.4f), 0);
        }
        double height = Math.Sin(Math.PI * t), length = 0.35 + 0.9 * (1 - height);
        var lean = new Vector2((float)(-Math.Cos(Math.PI * t) * length), (float)(0.25 + 0.2 * (1 - height)));
        return (lean, (float)Math.Clamp(height * 3, 0, 1));
    }

    /// <summary>Is <paramref name="c"/> hot: red to yellow and bright, as lava and fire are drawn?</summary>
    public static bool Hot(byte r, byte g, byte b) => r >= 0x70 && g <= r * 0.85 && b <= r * 0.35;

    /// <summary><paramref name="over"/> (a mod's) added over these: its sky and minimum if it gives them, its entries over these, its palette first.</summary>
    public LightRules Over(LightRules over) => this with
    {
        Day = over.Day.Count > 0 ? over.Day : Day,
        Indoors = over.Indoors ?? Indoors,
        FigureMinimum = over.FigureMinimum ?? FigureMinimum,
        HourAtZero = over.HourAtZero ?? HourAtZero,
        Palette = [.. over.Palette, .. Palette],
        Objects = Merge(Objects, over.Objects),
        Items = Merge(Items, over.Items),
        Conditions = Merge(Conditions, over.Conditions),
        Regions = Merge(Regions, over.Regions),
    };

    private static Dictionary<string, T> Merge<T>(IReadOnlyDictionary<string, T> under, IReadOnlyDictionary<string, T> over)
    {
        var all = under.ToDictionary(p => p.Key, p => p.Value);
        foreach (var (key, value) in over)
        {
            all[key] = value;
        }
        return all;
    }

    public static LightRules Parse(string text)
    {
        LightRules rules;
        try
        {
            rules = JsonSerializer.Deserialize<LightRules>(text, Json) ?? throw new InvalidDataException("empty file");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException(e.Message, e);
        }
        foreach (DayKey k in rules.Day)
        {
            _ = ParseColour(k.Color, $"day {k.Hour}");
            if (k.Hour is < 0 or > 24 || k.Strength < 0)
            {
                throw new InvalidDataException($"day {k.Hour}: needs an hour 0–24 and strength ≥ 0");
            }
        }
        if (rules.Indoors is { } inside)
        {
            _ = ParseColour(inside.Color, "indoors");
        }
        if (rules.HourAtZero is < 0 or >= 24)
        {
            throw new InvalidDataException("hourAtZero: 0–24");
        }
        if (rules.FigureMinimum is < 0 or > 1)
        {
            throw new InvalidDataException("figureMinimum: 0–1");
        }
        foreach (PaletteGlow p in rules.Palette)
        {
            if (p.From is < 0 or > 255 || p.To < p.From || p.To > 255 || p.MinPixels < 1 || p.Light is null)
            {
                throw new InvalidDataException($"palette {p.From}–{p.To}: needs 0 ≤ from ≤ to ≤ 255, minPixels ≥ 1 and a light");
            }
            p.Light.Validate($"palette {p.From}–{p.To}");
        }
        foreach (var (what, lights) in new[] { ("object", rules.Objects), ("item", rules.Items), ("condition", rules.Conditions) })
        {
            foreach (var (key, light) in lights)
            {
                light.Validate($"{what} {key}");
            }
        }
        foreach (var (region, lights) in rules.Regions)
        {
            foreach (PlacedLight l in lights)
            {
                l.Light.Validate($"region {region}");
            }
        }
        return rules;
    }

    public static LightRules Read(string path) => Parse(File.ReadAllText(path));

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary><c>#rrggbb</c> as 0–1 channels.</summary>
    public static Vector3 ParseColour(string text, string what = "colour")
    {
        if (text is not ['#', .. var hex] || hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
        {
            throw new InvalidDataException($"{what}: \"{text}\" isn't #rrggbb");
        }
        return new Vector3(v >> 16 & 0xFF, v >> 8 & 0xFF, v & 0xFF) / 255f;
    }
}
