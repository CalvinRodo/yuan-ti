using System.Text.Json;
using DarkSun.Data.Items;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace DarkSun.Game.Rules;

/// <summary>A special ability the AI tries (<c>6466:05EF</c>): ability bit <paramref name="Bit"/> casts spell record <paramref name="Spell"/>.</summary>
/// <param name="Bit">A bit of a type's mask (<see cref="CreatureRules.AbilityTypes"/>).</param>
/// <param name="Spell">The spell number cast (a spell record, numbered like <c>SPIN</c>: index + 1;
/// 173 and up are monster-only records with no <c>SPIN</c> text).</param>
public sealed record CreatureAbility(int Bit, int Spell);

/// <summary>
/// One entry of a type's resistances (<c>3E45:00D2</c>, <c>80F5:28F2</c>): a
/// hit or spell with any of <paramref name="Elements"/> affects the creature
/// <paramref name="Percent"/> % of the time.
/// </summary>
public sealed record CreatureResistance(DamageElement Elements, int Percent);

/// <summary>
/// The bits of a type's ability mask (<see cref="CreatureRules.AbilityTypes"/>,
/// <c>3E45:0000</c>), named from the code that tests them (<c>84C1:0000</c>'s
/// callers; <c>dsun-exe-reference.md</c> §8, "Ability bits") and, for the
/// bits nothing tests, from the manual's and clue book's monster entries.
/// Effects are spell records, numbered like <c>SPIN</c> (index + 1).
/// </summary>
public enum CreatureAbilityBit
{
    /// <summary>
    /// Under the ground: unaffected by ranged spells that leave an area behind
    /// (clouds, walls, webs, fog; <c>80F5:161F</c>). Bulettes, dune reapers and
    /// fire eels (which the manual has under the sand), xorns; also the rampager.
    /// </summary>
    Burrows = 0,
    /// <summary>In the air: tested exactly like <see cref="Burrows"/>. Wyverns, vrocks; also the fire elementals.</summary>
    Flies = 1,
    /// <summary>Its first natural attack also does 2d6 cold (effect 173; <c>80F5:1999</c>).</summary>
    ColdTouch = 2,
    /// <summary>Its first natural attack also paralyses for 2d6 rounds (effect 174). Shadows.</summary>
    ParalysingChill = 3,
    /// <summary>Its first natural attack also does 2d6 acid (effect 175). Slimes.</summary>
    AcidTouch = 4,
    /// <summary>Immune to Charm Person, Charm Monster, Charm Person or Mammal, Hold Person and Hold Monster (spell flag 4; <c>80F5:161F</c>).</summary>
    NoCharmOrHold = 5,
    /// <summary>The AI uses Detonate (effect 139).</summary>
    Detonate = 6,
    /// <summary>
    /// Not tested by any code; a guess: sees in the dark. Dark spiders (subterranean),
    /// psurlons (no eyes, "some other sense to see"), the Undermountain folk
    /// (infravision 120'), magera (caves) and the generic type the magera share.
    /// </summary>
    SeesInDark = 7,
    /// <summary>Not tested by any code, and no creature has it.</summary>
    Unused8 = 8,
    /// <summary>Can't be Stuck (condition 51: webs, entangle; <c>8053:03C0</c>), and unaffected by ranged spells that cover the ground (grease, web, walls, quicksand). Shadows, xorns.</summary>
    NotStuck = 9,
    /// <summary>
    /// Not tested by any code: "+1 or better weapon to hit" in the manual and
    /// clue book (babau, shadows, blue slaad, psurlons, lesser elementals). The
    /// resistances (<see cref="CreatureRules.Resistances"/>) are what enforce it.
    /// </summary>
    NeedsPlus1Weapon = 10,
    /// <summary>Not tested by any code: "+2 or better weapons" (elementals, vrocks); see <see cref="NeedsPlus1Weapon"/>.</summary>
    NeedsPlus2Weapon = 11,
    /// <summary>The AI throws a web (effect 176: an area that leaves Stuck).</summary>
    Web = 12,
    /// <summary>Its last natural attack also does 10 poison, none if saved (effect 177). Spiders.</summary>
    Poison10 = 13,
    /// <summary>Its last natural attack also poisons for 30, none if saved (effect 178). Mastyrials.</summary>
    Poison30 = 14,
    /// <summary>Its last natural attack also casts Poison (effect 120: death after 10 rounds unless saved or cured). Wyverns.</summary>
    DeadlyPoison = 15,
    /// <summary>Its first natural attack also does 20 acid and may corrode armour (effect 179, as <see cref="CorrodesGear"/>). Rampagers.</summary>
    Acid20 = 16,
    /// <summary>Regains 3 hit points a round, up to its maximum (<c>8053:0163</c>). Dark spiders, mastyrials, red slaadi.</summary>
    Regenerates = 17,
    /// <summary>The AI uses a paralysing touch (effect 180).</summary>
    ParalysingTouch = 18,
    /// <summary>The AI shoots a 1d6 missile (effect 181).</summary>
    Missile = 19,
    /// <summary>The AI breathes a 6d6 fire cone (effect 182).</summary>
    FireCone = 20,
    /// <summary>The AI breathes a paralysing cone (effect 183). Also makes it immune to Wall of Fog (<c>80F5:161F</c>).</summary>
    ParalysingCone = 21,
    /// <summary>
    /// The AI uses effect 184, whose record has no damage or condition. Only
    /// the babau has it; the clue book's babau has "a gaze attack which may
    /// affect your character as though touched by a ray of enfeeblement".
    /// </summary>
    EnfeeblingGaze = 22,
    /// <summary>A damaging ranged spell does 1000 more (<c>80F5:0E61</c>). No creature has it.</summary>
    DiesToRangedSpells = 23,
    /// <summary>
    /// Its first natural attack also casts effects 187, "Armor is corroded" (the
    /// first item on the arms, legs or chest is destroyed unless its item record
    /// +0x0F v ≥ 1 and 1d20 ≥ 10 − v; <c>80F5:2024</c>), and 188, "Weapon is
    /// corroded" (the weapon in hand, unless 1d20 ≥ 8 − its bonus; <c>80F5:1EBA</c>). Babau.
    /// </summary>
    CorrodesGear = 24,
    /// <summary>Its first natural attack, one hit in 10, also gives a permanent disease (effect 185). Blue slaadi.</summary>
    Disease = 25,
    /// <summary>The AI casts Fireball (spell 28).</summary>
    Fireball = 26,
    /// <summary>Sees invisible creatures (<c>80F5:31AB</c>, for the armour class and save adjustments). No creature has it.</summary>
    SeesInvisible = 27,
    /// <summary>
    /// Not tested by any code; a guess: can't be surprised or caught from behind.
    /// The manual's styr (whose type the animated statue has): "having four
    /// heads makes it impossible to be surprised"; also the xorn, slimes, the
    /// warrior and mage dark spiders, slaadi, vrocks and fire eels.
    /// </summary>
    CantBeSurprised = 28,
    /// <summary>The AI casts Fear (spell 44).</summary>
    Fear = 29,
    /// <summary>The AI casts Hold Person (spell 31).</summary>
    HoldPerson = 30,
    /// <summary>The AI breathes a 3d6 fire cone (effect 186).</summary>
    SmallFireCone = 31,
}

