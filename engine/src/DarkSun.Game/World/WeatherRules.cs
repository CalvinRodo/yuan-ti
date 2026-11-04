using System.Text.Json;
using System.Text.Json.Serialization;

namespace DarkSun.Game.World;

/// <summary>
/// Sandstorms over the open desert (none in DSUN.EXE): in regions <paramref name="Regions"/>
/// (region numbers), one blows in each <paramref name="EveryHours"/> of the game clock, at a
/// time the region and the hour pick, for <paramref name="ShortestMinutes"/>–<paramref name="LongestMinutes"/>
/// minutes (<see cref="BlowsAt"/>). Worked out from the clock alone, nothing is saved: a loaded
/// game has the same weather. While one blows a fight's sight is <paramref name="Sight"/> cells,
/// missiles are <paramref name="MissileToHit"/> to hit, clouds and fog last at most
/// <paramref name="CloudRounds"/> round, and the map plays sound <paramref name="Sound"/> softly
/// over and over (<paramref name="Volume"/>, 0–1, of the sound effects' loudness).
/// </summary>
public sealed record Sandstorm(
    IReadOnlyList<int> Regions, int EveryHours = 6, int ShortestMinutes = 10, int LongestMinutes = 30,
    int Sight = 4, int MissileToHit = -4, int CloudRounds = 1, int Sound = 97, float Volume = 0.35f)
{
    private const int Hour = 3600, Minute = 60;

    /// <summary>The storm's time in period <paramref name="period"/> of region <paramref name="region"/>: its start and length, in seconds.</summary>
    public (int Start, int Length) StormIn(int region, int period)
    {
        int length = Minute * (ShortestMinutes + (int)(Mix(region, period, 1) % (uint)(Math.Max(0, LongestMinutes - ShortestMinutes) + 1)));
        int room = Math.Max(1, EveryHours * Hour - length);
        return (period * EveryHours * Hour + (int)(Mix(region, period, 2) % (uint)room), length);
    }

    /// <summary>Is a storm blowing in <paramref name="region"/> at game clock <paramref name="clock"/> (seconds)?</summary>
    public bool BlowsAt(int region, int clock)
    {
        if (!Regions.Contains(region) || EveryHours < 1 || clock < 0)
        {
            return false;
        }
        var (start, length) = StormIn(region, clock / (EveryHours * Hour));
        return clock >= start && clock < start + length;
    }

    /// <summary>A fixed scramble of three numbers (not <c>GetHashCode</c>, which changes between runs).</summary>
    private static uint Mix(int a, int b, int c)
    {
        uint h = (uint)a * 0x9E3779B1u ^ (uint)b * 0x85EBCA77u ^ (uint)c * 0xC2B2AE3Du;
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        h ^= h >> 12;
        h *= 0x297A2D39u;
        return h ^ h >> 15;
    }
}

/// <summary>
/// The weather (none in DSUN.EXE): a mod's <c>rules/weather.json</c>. With no
/// <see cref="Sandstorm"/>, the skies are the original's.
/// </summary>
public sealed record WeatherRules
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public Sandstorm? Sandstorm { get; init; }

    public static WeatherRules Default { get; } = new();

    /// <summary>The sandstorm blowing in <paramref name="region"/> at <paramref name="clock"/>, or null.</summary>
    public Sandstorm? StormAt(int region, int clock) => Sandstorm is { } s && s.BlowsAt(region, clock) ? s : null;

    public static WeatherRules Read(string path)
    {
        WeatherRules rules;
        try
        {
            rules = JsonSerializer.Deserialize<WeatherRules>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("empty file");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException(e.Message, e);
        }
        if (rules.Sandstorm is { } s && (s.Regions is null || s.EveryHours < 1 || s.ShortestMinutes < 1 || s.LongestMinutes < s.ShortestMinutes
            || s.LongestMinutes * 60 > s.EveryHours * 3600 || s.Sight < 1 || s.CloudRounds < 0))
        {
            throw new InvalidDataException("sandstorm: needs regions, everyHours ≥ 1, 1 ≤ shortestMinutes ≤ longestMinutes ≤ the hours, sight ≥ 1");
        }
        return rules;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}
