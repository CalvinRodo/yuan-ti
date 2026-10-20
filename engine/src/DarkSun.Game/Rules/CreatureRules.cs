using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace DarkSun.Game.Rules;

/// <summary>A special ability the AI tries (<c>6466:05EF</c>): ability bit <paramref name="Bit"/> casts spell record <paramref name="Spell"/>.</summary>
/// <param name="Bit">A bit of a type's mask (<see cref="CreatureRules.AbilityTypes"/>).</param>
/// <param name="Spell">The spell number cast (a spell record, numbered like <c>SPIN</c>: index + 1;
/// 176 and up are monster-only records with no <c>SPIN</c> text).</param>
public sealed record CreatureAbility(int Bit, int Spell);

/// <summary>
/// What creatures can do in combat beyond their records: tables that are in
/// DSUN.EXE, not in the data files (<c>dsun-exe-reference.md</c> §8, "The AI").
/// <see cref="Default"/> is the original's; a mod can replace them with
/// <c>rules/creatures.json</c> (<see cref="Read"/>). Spells and powers are
/// numbered as <c>SPIN</c> (spell record index + 1; powers 139–172).
/// </summary>
public sealed partial record CreatureRules
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>
    /// The special abilities, by combat record +0x16 (<c>3E45:0000</c>, 32-bit
    /// masks; <c>84C1:0000</c>): the bits each type has. The AI uses those in
    /// <see cref="Abilities"/>; the others are tested by the damage, save and
    /// resistance code (<c>80F5</c>, <c>8053:0163</c>, <c>8B7D</c>; not traced).
    /// A type past the end has none.
    /// </summary>
    public required IReadOnlyList<IReadOnlyList<int>> AbilityTypes { get; init; }

    /// <summary>The abilities the AI tries, first that the creature has (<c>DS:05D0</c>, <c>DS:05DB</c>).</summary>
    public required IReadOnlyList<CreatureAbility> Abilities { get; init; }

    /// <summary>The AI tries an ability one turn in this many (<c>6466:05EF</c>: rand × 4 / 32768 = 0).</summary>
    public int AbilityChance { get; init; } = 4;

    /// <summary>A creature's spells, by character record +0x3E (<c>3FB9:0110</c>, 7 slots; <c>6798:032B</c>); 0 none.</summary>
    public required IReadOnlyList<IReadOnlyList<int>> SpellSets { get; init; }

    /// <summary>
    /// A creature's psionic powers, by character record +0x46 (<c>37F0:001C</c>,
    /// 1-based, 10 each); 0 none. The AI draws one at random (<c>6466:0453</c>),
    /// so a power listed more often comes up more often.
    /// </summary>
    public required IReadOnlyList<IReadOnlyList<int>> PsionicSets { get; init; }

    /// <summary>
    /// How much the AI wants to cast a spell (<c>37E5:0000</c>, signed): each it
    /// can cast rolls 0 … weight − 1 and the highest is cast (<c>6466:00B8</c>);
    /// the area targeter also wants |weight| ÷ 2 hit points of enemies caught
    /// (<c>6466:0179</c>). Missing: 0, never.
    /// </summary>
    public required IReadOnlyDictionary<int, int> SpellWeights { get; init; }

    /// <summary>Does a creature of ability type <paramref name="type"/> (+0x16) have <paramref name="bit"/>?</summary>
    public bool HasAbility(int type, int bit) => type >= 0 && type < AbilityTypes.Count && AbilityTypes[type].Contains(bit);

    /// <summary>The abilities the AI may use for <paramref name="type"/>, in the order it tries them.</summary>
    public IEnumerable<CreatureAbility> AbilitiesOf(int type) => Abilities.Where(a => HasAbility(type, a.Bit));

    public IReadOnlyList<int> SpellsOf(int set) => set > 0 && set < SpellSets.Count ? SpellSets[set] : [];

    public IReadOnlyList<int> PowersOf(int set) => set > 0 && set < PsionicSets.Count ? PsionicSets[set] : [];

    public int WeightOf(int spell) => SpellWeights.GetValueOrDefault(spell);

    public static CreatureRules Read(string path)
    {
        CreatureRules rules;
        try
        {
            rules = JsonSerializer.Deserialize<CreatureRules>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("empty file");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException(e.Message, e);
        }
        if (rules.AbilityChance < 1)
        {
            throw new InvalidDataException("abilityChance: at least 1 (one turn in that many)");
        }
        if (rules.AbilityTypes.SelectMany(t => t).Concat(rules.Abilities.Select(a => a.Bit)).FirstOrDefault(b => b is < 0 or > 31, -1) is var bad and >= 0)
        {
            throw new InvalidDataException($"ability bit {bad}: bits are 0–31");
        }
        return rules;
    }

    /// <summary>The rules as JSON, each list of numbers on one line (<c>[5, 7, 17]</c>) so a table reads as one row each.</summary>
    public string ToJson() => NumberList().Replace(JsonSerializer.Serialize(this, Json),
        m => "[" + string.Join(", ", m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries)) + "]");

    [GeneratedRegex(@"\[\s*(-?\d+(?:\s*,\s*-?\d+)*)\s*\]")]
    private static partial Regex NumberList();

    /// <summary>DSUN.EXE's tables.</summary>
    public static CreatureRules Default { get; } = new()
    {
        AbilityTypes =
        [
        [], [5, 7, 17], [], [10, 22, 24], [0],
        [7, 13, 28], [0], [11], [10], [0, 20, 28],
        [5, 19, 28], [14, 17], [0, 16, 29], [3, 5, 9, 10], [18],
        [2, 5, 9, 10], [17, 21, 28], [10, 25, 28], [10, 26, 27, 28, 29], [],
        [19], [28, 31], [4, 12, 28], [5, 7, 10], [5, 7],
        [1, 11, 28], [1, 15], [0, 9, 28], [10, 27, 28, 29], [28],
        [6], [7], [1, 11], [1, 10], [5, 11, 17, 24, 27, 28],
        [1, 11], [1, 10], [0, 9, 11], [0, 9, 10], [5, 23],
        [5], [], [17, 24, 25, 27], [8, 24, 26, 27], [0, 1, 3, 17, 24, 25],
        [1, 3, 8, 9, 11, 26], [8, 9, 11, 16, 17, 19, 24, 27], [8, 10, 16, 17, 18, 27], [8, 10, 11, 25, 26], [],
        [1, 2, 3, 8, 10, 11, 24, 26, 27], [0, 1, 3, 8, 10, 11, 16, 17, 19], [], [], [],
        [16], [], [], [26], [],
        [], [19], [4, 5], [26, 29, 30], [16, 19, 20, 21, 24],
        [12, 13, 15], [26, 29, 30], [17, 18], [3, 4, 5], [26, 29, 30],
        [17, 18], [7, 19, 20, 21], [25, 28, 29], [2, 5, 6, 17, 19, 21, 22], [4],
        [26, 29, 30], [16, 19, 20, 21, 24], [12, 13, 15], [26, 29, 30], [17],
        [3, 4, 5], [17, 20, 21, 26, 29, 30], [19, 20, 21, 26], [9, 12, 13, 14, 15], [17, 20, 21, 26, 29, 30],
        [19, 20, 21], [9, 12, 13, 15], [26, 29, 30], [20, 21], [3],
        [26, 29, 30], [19, 20, 21, 28], [9, 13, 15], [26, 29, 30], [17, 19, 20, 21],
        [12, 13, 15], [26, 29, 30], [17, 19, 20, 21, 28], [13, 15], [26, 29, 30],
        ],
        Abilities =
        [
        new(26, 28), new(20, 182), new(31, 186), new(30, 31), new(6, 139), new(12, 176), new(18, 180), new(19, 181), new(21, 183), new(22, 184), new(29, 44),

        ],
        SpellSets =
        [
        [],
        [9, 28, 19, 46, 59],
        [73, 87, 120, 137, 98],
        [73, 87],
        [73, 87, 115, 98, 132],
        [9, 23, 33, 54],
        [79, 87, 96],
        [79, 87, 96, 109],
        [79, 87, 96, 115],
        [1, 1, 1, 1, 1, 1, 1],
        ],
        PsionicSets =
        [
        [],
        [140, 165, 139, 147, 140, 139, 147, 140, 140, 147],
        [142, 140, 170, 139, 161, 142, 170, 139, 165, 161],
        [139, 142, 143, 144, 139, 142, 143, 144, 139, 142],
        [170, 170, 164, 161, 170, 161, 164, 161, 170, 161],
        [160, 147, 139, 159, 141, 160, 147, 139, 159, 141],
        [161, 161, 161, 161, 166, 161, 161, 161, 161, 161],
        [163, 164, 164, 164, 164, 161, 161, 161, 161, 161],
        [160, 162, 165, 165, 161, 147, 143, 154, 140, 144],
        [161, 161, 161, 161, 161, 161, 161, 161, 161, 161],
        ],
        SpellWeights = new Dictionary<int, int>
        {
        [2] = 22, [3] = 30, [4] = 10, [5] = 24, [8] = 12, [9] = 40, [11] = 20, [13] = 10, [14] = 5, [15] = 15,
        [16] = 5, [17] = 24, [18] = 13, [19] = 25, [20] = 30, [21] = 5, [22] = 4, [23] = 27, [24] = 4, [25] = 18,
        [26] = 25, [28] = 63, [29] = 35, [31] = 30, [32] = 20, [33] = 47, [34] = 23, [35] = 6, [36] = 45, [37] = 10,
        [38] = 40, [39] = 11, [40] = 55, [41] = 40, [42] = 30, [43] = 41, [44] = 26, [45] = 26, [46] = 59, [47] = 29,
        [48] = 26, [49] = 26, [50] = 63, [51] = 22, [52] = 10, [53] = 38, [54] = 35, [55] = 20, [56] = 20, [57] = 40,
        [58] = 40, [59] = 55, [61] = 15, [62] = 20, [63] = 20, [64] = 30, [66] = 60, [67] = 50, [70] = 11, [71] = 11,
        [72] = 6, [73] = 10, [74] = 40, [75] = 13, [76] = 6, [77] = 10, [79] = 19, [80] = 7, [81] = 16, [82] = 20,
        [83] = 40, [84] = 14, [86] = 10, [87] = 15, [88] = 2, [89] = 1, [90] = 10, [91] = 32, [92] = 32, [93] = 32,
        [94] = 32, [95] = 10, [96] = 10, [99] = 40, [100] = 10, [101] = 1, [102] = 32, [103] = 2, [105] = 30, [109] = 30,
        [111] = 10, [112] = 30, [114] = 30, [115] = 30, [120] = 30, [122] = 20, [128] = 20, [129] = 40, [130] = 10, [131] = 20,
        [132] = 51, [133] = 60, [134] = 20, [135] = 30, [137] = 60, [138] = 40, [139] = 6, [140] = 13, [141] = 4, [142] = 5,
        [143] = 15, [144] = 10, [145] = 6, [146] = 7, [147] = 13, [149] = 10, [150] = 18, [153] = 12, [154] = 5, [155] = 23,
        [156] = 4, [158] = 3, [159] = 25, [160] = 20, [161] = 24, [162] = 30, [163] = 2, [164] = 7, [165] = 1, [166] = 7,
        [167] = 45, [168] = 1, [169] = 1, [170] = 2, [171] = 2, [172] = 10, [173] = 3, [174] = 1,
        },
    };
}

