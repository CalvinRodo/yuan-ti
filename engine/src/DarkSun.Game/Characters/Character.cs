using System.Text.Json;
using System.Text.Json.Serialization;
using DarkSun.Data.Gff;
using DarkSun.Data.Items;
using DarkSun.Data.Text;
using DarkSun.Game.Magic;

namespace DarkSun.Game.Characters;

/// <summary>
/// A player character as created (the Character Generation screen): race,
/// gender, classes, abilities (as rolled, before racial adjustments; see
/// <see cref="CharacterCreation.Adjusted"/>), hit points, alignment,
/// psionic disciplines and sphere, name, and the map sprite it walks as (an
/// <c>OJFF</c> number; DSUN.EXE's choice per race isn't traced).
/// </summary>
public sealed record Character
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    /// <summary>The name was picked at random (it changes with the race and gender).</summary>
    public bool RandomName { get; set; } = true;

    public Race Race { get; set; }

    public Gender Gender { get; set; }

    public List<CharacterClass> Classes { get; set; } = [];

    /// <summary>Abilities before racial adjustments, in <see cref="Ability"/> order.</summary>
    public int[] Rolled { get; set; } = new int[6];

    public int Hp { get; set; }

    public Alignment Alignment { get; set; } = Alignment.TrueNeutral;

    public List<Discipline> Disciplines { get; set; } = [];

    /// <summary>Clerics, druids and rangers choose an element (<see cref="ClassRules.ChoosesSphere"/>).</summary>
    public Sphere? Sphere { get; set; }

    public int Sprite { get; set; }

    public int Experience { get; set; }

    /// <summary>Has been in a game: only the name can be changed then (the manual's EDIT NAME).</summary>
    public bool Adventured { get; set; }

    /// <summary>A preserver's spellbook: the wizard spells (<c>SPIN</c> numbers) learned (<see cref="Magic.Spellcasting"/>).</summary>
    public List<int> Spellbook { get; set; } = [];

    /// <summary>The spells memorized, one entry per memorized copy.</summary>
    public List<int> Memorized { get; set; } = [];

    /// <summary>The memorized spells cast since the last rest (a spell can be cast as often as it is memorized).</summary>
    public List<int> Cast { get; set; } = [];

    /// <summary>The psionic powers learned (<c>SPIN</c> 139–172).</summary>
    public List<int> Powers { get; set; } = [];

    /// <summary>
    /// A power's rank where it is above 1 (<c>DS:0077 + 0x22·member + power</c>,
    /// bits 3–7, saved as <c>PSPP</c>): learning a known power again from an item
    /// raises it, up to <see cref="Spellcasting.MaxPowerRank"/>
    /// (<see cref="Spellcasting.LearnFromItem"/>). It adds to the power check and to a defence mode.
    /// </summary>
    public Dictionary<int, int> PowerRanks { get; set; } = [];

    /// <summary>Its rank in power <paramref name="number"/>: 0 unknown, else 1 or what <see cref="PowerRanks"/> says.</summary>
    public int PowerRank(int number) => Powers.Contains(number) ? Math.Max(1, PowerRanks.GetValueOrDefault(number, 1)) : 0;

    /// <summary>Psionic strength points spent since the last rest.</summary>
    public int PspSpent { get; set; }

    /// <summary>Computer control (DSUN.EXE's button under the leader's): the character fights on its own. Kept for combat, which isn't in yet.</summary>
    public bool ComputerControl { get; set; }

    /// <summary>The party's leader (the leader button; one character of the party): the one selected when a game starts.</summary>
    public bool Leader { get; set; }

    /// <summary>
    /// A dual-classed human's earlier classes, at the level each reached when
    /// it was left (<see cref="CharacterCreation.Dual"/>): never to advance
    /// again, and of no use until the current class's level exceeds it.
    /// </summary>
    public List<FormerClass> Former { get; set; } = [];

    /// <summary>
    /// The item classes it counts as (DSUN.EXE's character record +0x12,
    /// <c>6868:0000</c>): a cleric by its sphere (all four without one).
    /// </summary>
    [JsonIgnore]
    public ItemClasses ItemClasses => Classes.Concat(Former.Select(f => f.Class)).Aggregate(ItemClasses.None, (all, k) => all | k switch
    {
        CharacterClass.Cleric => Sphere is { } s ? (ItemClasses)(1 << (int)s) : ItemClasses.Clerics,
        CharacterClass.Druid => ItemClasses.Druid,
        CharacterClass.Fighter => ItemClasses.Fighter,
        CharacterClass.Gladiator => ItemClasses.Gladiator,
        CharacterClass.Preserver => ItemClasses.Preserver,
        CharacterClass.Psionicist => ItemClasses.Psionicist,
        CharacterClass.Ranger => ItemClasses.Ranger,
        _ => ItemClasses.Thief,
    });
}

