using System.Text.Json;
using DarkSun.Game.Animation;

namespace DarkSun.Game.Lighting;

/// <summary>
/// The light a spell throws (<c>[display] lighting</c>, with spell flashes on), at the same
/// points as its particles (<see cref="LookPart"/>): a flash at the caster (<c>Cast</c>), a light
/// flying with the projectile (<c>Flight</c>), a flash where it bursts or hits (<c>Burst</c>,
/// <c>Hit</c>), and a glow over a field while it lasts (<c>Field</c>). The engine's
/// <c>looks/spell_lights.json</c> (written with the particles by <c>tools/particle_looks.py</c>),
/// with each mod's <c>rules/spell_lights.json</c> over it, part by part:
/// <c>{"10": {"Burst": {"Color": "#ff8030", "Radius": 96, "Seconds": 0.8}}}</c>.
/// </summary>
public sealed class SpellLights
{
    private readonly Dictionary<(int Spell, LookPart Part), Light> _lights;

    public SpellLights(IEnumerable<((int, LookPart), Light)> lights)
    {
        _lights = [];
        foreach (var (key, light) in lights)
        {
            _lights.TryAdd(key, light);
        }
    }

    public static SpellLights None { get; } = new([]);

    public int Count => _lights.Count;

    public Light? For(int spell, LookPart part) => _lights.GetValueOrDefault((spell, part));

    private static readonly JsonSerializerOptions Json = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>A file's entries (keys starting with <c>_</c> are notes); the first given of a spell's part wins when they are put together.</summary>
    public static IEnumerable<((int, LookPart), Light)> Parse(string json)
    {
        Dictionary<string, Dictionary<string, Light>> spells;
        try
        {
            spells = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, Json)?
                .Where(s => !s.Key.StartsWith('_'))
                .ToDictionary(s => s.Key, s => s.Value.Deserialize<Dictionary<string, Light>>(Json) ?? [])
                ?? throw new InvalidDataException("empty file");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException(e.Message, e);
        }
        var entries = new List<((int, LookPart), Light)>();
        foreach (var (spellKey, parts) in spells)
        {
            if (!int.TryParse(spellKey, out int spell) || spell < 1)
            {
                throw new InvalidDataException($"\"{spellKey}\" isn't a spell number");
            }
            foreach (var (partName, light) in parts)
            {
                if (!Enum.TryParse(partName, out LookPart part) || light is null)
                {
                    throw new InvalidDataException($"spell {spell}: \"{partName}\" isn't Cast, Flight, Burst, Hit or Field");
                }
                light.Validate($"spell {spell} {part}");
                entries.Add(((spell, part), light));
            }
        }
        return entries;
    }
}
