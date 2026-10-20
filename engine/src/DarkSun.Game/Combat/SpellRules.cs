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
    /// <summary>Illusions (<c>80F5:2540</c>): Intelligence 19+ is immune.</summary>
    public const int Illusion = 0x0008;
    /// <summary>Fear and the psionic attacks.</summary>
    public const int Fear = 0x0010;
    /// <summary>Wisdom 19+ is immune, Mind Bar adds 75 to magic resistance, the undead are immune (<c>80F5:2580</c>).</summary>
    public const int MindAffecting = 0x0026;
    /// <summary>Wisdom's magical defence adds to the save (<c>80F5:2560</c>).</summary>
    public const int WisdomSave = 0x001E;
    /// <summary>Dexterity's adjustment adds to the save (<c>80F5:25C7</c>); no shipped record sets it.</summary>
    public const int DexteritySave = 0x0040;
    /// <summary>A gaze, which Gaze Reflection turns back (<c>80F5:25E7</c>); no shipped record sets it.</summary>
    public const int Gaze = 0x0080;
    /// <summary>The Brave save 4 better and spend a charge (<c>80F5:25A0</c>); no shipped record sets it.</summary>
    public const int BraveSave = 0x0100;
    public const int CombatOnly = 0x0200;
    public const int Damaging = 0x4000;
    public const int NoDamageIfSaved = 0x8000;
}

/// <summary>
/// An area template (<c>3C81:0000</c>, 0x12 bytes). <paramref name="Kind"/> (byte
/// +2 low nibble; <c>7D80:0F9D</c>): 1 a burst, gone at once; the others stay
/// on the map as a field (<see cref="Battle.Fields"/>): 0 fog, which only
/// hides, 2 on the ground, acting on whoever enters or moves in it, 3 a
/// cloud, acting on whoever enters it or starts a turn in it.
/// <paramref name="Shape"/> (+5 bits 2–3; <c>8BEB:0000</c>): 0 a line or wall,
/// 1 a circle, 2 a cone. <paramref name="Effect"/> (+0): what the field does
/// (a <c>SPIN</c> number). <paramref name="Flags"/> (+2 high nibble): 0x10 hides
/// what is behind it, 0x20 blocks movement, 0x40 burns (<c>7D80:003B</c>: fire
/// clears it). <paramref name="Turn"/> (+6 high nibble): sixteenths of a turn
/// added to a line's direction (4, a quarter turn, makes a wall across).
/// <paramref name="Width"/> and <paramref name="Length"/> (+7, +8, signed;
/// <c>8BEB:0393</c>): with a positive width the area is as wide as its size and
/// length ÷ width times as long; a negative length is fixed (sixteenths of a
/// cell). A field lasts <paramref name="DurationUnit"/> (+0xC; 30000
/// permanent) × (<paramref name="DurationDice"/> d <paramref name="DurationSides"/>
/// + <paramref name="DurationPerLevel"/> × level) game seconds (+0xA, +0xB; <c>7D80:05C7</c>).
/// <paramref name="Graphic"/> (+3): the <c>OJFF</c> a field shows on its cells
/// (<c>7D80:2782</c>), and what a spell without a hit graphic shows on those it
/// reaches (<c>80F5:0079</c>); −1 none.
/// </summary>
public sealed record AreaTemplate(int Kind, int Shape, int Effect = 0, int Flags = 0, int Turn = 0, int Width = 1, int Length = 1,
    int DurationDice = 0, int DurationSides = 0, int DurationPerLevel = 0, int DurationUnit = AreaTemplate.Round, int Graphic = -1)
{
    public const int Fog = 0, Burst = 1, Ground = 2, Cloud = 3;
    public const int Line = 0, Circle = 1, Cone = 2;
    public const int HidesFlag = 0x10, BlocksFlag = 0x20, BurnsFlag = 0x40;
    public const int Round = 60, Permanent = 30000;

    [JsonIgnore]
    public bool Lingers => Kind != Burst;

    [JsonIgnore]
    public bool Hides => (Flags & HidesFlag) != 0;

    [JsonIgnore]
    public bool Blocks => (Flags & BlocksFlag) != 0;

    [JsonIgnore]
    public bool Burns => (Flags & BurnsFlag) != 0;

    /// <summary>
    /// The area's width and length for an area of <paramref name="size"/>
    /// sixteenths (<c>8BEB:0393</c>).
    /// </summary>
    public (int Width, int Length) Proportions(int size)
    {
        if (Width < 0)
        {
            return (-Width, Length < 0 ? -Length : size);
        }
        int w = Math.Max(1, Width);
        return (size, Length < 0 ? -Length : Length * size / w);
    }
}

