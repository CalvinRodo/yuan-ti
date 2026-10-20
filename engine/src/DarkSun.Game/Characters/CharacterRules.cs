using System.Text.Json;
using System.Text.Json.Serialization;

namespace DarkSun.Game.Characters;

/// <summary>The races, in DSUN.EXE's order (its string table; body portraits <c>BMP</c> 20000 + race × 2 + gender).</summary>
public enum Race { Human, Dwarf, Elf, HalfElf, HalfGiant, Halfling, Mul, ThriKreen }

public enum Gender { Male, Female }

/// <summary>The classes, in DSUN.EXE's order (alphabetical; the class list's <c>ICON</c>s 2002–2009).</summary>
public enum CharacterClass { Cleric, Druid, Fighter, Gladiator, Preserver, Psionicist, Ranger, Thief }

/// <summary>Strength, Dexterity, Constitution, Intelligence, Wisdom, Charisma, in that order.</summary>
public enum Ability { Str, Dex, Con, Int, Wis, Cha }

public enum Alignment { LawfulGood, LawfulNeutral, LawfulEvil, NeutralGood, TrueNeutral, NeutralEvil, ChaoticGood, ChaoticNeutral, ChaoticEvil }

/// <summary>The psionic disciplines (<c>ICON</c> 2038–2040).</summary>
public enum Discipline { Psychokinesis, Psychometabolism, Telepathy }

/// <summary>The elemental spheres (<c>ICON</c> 2042–2045).</summary>
public enum Sphere { Air, Earth, Fire, Water }

public sealed record WisdomBonus(int Wisdom, int Level);

public sealed record SpellbookSize(int Intelligence, int Spells);

/// <param name="HitPointFactor">Hit points are multiplied by this (DSUN.EXE doubles half-giants', <c>6ECD:0156</c>).</param>
public sealed record RaceRules(
    Race Race, string Name, IReadOnlyList<Gender> Genders, IReadOnlyDictionary<Ability, int> Adjustments,
    IReadOnlyList<CharacterClass> Classes, bool MultiClass, int HitPointFactor = 1);

/// <param name="Levels">Experience needed for levels 1, 2, 3…</param>
public sealed record ClassRules(
    CharacterClass Class, string Name, IReadOnlyDictionary<Ability, int> Requirements, IReadOnlyList<Ability> PrimeRequisites,
    int HitDie, bool Warrior, bool ChoosesSphere, IReadOnlyList<int> Levels);

