using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DarkSun.Game.Animation;

/// <summary>Where a particle starts (<see cref="ParticleDefinition.Shape"/>).</summary>
public enum ParticleShape
{
    /// <summary>At the effect's point (a flight's: each head as it flies).</summary>
    Point,

    /// <summary>Anywhere within <see cref="ParticleDefinition.Radius"/> of the point, flattened as the floor is seen.</summary>
    Circle,

    /// <summary>At the start, flying within <see cref="ParticleDefinition.Spread"/> of the way to the point.</summary>
    Cone,

    /// <summary>Anywhere along the line from the start to the point, or across the area's cells from the nearest to the start to the farthest.</summary>
    Line,

    /// <summary>Anywhere in one of the area's cells (a burst's or a field's).</summary>
    Cell,
}

/// <summary>What a particle is drawn as.</summary>
public enum ParticleTexture
{
    Pixel,
    Glow,
    Spark,
}

public enum ParticleBlend
{
    Mix,
    Add,
}

/// <summary>The four places a spell is drawn (<c>RegionView.CombatLooks</c>): its cast, flight, burst, hit, and a field it leaves.</summary>
public enum LookPart
{
    Cast,
    Flight,
    Burst,
    Hit,
    Field,
}

/// <summary>
/// The particle looks (<c>[display] spell_effects</c>): <see cref="Pixel"/> at the game's
/// own pixel scale and palette, <see cref="Modern"/> at full resolution with glow.
/// </summary>
public enum ParticleStyle
{
    Pixel,
    Modern,
}

/// <summary>Light a modern effect throws on the ground around it.</summary>
public sealed record ParticleGlow(float Radius = 24, string Color = "#ffffff", float Alpha = 0.5f);

/// <summary>
/// One effect's particles: a mod's <c>looks/particles/NAME.json</c>. Lists over a
/// particle's life (<see cref="Size"/>, <see cref="Alpha"/>, <see cref="Colors"/>) are
/// keys spread evenly from its birth to its death; <see cref="Palette"/> steps through
/// palette indexes instead. Pairs (<see cref="Lifetime"/>, <see cref="Speed"/>) are a
/// range rolled per particle; angles are in degrees, 0 to the right and 90 down;
/// distances are map pixels.
/// </summary>
public sealed record ParticleDefinition
{
    public ParticleShape Shape { get; init; } = ParticleShape.Point;

    /// <summary><see cref="ParticleShape.Circle"/>'s size.</summary>
    public float Radius { get; init; }

    /// <summary>Particles a second (a field's: a second and a cell; a flight's: a second and a head).</summary>
    public float Rate { get; init; }

    /// <summary>Particles at once as it starts.</summary>
    public int Burst { get; init; }

    public float[] Lifetime { get; init; } = [0.5f, 0.5f];

    public float[] Speed { get; init; } = [0, 0];

    public float Direction { get; init; } = -90;

    /// <summary>Degrees either side of <see cref="Direction"/>.</summary>
    public float Spread { get; init; } = 180;

    /// <summary><see cref="Direction"/> is from the way to the point, not the screen's (a cone's always is).</summary>
    public bool Aim { get; init; }

    /// <summary>Pixels a second added to the speed each second (x, y).</summary>
    public float[] Gravity { get; init; } = [0, 0];

    /// <summary>The share of its speed a particle loses each second.</summary>
    public float Drag { get; init; }

    public float[] Size { get; init; } = [1];

    public float[] Alpha { get; init; } = [1];

    /// <summary>Palette indexes over its life (the pixel style: the region's palette, so colour cycling still turns them).</summary>
    public int[] Palette { get; init; } = [];

    /// <summary>Colours over its life, <c>#rrggbb</c> or <c>#rrggbbaa</c> (the modern style, or the pixel style without a palette).</summary>
    public string[] Colors { get; init; } = [];

    public ParticleTexture Texture { get; init; } = ParticleTexture.Pixel;

    public ParticleBlend Blend { get; init; } = ParticleBlend.Mix;

    /// <summary>Seconds it gives off particles; a flight does while it flies, a field while it lasts.</summary>
    public float Seconds { get; init; }

    /// <summary>The playback waits for it to end (a flight always waits for its flight).</summary>
    public bool Blocks { get; init; }

    /// <summary>The most particles alive at once.</summary>
    public int Max { get; init; } = 300;

    /// <summary>A flight: how many fly at once, each its own way.</summary>
    public int Heads { get; init; } = 1;

    /// <summary>A flight: how far off the straight way a head may swing, in pixels (0: straight).</summary>
    public float Wander { get; init; }

    /// <summary>A flight: the share of its time by which the heads arrive one after another.</summary>
    public float Stagger { get; init; }

    /// <summary>A flight: it takes at least this long (the playback waits longer than the original's projectile).</summary>
    public float MinSeconds { get; init; }

    /// <summary>A flight: each head drawn as a particle of this size.</summary>
    public float HeadSize { get; init; }

    /// <summary>A flight: particles at once where each head arrives.</summary>
    public int ArriveBurst { get; init; }

    /// <summary>The modern style's light on the ground.</summary>
    public ParticleGlow? Glow { get; init; }

    private (float R, float G, float B, float A)[]? _rgba;