/// <summary>A class a dual-classed character has left, at the level it had then.</summary>
public sealed record FormerClass(CharacterClass Class, int Level);

/// <summary>The random names new characters get: <c>TEXT</c> 0–99 male, 100–199 female, 200–299 thri-kreen (<c>RESOURCE.GFF</c>).</summary>
public sealed class CharacterNames(IReadOnlyList<string> male, IReadOnlyList<string> female, IReadOnlyList<string> thriKreen)
{
    public static CharacterNames Empty { get; } = new([], [], []);

    public static CharacterNames Load(IResourceSource resources)
    {
        List<string> Range(int from) => [.. Enumerable.Range(from, 100)
            .Select(n => GameText.Read(resources, "TEXT", n)?.Split('\n')[0].Trim())
            .OfType<string>().Where(s => s.Length > 0)];
        return new CharacterNames(Range(0), Range(100), Range(200));
    }

    public string Pick(Race race, Gender gender, Random random)
    {
        IReadOnlyList<string> list = race == Race.ThriKreen && thriKreen.Count > 0 ? thriKreen
            : gender == Gender.Female && female.Count > 0 ? female : male;
        return list.Count > 0 ? list[random.Next(list.Count)] : "Nameless";
    }
}

/// <summary>
/// Character creation, as the manual describes the Character Generation
/// screen: the portrait cycles through the race and gender pairs; the die
/// rolls new abilities and hit points; an ability goes up or down by one
/// within its range; classes are chosen from those the race allows (one for
/// humans, up to three for the others, never two exclusive ones), and
/// choosing one raises the abilities it requires; psionicists know all
/// three disciplines, everyone else picks one; clerics, druids and rangers
/// pick a sphere; hit points and alignment go up and down.
/// </summary>
public sealed class CharacterCreation(CharacterRules rules, CharacterNames names, Random random)
{
    public CharacterRules Rules { get; } = rules;

    /// <summary>A new character: a human male fighter (the manual: "the fighter class has been pre-selected"), rolled, with a random name, its map icon and its starting experience.</summary>
    public Character New()
    {
        var c = new Character { Race = Race.Human, Gender = Gender.Male, Classes = [CharacterClass.Fighter], Disciplines = [Discipline.Psychokinesis] };
        c.Name = names.Pick(c.Race, c.Gender, random);
        c.Sprite = Icon(c);
        GiveStartingExperience(c);
        Roll(c);
        return c;
    }

    /// <summary>
    /// The map icon DSUN.EXE gives a look: <c>OJFF</c> <see cref="CharacterRules.IconBase"/>
    /// + its place in <see cref="Looks"/> (300 human male … 313 thri-kreen; half-elves
    /// share the humans' pictures). The party is put on the map with these (<c>6CCA:1046</c>).
    /// </summary>
    public int Icon(Character c) => Rules.IconBase + Math.Max(0, Looks.ToList().IndexOf((c.Race, c.Gender)));