/// <summary>
/// How a spell, power or monster effect looks and sounds (its record, read by
/// <c>7D80:003B</c> and <c>80F5:0079</c>): <paramref name="CastGraphic"/> (+6,
/// an <c>OJFF</c> played over the caster), <paramref name="CastSound"/> (+8),
/// <paramref name="Projectile"/> (+9: an entry of <see cref="SpellRules.Projectiles"/>
/// flown from the caster to the target point, −1 none) with
/// <paramref name="FlightSound"/> (+0xC), and <paramref name="HitGraphic"/>
/// (+0xD, played over each one it affects without hurting; −1: the template's
/// <see cref="AreaTemplate.Graphic"/>) with <paramref name="HitSound"/> (+0xF).
/// Sounds are <c>play_sound_effect</c> numbers.
/// </summary>
public sealed record SpellLook(int Number, int CastGraphic, int CastSound, int Projectile, int FlightSound, int HitGraphic, int HitSound);

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

/// <summary>A cure (<c>80F5:05E6</c>): effect <paramref name="Effect"/> heals <paramref name="Dice"/>d<paramref name="Sides"/> + <paramref name="Bonus"/>.</summary>
public sealed record Cure(int Effect, int Dice, int Sides, int Bonus);

/// <summary>Whose side a <see cref="TargetFilter"/> takes.</summary>
public enum TargetSide { Any, Caster, Other }

/// <summary>
/// Whom effects <paramref name="Effects"/> take at all (<c>80F5:1CF7</c>):
/// only races <paramref name="Races"/> (when given), only the undead, only
/// the caster's side or the other, and only those with no enemy beside them
/// (<c>80F5:249D</c>).
/// </summary>
public sealed record TargetFilter(
    IReadOnlyList<int> Effects,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] IReadOnlyList<int>? Races = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Undead = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] TargetSide Side = TargetSide.Any,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool NoEnemyBeside = false);

/// <summary>How a save against an effect goes, regardless of the roll.</summary>
public enum SaveOutcome { Rolled, Fails, Succeeds }

