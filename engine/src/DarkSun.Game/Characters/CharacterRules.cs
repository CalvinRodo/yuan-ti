using System.Text.Json;
using System.Text.Json.Nodes;
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
/// <param name="Move">Movement in a fight (character record +0x28, combat record +0x1B; <c>6ECD:1344</c>): 12, a thri-kreen 15.</param>
/// <param name="NaturalAttacks">Its attacks without a weapon (+0x2B …): a thri-kreen's claws and bite; null: <see cref="CharacterRules.UnarmedDice"/>.</param>
/// <param name="ConstitutionSaves">Saves Con × 2 ÷ 7 better (<c>8E77:059B</c>: dwarves and halflings).</param>
/// <param name="ThiefSkills">Its adjustment to each of the eight thief skills (<c>3FAA:0008 + 8 · race</c>; <see cref="CharacterRules.ThiefSkills"/>); null: none.</param>
/// <param name="ArmorClass">Its armour class before Dexterity and armour (character record +0x27, set when a character is made: 10, a thri-kreen 5).</param>
/// <param name="WeaponsOnly">It wears nothing but weapons and their ammunition: a thri-kreen (no armour, shield, helm, ring …).</param>
public sealed record RaceRules(
    Race Race, string Name, IReadOnlyList<Gender> Genders, IReadOnlyDictionary<Ability, int> Adjustments,
    IReadOnlyList<CharacterClass> Classes, bool MultiClass, int HitPointFactor = 1, int Move = 12,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<DarkSun.Data.Regions.NaturalAttack>? NaturalAttacks = null,
    bool ConstitutionSaves = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<int>? ThiefSkills = null,
    int ArmorClass = 10, bool WeaponsOnly = false);

/// <summary>
/// A thief skill (<c>3FAA:0000</c>, read by <c>87B3:030B</c>): <paramref name="Base"/>
/// + 4 a thief level + the race's adjustment; Dexterity below
/// <paramref name="DexBelow"/> −5 a point, above <paramref name="DexAbove"/> +5
/// a point, above <paramref name="DexWellAbove"/> −3 a point; less
/// <paramref name="GearPenalty"/> with anything in <see cref="CharacterRules.GearSlots"/>.
/// </summary>
public sealed record ThiefSkill(string Name, int Base, int DexBelow, int DexAbove, int DexWellAbove, int GearPenalty);

/// <summary>
/// A saving throw of a class (<c>40B1:006C + 15·group + 3·save</c>): <paramref name="Base"/>
/// at level 1, less <paramref name="Rate"/> ÷ 100 a level after, by at most <paramref name="Max"/>.
/// </summary>
public sealed record SaveRow(int Base, int Rate, int Max)
{
    public int At(int level) => Base - Math.Min(Max, Rate * (Math.Max(1, level) - 1) / 100);
}

/// <summary>From <paramref name="WarriorLevel"/> in a warrior class on, <paramref name="Halves"/> attacks a round in halves (<c>8E77:038F</c>).</summary>
public sealed record AttackStep(int WarriorLevel, int Halves);

