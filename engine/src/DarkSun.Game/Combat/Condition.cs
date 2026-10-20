namespace DarkSun.Game.Combat;

/// <summary>
/// The conditions an effect can give (spell record +0x12), bit n of the
/// 10-byte set <c>7D80:1864</c> builds; named from <c>3F8D:0000</c>
/// (<c>dsun-exe-reference.md</c> §8, "Conditions").
/// </summary>
public enum Condition
{
    None = 0,
    Acid = 1, ImprovedAc = 2, Berserk = 3, Biofeedback = 4, Blink = 5, Blessed = 7, Blind = 8, Brave = 9,
    Charmed = 10, Confused = 11, Cursed = 12, Diseased = 13, DetectTraps = 14, DetectInvisible = 15, Enlarged = 16,
    Afraid = 17, CloakOfFear = 18, Feebleminded = 19, FireShield = 20, FreeAction = 21, Hasted = 22, Invisible = 23,
    InvisibleToUndead = 24, MirrorImages = 25, Englobed = 26, ProtectionFromMissiles = 28, ProtectionFromParalysis = 29,
    SynapticStatic = 30, LowResistance = 31, MindBar = 32, CantAttack = 33, Paralyzed = 34, Poisoned = 35,
    ProtectionFromCold = 36, ProtectionFromEnergy = 37, ProtectionFromEvil = 38, ProtectionFromEvil10 = 39,
    ProtectionFromFire = 40, ProtectionFromLightning = 41, NegativePlaneProtection = 42, GazeReflection = 43,
    SpellTurning = 44, SavePenalty = 45, Shielded = 46, Slowed = 47, Stoneskin = 48, GraftWeapon = 49, NoSpells = 50,
    Stuck = 51, DispellingEvil = 52, Ironskin = 53, Blur = 55, SpiritArmor = 56, Barkskin = 57, Displacement = 58,
    FleshArmor = 59, MagicalVestments = 60, AnimalAffinity = 61, BodyWeaponry = 62, StrengthEnhanced = 63,
    StrengthBorrowed = 64, StrengthLent = 65, AdrenalinControl = 66, Strength = 67, Weakened = 68, ExtraHitPoints = 69,
    FlameBlade = 70, SpiritualHammer = 71, Shillelagh = 72, Prayer = 73,
}

/// <summary>
/// An effect on a combatant (the effect list <c>3BF6:0106</c>, 10 bytes an
/// entry): from spell record <see cref="Effect"/>, giving <see cref="Condition"/>.
/// It ends when the round clock passes <see cref="EndsAt"/> or, for an effect
/// with charges (unit −1), when <see cref="Charges"/> run out (<c>7D80:1362</c>,
/// <c>7D80:1723</c>); <see cref="Permanent"/> ones last the fight. One from a
/// field (<see cref="Field"/>) with no time of its own lasts while its holder
/// stays in the field.
/// </summary>
public sealed class ActiveEffect(int effect, Condition condition, int level, Combatant? caster)
{
    public int Effect { get; } = effect;

    public Condition Condition { get; } = condition;

    public int Level { get; } = level;

    public Combatant? Caster { get; } = caster;

    /// <summary>The round clock (game seconds) at which it ends, for a timed effect.</summary>
    public int? EndsAt { get; set; }

    /// <summary>Uses left, for an effect with charges.</summary>
    public int Charges { get; set; }

    /// <summary>What it adds (effect entry +9): Strength's d6, Weakened's −1, Aid's hit points.</summary>
    public int Value { get; init; }

    /// <summary>The field that gave it, if any.</summary>
    public AreaField? Field { get; init; }

    public bool Permanent => EndsAt is null && Charges == 0;
}