    /// <summary>A character not yet adventured has <see cref="CharacterRules.StartingExperience"/> in each class.</summary>
    private void GiveStartingExperience(Character c)
    {
        if (!c.Adventured)
        {
            c.Experience = Rules.StartingExperience * Math.Max(1, c.Classes.Count);
        }
    }

    // -- abilities --------------------------------------------------------------------

    public int Adjustment(Character c, Ability a) => Rules.Race(c.Race).Adjustments.GetValueOrDefault(a);

    public int Adjusted(Character c, Ability a) => c.Rolled[(int)a] + Adjustment(c, a);

    /// <summary>
    /// How low and high ability <paramref name="a"/> may be (adjusted): up to
    /// 20 + the race's adjustment, down to the classes' minimum
    /// (<c>6CCA:16BD</c> clamps a change to these).
    /// </summary>
    public (int Low, int High) Range(Character c, Ability a)
    {
        int adjust = Adjustment(c, a);
        int low = Math.Clamp(Rules.RollFloor > 0 ? Rules.RollFloor + adjust : Rules.AbilityMin, Rules.AbilityMin, Rules.AbilityMax);
        int high = Math.Clamp(Rules.UnadjustedMax + adjust, Rules.AbilityMin, Rules.AbilityMax);
        int required = c.Classes.Select(k => Rules.Class(k).Requirements.GetValueOrDefault(a)).DefaultIfEmpty(0).Max();
        return (Math.Min(Math.Max(low, required), high), high);
    }

    /// <summary>
    /// The die (<c>6CCA:16BD</c>): each ability the best of <see cref="CharacterRules.RollTries"/>
    /// rolls of 4d4+4, raised to what the classes need; then hit points.
    /// </summary>
    public void Roll(Character c)
    {
        foreach (Ability a in Enum.GetValues<Ability>())
        {
            int roll = Enumerable.Range(0, Math.Max(1, Rules.RollTries))
                .Max(_ => Rules.RollBonus + Enumerable.Range(0, Rules.RollDice).Sum(_ => random.Next(1, Rules.RollSides + 1)));
            c.Rolled[(int)a] = Math.Max(Rules.RollFloor, roll);
        }
        Fit(c);
        c.Hp = RollHp(c);
    }

    public bool Raise(Character c, Ability a) => Step(c, a, +1);

    public bool Lower(Character c, Ability a) => Step(c, a, -1);

    private bool Step(Character c, Ability a, int by)
    {
        var (low, high) = Range(c, a);
        int now = Adjusted(c, a);
        if (now + by < low || now + by > high)
        {
            return false;
        }
        c.Rolled[(int)a] += by;
        c.Hp = Math.Clamp(c.Hp, HpRange(c).Low, HpRange(c).High);
        return true;
    }

    /// <summary>Every ability into its range (after a change of race or class).</summary>
    private void Fit(Character c)
    {
        foreach (Ability a in Enum.GetValues<Ability>())
        {
            var (low, high) = Range(c, a);
            c.Rolled[(int)a] = Math.Clamp(Adjusted(c, a), low, high) - Adjustment(c, a);
        }
        var hp = HpRange(c);
        c.Hp = Math.Clamp(c.Hp, hp.Low, hp.High);
    }

    // -- hit points -------------------------------------------------------------------

    private bool Warrior(Character c) => c.Classes.Any(k => Rules.Class(k).Warrior);

    /// <summary>
    /// Hit points as DSUN.EXE ranges them (<c>6ECD:0156</c>): a hit die for
    /// each level of each class (from 1 to the die), times the race's factor
    /// (half-giants 2), divided by the number of classes, plus Constitution's
    /// bonus (once); at least 1.
    /// </summary>
    public (int Low, int High) HpRange(Character c)
    {
        if (c.Classes.Count == 0)
        {
            return (1, 1);
        }
        var levels = Levels(c);
        int factor = Math.Max(1, Rules.Race(c.Race).HitPointFactor);
        int bonus = CharacterRules.ConstitutionBonus(Adjusted(c, Ability.Con), Warrior(c));
        int low = levels.Sum(l => Math.Max(1, l)) * factor / c.Classes.Count + bonus;
        int high = c.Classes.Select((k, i) => Math.Max(1, levels[i]) * Rules.Class(k).HitDie).Sum() * factor / c.Classes.Count + bonus;
        return (Math.Max(1, low), Math.Max(1, high));
    }