/// <param name="Levels">
/// Experience needed for levels 1, 2, 3… DSUN.EXE's table (<c>38F0:027C</c>, in hundreds;
/// a row per class by <c>38F0:00CA</c>[code]) checked by <c>8E77:08A6</c>; levels go up to 9.
/// </param>
/// <param name="Thac0Factor">THAC0 is 20 − (level − 1) × this ÷ 12 (<c>8E77:0416</c>, <c>40B1:0000</c>): warriors 12, priests 8, rogues and psionicists 6, wizards 4.</param>
/// <param name="Saves">Its five saving throws (paralysation, rod, petrification, breath, spell; <c>8E77:059B</c>).</param>
/// <param name="ArmorBonusLevel">
/// From this level on the class improves its armour class by 1 (0: never): the gladiator's at 5 (the
/// guide: "Gladiators receive -1 AC at Level 5"; DSUN.EXE's code for it isn't traced).
/// </param>
public sealed record ClassRules(
    CharacterClass Class, string Name, IReadOnlyDictionary<Ability, int> Requirements, IReadOnlyList<Ability> PrimeRequisites,
    int HitDie, bool Warrior, bool ChoosesSphere, IReadOnlyList<int> Levels, int Thac0Factor = 12, IReadOnlyList<SaveRow>? Saves = null,
    int ArmorBonusLevel = 0)
{
    public int Thac0(int level) => 20 - (Math.Max(1, level) - 1) * Thac0Factor / 12;
}

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
/// <item>also from DSUN.EXE: the experience tables (<c>38F0:027C</c>), spells
/// per day (<c>6798:0B32</c>'s formula; <see cref="WizardSlots"/>), Constitution's
/// hit points (<c>40B1:0052</c>), saves and THAC0 (<c>40B1</c>) and the ability
/// tables (<c>DS:07A8</c>);</item>
/// <item>from the manual, not yet traced in DSUN.EXE: the classes each race may
/// take, prime requisites, the alignments a party may have, and that cleric
/// and druid don't combine. Where it contradicts itself the stricter reading
/// is taken: half-giants can't be rangers and muls and thri-kreen can't be
/// druids. The dual-class requirements and psionic strength are AD&amp;D 2nd
/// edition's (see their fields).</item>
/// </list>
/// A mod can replace the rules with a JSON file (<see cref="Read"/>); the
/// tabletop rules that come with the game (<c>engine/game/mods/Tabletop</c>) are one.
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

    /// <summary>
    /// The wizard spells a new preserver knows, by its preserver level (<c>6ECD:1977</c>, set again
    /// whenever creation changes that level): level 1 Grease and Magic Missile; 2 adds Shield and Wall
    /// of Fog; 3 adds Fog Cloud and Stinking Cloud. A level past the list keeps them all (DSUN.EXE's
    /// switch gives none past level 3, which its starting experience never reaches).
    /// </summary>
    public IReadOnlyList<IReadOnlyList<int>> PreserverStartingSpells { get; init; } = [[8, 9], [10, 12], [16, 23]];

    /// <summary>
    /// The psionic powers a new character who isn't a psionicist knows, by its discipline
    /// (<c>6ECD:1871</c>, 11 power indexes a discipline at <c>38D4:0006</c>, −1 for none; here
    /// <c>SPIN</c> numbers, 139 + index): four of the discipline's and power 169, the same for all.
    /// Nothing is chosen: DSUN.EXE forgets every power and teaches these.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<int>> DisciplineStartingPowers { get; init; } =
        [[139, 142, 143, 144, 169], [147, 150, 156, 155, 169], [162, 164, 170, 166, 169]];

    /// <summary>
    /// A new psionicist's powers by its psionicist level (<c>6ECD:1871</c>, <c>38D4:001C</c> + 11 × level).
    /// The table ends at level 3, which its starting experience never passes; a level past it keeps the last.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<int>> PsionicistStartingPowers { get; init; } =
        [[139, 147, 162, 144, 169], [139, 147, 162, 144, 170], [139, 142, 143, 147, 150, 156, 162, 164, 170, 144, 169]];

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

    /// <summary>The current class's prime requisites must be this or more to dual-class (AD&amp;D 2nd edition; DSUN.EXE's checks aren't traced).</summary>
    public int DualFromPrime { get; init; } = 15;

    /// <summary>The new class's prime requisites must be this or more (AD&amp;D 2nd edition).</summary>
    public int DualToPrime { get; init; } = 17;

    // -- spells and psionics (Magic.Spellcasting) --

    /// <summary>
    /// Wizard spells per day: row n is preserver level n + 1, column m spell level m + 1.
    /// DSUN.EXE works them out (<c>6798:0B32</c>, profiles <c>DS:0664</c> / <c>DS:0678</c>): for
    /// a profile of a maximum, an offset and a parity, n = level − (level + parity) ÷ 2 −
    /// (spell level + offset − 1), 2 where that is 1 and level and parity agree, held to
    /// 0 … maximum. Preservers: maximum 5; clerics and druids: 9; rangers: 3, offset 3,
    /// parity 1. These tables are that formula's.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<int>> WizardSlots { get; init; } =
    [
        [1], [2], [2, 1], [2, 2], [3, 2, 1], [3, 2, 2], [4, 3, 2, 1], [4, 3, 2, 2], [5, 4, 3, 2, 1], [5, 4, 3, 2, 2],
        [5, 5, 4, 3, 2], [5, 5, 4, 3, 2],
    ];

    /// <summary>Priest spells per day by cleric or druid level (<see cref="WizardSlots"/>' formula, maximum 9).</summary>
    public IReadOnlyList<IReadOnlyList<int>> PriestSlots { get; init; } =
    [
        [1], [2], [2, 1], [2, 2], [3, 2, 1], [3, 2, 2], [4, 3, 2, 1], [4, 3, 2, 2], [5, 4, 3, 2, 1], [5, 4, 3, 2, 2],
        [6, 5, 4, 3, 2], [6, 5, 4, 3, 2],
    ];

    /// <summary>A ranger's priest spells by ranger level (none before the 8th; added to a cleric's or druid's).</summary>
    public IReadOnlyList<IReadOnlyList<int>> RangerSlots { get; init; } =
    [
        [], [], [], [], [], [], [], [1], [2], [2, 1], [2, 2], [3, 2, 1],
    ];

    /// <summary>
    /// A cleric's or druid's extra spells for Wisdom: one of <c>Level</c> from <c>Wisdom</c>
    /// up (cumulative). DSUN.EXE's is a second profile of the same formula, on Wisdom
    /// (maximum 3, offset 6; <c>6798:071C</c> passes the Wisdom).
    /// </summary>
    public IReadOnlyList<WisdomBonus> WisdomBonusSpells { get; init; } =
    [
        new(13, 1), new(14, 1), new(15, 2), new(16, 2), new(17, 1), new(17, 3), new(18, 3), new(19, 2), new(19, 4), new(20, 4),
        new(21, 3), new(21, 5), new(22, 5), new(23, 4), new(25, 5),
    ];

    /// <summary>
    /// The Wisdom bonus only adds to spell levels the class's level already gives
    /// (AD&amp;D 2nd edition). DSUN.EXE adds the two profiles, so a wise priest has
    /// slots of a level before the class level gives them; false: as the original.
    /// </summary>
    public bool WisdomBonusNeedsSlots { get; init; }

    /// <summary>
    /// How many spells of each level a preserver's spellbook holds, by Intelligence;
    /// none listed: no limit. DSUN.EXE has no limit (no code reads Intelligence for it).
    /// </summary>
    public IReadOnlyList<SpellbookSize> SpellbookSizes { get; init; } = [];

    /// <summary>
    /// A psionicist's psionic strength points by level; past the table,
    /// <see cref="PspPerLevelAfter"/> more each level (AD&amp;D 2nd edition's). Not yet
    /// DSUN.EXE's: <c>38D4:013B</c> holds 20, 22, … 34; <c>6698:0B8A</c> works points out as
    /// 2 × Wis − 10 + (Int − 15) + (Con − 15) (each if above 15) + (Wis − 5) × the
    /// psionicist level (1 for others), and <c>8E77:0162</c> adds 10 a level on a
    /// level-up; which applies when isn't traced.
    /// </summary>
    public IReadOnlyList<int> PsionicistPsp { get; init; } = [20, 22, 24, 26, 28, 30, 32, 34];

    public int PspPerLevelAfter { get; init; } = 2;

    /// <summary>Psionic strength points of a character who isn't a psionicist (its wild talent).</summary>
    public int WildTalentPsp { get; init; } = 10;

    /// <summary>Psionic strength points a power costs.</summary>
    public int PowerCost { get; init; } = 5;

    /// <summary>Strength's damage, Dexterity's defence and the other ability tables (<c>DS:07A8</c>).</summary>
    public AbilityTables Abilities { get; init; } = AbilityTables.Default;

    /// <summary>
    /// Constitution's hit points a level, by score 0–25, for warriors' levels
    /// (<see cref="WarriorConstitutionHitPoints"/>) and the others'. DSUN.EXE has one
    /// table (<c>40B1:0052</c>) and holds other classes' levels to +2 (<c>8E77:032C</c>).
    /// </summary>
    public IReadOnlyList<int> ConstitutionHitPoints { get; init; } =
        [-3, -3, -2, -2, -1, -1, -1, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2];

    public IReadOnlyList<int> WarriorConstitutionHitPoints { get; init; } =
        [-3, -3, -2, -2, -1, -1, -1, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4, 5, 5, 6, 6, 6, 7, 7];

    /// <summary>Attacks a round in halves: 2, then by a warrior class's level (<c>8E77:038F</c>).</summary>
    public int BaseAttackHalves { get; init; } = 2;

    public IReadOnlyList<AttackStep> AttackSteps { get; init; } = [new(1, 3), new(7, 4), new(13, 5)];

    /// <summary>A blow without a weapon (the new-character template, <c>38D4:0048</c>): its attacks a round of 1d1.</summary>
    public int UnarmedDice { get; init; } = 1;

    public int UnarmedSides { get; init; } = 1;

    /// <summary>
    /// <see cref="RaceRules.ConstitutionSaves"/> counts only on a warrior class's
    /// saves, all five, as DSUN.EXE has it (its test is on the class group,
    /// <c>8E77:064D</c>); false: on every class's.
    /// </summary>
    public bool ConstitutionSavesWarriorsOnly { get; init; } = true;

    /// <summary>
    /// The thief skills, in AD&amp;D's order (pick pockets, open locks, find/remove
    /// traps, move silently, hide in shadows, hear noise, climb walls, read
    /// languages; <c>REQUEST(3)</c>'s numbers 0–7), DSUN.EXE's (<c>3FAA:0000</c>).
    /// </summary>
    public IReadOnlyList<ThiefSkill> ThiefSkills { get; init; } =
    [
        new("Pick pockets", 28, 12, 16, 21, 5),
        new("Open locks", 18, 11, 15, 20, 0),
        new("Find/remove traps", 13, 12, 17, 21, 0),
        new("Move silently", 28, 13, 16, 19, 10),
        new("Hide in shadows", 18, 11, 16, 19, 5),
        new("Hear noise", 23, 0, 25, 25, 0),
        new("Climb walls", 78, 0, 25, 25, 10),
        new("Read languages", -4, 0, 25, 25, 0),
    ];

    public int ThiefSkillPerLevel { get; init; } = 4;

    /// <summary>
    /// Anything worn in these slots costs <see cref="ThiefSkill.GearPenalty"/>
    /// (<c>3FB7:0000</c>: legs, ammunition, either hand); what the item is
    /// doesn't matter (<c>87B3:0672</c>).
    /// </summary>
    public IReadOnlyList<int> GearSlots { get; init; } = [6, 1, 10, 3];

    /// <summary>Anything worn here makes hearing noise impossible (<c>3FB6:0000</c>: the legs slot).</summary>
    public IReadOnlyList<int> NoHearingSlots { get; init; } = [6];

    /// <summary>
    /// A party skill check (<c>REQUEST(3, 32766, …)</c>, <c>87B3:028D</c>) rolls for
    /// the member with the best chance. DSUN.EXE means to, but its search starts
    /// from 0 and keeps a lower one, so it always rolls for the first slot;
    /// false: as the original.
    /// </summary>
    public bool PartySkillCheckBest { get; init; }

    /// <summary>
    /// Item 2005 worn on the legs makes moving silently certain and hearing noise
    /// impossible. DSUN.EXE passes its slot and item lists the wrong way round
    /// (<c>87B3:04F5</c>), so it never applies; false: as the original.
    /// </summary>
    public bool SilentItemWorks { get; init; }

    /// <summary>STAT_ROLL's difficulties 0–3 (<c>3971:0004</c>): added to the ability the d20 must come under.</summary>
    public IReadOnlyList<int> StatRollModifiers { get; init; } = [5, 0, -5, -10];

    /// <summary>A save with no class to give one.</summary>
    public int NoClassSave { get; init; } = 20;

    public RaceRules Race(Race race) => Races.First(r => r.Race == race);

    public ClassRules Class(CharacterClass c) => Classes.First(r => r.Class == c);

    /// <summary>Constitution's hit point bonus a level (<see cref="ConstitutionHitPoints"/>).</summary>
    /// <summary>
    /// The least a level's hit point roll gives, by Constitution (<c>40B1:0038</c>): 1, then 2 at 20,
    /// 3 at 21–22, 4 from 23.
    /// </summary>
    public IReadOnlyList<int> LevelHitPointFloor { get; init; } = [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 3, 3, 4, 4, 4];

    public int LevelHitPointFloorOf(int con) =>
        LevelHitPointFloor is { Count: > 0 } t ? t[Math.Clamp(con, 0, t.Count - 1)] : 1;

    public int ConstitutionBonus(int con, bool warrior) =>
        (warrior ? WarriorConstitutionHitPoints : ConstitutionHitPoints) is { Count: > 0 } t ? t[Math.Clamp(con, 0, t.Count - 1)] : 0;

    /// <summary>
    /// A mod's rules. A race or class that leaves out a field (a file written
    /// before the field existed) gets the original's for that race or class,
    /// not the record's default: a thief keeps a thief's THAC0 and saves. A file
    /// without <c>Races</c> or <c>Classes</c> keeps the original's, so a mod need
    /// only hold what it changes.
    /// </summary>
    public static CharacterRules Read(string path)
    {
        try
        {
            JsonNode node = JsonNode.Parse(File.ReadAllText(path)) ?? throw new InvalidDataException("empty file");
            JsonNode original = JsonNode.Parse(Default.ToJson())!;
            foreach (var (list, key) in new[] { ("Races", "Race"), ("Classes", "Class") })
            {
                if (node is JsonObject root && !root.ContainsKey(list))
                {
                    root[list] = original[list]!.DeepClone();
                }
                foreach (JsonObject entry in (node[list] as JsonArray ?? []).OfType<JsonObject>())
                {
                    if ((original[list] as JsonArray)!.OfType<JsonObject>()
                        .FirstOrDefault(o => o[key]?.ToJsonString() == entry[key]?.ToJsonString()) is { } same)
                    {
                        foreach (var (name, value) in same.Where(p => !entry.ContainsKey(p.Key)).ToList())
                        {
                            entry[name] = value?.DeepClone();
                        }
                    }
                }
            }
            return node.Deserialize<CharacterRules>(Json) ?? throw new InvalidDataException("empty file");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException(e.Message, e);
        }
    }

    public string ToJson() => Combat.SpellRules.Compact(JsonSerializer.Serialize(this, Json));

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

    // 40B1:006C: the class groups' saves.
    private static readonly SaveRow[] PriestSaves = [new(10, 45, 2), new(14, 45, 6), new(13, 45, 5), new(16, 45, 8), new(15, 45, 7)];
    private static readonly SaveRow[] WarriorSaves = [new(14, 69, 3), new(16, 69, 5), new(15, 69, 4), new(17, 82, 4), new(17, 69, 6)];
    private static readonly SaveRow[] WizardSaves = [new(14, 30, 8), new(11, 40, 3), new(13, 40, 5), new(15, 40, 7), new(12, 40, 4)];
    private static readonly SaveRow[] RogueSaves = [new(13, 25, 8), new(14, 50, 4), new(12, 25, 7), new(16, 25, 11), new(15, 50, 5)];

    public static CharacterRules Default { get; } = new()
    {
        Races =
        [
            new(Characters.Race.Human, "Human", [Gender.Male, Gender.Female], A(), All, MultiClass: false),
            new(Characters.Race.Dwarf, "Dwarf", [Gender.Male, Gender.Female],
                A((Ability.Con, 2), (Ability.Str, 1), (Ability.Dex, -1), (Ability.Cha, -2)),
                [CharacterClass.Cleric, CharacterClass.Fighter, CharacterClass.Gladiator, CharacterClass.Psionicist, CharacterClass.Thief], true,
                ConstitutionSaves: true,
                ThiefSkills: [0, 10, 15, 0, 0, 0, -10, -5]),
            new(Characters.Race.Elf, "Elf", [Gender.Male, Gender.Female],
                A((Ability.Dex, 2), (Ability.Int, 1), (Ability.Wis, -1), (Ability.Con, -2)),
                [CharacterClass.Cleric, CharacterClass.Fighter, CharacterClass.Gladiator, CharacterClass.Preserver, CharacterClass.Psionicist, CharacterClass.Ranger, CharacterClass.Thief], true,
                ThiefSkills: [5, -5, 0, 5, 10, 5, 0, 0]),
            new(Characters.Race.HalfElf, "Half-elf", [Gender.Male, Gender.Female],
                A((Ability.Dex, 1), (Ability.Con, -1)),
                All, true,
                ThiefSkills: [10, 0, 0, 0, 5, 0, 0, 0]),
            new(Characters.Race.HalfGiant, "Half-giant", [Gender.Male, Gender.Female],
                // DSUN.EXE's (38D4:016B); the manual has Str +4, Con +2, Int −2, Wis −2, Cha −2.
                A((Ability.Str, 4), (Ability.Dex, -5), (Ability.Con, 2), (Ability.Int, -5), (Ability.Wis, -3), (Ability.Cha, -3)),
                [CharacterClass.Cleric, CharacterClass.Fighter, CharacterClass.Gladiator, CharacterClass.Psionicist], true, HitPointFactor: 2),
            new(Characters.Race.Halfling, "Halfling", [Gender.Male, Gender.Female],
                A((Ability.Dex, 2), (Ability.Wis, 2), (Ability.Con, -1), (Ability.Cha, -1), (Ability.Str, -2)),
                [CharacterClass.Cleric, CharacterClass.Druid, CharacterClass.Fighter, CharacterClass.Gladiator, CharacterClass.Psionicist, CharacterClass.Ranger, CharacterClass.Thief], true,
                ConstitutionSaves: true,
                ThiefSkills: [5, 5, 5, 10, 15, 5, -15, -5]),
            new(Characters.Race.Mul, "Mul", [Gender.Male],
                A((Ability.Str, 2), (Ability.Con, 1), (Ability.Int, -1), (Ability.Cha, -2)),
                [CharacterClass.Cleric, CharacterClass.Fighter, CharacterClass.Gladiator, CharacterClass.Psionicist, CharacterClass.Thief], true,
                ThiefSkills: [0, -5, 0, 5, 0, 0, 5, -5]),
            new(Characters.Race.ThriKreen, "Thri-kreen", [Gender.Female],
                A((Ability.Dex, 2), (Ability.Wis, 1), (Ability.Int, -1), (Ability.Cha, -2)),
                [CharacterClass.Cleric, CharacterClass.Fighter, CharacterClass.Gladiator, CharacterClass.Psionicist, CharacterClass.Ranger], true,
                // 6ECD:1344: four claws of 1d4 a round (8 halves) and a bite of 1d4 + 1; Move 15.
                Move: 15, NaturalAttacks: [new(8, 1, 4, 0), new(2, 1, 4, 1)],
                ArmorClass: 5, WeaponsOnly: true),
        ],
        Classes =
        [
            new(CharacterClass.Cleric, "Cleric", Minimums(Ability.Wis, 9), [Ability.Wis], 8, false, true,
                [0, 1500, 3000, 6000, 13000, 27500, 55000, 110000, 225000], 8, PriestSaves),
            new(CharacterClass.Druid, "Druid", Minimums(Ability.Wis, 12), [Ability.Wis, Ability.Cha], 8, false, true,
                [0, 2000, 4000, 7500, 12500, 20000, 35000, 60000, 90000], 8, PriestSaves),
            new(CharacterClass.Fighter, "Fighter", Minimums(Ability.Str, 9), [Ability.Str], 10, true, false,
                [0, 2000, 4000, 8000, 16000, 32000, 64000, 125000, 250000], 12, WarriorSaves),
            // DSUN.EXE gives gladiators the fighter's row.
            new(CharacterClass.Gladiator, "Gladiator", Minimums(Ability.Str, 13), [Ability.Str], 10, true, false,
                [0, 2000, 4000, 8000, 16000, 32000, 64000, 125000, 250000], 12, WarriorSaves, ArmorBonusLevel: 5),
            new(CharacterClass.Preserver, "Preserver", Minimums(Ability.Int, 9), [Ability.Int], 4, false, false,
                [0, 2500, 5000, 10000, 20000, 40000, 60000, 90000, 135000], 4, WizardSaves),
            new(CharacterClass.Psionicist, "Psionicist", Minimums(Ability.Wis, 12), [Ability.Con, Ability.Wis], 6, false, false,
                [0, 2200, 4400, 8800, 16500, 30000, 55000, 100000, 200000], 6, RogueSaves),
            new(CharacterClass.Ranger, "Ranger", Minimums(Ability.Wis, 14), [Ability.Str, Ability.Dex, Ability.Wis], 10, true, true,
                [0, 2200, 4500, 9000, 18000, 36000, 75000, 150000, 300000], 12, WarriorSaves),
            new(CharacterClass.Thief, "Thief", Minimums(Ability.Dex, 9), [Ability.Dex], 6, false, false,
                [0, 1200, 2500, 5000, 10000, 20000, 40000, 70000, 110000], 6, RogueSaves),
        ],
    };
}
