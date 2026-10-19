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

public sealed record RaceRules(
    Race Race, string Name, IReadOnlyList<Gender> Genders, IReadOnlyDictionary<Ability, int> Adjustments,
    IReadOnlyList<CharacterClass> Classes, bool MultiClass);

/// <param name="Levels">Experience needed for levels 1, 2, 3…</param>
public sealed record ClassRules(
    CharacterClass Class, string Name, IReadOnlyDictionary<Ability, int> Requirements, IReadOnlyList<Ability> PrimeRequisites,
    int HitDie, bool Warrior, bool ChoosesSphere, IReadOnlyList<int> Levels);

/// <summary>
/// The rules character creation follows. <see cref="Default"/> is the
/// original's, from its manual (pages 19–26 and the tables on 89–92): races
/// and the classes each may take, racial ability adjustments, class
/// requirements, prime requisites, hit dice and experience tables, the
/// alignments a party may have, and the classes that can't be combined. The
/// manual contradicts itself in two places; the stricter reading is taken:
/// half-giants can't be rangers (the ranger's "Races Allowed" leaves them
/// out) and muls and thri-kreen can't be druids (their race descriptions
/// leave it out). DSUN.EXE's own tables aren't traced yet. A mod can replace
/// the rules with a JSON file (<see cref="Read"/>).
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

    /// <summary>Rolled before adjustments: <see cref="RollDice"/> d <see cref="RollSides"/> + <see cref="RollBonus"/> (Dark Sun's 4d4+4), at least <see cref="RollFloor"/>.</summary>
    public int RollDice { get; init; } = 4;

    public int RollSides { get; init; } = 4;

    public int RollBonus { get; init; } = 4;

    /// <summary>The manual: "The possible values range from 9 (low) to 24 (high)".</summary>
    public int RollFloor { get; init; } = 9;

    /// <summary>The highest an ability may be raised before adjustments (adjusted, it is this plus the race's adjustment).</summary>
    public int UnadjustedMax { get; init; } = 20;

    /// <summary>"Your party characters must be good or neutral".</summary>
    public IReadOnlyList<Alignment> Alignments { get; init; } =
        [Alignment.LawfulGood, Alignment.LawfulNeutral, Alignment.NeutralGood, Alignment.TrueNeutral, Alignment.ChaoticGood, Alignment.ChaoticNeutral];

    /// <summary>Classes that can't be taken together ("a character cannot be a cleric and a druid at the same time").</summary>
    public IReadOnlyList<IReadOnlyList<CharacterClass>> Exclusive { get; init; } = [[CharacterClass.Cleric, CharacterClass.Druid]];

    public int MaxClasses { get; init; } = 3;

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
                A((Ability.Str, 4), (Ability.Con, 2), (Ability.Int, -2), (Ability.Wis, -2), (Ability.Cha, -2)),
                [CharacterClass.Cleric, CharacterClass.Fighter, CharacterClass.Gladiator, CharacterClass.Psionicist], true),
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
            new(CharacterClass.Cleric, "Cleric", A((Ability.Wis, 9)), [Ability.Wis], 8, false, true,
                [0, 1500, 3000, 6000, 13000, 27500, 55000, 110000, 225000]),
            new(CharacterClass.Druid, "Druid", A((Ability.Wis, 12), (Ability.Cha, 15)), [Ability.Wis, Ability.Cha], 8, false, true,
                [0, 1500, 3000, 6000, 13000, 27500, 55000, 110000, 225000]),
            new(CharacterClass.Fighter, "Fighter", A((Ability.Str, 9)), [Ability.Str], 10, true, false,
                [0, 2000, 4000, 8000, 16000, 32000, 64000, 125000, 250000]),
            new(CharacterClass.Gladiator, "Gladiator", A((Ability.Dex, 12), (Ability.Str, 13), (Ability.Con, 15)), [Ability.Str], 10, true, false,
                [0, 2250, 4500, 9000, 18000, 36000, 75000, 150000, 300000]),
            new(CharacterClass.Preserver, "Preserver", A((Ability.Int, 9)), [Ability.Int], 4, false, false,
                [0, 2500, 5000, 10000, 20000, 40000, 60000, 90000, 135000]),
            new(CharacterClass.Psionicist, "Psionicist", A((Ability.Con, 11), (Ability.Int, 12), (Ability.Wis, 15)), [Ability.Con, Ability.Wis], 6, false, false,
                [0, 2200, 4400, 8800, 16500, 30000, 55000, 100000, 200000]),
            new(CharacterClass.Ranger, "Ranger", A((Ability.Str, 13), (Ability.Dex, 13), (Ability.Wis, 14), (Ability.Con, 14)), [Ability.Str, Ability.Dex, Ability.Wis], 10, true, true,
                [0, 2250, 4500, 9000, 18000, 36000, 75000, 150000, 300000]),
            new(CharacterClass.Thief, "Thief", A((Ability.Dex, 9)), [Ability.Dex], 6, false, false,
                [0, 1250, 2500, 5000, 10000, 20000, 40000, 70000, 110000]),
        ],
    };
}
