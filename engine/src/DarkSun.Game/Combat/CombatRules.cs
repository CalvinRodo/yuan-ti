using System.Text.Json;
using System.Text.Json.Serialization;
using DarkSun.Data.Items;

namespace DarkSun.Game.Combat;

/// <summary>
/// The steps of a computer-controlled turn (<c>612E:0BAD</c>), tried in the
/// order of <see cref="CombatRules.AiSteps"/>; the first that acts ends the
/// choice (<c>dsun-exe-reference.md</c> §8, "The AI").
/// </summary>
public enum AiStep
{
    /// <summary>A target farther than <see cref="CombatRules.SightRange"/> ends the turn.</summary>
    TooFar,
    /// <summary>Cast one of its spells, weighed by <see cref="DarkSun.Game.Rules.CreatureRules.SpellWeights"/>; once a turn.</summary>
    Spells,
    /// <summary>A special ability (<see cref="DarkSun.Game.Rules.CreatureRules.Abilities"/>), one turn in <c>AbilityChance</c>.</summary>
    Ability,
    /// <summary>A psionic power from its set (monsters only).</summary>
    Psionics,
    /// <summary>Attack the target if it is within reach and in sight.</summary>
    Attack,
    /// <summary>Attack an enemy next to it instead.</summary>
    Adjacent,
    /// <summary>Move toward the target (or the nearest enemy it can see).</summary>
    Move,
    /// <summary>Shoot a missile weapon at an enemy in range.</summary>
    Missile,
    /// <summary>Guard (it may still attack) and end the turn.</summary>
    Guard,
}

/// <summary>How the AI picks a new target.</summary>
public enum TargetChoice
{
    /// <summary>The original's: any enemy in range, at random (<c>612E:1284</c>).</summary>
    Random,
    /// <summary>The nearest enemy.</summary>
    Nearest,
    /// <summary>The enemy with the fewest hit points left.</summary>
    Weakest,
    /// <summary>A spellcaster or psionicist first, else the nearest.</summary>
    Casters,
}

/// <summary>
/// How the AI scores a weapon (<c>612E:0350</c>): dice × sides ×
/// <see cref="PerDieSide"/> + damage bonus × <see cref="DamageBonus"/> + item
/// bonus × <see cref="ItemBonus"/> + <see cref="Magic"/> if magic; a missile
/// weapon × its rate, a melee weapon + Strength's damage + the material
/// modifier × <see cref="Material"/>, then × attacks a round (in halves).
/// </summary>
public sealed record WeaponScoring(int PerDieSide = 1, int DamageBonus = 2, int ItemBonus = 4, int Magic = 10, int Material = 3);