    public int RollHp(Character c)
    {
        if (c.Classes.Count == 0)
        {
            return 1;
        }
        var levels = Levels(c);
        int factor = Math.Max(1, Rules.Race(c.Race).HitPointFactor);
        int rolled = c.Classes.Select((k, i) => Enumerable.Range(0, Math.Max(1, levels[i])).Sum(_ => random.Next(1, Rules.Class(k).HitDie + 1))).Sum()
            * factor / c.Classes.Count;
        var (low, high) = HpRange(c);
        return Math.Clamp(rolled + CharacterRules.ConstitutionBonus(Adjusted(c, Ability.Con), Warrior(c)), low, high);
    }

    public bool ChangeHp(Character c, int by)
    {
        var (low, high) = HpRange(c);
        if (c.Hp + by < low || c.Hp + by > high)
        {
            return false;
        }
        c.Hp += by;
        return true;
    }

    // -- race, gender, alignment ---------------------------------------------------------

    /// <summary>The race and gender pairs, in portrait order.</summary>
    public IReadOnlyList<(Race Race, Gender Gender)> Looks =>
        [.. Rules.Races.SelectMany(r => r.Genders.Select(g => (r.Race, g)))];

    /// <summary>
    /// <paramref name="c"/>'s body portrait: <c>BMP</c> 20000 + its place in
    /// <see cref="Looks"/> (human male, human female, dwarf male, …; muls and
    /// thri-kreen have one each).
    /// </summary>
    public int Portrait(Character c) => PortraitBase + Math.Max(0, Looks.ToList().IndexOf((c.Race, c.Gender)));

    public const int PortraitBase = 20000;

    /// <summary>The portrait's left (+1) or right (−1) click: the next race and gender.</summary>
    public void CycleLooks(Character c, int by)
    {
        var looks = Looks;
        int at = Math.Max(0, looks.ToList().IndexOf((c.Race, c.Gender)));
        var (race, gender) = looks[((at + by) % looks.Count + looks.Count) % looks.Count];
        SetLooks(c, race, gender);
    }

    public void SetLooks(Character c, Race race, Gender gender)
    {
        c.Race = race;
        c.Gender = gender;
        RaceRules r = Rules.Race(race);
        c.Classes = [.. c.Classes.Where(r.Classes.Contains).Take(r.MultiClass ? Rules.MaxClasses : 1)];
        if (c.Classes.Count == 0)
        {
            c.Classes.Add(r.Classes.Contains(CharacterClass.Fighter) ? CharacterClass.Fighter : r.Classes[0]);
        }
        if (c.RandomName)
        {
            c.Name = names.Pick(race, gender, random);
        }
        c.Sprite = Icon(c);
        GiveStartingExperience(c);
        FixPowers(c);
        Fit(c);
    }

    public void CycleAlignment(Character c, int by)
    {
        var list = Rules.Alignments;
        int at = Math.Max(0, list.ToList().IndexOf(c.Alignment));
        c.Alignment = list[((at + by) % list.Count + list.Count) % list.Count];
    }

    public void Rename(Character c, string name)
    {
        c.Name = name.Trim();
        c.RandomName = false;
    }

    // -- classes ---------------------------------------------------------------------

    /// <summary>Whether <paramref name="k"/> can be added: the race allows it, there's room (one class for races that can't multi-class), and nothing chosen excludes it.</summary>
    public bool CanAdd(Character c, CharacterClass k)
    {
        RaceRules r = Rules.Race(c.Race);
        if (!r.Classes.Contains(k) || c.Classes.Contains(k))
        {
            return false;
        }
        if (c.Classes.Count >= (r.MultiClass ? Rules.MaxClasses : 1))
        {
            return false;
        }
        return !Rules.Exclusive.Any(set => set.Contains(k) && c.Classes.Any(set.Contains));
    }

