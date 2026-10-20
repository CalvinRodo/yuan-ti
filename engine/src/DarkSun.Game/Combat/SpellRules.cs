using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DarkSun.Data.Items;

namespace DarkSun.Game.Combat;

/// <summary>
/// What a spell, power or monster ability does in combat: DSUN.EXE's 32-byte
/// record (<c>3CB4:0037 + 0x20 · index</c>, the fields from −6 on;
/// <c>dsun-exe-reference.md</c> §8, "Spells in combat"). <see cref="Number"/> is
/// the <c>SPIN</c> number (record index + 1): spells 1–138, powers 139–172,
/// monster effects 173–196.
/// </summary>
/// <param name="RangeBase">−6: range = (base + per level × level) × 4 sixteenths of a cell (<c>8BEB:07F3</c>); −1 touch, −2 a short reach (28).</param>
/// <param name="DurationCount">−3 low nibble: dice of duration (with <paramref name="DurationSides"/>).</param>
/// <param name="DurationPerLevel">−2: duration added per level step.</param>
/// <param name="Unit">+0: what a duration counts: 60 rounds, 600 turns, 3600 hours (in game seconds), −9999 permanent, 0 instant, −1 charges (<see cref="UsesCharges"/>).</param>
/// <param name="AreaBase">+2: the area's size, base + per level × level, sixteenths of a cell (<c>8BEB:086F</c>).</param>
/// <param name="Target">+5: 1 an area, 2 a line, 3 touch, 4 a creature, 5 a creature (missile), 6 a cone, 7 self, 8 self.</param>
/// <param name="Template">+0x10: the area template (<see cref="SpellRules.Templates"/>), −1 none.</param>
/// <param name="Flags">+0x0A (<see cref="SpellFlags"/>).</param>
/// <param name="Condition">+0x12: the condition a failed save gives.</param>
/// <param name="Elements">+0x13: what it is made of, for resistances.</param>
/// <param name="BaseDice">Damage (<c>7D80:1CC2</c>): +0x16 bits 3–7.</param>
/// <param name="DicePerStep">+0x15 bits 5–7.</param>
/// <param name="Sides">+0x17 bits 0–3.</param>
/// <param name="FlatPerStep">+0x15 bits 0–4.</param>
/// <param name="LevelsPerStep">+0x16 bits 0–2 (0 counts as 1).</param>
/// <param name="LevelBonus">+0x17 bits 4–7, signed: levels added before dividing.</param>
/// <param name="SaveAllowed">+0x18 bit 0.</param>
/// <param name="SaveModifier">+0x18 bits 1–4, signed.</param>
/// <param name="SaveCategory">+0x18 bits 5–7 (<see cref="SpellRules.SaveCategories"/>); 6 none.</param>
public sealed record SpellEffect(
    int Number, int RangeBase, int RangePerLevel, int DurationCount, int DurationSides, int DurationPerLevel,
    int Unit, int AreaBase, int AreaPerLevel, int Target, int Template, int Flags, Condition Condition, DamageElement Elements,
    int BaseDice, int DicePerStep, int Sides, int FlatPerStep, int LevelsPerStep, int LevelBonus,
    int SaveAllowed, int SaveModifier, int SaveCategory)
{
    public const int Permanent = -9999, Charges = -1, Round = 60;

    /// <summary>The highest caster level the formulas use (<c>7D80:1CC2</c>).</summary>
    public const int MaxLevel = 10;

    [JsonIgnore]
    public bool UsesCharges => Unit == Charges;

    [JsonIgnore]
    public bool Damaging => (Flags & SpellFlags.Damaging) != 0;

    [JsonIgnore]
    public bool IsTouch => RangeBase is -1 or -2;

    /// <summary>A save cancels the damage rather than halving it.</summary>
    [JsonIgnore]
    public bool SaveCancels => (Flags & SpellFlags.NoDamageIfSaved) != 0;

    /// <summary>Spells of one creature or one cell (touch, a creature, self).</summary>
    [JsonIgnore]
    public bool SingleTarget => Template < 0 || Target is 3 or 4 or 5 or 7 or 8;

    /// <summary>Damage steps for a caster of <paramref name="level"/>: (level + bonus) / levels a step, at least 1.</summary>
    public int Steps(int level) => Math.Max(1, (Math.Min(level, MaxLevel) + LevelBonus) / Math.Max(1, LevelsPerStep));

    /// <summary>Dice of damage (<see cref="Sides"/> each) and the flat part, for <paramref name="level"/>.</summary>
    public (int Dice, int Flat) DamageAt(int level) => (BaseDice + DicePerStep * Steps(level), FlatPerStep * Steps(level));

    /// <summary>The most damage it can do: every die at its highest (the AI's estimate).</summary>
    public int MaxDamage(int level)
    {
        var (dice, flat) = DamageAt(level);
        return dice * Sides + flat;
    }

    /// <summary>Range in cells (<c>8BEB:07F3</c>, ÷ 16): touch is 1.</summary>
    public int RangeCells(int level) => RangeBase switch
    {
        -1 or -2 => 1,
        _ => Math.Max(1, (RangeBase + RangePerLevel * Math.Min(level, MaxLevel)) * 4 / 16),
    };

    /// <summary>The area's size in sixteenths of a cell (<c>8BEB:086F</c>).</summary>
    public int AreaSize(int level) => AreaBase + AreaPerLevel * Math.Min(level, MaxLevel);
}