/// <summary>
/// What creatures can do in combat beyond their records: tables that are in
/// DSUN.EXE, not in the data files (<c>dsun-exe-reference.md</c> §8, "The AI").
/// <see cref="Default"/> is the original's; a mod can replace them with
/// <c>rules/creatures.json</c> (<see cref="Read"/>). Spells and powers are
/// numbered as <c>SPIN</c> (spell record index + 1; powers 139–172).
/// </summary>
public sealed partial record CreatureRules
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    /// <summary>
    /// The special abilities, by creature type (combat record +0x16;
    /// <c>3E45:0000</c>, 42 32-bit masks; <c>84C1:0000</c>): the bits each type
    /// has, named by <see cref="CreatureAbilityBit"/>. The AI uses those in
    /// <see cref="Abilities"/>; the damage, save and resistance code tests the
    /// others. A type past the end (Aposs 128, Maris 129) has none; DSUN.EXE
    /// reads past the table for them.
    /// </summary>
    public required IReadOnlyList<IReadOnlyList<int>> AbilityTypes { get; init; }

    /// <summary>
    /// What each creature type resists, by type like <see cref="AbilityTypes"/>
    /// (<c>3E45:00A8</c> gives each type one of 16 rows at <c>3E45:00D2</c>, 4
    /// entries each; <c>80F5:28F2</c>). Most are AD&amp;D's "hit only by +1 (or
    /// +2) weapons": physical elements 0 %, the same with a magic bit 100 %.
    /// </summary>
    public required IReadOnlyList<IReadOnlyList<CreatureResistance>> Resistances { get; init; }

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

    public bool HasAbility(int type, CreatureAbilityBit bit) => HasAbility(type, (int)bit);

    /// <summary>
    /// The percent chance a hit or spell of <paramref name="elements"/> affects
    /// type <paramref name="type"/>: the highest of its resistances that share a
    /// bit with them, else 100 (<c>80F5:28F2</c>, before the target's
    /// protection conditions halve it).
    /// </summary>
    public int ChanceToAffect(int type, DamageElement elements) =>
        type >= 0 && type < Resistances.Count && Resistances[type].Where(r => (r.Elements & elements) != 0).ToList() is { Count: > 0 } hits
            ? hits.Max(r => r.Percent)
            : 100;

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
        if (rules.Resistances.SelectMany(t => t).FirstOrDefault(r => r.Percent is < 0 or > 100) is { } r)
        {
            throw new InvalidDataException($"resistance {r.Elements}: percent is 0–100, not {r.Percent}");
        }
        return rules;
    }

    /// <summary>
    /// The rules as JSON, each list of numbers (<c>[5, 7, 17]</c>) and each
    /// resistance on one line, so a table reads as one row each.
    /// </summary>
    public string ToJson()
    {
        string json = NumberList().Replace(JsonSerializer.Serialize(this, Json),
            m => "[" + string.Join(", ", m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries)) + "]");
        return FlatObject().Replace(json, m => "{ " + string.Join(" ", m.Groups[1].Value.Split('\n', StringSplitOptions.TrimEntries).Where(f => f.Length > 0)) + " }");
    }

    [GeneratedRegex(@"\[\s*(-?\d+(?:\s*,\s*-?\d+)*)\s*\]")]
    private static partial Regex NumberList();

    /// <summary>An object of numbers and strings only, one field a line.</summary>
    [GeneratedRegex(@"\{\s*\n((?:\s*""\w+"": (?:-?\d+|""[^""]*""),?\n)+)\s*\}")]
    private static partial Regex FlatObject();

    private const DamageElement Physical = DamageElement.Bludgeoning | DamageElement.Slashing | DamageElement.Piercing;

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
        [5], [],
        ],
        Resistances =
        [
        [], [],
        [new(DamageElement.Psionic, 0)], // 2
        [new(Physical, 0), new(DamageElement.Magic | DamageElement.Plus1 | DamageElement.Plus2 | DamageElement.Plus3, 100)], // 3 babau
        [],
        [new(DamageElement.Poison, 0)], // 5 spiders
        [],
        [new(Physical | DamageElement.Plus1, 0), new(DamageElement.Magic | DamageElement.Plus2 | DamageElement.Plus3, 100)], // 7 elementals
        [new(Physical, 0), new(DamageElement.Magic | DamageElement.Plus1 | DamageElement.Plus2 | DamageElement.Plus3, 100)], // 8 lesser elementals
        [],
        [new(DamageElement.Psionic, 0)], // 10
        [new(DamageElement.Bludgeoning, 0), new(DamageElement.Slashing | DamageElement.Piercing, 100)], // 11 mastyrials
        [new(Physical | DamageElement.Psionic, 50), new(DamageElement.Magic | DamageElement.Plus1 | DamageElement.Plus2 | DamageElement.Plus3, 100)], // 12 rampager
        [new(Physical, 0), new(DamageElement.Magic | DamageElement.Plus1 | DamageElement.Plus2 | DamageElement.Plus3, 100)], // 13 shadows
        [],
        [new(Physical | DamageElement.Poison | DamageElement.Negative, 0), new(DamageElement.Plus1 | DamageElement.Plus2 | DamageElement.Plus3, 100)], // 15 shadow
        [],
        [new(Physical, 0), new(DamageElement.Magic | DamageElement.Plus1 | DamageElement.Plus2 | DamageElement.Plus3, 100)], // 17 blue slaad
        [new(Physical, 0), new(DamageElement.Magic | DamageElement.Plus1 | DamageElement.Plus2 | DamageElement.Plus3, 100)], // 18
        [new(DamageElement.Fire, 50), new(Physical, 100)], // 19 ssurrans, tribesmen
        [],
        [new(DamageElement.Fire | DamageElement.Cold, 0), new(Physical, 100)], // 21 animated statue
        [new(DamageElement.Fire | DamageElement.Bludgeoning | DamageElement.Piercing | DamageElement.Acid, 0), new(DamageElement.Slashing, 100)], // 22 slimes
        [new(Physical | DamageElement.Poison | DamageElement.Negative, 0), new(DamageElement.Plus1 | DamageElement.Plus2 | DamageElement.Plus3, 100)], // 23 psurlons
        [],
        [new(Physical | DamageElement.Plus1, 0), new(DamageElement.Magic | DamageElement.Plus2 | DamageElement.Plus3, 100)], // 25 vrock
        [],
        [new(DamageElement.Fire | DamageElement.Cold, 0), new(DamageElement.Electricity, 50), new(Physical, 100)], // 27 xorn
        [], [], [], [],
        [new(Physical | DamageElement.Fire, 0), new(DamageElement.Plus1 | DamageElement.Plus2 | DamageElement.Plus3, 100)], // 32 fire elemental
        [new(Physical | DamageElement.Plus1, 0), new(DamageElement.Magic | DamageElement.Plus2 | DamageElement.Plus3, 100)], // 33 lesser fire elemental
        [],
        [new(Physical | DamageElement.Plus1, 0), new(DamageElement.Magic | DamageElement.Plus2 | DamageElement.Plus3, 100)], // 35
        [new(Physical, 0), new(DamageElement.Magic | DamageElement.Plus1 | DamageElement.Plus2 | DamageElement.Plus3, 100)], // 36
        [new(Physical | DamageElement.Plus1, 0), new(DamageElement.Magic | DamageElement.Plus2 | DamageElement.Plus3, 100)], // 37
        [new(Physical, 0), new(DamageElement.Magic | DamageElement.Plus1 | DamageElement.Plus2 | DamageElement.Plus3, 100)], // 38
        [], [], [],
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

