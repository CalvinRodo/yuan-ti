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

/// <summary>
/// The conditions' names (<c>3F8D:0000</c>, 6 bytes each: the name, then the
/// <c>ICON</c>) and the rank <c>78A9:2170</c> gives each (a jump into a run of
/// increments), by which the status word of an Okay member names its
/// strongest condition (<c>78A9:20A0</c>, <c>78A9:1FBB</c>).
/// </summary>
public static class Conditions
{
    private static readonly string[] Names =
    [
        "", "Acid", "Improved AC", "Berserk", "Biofeedback", "Blink", "", "Blessed", "Blind", "Brave", "Charmed",
        "Confused", "Cursed", "Diseased", "Detect Traps", "Detect Invis", "Enlarged", "Afraid", "Cloak of Fear",
        "Feeblemind", "Fire Shield", "Free Action", "Hasted", "Invisible", "Invis to Undead", "Mirror Images", "Englobed",
        "", "Prot Missile", "Prot Paralysis", "Synaptic Static", "Low Resistance", "Mind Bar", "Can't Attack", "Paralyzed",
        "Poisoned", "Prot Cold", "Prot Energy", "Prot Evil", "Prot Evil 10'", "Prot Fire", "Prot Lightning",
        "Neg Plane Prot", "Gaze Reflection", "Spell Turning", "Save penalty", "Shielded", "Slowed", "Stoneskin",
        "Graft Weapon", "No spell use", "Stuck", "Dispelling evil", "Ironskin", "", "Blur", "Spirit Armor", "Barkskin",
        "Displacement", "Flesh Armor", "Magical Vestments", "Animal Affinity", "Body Weaponry", "Strength Enhanced",
        "Strength Borrowed", "Strength Lent", "Adrenalin Control", "Strength", "Weakened",
    ];

    private static readonly int[] Ranks =
    [
        0, 65, 27, 64, 47, 41, 40, 25, 67, 24, 60, 54, 66, 56, 2, 1, 23, 57, 22, 55, 21, 20, 49, 50, 18, 17, 46, 45, 44, 43,
        16, 53, 15, 62, 63, 68, 14, 13, 12, 11, 10, 9, 8, 7, 6, 52, 26, 58, 48, 33, 59, 61, 5, 32, 4, 19, 29, 28, 42, 30,
        31, 35, 34, 38, 37, 36, 3, 39, 51,
    ];

    /// <summary>The highest condition the original names (and ranks); the engine's own above it have neither.</summary>
    public const Condition Last = Condition.Weakened;

    /// <summary>Condition <paramref name="c"/>'s name, or null if it has none.</summary>
    public static string? Name(Condition c) => c is > Condition.None and <= Last && Names[(int)c] is { Length: > 0 } name ? name : null;

    /// <summary>How strongly <paramref name="c"/> claims the status word (0 none, up to 68: Poisoned).</summary>
    public static int Rank(Condition c) => c is > Condition.None and <= Last ? Ranks[(int)c] : 0;

    /// <summary>
    /// The status word of an Okay member under <paramref name="conditions"/>: the name of
    /// the highest-ranked, the lower-numbered first on a tie; null (the word stays Okay)
    /// for none.
    /// </summary>
    public static string? Strongest(IEnumerable<Condition> conditions) =>
        conditions.Where(c => Name(c) is not null).Distinct().OrderByDescending(Rank).ThenBy(c => c).Select(Name).FirstOrDefault();
}