    /// <summary>Click on a class: take it off if chosen, else add it if allowed (raising the abilities it requires). Returns whether anything changed.</summary>
    public bool ToggleClass(Character c, CharacterClass k)
    {
        if (c.Classes.Remove(k))
        {
            GiveStartingExperience(c);
            FixPowers(c);
            Fit(c);
            return true;
        }
        if (!CanAdd(c, k))
        {
            return false;
        }
        c.Classes.Add(k);
        c.Classes.Sort();
        GiveStartingExperience(c);
        FixPowers(c);
        Fit(c);
        c.Hp = RollHp(c);
        return true;
    }

    // -- psionics and spheres -------------------------------------------------------------

    public bool NeedsSphere(Character c) => c.Classes.Any(k => Rules.Class(k).ChoosesSphere);

    /// <summary>Psionicists know all three disciplines; everyone else one.</summary>
    public bool AllDisciplines(Character c) => c.Classes.Contains(CharacterClass.Psionicist);

    public void ChooseDiscipline(Character c, Discipline d)
    {
        if (!AllDisciplines(c))
        {
            c.Disciplines = [d];
        }
    }

    public void ChooseSphere(Character c, Sphere s)
    {
        if (NeedsSphere(c))
        {
            c.Sphere = s;
        }
    }

    private void FixPowers(Character c)
    {
        c.Disciplines = AllDisciplines(c) ? [.. Enum.GetValues<Discipline>()]
            : c.Disciplines.Count > 0 ? [c.Disciplines[0]] : [Discipline.Psychokinesis];
        c.Sphere = NeedsSphere(c) ? c.Sphere ?? Sphere.Earth : null;
    }

    // -- done ----------------------------------------------------------------------------

    // -- dual-classing (humans; the manual, pages 12 and 21) ---------------------------------

    /// <summary>
    /// The classes a character could dual-class into now, or why none
    /// ("NO OTHER CLASSES AVAILABLE" in DSUN.EXE). A human (a race that
    /// can't multi-class) with one class at <see cref="CharacterRules.DualLevel"/>
    /// or more, its prime requisites at <see cref="CharacterRules.DualFromPrime"/>
    /// or more, and fewer than <see cref="CharacterRules.MaxClasses"/> classes
    /// in all, may take a class the race allows, it hasn't had and nothing
    /// excludes, whose prime requisites are at <see cref="CharacterRules.DualToPrime"/>
    /// and whose requirements it meets. (The prime requisite scores are AD&amp;D
    /// 2nd edition's; DSUN.EXE's checks aren't traced.)
    /// </summary>
    public (IReadOnlyList<CharacterClass> Classes, string? Why) DualChoices(Character c)
    {
        RaceRules r = Rules.Race(c.Race);
        if (r.MultiClass)
        {
            return ([], "ONLY HUMANS CAN DUAL-CLASS");
        }
        if (c.Classes.Count != 1 || c.Former.Count + 1 >= Rules.MaxClasses)
        {
            return ([], "NO OTHER CLASSES AVAILABLE");
        }
        CharacterClass now = c.Classes[0];
        if (Levels(c)[0] < Rules.DualLevel)
        {
            return ([], $"YOU MUST BE LEVEL {Rules.DualLevel} FIRST");
        }
        if (Rules.Class(now).PrimeRequisites.Any(a => Adjusted(c, a) < Rules.DualFromPrime))
        {
            return ([], $"NEEDS {Rules.DualFromPrime} IN {string.Join(", ", Rules.Class(now).PrimeRequisites).ToUpperInvariant()}");
        }
        var had = c.Former.Select(f => f.Class).Append(now).ToList();
        var choices = r.Classes.Where(k => !had.Contains(k)
            && !Rules.Exclusive.Any(set => set.Contains(k) && had.Any(set.Contains))
            && Rules.Class(k).PrimeRequisites.All(a => Adjusted(c, a) >= Rules.DualToPrime)
            && Rules.Class(k).Requirements.All(q => Adjusted(c, q.Key) >= q.Value)).ToList();
        return (choices, choices.Count == 0 ? "NO OTHER CLASSES AVAILABLE" : null);
    }

