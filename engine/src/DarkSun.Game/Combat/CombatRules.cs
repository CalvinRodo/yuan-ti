using System.Text.Json;
using System.Text.Json.Serialization;

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

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}