/// <summary>
/// An effect whose save depends on the target's level (<c>80F5:119C</c>):
/// <paramref name="Below"/> below <paramref name="Level"/>, <paramref name="From"/>
/// at it and above. With <paramref name="UnlessClasses"/>, there is no save
/// for a target without levels in one of those class codes.
/// </summary>
public sealed record SaveRule(
    int Effect,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Level = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] SaveOutcome Below = SaveOutcome.Rolled,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] SaveOutcome From = SaveOutcome.Rolled,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] IReadOnlyList<int>? UnlessClasses = null);

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

    /// <summary>How each effect looks and sounds, by number (optional in a mod's file: then none).</summary>
    public IReadOnlyList<SpellLook> Looks { get; init; } = [];

    /// <summary>The projectiles' <c>OJFF</c> numbers (<c>3CB4:0000</c>, 3 bytes each; the third byte isn't read here).</summary>
    public IReadOnlyList<int> Projectiles { get; init; } = [];

    /// <summary>
    /// What a missile weapon shoots (<c>896E:0000</c>, by the item's number):
    /// <c>OJFF</c> 8117 for items 1010 and 2646, 8500 for 1015, 1016, 2533 and
    /// 2644, else <see cref="DefaultMissile"/>.
    /// </summary>
    public IReadOnlyDictionary<int, int> MissileProjectiles { get; init; } =
        new Dictionary<int, int> { [1010] = 8117, [2646] = 8117, [1015] = 8500, [1016] = 8500, [2533] = 8500, [2644] = 8500 };

    /// <summary>The arrow (<c>OJFF</c> 8101).</summary>
    public int DefaultMissile { get; init; } = 8101;

    /// <summary>The cures (<c>80F5:05E6</c>): Cure Light (1d8), Serious (2d8 + 1) and Critical Wounds (3d8 + 3), and effect 152 (2).</summary>
    public IReadOnlyList<Cure> Cures { get; init; } = [new(72, 1, 8, 0), new(113, 2, 8, 1), new(128, 3, 8, 3), new(152, 0, 0, 2)];

    /// <summary>Dispel Magic, wizard's and priest's (<c>80F5:05E6</c> cases 26 and 98): each effect ends 50 in 100 (<see cref="CombatRules.DispelChance"/>).</summary>
    public IReadOnlyList<int> Dispels { get; init; } = [27, 99];

    /// <summary>What Dispel Magic leaves alone (<c>80F5:0D6E</c>).</summary>
    public IReadOnlyList<Condition> Undispellable { get; init; } =
    [
        Condition.Biofeedback, Condition.Diseased, Condition.Feebleminded, Condition.SynapticStatic, Condition.MindBar,
        Condition.Poisoned, Condition.GraftWeapon, Condition.NoSpells, Condition.Stuck, (Condition)54,
    ];

    /// <summary>The effects that end <see cref="Afflictions"/> (<c>80F5:05E6</c>: 195).</summary>
    public IReadOnlyList<int> AfflictionCures { get; init; } = [195];

    /// <summary>What effect 195 ends (<c>80F5:0D46</c>).</summary>
    public IReadOnlyList<Condition> Afflictions { get; init; } =
    [
        Condition.Acid, Condition.Berserk, Condition.Blind, Condition.Confused, Condition.Cursed, Condition.Afraid,
        Condition.LowResistance, Condition.CantAttack, Condition.SavePenalty, Condition.Slowed,
    ];

    /// <summary>
    /// Whom some effects take at all (<c>80F5:1CF7</c>): Charm Person and the
    /// Hold Persons a person (races 1–7), Charm Person or Mammal also race 12;
    /// Hold Undead and Turn Undead the undead; Dismissal and Abjure race 14;
    /// Haste the caster's side; Slow, Curse and Minor Malison the other side;
    /// Bless and Aid the caster's side without an enemy beside them.
    /// </summary>
    public IReadOnlyList<TargetFilter> TargetFilters { get; init; } =
    [
        new([3, 31, 87], Races: [1, 2, 3, 4, 5, 6, 7]),
        new([83], Races: [1, 2, 3, 4, 5, 6, 7, 12]),
        new([32, 191], Undead: true),
        new([61, 108], Races: [14]),
        new([30], Side: TargetSide.Caster),
        new([35, 38, 71], Side: TargetSide.Other),
        new([70, 81], Side: TargetSide.Caster, NoEnemyBeside: true),
    ];

    /// <summary>
    /// Saves set by level or class (<c>80F5:119C</c>): Scare can't miss below
    /// level 6 and can't land from 6; no save against Cloudkill below level 7,
    /// nor against Chaos without a fighter's, gladiator's or ranger's levels
    /// (class codes 9, 10, 13–16; <c>80F5:3589</c>).
    /// </summary>
    public IReadOnlyList<SaveRule> SaveRules { get; init; } =
    [
        new(22, 6, SaveOutcome.Fails, SaveOutcome.Succeeds),
        new(58, 7, SaveOutcome.Fails),
        new(57, UnlessClasses: [9, 10, 13, 14, 15, 16]),
    ];

    public bool IsCure(int number) => Cures.Any(c => c.Effect == number);

    public SpellLook? LookOf(int number) => Looks.FirstOrDefault(l => l.Number == number);

    /// <summary>The projectile <c>OJFF</c> of a look, or null.</summary>
    public int? ProjectileOf(SpellLook look) => look.Projectile >= 0 && look.Projectile < Projectiles.Count ? Projectiles[look.Projectile] : null;

    /// <summary>What a missile weapon of item <paramref name="rdff"/> shoots.</summary>
    public int MissileOf(int rdff) => MissileProjectiles.TryGetValue(Math.Abs(rdff), out int ojff) ? ojff : DefaultMissile;

    /// <summary>
    /// The save a category rolls against, 1–5 (paralysation, rod, petrification,
    /// breath, spell; <c>DS:1E75</c>). Most spells are category 5, so they use
    /// the third save; kept as the original has it.
    /// </summary>
    public IReadOnlyList<int> SaveCategories { get; init; } = [1, 1, 1, 2, 3, 3, 4, 5, 5, 5];

    public SpellEffect? Effect(int number) => number >= 1 && number <= Effects.Count ? Effects[number - 1] : null;

    public AreaTemplate? TemplateOf(SpellEffect e) => e.Template >= 0 && e.Template < Templates.Count ? Templates[e.Template] : null;

    public PsionicPower? Power(int number) => Powers.FirstOrDefault(p => p.Number == number);

    /// <summary>
    /// The area size the code compares with 8 (<c>8BEB:086F</c> at level
    /// <paramref name="level"/>): 8 for touch, creature and self targets, else
    /// the area's, doubled for a circle. Up to 8 is "small".
    /// </summary>
    public int AreaOf(SpellEffect e, int level = 1) =>
        e.Target is 3 or 4 or 5 or 7 or 8 ? 8 : e.AreaSize(level) * (TemplateOf(e) is { Shape: AreaTemplate.Circle } ? 2 : 1);

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
    public string ToJson() => Compact(JsonSerializer.Serialize(this, Json));

    /// <summary>Indented JSON with each list of numbers, and each object of numbers, strings and such lists, on one line.</summary>
    internal static string Compact(string json) => FlatObject().Replace(NumberList().Replace(json,
            m => "[" + string.Join(", ", ListItem().Matches(m.Groups[1].Value).Select(i => i.Value)) + "]"),
        m => "{ " + string.Join(" ", m.Groups[1].Value.Split('\n', StringSplitOptions.TrimEntries).Where(f => f.Length > 0)) + " }");

    [GeneratedRegex(@"\[\s*((?:-?\d+|""[^""\n]*"")(?:\s*,\s*(?:-?\d+|""[^""\n]*""))*)\s*\]")]
    private static partial Regex NumberList();

    [GeneratedRegex(@"-?\d+|""[^""\n]*""")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"\{\s*\n((?:\s*""\w+"": (?:-?\d+|""[^""]*""|true|false|\[[^\]\n]*\]),?\n)+)\s*\}")]
    private static partial Regex FlatObject();
}