/// <summary>
/// Quality-of-life fixes for what DSUN.EXE does oddly (<c>dsun-exe-reference.md</c>
/// §8), each false as the original has it.
/// </summary>
/// <param name="MirrorImagesTakeBlows">Mirror images take weapon hits too (the original tests them only for spells, <c>80F5:1999</c>).</param>
/// <param name="NegativePlaneProtectionSaves">Negative Plane Protection gets its save (the original saves as against Vampiric Touch, whose record allows none).</param>
/// <param name="BraveAgainstFear">Brave's +4 counts against fear (flag 0x10), not only flag 0x100, which no record has.</param>
/// <param name="DexterityAgainstAreas">Dexterity's adjustment counts against every area effect, not only flag 0x40, which no record has.</param>
/// <param name="NoStrengthFromAbsorbDisease">Absorb Disease gives no Strength (the original falls through into Strength's handler).</param>
/// <param name="BerserkSideEnds">A Berserk or Confused combatant goes back to its own side when the condition ends (the original keeps the random one).</param>
/// <param name="WallsFromOneCell">A wall cast at one cell runs across the line of fire, its area long (the original's AI gives both ends the same cell, a wall of one point).</param>
/// <param name="FireElementalResistances">The fire elemental (type 32) needs +2 weapons and the lesser one (33) +1, as the clue book has them, both untouched by fire (the original's rows are the wrong way round: <see cref="Battle.FireElementalRows"/>).</param>
/// <param name="BlessFilterFromItsCell">Bless and Aid leave out those of their side with an enemy beside them, measured from where they stand (the original measures from (x, x), a slip, <c>80F5:249D</c>).</param>
/// <param name="OneArmorChargePerWound">Armor loses one charge to a wound, from a blow or a spell (the original takes one when a blow lands, <c>80F5:1999</c>, or a hostile spell does, even one that does no harm, and another for the wound, <c>80F5:0E61</c>).</param>
public sealed record CombatFixes(
    bool MirrorImagesTakeBlows = false, bool NegativePlaneProtectionSaves = false, bool BraveAgainstFear = false,
    bool DexterityAgainstAreas = false, bool NoStrengthFromAbsorbDisease = false, bool BerserkSideEnds = false,
    bool WallsFromOneCell = false, bool BlessFilterFromItsCell = false, bool FireElementalResistances = false,
    bool OneArmorChargePerWound = false)
{
    /// <summary>All of them.</summary>
    public static CombatFixes All { get; } = new(true, true, true, true, true, true, true, true, true, true);
}

/// <summary>
/// One row of a creature's spells per level (<c>6798:0B32</c>; the rows at
/// <c>DS:0678</c>, chosen by class code at <c>DS:0664</c>): for a class in
/// <paramref name="Classes"/>, with v its level (or Wisdom, with
/// <paramref name="Wisdom"/>), a spell level s gets v − (v + parity) / 2 −
/// (s + offset − 1) casts, 2 instead of 1 when v's oddness is
/// <paramref name="Parity"/>'s, 0 … <paramref name="Max"/>.
/// </summary>
public sealed record SpellSlotRow(IReadOnlyList<int> Classes, int Max, int Offset = 0, int Parity = 0, bool Wisdom = false)
{
    public int Slots(int value, int spellLevel)
    {
        int n = value - (value + Parity) / 2 - (spellLevel + Offset - 1);
        if (n == 1 && ((value & 1) ^ Parity) == 0)
        {
            n = 2;
        }
        return Math.Clamp(n, 0, Max);
    }
}

/// <summary>
/// What a condition plainly changes in a fight; those that depend on a level,
/// an alignment or a caster (Barkskin's and Magical Vestments' caps, Prayer,
/// Protection from Evil…) stay in <see cref="Battle"/>.
/// </summary>
/// <param name="ToHit">Added to the to-hit bonus of an attacker with it (<c>80F5:3849</c>): Bless +1, Curse −1, Slow −4.</param>
/// <param name="ToHitAgainst">Added to the to-hit bonus of one attacking a target with it: Blur −2.</param>
/// <param name="ArmorClass">Added to the armour class of one with it (<c>80F5:39B7</c>; lower is better): Displacement −2, Blind +4.</param>
/// <param name="ArmorClassCap">Its armour class is at most this (<c>612E:2113</c>): Spirit Armor 4, Armor 6, Shield 4.</param>
/// <param name="CapFromFrontOnly">The cap counts only when not attacked from behind (Shield).</param>
/// <param name="Save">Added to its saves (<c>80F5:119C</c>): Bless +1, Spirit Armor +3.</param>
/// <param name="NotOnSave">A save (0–4) <paramref name="Save"/> doesn't count on: Spirit Armor's paralysation save (1 in the original's count).</param>
/// <param name="SaveAgainst">Only against effects of these elements: Protection from Lightning +4 against electricity.</param>
/// <param name="Halves">Damage of these elements (with magic) is halved (<c>80F5:28F2</c>): the protections, Fire Shield.</param>
/// <param name="Stops">Damage of these elements alone does nothing: Protection from Energy.</param>
public sealed record ConditionModifier(
    Condition Condition,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int ToHit = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int ToHitAgainst = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int ArmorClass = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int? ArmorClassCap = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool CapFromFrontOnly = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Save = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int? NotOnSave = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] DamageElement SaveAgainst = DamageElement.None,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] DamageElement Halves = DamageElement.None,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] DamageElement Stops = DamageElement.None);