    /// <summary>Dual-class into <paramref name="k"/>: the current class is kept at its level, the new one starts at level 1 (no experience). False if it isn't one of <see cref="DualChoices"/>.</summary>
    public bool Dual(Character c, CharacterClass k)
    {
        if (!DualChoices(c).Classes.Contains(k))
        {
            return false;
        }
        c.Former.Add(new FormerClass(c.Classes[0], Levels(c)[0]));
        c.Classes = [k];
        c.Experience = 0;
        c.Memorized.Clear();
        c.Cast.Clear();
        FixPowers(c);
        return true;
    }

    /// <summary>Is the former class in use again: the current class's level exceeds it?</summary>
    public bool Active(Character c, FormerClass f) => Levels(c).DefaultIfEmpty(0).Max() > f.Level;

    /// <summary>Every class the character can use now with its level: the current ones, and the former ones in use again.</summary>
    public IReadOnlyList<(CharacterClass Class, int Level)> ActiveLevels(Character c) =>
        [.. c.Classes.Zip(Levels(c)), .. c.Former.Where(f => Active(c, f)).Select(f => (f.Class, f.Level))];

    /// <summary>What still stops <paramref name="c"/> being finished (empty when it can be).</summary>
    public IReadOnlyList<string> Problems(Character c)
    {
        var problems = new List<string>();
        if (c.Name.Trim().Length == 0)
        {
            problems.Add("Give the character a name.");
        }
        if (c.Classes.Count == 0)
        {
            problems.Add("Choose a class.");
        }
        foreach (CharacterClass k in c.Classes)
        {
            foreach (var (a, need) in Rules.Class(k).Requirements)
            {
                if (Adjusted(c, a) < need)
                {
                    problems.Add($"{Rules.Class(k).Name} needs {a} {need}.");
                }
            }
        }
        if (!Rules.Alignments.Contains(c.Alignment))
        {
            problems.Add("The party can't have that alignment.");
        }
        return problems;
    }

    /// <summary>Level in each class: experience is divided evenly among them.</summary>
    public IReadOnlyList<int> Levels(Character c) =>
        [.. c.Classes.Select(k => Rules.Class(k).Levels.Count(xp => c.Experience / Math.Max(1, c.Classes.Count) >= xp))];
}

/// <summary>
/// Characters created and kept between games (DSUN.EXE's <c>CHARSAVE.GFF</c>;
/// here one JSON file each): the manual's ADD picks from them, DROP puts a
/// party member back.
/// </summary>
public sealed class CharacterRoster(string folder)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Folder { get; } = folder;

    public IReadOnlyList<Character> All()
    {
        if (!Directory.Exists(Folder))
        {
            return [];
        }
        var list = new List<Character>();
        foreach (string path in Directory.EnumerateFiles(Folder, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<Character>(File.ReadAllText(path), Json) is { } c)
                {
                    list.Add(c);
                }
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
                // A file that can't be read isn't offered.
            }
        }
        return [.. list.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public void Save(Character c)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, $"{c.Id:N}.json"), JsonSerializer.Serialize(c, Json));
    }

    public void Delete(Character c) => File.Delete(Path.Combine(Folder, $"{c.Id:N}.json"));

    public static string ToJson(Character c) => JsonSerializer.Serialize(c, Json);

    public static Character FromJson(string json) =>
        JsonSerializer.Deserialize<Character>(json, Json) ?? throw new InvalidDataException("empty character");
}