/// <summary>Spell record +0x0A bits the code reads.</summary>
public static class SpellFlags
{
    public const int Summons = 0x0001;
    /// <summary>Confusion, Scare, Feeblemind, Domination…: mind effects (with <see cref="CharmOrHold"/> and 0x20, flags 0x26 to <c>80F5:2580</c>).</summary>
    public const int Mind = 0x0002;
    /// <summary>The charm and hold spells (<c>80F5:2500</c>).</summary>
    public const int CharmOrHold = 0x0004;
    /// <summary>Illusions and fear (<c>80F5:2540</c>).</summary>
    public const int Illusion = 0x0008;
    public const int Fear = 0x0010;
    public const int MindAffecting = 0x0026;
    public const int CombatOnly = 0x0200;
    public const int Damaging = 0x4000;
    public const int NoDamageIfSaved = 0x8000;
}

/// <summary>
/// An area template (<c>3C81:0000</c>, 0x12 bytes): <paramref name="Kind"/> (byte
/// +2 low nibble: 1 a burst that's gone at once, 0 fog, 2 on the ground, 3 a
/// cloud; <c>80F5:161F</c>) and <paramref name="Shape"/> (byte +5 bits 2–3:
/// 0 a line or wall, 1 a circle, 2 a cone; <c>8BEB:0000</c>).
/// </summary>
public sealed record AreaTemplate(int Kind, int Shape)
{
    public const int Burst = 1, Ground = 2;
    public const int Line = 0, Circle = 1, Cone = 2;

    [JsonIgnore]
    public bool Lingers => Kind != Burst;
}

/// <summary>
/// A psionic power's 8-byte record (<c>3FB9:0000 + 8 · (power − 139)</c>):
/// the power check is d20 ≤ ability (<paramref name="Ability"/>, Str–Cha) +
/// <paramref name="Modifier"/>; <paramref name="Cost"/> points to start,
/// <paramref name="Maintenance"/> a round (99 none); <paramref name="AttackMode"/>
/// 0–3 for the psionic attacks (Psychic Crush, Ego Whip, Id Insinuation,
/// Psionic Blast), −1 otherwise.
/// </summary>
public sealed record PsionicPower(
    int Number, int Ability, int Modifier, int Cost, int Maintenance, int Discipline, int Science, int Defence, int AttackMode);

/// <summary>
/// DSUN.EXE's spell, power and area tables (<see cref="Default"/>); a mod can
/// replace them with <c>rules/spells.json</c> (<see cref="Read"/>).
/// </summary>
public sealed partial record SpellRules
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    /// <summary>One per <c>SPIN</c> number, from 1.</summary>
    public required IReadOnlyList<SpellEffect> Effects { get; init; }

    public required IReadOnlyList<AreaTemplate> Templates { get; init; }

    /// <summary>Powers 139–172.</summary>
    public required IReadOnlyList<PsionicPower> Powers { get; init; }

    /// <summary>
    /// The save a category rolls against, 1–5 (paralysation, rod, petrification,
    /// breath, spell; <c>DS:1E75</c>). Most spells are category 5, so they use
    /// the third save; kept as the original has it.
    /// </summary>
    public IReadOnlyList<int> SaveCategories { get; init; } = [1, 1, 1, 2, 3, 3, 4, 5, 5, 5];

    public SpellEffect? Effect(int number) => number >= 1 && number <= Effects.Count ? Effects[number - 1] : null;

    public AreaTemplate? TemplateOf(SpellEffect e) => e.Template >= 0 && e.Template < Templates.Count ? Templates[e.Template] : null;

    public PsionicPower? Power(int number) => Powers.FirstOrDefault(p => p.Number == number);

    public static SpellRules Read(string path)
    {
        SpellRules rules;
        try
        {
            rules = JsonSerializer.Deserialize<SpellRules>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("empty file");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException(e.Message, e);
        }
        if (rules.Effects.Select((e, i) => (e, i)).FirstOrDefault(p => p.e.Number != p.i + 1) is { e: not null } bad)
        {
            throw new InvalidDataException($"effects: entry {bad.i + 1} is numbered {bad.e.Number}; they run from 1 in order");
        }
        if (rules.SaveCategories.Any(s => s is < 1 or > 5))
        {
            throw new InvalidDataException("saveCategories: saves are 1–5");
        }
        return rules;
    }

    /// <summary>The rules as JSON, each record on one line.</summary>
    public string ToJson() => FlatObject().Replace(NumberList().Replace(JsonSerializer.Serialize(this, Json),
            m => "[" + string.Join(", ", m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries)) + "]"),
        m => "{ " + string.Join(" ", m.Groups[1].Value.Split('\n', StringSplitOptions.TrimEntries).Where(f => f.Length > 0)) + " }");

    [GeneratedRegex(@"\[\s*(-?\d+(?:\s*,\s*-?\d+)*)\s*\]")]
    private static partial Regex NumberList();

    [GeneratedRegex(@"\{\s*\n((?:\s*""\w+"": (?:-?\d+|""[^""]*""),?\n)+)\s*\}")]
    private static partial Regex FlatObject();
}