/// <summary>
/// A class's save bonus (<c>80F5:119C</c>): one with levels in a class of
/// <paramref name="Classes"/> (class codes) saves <paramref name="Bonus"/> better
/// against effects of <paramref name="Elements"/> or with spell flags
/// <paramref name="Flags"/>.
/// </summary>
public sealed record ClassSaveBonus(IReadOnlyList<int> Classes, int Bonus, DamageElement Elements = DamageElement.None, int Flags = 0);

/// <summary>
/// The combat numbers and the AI's choices that DSUN.EXE has in its code, not
/// its data (<c>dsun-exe-reference.md</c> §8). <see cref="Default"/> is the
/// original's; a mod can change them with <c>rules/combat.json</c>.
/// </summary>
public sealed record CombatRules
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    /// <summary>The AI's choices, in order (<c>612E:0BAD</c>). Leave one out and the AI never does it.</summary>
    public IReadOnlyList<AiStep> AiSteps { get; init; } =
        [AiStep.TooFar, AiStep.Spells, AiStep.Ability, AiStep.Psionics, AiStep.Attack, AiStep.Adjacent, AiStep.Move, AiStep.Missile, AiStep.Guard];

    /// <summary>How a new target is picked; <see cref="TargetChoice.Random"/> is the original's.</summary>
    public TargetChoice Targeting { get; init; } = TargetChoice.Random;

    /// <summary>How far the AI looks for enemies and still acts against its target (24 cells, <c>612E:0000</c>, <c>1A0A:2BA5</c>).</summary>
    public int SightRange { get; init; } = 24;

    /// <summary>The AI casts spells only with Intelligence above this (combat record +0x25).</summary>
    public int SpellIntelligence { get; init; } = 9;

    /// <summary>A caster this close to its target first steps back (order mode 1).</summary>
    public int StepBackRange { get; init; } = 5;

    /// <summary>Movement points a turn needs left to step back, or to forget its target at the end.</summary>
    public int SpareMovement { get; init; } = 10;

    public WeaponScoring WeaponScore { get; init; } = new();

    /// <summary>Movement points: Move × this a round (<c>612E:0754</c>).</summary>
    public int MovementPerMove { get; init; } = 10;

    /// <summary>A step's cost straight and diagonal (<c>DS:055C</c>).</summary>
    public int StepCost { get; init; } = 10;

    public int DiagonalStepCost { get; init; } = 14;

    /// <summary>Initiative: base + 1d(dice) (0 … dice − 1 in the original) + Dexterity's reaction.</summary>
    public int InitiativeBase { get; init; } = 20;

    public int InitiativeDice { get; init; } = 10;

    /// <summary>To-hit from behind (the target faces away): this much easier, and shields don't count.</summary>
    public int FromBehind { get; init; } = 2;

    /// <summary>A thief's backstab to-hit bonus (instead of <see cref="FromBehind"/>).</summary>
    public int Backstab { get; init; } = 4;

    /// <summary>Hit points a round for <see cref="DarkSun.Game.Rules.CreatureAbilityBit.Regenerates"/>.</summary>
    public int Regeneration { get; init; } = 3;

    /// <summary>The chance in 100 that a hit strikes a mirror image instead (<c>80F5:1999</c>).</summary>
    public int MirrorImageChance { get; init; } = 75;

    /// <summary>
    /// A creature's casts (<c>6798:0059</c>): its spells per level by these rows
    /// (summed over its classes; a human's later classes only below its first's
    /// level), times its hit points ÷ its most, dealt out among its set's spells
    /// a cast at a time. Clerics and druids (codes 1–8) by level and by Wisdom,
    /// preservers, defilers and templars (11, 18, 19), rangers (13–16).
    /// </summary>
    public IReadOnlyList<SpellSlotRow> SpellSlots { get; init; } =
    [
        new([1, 2, 3, 4, 5, 6, 7, 8], 9),
        new([1, 2, 3, 4, 5, 6, 7, 8], 3, Offset: 6, Wisdom: true),
        new([11, 18, 19], 5),
        new([13, 14, 15, 16], 3, Offset: 3, Parity: 1),
    ];

    /// <summary>The chance in 100, + 5 a level the caster has over the effect's, that Dispel Magic ends an effect (<c>80F5:05E6</c>).</summary>
    public int DispelChance { get; init; } = 50;

    /// <summary>The conditions' plain modifiers to hitting, armour class, saves and damage.</summary>
    public IReadOnlyList<ConditionModifier> ConditionModifiers { get; init; } =
    [
        new(Condition.Blessed, ToHit: 1, Save: 1),
        new(Condition.Cursed, ToHit: -1),
        new(Condition.Slowed, ToHit: -4, ArmorClass: 4),
        new(Condition.GraftWeapon, ToHit: 1),
        new(Condition.Blur, ToHitAgainst: -2),
        new(Condition.SpiritArmor, ArmorClassCap: 4, Save: 3, NotOnSave: 1),
        new(Condition.ImprovedAc, ArmorClassCap: 6),
        new(Condition.Shielded, ArmorClassCap: 4, CapFromFrontOnly: true),
        new(Condition.Displacement, ArmorClass: -2),
        new(Condition.Blind, ArmorClass: 4),
        new(Condition.Biofeedback, ArmorClass: -1),
        new(Condition.SavePenalty, Save: -1),
        new(Condition.Barkskin, Save: 1),
        new(Condition.ProtectionFromLightning, Save: 4, SaveAgainst: DamageElement.Electricity, Halves: DamageElement.Electricity),
        new(Condition.ProtectionFromCold, Save: 3, SaveAgainst: DamageElement.Cold, Halves: DamageElement.Cold),
        new(Condition.ProtectionFromFire, Save: 3, SaveAgainst: DamageElement.Fire, Halves: DamageElement.Fire),
        new(Condition.FireShield, Halves: DamageElement.Fire | DamageElement.Cold),
        new(Condition.ProtectionFromEnergy, Stops: DamageElement.Fire | DamageElement.Cold | DamageElement.Electricity | DamageElement.Magic),
    ];

    /// <summary>Druids (class codes 5–8) +2 against fire and electricity; psionicists (12) +2 against mind and charm effects.</summary>
    public IReadOnlyList<ClassSaveBonus> ClassSaveBonuses { get; init; } =
    [
        new([5, 6, 7, 8], 2, DamageElement.Fire | DamageElement.Electricity),
        new([12], 2, Flags: SpellFlags.Mind | SpellFlags.CharmOrHold),
    ];

    /// <summary>
    /// Fixes for what the original does oddly; each is off by default, as
    /// DSUN.EXE has it (<see cref="CombatFixes"/>).
    /// </summary>
    public CombatFixes Fixes { get; init; } = new();

    public static CombatRules Default { get; } = new();

    public static CombatRules Read(string path)
    {
        CombatRules rules;
        try
        {
            rules = JsonSerializer.Deserialize<CombatRules>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("empty file");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException(e.Message, e);
        }
        if (rules.SightRange < 1 || rules.StepCost < 1 || rules.DiagonalStepCost < 1 || rules.InitiativeDice < 1)
        {
            throw new InvalidDataException("sightRange, stepCost, diagonalStepCost and initiativeDice must be at least 1");
        }
        return rules;
    }

    public string ToJson() => SpellRules.Compact(JsonSerializer.Serialize(this, Json));
}