    /// <summary><see cref="Colors"/>, read.</summary>
    [JsonIgnore]
    public IReadOnlyList<(float R, float G, float B, float A)> Rgba => _rgba ??= [.. Colors.Select(ParseColour)];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static ParticleDefinition Parse(string json)
    {
        ParticleDefinition d;
        try
        {
            d = JsonSerializer.Deserialize<ParticleDefinition>(json, Json) ?? throw new InvalidDataException("empty file");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException(e.Message, e);
        }
        if (d.Lifetime is not [> 0, var longest] || longest < d.Lifetime[0] || d.Speed is not [_, _] || d.Gravity is not [_, _])
        {
            throw new InvalidDataException("lifetime needs [shortest > 0, longest], speed and gravity two numbers each");
        }
        if (d.Size.Length == 0 || d.Alpha.Length == 0 || d.Palette.Length == 0 && d.Colors.Length == 0)
        {
            throw new InvalidDataException("size, alpha, and palette or colors need a value each");
        }
        if (d.Rate < 0 || d.Burst < 0 || d.Max < 1 || d.Heads < 1 || d.Seconds < 0 || d.Stagger is < 0 or >= 1)
        {
            throw new InvalidDataException("rate, burst and seconds can't be negative, max and heads must be 1 or more, stagger 0 to under 1");
        }
        if (d.Palette.Any(i => i is < 0 or > 255))
        {
            throw new InvalidDataException("palette indexes are 0–255");
        }
        _ = d.Rgba;
        if (d.Glow is { } glow)
        {
            ParseColour(glow.Color);
        }
        return d;
    }

    public static (float R, float G, float B, float A) ParseColour(string colour)
    {
        string hex = colour.TrimStart('#');
        if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v))
        {
            throw new InvalidDataException($"colour \"{colour}\": #rrggbb or #rrggbbaa");
        }
        if (hex.Length == 6)
        {
            v = v << 8 | 0xFF;
        }
        return ((v >> 24) / 255f, (v >> 16 & 0xFF) / 255f, (v >> 8 & 0xFF) / 255f, (v & 0xFF) / 255f);
    }
}

/// <summary>
/// Which particles each spell's parts use, by style: the maps (the mods'
/// <c>rules/particle_looks.json</c>, then the engine's) and the definitions they name
/// (<c>looks/particles/NAME.json</c>), the first given winning. A map is
/// <c>{"9": {"Flight": {"Pixel": "magic-missile-pixel", "Modern": "magic-missile"}}}</c>;
/// a spell, part or style it doesn't give keeps the original's <c>OJFF</c>.
/// </summary>
public sealed class ParticleLooks
{
    private readonly Dictionary<(int Spell, LookPart Part, ParticleStyle Style), string> _looks = [];

    public static ParticleLooks None { get; } = new([], new Dictionary<string, ParticleDefinition>());

    /// <param name="maps">The maps' JSON, the first's entries winning.</param>
    /// <param name="definitions">The definitions by name.</param>
    public ParticleLooks(IEnumerable<string> maps, IReadOnlyDictionary<string, ParticleDefinition> definitions)
    {
        Definitions = definitions;
        foreach (string map in maps)
        {
            foreach (var (key, name) in ParseMap(map))
            {
                _looks.TryAdd(key, name);
            }
        }
    }

    public IReadOnlyDictionary<string, ParticleDefinition> Definitions { get; }

    /// <summary>Every spell, part and style given, and the definition it names.</summary>
    public IReadOnlyDictionary<(int Spell, LookPart Part, ParticleStyle Style), string> Looks => _looks;

    /// <summary>The particles of spell <paramref name="spell"/>'s <paramref name="part"/> in <paramref name="style"/>, or null (the original's <c>OJFF</c>).</summary>
    public ParticleDefinition? For(int spell, LookPart part, ParticleStyle style) =>
        _looks.TryGetValue((spell, part, style), out string? name) ? Definitions.GetValueOrDefault(name) : null;

    /// <summary>Looks naming a definition that isn't there (they keep the original's).</summary>
    public IEnumerable<string> Missing =>
        _looks.Where(l => !Definitions.ContainsKey(l.Value)).Select(l => $"spell {l.Key.Spell} {l.Key.Part} {l.Key.Style}: no particles \"{l.Value}\"");

    private static readonly JsonSerializerOptions Json = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>A map's entries; keys starting with <c>_</c> are notes.</summary>
    public static IEnumerable<((int, LookPart, ParticleStyle), string)> ParseMap(string json)
    {
        Dictionary<string, JsonElement> spells;
        try
        {
            spells = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, Json) ?? throw new InvalidDataException("empty file");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException(e.Message, e);
        }
        var entries = new List<((int, LookPart, ParticleStyle), string)>();
        foreach (var (spellKey, parts) in spells.Where(s => !s.Key.StartsWith('_')))
        {
            if (!int.TryParse(spellKey, CultureInfo.InvariantCulture, out int spell) || parts.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"\"{spellKey}\": a spell number, holding its parts");
            }
            foreach (JsonProperty part in parts.EnumerateObject())
            {
                if (!Enum.TryParse(part.Name, true, out LookPart p) || part.Value.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException($"spell {spell}: \"{part.Name}\" isn't Cast, Flight, Burst, Hit or Field");
                }
                foreach (JsonProperty style in part.Value.EnumerateObject())
                {
                    if (!Enum.TryParse(style.Name, true, out ParticleStyle s) || style.Value.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidDataException($"spell {spell} {p}: \"{style.Name}\" isn't Pixel or Modern, naming particles");
                    }
                    entries.Add(((spell, p, s), style.Value.GetString()!));
                }
            }
        }
        return entries;
    }
}