/// <summary>
/// The rules character creation follows. <see cref="Default"/> is the
/// original's: DSUN.EXE's own tables where they are traced (segment
/// <c>38D4</c>, read by the creation code in <c>6CCA</c> and <c>6ECD</c>;
/// <c>dsun-exe-reference.md</c>, "Character creation"), the manual (pages
/// 19–26, 89–92) for the rest:
/// <list type="bullet">
/// <item>from DSUN.EXE: the racial adjustments (<c>38D4:014D</c>; they match the
/// manual but for the half-giant's), the ability roll (best of four 4d4+4,
/// <c>6CCA:16BD</c>) and limits (20 + the adjustment; at least 17 in each
/// class's key ability and the class's floor in the others, <c>6ECD:1F19</c>
/// with <c>38D4:0180</c>), the hit dice (<c>38D4:014A</c>) and hit points
/// (<c>6ECD:0156</c>), the starting experience (3000 in each class, the new
/// character template at <c>38D4:0048</c>) and the map icons (<c>OJFF</c> 300–313);</item>
/// <item>from the manual: the classes each race may take, prime requisites,
/// the experience tables, the alignments a party may have, and that cleric
/// and druid don't combine. Where it contradicts itself the stricter reading
/// is taken: half-giants can't be rangers and muls and thri-kreen can't be
/// druids.</item>
/// </list>
/// A mod can replace the rules with a JSON file (<see cref="Read"/>).
/// </summary>
public sealed record CharacterRules
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public required IReadOnlyList<RaceRules> Races { get; init; }

    public required IReadOnlyList<ClassRules> Classes { get; init; }

    /// <summary>Lowest and highest an ability may be, after racial adjustments.</summary>
    public int AbilityMin { get; init; } = 3;

    public int AbilityMax { get; init; } = 24;

    /// <summary>
    /// Rolled: <see cref="RollDice"/> d <see cref="RollSides"/> + <see cref="RollBonus"/>
    /// (4d4+4) <see cref="RollTries"/> times, the best kept (<c>6CCA:16BD</c>), at least
    /// <see cref="RollFloor"/> (DSUN.EXE has none of its own: the class minimums do it).
    /// </summary>
    public int RollDice { get; init; } = 4;

    public int RollSides { get; init; } = 4;

    public int RollBonus { get; init; } = 4;

    public int RollTries { get; init; } = 4;

    public int RollFloor { get; init; }

    /// <summary>Experience a new character has in each of its classes (DSUN.EXE's template, <c>38D4:0048</c>: 3000).</summary>
    public int StartingExperience { get; init; } = 3000;

    /// <summary>The map icons: <c>OJFF</c> this + the look's place in the portrait order (300–313, all walking with <c>SCMD</c> 174).</summary>
    public int IconBase { get; init; } = 300;

    /// <summary>The highest an ability may be raised before adjustments (adjusted, it is this plus the race's adjustment).</summary>
    public int UnadjustedMax { get; init; } = 20;

    /// <summary>"Your party characters must be good or neutral".</summary>
    public IReadOnlyList<Alignment> Alignments { get; init; } =
        [Alignment.LawfulGood, Alignment.LawfulNeutral, Alignment.NeutralGood, Alignment.TrueNeutral, Alignment.ChaoticGood, Alignment.ChaoticNeutral];

    /// <summary>Classes that can't be taken together ("a character cannot be a cleric and a druid at the same time").</summary>
    public IReadOnlyList<IReadOnlyList<CharacterClass>> Exclusive { get; init; } = [[CharacterClass.Cleric, CharacterClass.Druid]];

    public int MaxClasses { get; init; } = 3;

    /// <summary>A human may dual-class from this level in the current class ("at least third level", the manual).</summary>
    public int DualLevel { get; init; } = 3;

    /// <summary>The current class's prime requisites must be this or more to dual-class (AD&amp;D 2nd edition).</summary>
    public int DualFromPrime { get; init; } = 15;

    /// <summary>The new class's prime requisites must be this or more (AD&amp;D 2nd edition).</summary>
    public int DualToPrime { get; init; } = 17;

    // -- spells and psionics (Magic.Spellcasting): AD&D 2nd edition's, where DSUN.EXE's aren't traced --

    /// <summary>Wizard spells per day: row n is preserver level n + 1, column m spell level m + 1.</summary>
    public IReadOnlyList<IReadOnlyList<int>> WizardSlots { get; init; } =
    [
        [1], [2], [2, 1], [3, 2], [4, 2, 1], [4, 2, 2], [4, 3, 2, 1], [4, 3, 3, 2], [4, 3, 3, 2, 1], [4, 4, 3, 2, 2],
        [4, 4, 4, 3, 3], [4, 4, 4, 4, 4],
    ];

    /// <summary>Priest spells per day by cleric or druid level.</summary>
    public IReadOnlyList<IReadOnlyList<int>> PriestSlots { get; init; } =
    [
        [1], [2], [2, 1], [3, 2], [3, 3, 1], [3, 3, 2], [3, 3, 2, 1], [3, 3, 3, 2], [4, 4, 3, 2, 1], [4, 4, 3, 3, 2],
        [5, 4, 4, 3, 2], [6, 5, 5, 3, 2],
    ];

    /// <summary>A ranger's priest spells by ranger level (none before the 8th).</summary>
    public IReadOnlyList<IReadOnlyList<int>> RangerSlots { get; init; } =
    [
        [], [], [], [], [], [], [], [1], [2], [2, 1], [2, 2], [2, 2, 1],
    ];

    /// <summary>A priest's extra spells for Wisdom: one of <c>Level</c> from <c>Wisdom</c> up (cumulative).</summary>
    public IReadOnlyList<WisdomBonus> WisdomBonusSpells { get; init; } =
        [new(13, 1), new(14, 1), new(15, 2), new(16, 2), new(17, 3), new(18, 4), new(19, 1), new(19, 3), new(20, 2), new(20, 4), new(21, 3), new(21, 5)];

    /// <summary>How many spells of each level a preserver's spellbook holds, by Intelligence.</summary>
    public IReadOnlyList<SpellbookSize> SpellbookSizes { get; init; } =
        [new(9, 6), new(10, 7), new(13, 9), new(15, 11), new(17, 14), new(18, 18), new(19, 99)];

    /// <summary>
    /// A psionicist's psionic strength points by level; past the table,
    /// <see cref="PspPerLevelAfter"/> more each level. (DSUN.EXE's
    /// <c>38D4:013B</c> holds 20, 22, … 34, likely this; not traced.)
    /// </summary>
    public IReadOnlyList<int> PsionicistPsp { get; init; } = [20, 22, 24, 26, 28, 30, 32, 34];

    public int PspPerLevelAfter { get; init; } = 2;

    /// <summary>Psionic strength points of a character who isn't a psionicist (its wild talent).</summary>
    public int WildTalentPsp { get; init; } = 10;

    /// <summary>The powers a first-level psionicist knows (one more each level; others know one).</summary>
    public int PowersAtFirstLevel { get; init; } = 3;

    /// <summary>Psionic strength points a power costs.</summary>
    public int PowerCost { get; init; } = 5;

    public RaceRules Race(Race race) => Races.First(r => r.Race == race);

    public ClassRules Class(CharacterClass c) => Classes.First(r => r.Class == c);

    /// <summary>Constitution's hit point bonus (AD&amp;D 2nd edition): +1 at 15, +2 at 16, and for warriors +3 at 17 and +4 at 18 (others stay at +2), +5 at 19 and above.</summary>
    public static int ConstitutionBonus(int con, bool warrior) => con switch
    {
        >= 19 => 5,
        18 => warrior ? 4 : 2,
        17 => warrior ? 3 : 2,
        16 => 2,
        15 => 1,
        <= 3 => -2,
        <= 6 => -1,
        _ => 0,
    };

    public static CharacterRules Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<CharacterRules>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("empty file");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException(e.Message, e);
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    private static Dictionary<Ability, int> A(params (Ability, int)[] values) => values.ToDictionary(v => v.Item1, v => v.Item2);

    private static readonly CharacterClass[] All = Enum.GetValues<CharacterClass>();

    /// <summary>
    /// A class's ability minimums as DSUN.EXE has them (<c>6ECD:1F19</c>, table
    /// <c>38D4:0180</c>: a key ability and a number per class): 17 in the key
    /// ability, the class's number in every other. (The manual lists fewer and
    /// lower requirements; the number is its minimum for the key ability.)
    /// </summary>
    private static Dictionary<Ability, int> Minimums(Ability key, int floor) =>
        Enum.GetValues<Ability>().ToDictionary(a => a, a => a == key ? KeyAbilityMinimum : floor);

    /// <summary>The least a class's key ability may be (<c>6ECD:1F19</c>).</summary>
    public const int KeyAbilityMinimum = 17;

    public static CharacterRules Default { get; } = new()
    {
        Races =
        [
            new(Characters.Race.Human, "Human", [Gender.Male, Gender.Female], A(), All, MultiClass: false),
            new(Characters.Race.Dwarf, "Dwarf", [Gender.Male, Gender.Female],
                A((Ability.Con, 2), (Ability.Str, 1), (Ability.Dex, -1), (Ability.Cha, -2)),
                [CharacterClass.Cleric, CharacterClass.Fighter, CharacterClass.Gladiator, CharacterClass.Psionicist, CharacterClass.Thief], true),
            new(Characters.Race.Elf, "Elf", [Gender.Male, Gender.Female],
                A((Ability.Dex, 2), (Ability.Int, 1), (Ability.Wis, -1), (Ability.Con, -2)),
                [CharacterClass.Cleric, CharacterClass.Fighter, CharacterClass.Gladiator, CharacterClass.Preserver, CharacterClass.Psionicist, CharacterClass.Ranger, CharacterClass.Thief], true),
            new(Characters.Race.HalfElf, "Half-elf", [Gender.Male, Gender.Female],
                A((Ability.Dex, 1), (Ability.Con, -1)),
                All, true),
            new(Characters.Race.HalfGiant, "Half-giant", [Gender.Male, Gender.Female],
                // DSUN.EXE's (38D4:016B); the manual has Str +4, Con +2, Int −2, Wis −2, Cha −2.
                A((Ability.Str, 4), (Ability.Dex, -5), (Ability.Con, 2), (Ability.Int, -5), (Ability.Wis, -3), (Ability.Cha, -3)),
                [CharacterClass.Cleric, CharacterClass.Fighter, CharacterClass.Gladiator, CharacterClass.Psionicist], true, HitPointFactor: 2),
            new(Characters.Race.Halfling, "Halfling", [Gender.Male, Gender.Female],
                A((Ability.Dex, 2), (Ability.Wis, 2), (Ability.Con, -1), (Ability.Cha, -1), (Ability.Str, -2)),
                [CharacterClass.Cleric, CharacterClass.Druid, CharacterClass.Fighter, CharacterClass.Gladiator, CharacterClass.Psionicist, CharacterClass.Ranger, CharacterClass.Thief], true),
            new(Characters.Race.Mul, "Mul", [Gender.Male],
                A((Ability.Str, 2), (Ability.Con, 1), (Ability.Int, -1), (Ability.Cha, -2)),
                [CharacterClass.Cleric, CharacterClass.Fighter, CharacterClass.Gladiator, CharacterClass.Psionicist, CharacterClass.Thief], true),
            new(Characters.Race.ThriKreen, "Thri-kreen", [Gender.Female],
                A((Ability.Dex, 2), (Ability.Wis, 1), (Ability.Int, -1), (Ability.Cha, -2)),
                [CharacterClass.Cleric, CharacterClass.Fighter, CharacterClass.Gladiator, CharacterClass.Psionicist, CharacterClass.Ranger], true),
        ],
        Classes =
        [
            new(CharacterClass.Cleric, "Cleric", Minimums(Ability.Wis, 9), [Ability.Wis], 8, false, true,
                [0, 1500, 3000, 6000, 13000, 27500, 55000, 110000, 225000]),
            new(CharacterClass.Druid, "Druid", Minimums(Ability.Wis, 12), [Ability.Wis, Ability.Cha], 8, false, true,
                [0, 1500, 3000, 6000, 13000, 27500, 55000, 110000, 225000]),
            new(CharacterClass.Fighter, "Fighter", Minimums(Ability.Str, 9), [Ability.Str], 10, true, false,
                [0, 2000, 4000, 8000, 16000, 32000, 64000, 125000, 250000]),
            new(CharacterClass.Gladiator, "Gladiator", Minimums(Ability.Str, 13), [Ability.Str], 10, true, false,
                [0, 2250, 4500, 9000, 18000, 36000, 75000, 150000, 300000]),
            new(CharacterClass.Preserver, "Preserver", Minimums(Ability.Int, 9), [Ability.Int], 4, false, false,
                [0, 2500, 5000, 10000, 20000, 40000, 60000, 90000, 135000]),
            new(CharacterClass.Psionicist, "Psionicist", Minimums(Ability.Wis, 12), [Ability.Con, Ability.Wis], 6, false, false,
                [0, 2200, 4400, 8800, 16500, 30000, 55000, 100000, 200000]),
            new(CharacterClass.Ranger, "Ranger", Minimums(Ability.Wis, 14), [Ability.Str, Ability.Dex, Ability.Wis], 10, true, true,
                [0, 2250, 4500, 9000, 18000, 36000, 75000, 150000, 300000]),
            new(CharacterClass.Thief, "Thief", Minimums(Ability.Dex, 9), [Ability.Dex], 6, false, false,
                [0, 1250, 2500, 5000, 10000, 20000, 40000, 70000, 110000]),
        ],
    };
}
