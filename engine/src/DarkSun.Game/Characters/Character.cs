using System.Text.Json;
using System.Text.Json.Serialization;
using DarkSun.Data.Gff;
using DarkSun.Data.Items;
using DarkSun.Data.Text;
using DarkSun.Game.Magic;
using DarkSun.Game.World;

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

    /// <summary>
    /// Spells a preserver may still choose for the levels gained (<c>8CCC:0000</c>: on
    /// gaining a preserver level the study window opens, <c>WIND</c> 17500, CHOOSE A SPELL).
    /// </summary>
    public int SpellsToChoose { get; set; }

    /// <summary>
    /// Powers a psionicist may still learn or enhance for the levels gained (<c>8E77:0765</c> →
    /// <c>430A:0057</c>: on gaining a psionicist level the window opens, <c>WIND</c> 17501, PICK A PSIONIC POWER).
    /// </summary>
    public int PowersToChoose { get; set; }

    /// <summary>
    /// The highest level each class (<see cref="Classes"/>' order) has had (character record +0x3F):
    /// a level gained past it brings hit points and choices (<see cref="CharacterCreation.CheckLevels"/>).
    /// Empty until the first check, which takes the levels as they are.
    /// </summary>
    public List<int> HighestLevels { get; set; } = [];

    /// <summary>The hit points rolled for all the levels of all the classes (+0x0A): maximum hit points are this ÷ the classes + Constitution's bonus. 0: not known yet (taken from <see cref="Hp"/>).</summary>
    public int HitDieTotal { get; set; }

    /// <summary>The spells cast since the last rest: each takes one of its level's casts (<see cref="Magic.Spellcasting.CastsLeft"/>).</summary>
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

    /// <summary>Spells (and powers cast on the map) on it outside a fight, with their ends on the game clock (<see cref="Combat.LastingEffects"/>).</summary>
    public List<Combat.LastingEffect> Lasting { get; set; } = [];

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
    /// The items it brings to a new game: its class's <see cref="StartingGear"/>,
    /// given when DONE is clicked on the creation screen (<c>6ECD:1D48</c>; DSUN.EXE
    /// keeps them with the character), worn ones in their slots. Empty for a
    /// character made before it had any, which gets the gear when a game starts.
    /// </summary>
    public List<ItemState> Items { get; set; } = [];

    /// <summary>
    /// The item classes it counts as (DSUN.EXE's character record +0x12,
    /// <c>6868:0000</c>): a cleric by its sphere (all four without one).
    /// </summary>
    [JsonIgnore]
    public ItemClasses ItemClasses => ItemClassesOf(Classes.Concat(Former.Select(f => f.Class)));

    /// <summary>The item classes of <paramref name="classes"/> (a cleric by its sphere).</summary>
    public ItemClasses ItemClassesOf(IEnumerable<CharacterClass> classes) => classes.Aggregate(ItemClasses.None, (all, k) => all | k switch
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
/// <summary>A level a class gained (<see cref="CharacterCreation.CheckLevels"/>), with the hit points it rolled.</summary>
public sealed record LevelGain(CharacterClass Class, int Level, int HitPoints);

public sealed class CharacterCreation(CharacterRules rules, CharacterNames names, Random random)
{
    public CharacterRules Rules { get; } = rules;

    /// <summary>A new character: a human male fighter (the manual: "the fighter class has been pre-selected"), rolled, with a random name, its map icon and its starting experience.</summary>
    public Character New()
    {
        // The psionic discipline and the sphere start unchosen, as in the original: the player picks them.
        var c = new Character { Race = Race.Human, Gender = Gender.Male, Classes = [CharacterClass.Fighter], Disciplines = [] };
        c.Name = names.Pick(c.Race, c.Gender, random);
        c.Sprite = Icon(c);
        GiveStartingExperience(c);
        Roll(c);
        GiveStartingSpells(c);
        GiveStartingPowers(c);
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

    /// <summary>
    /// <c>6ECD:1977</c>: a preserver's wizard spells become those it starts with at its preserver
    /// level (<see cref="CharacterRules.PreserverStartingSpells"/>, each level adding to the last);
    /// anyone else's book is left alone.
    /// </summary>
    private void GiveStartingSpells(Character c)
    {
        int i = c.Classes.IndexOf(CharacterClass.Preserver);
        if (i < 0)
        {
            return;
        }
        int level = Levels(c)[i];
        c.Spellbook.RemoveAll(n => Spellcasting.Get(n)?.Kind == SpellKind.Wizard);
        foreach (int n in Rules.PreserverStartingSpells.Take(level).SelectMany(s => s).Where(n => !c.Spellbook.Contains(n)))
        {
            c.Spellbook.Add(n);
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

    /// <summary>A click on an ability: up (left) or down (right), past the top round to the bottom and back (the guide).</summary>
    private bool Step(Character c, Ability a, int by)
    {
        var (low, high) = Range(c, a);
        int now = Adjusted(c, a);
        if (low >= high)
        {
            return false;
        }
        int next = now + by > high ? low : now + by < low ? high : now + by;
        c.Rolled[(int)a] += next - now;
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
        int bonus = Rules.ConstitutionBonus(Adjusted(c, Ability.Con), Warrior(c));
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
        return Math.Clamp(rolled + Rules.ConstitutionBonus(Adjusted(c, Ability.Con), Warrior(c)), low, high);
    }

    /// <summary>A click on the hit points: up or down, past the top round to the bottom and back, as the abilities.</summary>
    public bool ChangeHp(Character c, int by)
    {
        var (low, high) = HpRange(c);
        if (low >= high)
        {
            return false;
        }
        c.Hp = c.Hp + by > high ? low : c.Hp + by < low ? high : c.Hp + by;
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
        // The classes the new race may keep together, in the order chosen.
        var kept = new List<CharacterClass>();
        foreach (CharacterClass k in c.Classes)
        {
            if (kept.Count < (r.MultiClass ? Rules.MaxClasses : 1) && r.Allows([.. kept, k]))
            {
                kept.Add(k);
            }
        }
        c.Classes = kept;
        if (c.Classes.Count == 0 && r.Classes.Contains(CharacterClass.Fighter))
        {
            c.Classes = [CharacterClass.Fighter]; // pre-selected for every race, as the manual has it
        }
        if (c.Classes.Count == 0)
        {
            c.Classes.Add(r.Classes.Contains(CharacterClass.Fighter) ? CharacterClass.Fighter : r.Classes[0]);
        }
        FitAlignment(c);
        if (c.RandomName)
        {
            c.Name = names.Pick(race, gender, random);
        }
        c.Sprite = Icon(c);
        GiveStartingExperience(c);
        FixPowers(c);
        Fit(c);
        GiveStartingPowers(c);
    }

    /// <summary>
    /// The next alignment every chosen class allows (<c>6CCA:1784</c> → <c>17C7</c>: an alignment a class
    /// doesn't allow is stepped past; with no class, True Neutral).
    /// </summary>
    public void CycleAlignment(Character c, int by)
    {
        var list = Rules.Alignments;
        int at = Math.Max(0, list.ToList().IndexOf(c.Alignment));
        for (int step = 1; step <= list.Count; step++)
        {
            Alignment next = list[((at + by * step) % list.Count + list.Count) % list.Count];
            if (Allowed(c.Classes, next))
            {
                c.Alignment = next;
                return;
            }
        }
    }

    /// <summary>Does <paramref name="alignment"/> suit every one of <paramref name="classes"/> (<see cref="CharacterRules.ClassAlignments"/>)?</summary>
    private bool Allowed(IEnumerable<CharacterClass> classes, Alignment alignment) => classes.All(k => Rules.AlignmentAllows(k, alignment));

    /// <summary>
    /// An alignment the classes allow, if the character's doesn't: the first in <see cref="CharacterRules.Alignments"/>.
    /// (DSUN.EXE checks the alignment only when it is changed, so a class taken after it could be at odds with it.)
    /// </summary>
    private void FitAlignment(Character c)
    {
        if (!Allowed(c.Classes, c.Alignment) && Rules.Alignments.FirstOrDefault(a => Allowed(c.Classes, a)) is var a && Allowed(c.Classes, a))
        {
            c.Alignment = a;
        }
    }

    public void Rename(Character c, string name)
    {
        c.Name = name.Trim();
        c.RandomName = false;
    }

    // -- classes ---------------------------------------------------------------------

    /// <summary>
    /// Whether <paramref name="k"/> can be added: there's room (one class for races that can't multi-class),
    /// the race allows it with the classes chosen (<see cref="RaceRules.Allows"/>; DSUN.EXE's class buttons,
    /// <c>6CCA:0A45</c>), nothing chosen excludes it, and some alignment suits them all.
    /// </summary>
    public bool CanAdd(Character c, CharacterClass k)
    {
        RaceRules r = Rules.Race(c.Race);
        if (c.Classes.Contains(k) || c.Classes.Count >= (r.MultiClass ? Rules.MaxClasses : 1) || !r.Allows([.. c.Classes, k]))
        {
            return false;
        }
        return !Rules.Exclusive.Any(set => set.Contains(k) && c.Classes.Any(set.Contains))
            && Rules.Alignments.Any(a => Allowed([.. c.Classes, k], a));
    }

    /// <summary>Click on a class: take it off if chosen, else add it if allowed (raising the abilities it requires). Returns whether anything changed.</summary>
    public bool ToggleClass(Character c, CharacterClass k)
    {
        if (c.Classes.Remove(k))
        {
            GiveStartingExperience(c);
            FixPowers(c);
            Fit(c);
            GiveStartingSpells(c);
            GiveStartingPowers(c);
            return true;
        }
        if (!CanAdd(c, k))
        {
            return false;
        }
        c.Classes.Add(k);
        c.Classes.Sort();
        FitAlignment(c);
        GiveStartingExperience(c);
        FixPowers(c);
        Fit(c);
        c.Hp = RollHp(c);
        GiveStartingSpells(c);
        GiveStartingPowers(c);
        return true;
    }

    // -- psionics and spheres -------------------------------------------------------------

    /// <summary>
    /// <c>6ECD:1871</c>, when a character is made and for each member when a game starts
    /// (<c>6BB8:064C</c>): every power is forgotten, then a psionicist learns those of its
    /// psionicist level (<see cref="CharacterRules.PsionicistStartingPowers"/>) and anyone else
    /// those of its discipline (<see cref="CharacterRules.DisciplineStartingPowers"/>), each at rank 1.
    /// </summary>
    public void GiveStartingPowers(Character c)
    {
        int level = LevelIn(c, CharacterClass.Psionicist);
        IReadOnlyList<int> powers = level > 0 ? Rules.PsionicistStartingPowers[Math.Min(level, Rules.PsionicistStartingPowers.Count) - 1]
            : c.Disciplines.Count > 0 ? Rules.DisciplineStartingPowers[(int)c.Disciplines[^1]] : [];
        c.Powers = [.. powers];
        c.PowerRanks.Clear();
    }

    public bool NeedsSphere(Character c) => c.Classes.Any(k => Rules.Class(k).ChoosesSphere);

    /// <summary>Psionicists know all three disciplines; everyone else one.</summary>
    public bool AllDisciplines(Character c) => c.Classes.Contains(CharacterClass.Psionicist);

    public void ChooseDiscipline(Character c, Discipline d)
    {
        if (!AllDisciplines(c))
        {
            c.Disciplines = [d];
            GiveStartingPowers(c);
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
            : c.Disciplines.Count > 0 ? [c.Disciplines[0]] : [];
        c.Sphere = NeedsSphere(c) ? c.Sphere : null;
    }

    // -- done ----------------------------------------------------------------------------

    // -- dual-classing (humans; the manual, pages 12 and 21) ---------------------------------

    /// <summary>
    /// The classes a character could dual-class into now, or why none
    /// ("NO OTHER CLASSES AVAILABLE" in DSUN.EXE). A human (a race that
    /// can't multi-class) with one class at <see cref="CharacterRules.DualLevel"/>
    /// or more (a second dual-class: every class so far at <see cref="CharacterRules.SecondDualLevel"/>),
    /// its prime requisites at <see cref="CharacterRules.DualFromPrime"/>
    /// or more, and fewer than <see cref="CharacterRules.MaxClasses"/> classes
    /// in all, may take a class the race allows of a group it hasn't had
    /// (<see cref="CharacterRules.DualGroups"/>), a ranger or druid only of an alignment
    /// it allows, whose key ability is at <see cref="CharacterRules.DualToPrime"/>
    /// and whose requirements it meets (<c>8CCC:1934</c>).
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
        // The first dual-class from third level (the manual); a second when the classes so far are each at
        // second level (the guide), the new one needn't have passed the old.
        int needed = c.Former.Count == 0 ? Rules.DualLevel : Rules.SecondDualLevel;
        if (Levels(c)[0] < needed || c.Former.Any(f => f.Level < needed))
        {
            return ([], $"YOU MUST BE LEVEL {needed} FIRST");
        }
        if (Rules.Class(now).PrimeRequisites.Any(a => Adjusted(c, a) < Rules.DualFromPrime))
        {
            return ([], $"NEEDS {Rules.DualFromPrime} IN {string.Join(", ", Rules.Class(now).PrimeRequisites).ToUpperInvariant()}");
        }
        var had = c.Former.Select(f => f.Class).Append(now).ToList();
        var choices = r.Classes.Where(k => !had.Contains(k)
            && !Rules.Exclusive.Any(set => set.Contains(k) && had.Any(set.Contains))
            && !Rules.DualGroups.Any(group => group.Contains(k) && had.Any(group.Contains))
            && (!Rules.DualAlignmentClasses.Contains(k) || Rules.AlignmentAllows(k, c.Alignment))
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
        c.Cast.Clear();
        c.HighestLevels = [.. Levels(c)];
        FixPowers(c);
        GiveStartingSpells(c);
        return true;
    }

    // -- gaining levels ----------------------------------------------------------------

    /// <summary>
    /// The level check (<c>8E77:08A6</c>, after an award and at the end of a fight): each class whose
    /// level (from experience, <see cref="Levels"/>) has passed the highest it had
    /// (<see cref="Character.HighestLevels"/>) gains it (<c>8E77:0765</c>): its hit points
    /// (<see cref="LevelHitPoints"/>), and a preserver a spell, a psionicist a power, to choose.
    /// Levels stop at 9 (the classes' tables). The first check on a character only notes its levels.
    /// Maximum hit points become the rolls ÷ the classes + Constitution's bonus (<c>8E77:0260</c>).
    /// </summary>
    public IReadOnlyList<LevelGain> CheckLevels(Character c)
    {
        var levels = Levels(c);
        if (NoteLevels(c))
        {
            return [];
        }
        var gains = new List<LevelGain>();
        for (int i = 0; i < c.Classes.Count; i++)
        {
            while (c.HighestLevels[i] < levels[i])
            {
                int level = ++c.HighestLevels[i];
                CharacterClass k = c.Classes[i];
                gains.Add(new LevelGain(k, level, LevelHitPoints(c, k, level)));
                c.SpellsToChoose += k == CharacterClass.Preserver ? 1 : 0;
                c.PowersToChoose += k == CharacterClass.Psionicist ? 1 : 0;
            }
        }
        int rolled = gains.Sum(g => g.HitPoints);
        if (rolled > 0)
        {
            int classes = Math.Max(1, c.Classes.Count), bonus = Rules.ConstitutionBonus(Adjusted(c, Ability.Con), Warrior(c));
            int total = (c.HitDieTotal > 0 ? c.HitDieTotal : Math.Max(0, c.Hp - bonus) * classes) + rolled;
            c.HitDieTotal = total;
            c.Hp = Math.Max(1, total / classes + bonus);
        }
        return gains;
    }

    /// <summary>
    /// A character not yet checked (<see cref="Character.HighestLevels"/> not one a class) takes the
    /// levels it has as the highest; done before experience is added, so what that brings counts. True if it was.
    /// </summary>
    public bool NoteLevels(Character c)
    {
        if (c.HighestLevels.Count == c.Classes.Count)
        {
            return false;
        }
        c.HighestLevels = [.. Levels(c)];
        return true;
    }

    /// <summary>
    /// The hit points a class's new level rolls (<c>8E77:0000</c>): its hit die, at least
    /// <see cref="CharacterRules.LevelHitPointFloor"/> for the Constitution, doubled for a
    /// half-giant (<see cref="RaceRules.HitPointFactor"/>). A human's new class gives none
    /// until it passes the levels of the classes it left.
    /// </summary>
    public int LevelHitPoints(Character c, CharacterClass k, int level)
    {
        if (c.Race == Race.Human && c.Former.Any(f => f.Level >= level))
        {
            return 0;
        }
        int roll = Math.Max(random.Next(1, Math.Max(1, Rules.Class(k).HitDie) + 1), Rules.LevelHitPointFloorOf(Adjusted(c, Ability.Con)));
        return roll * Math.Max(1, Rules.Race(c.Race).HitPointFactor);
    }

    /// <summary>What the level check says of a level gained (<c>8E77:08A6</c>): "%s is %d%s level %s", or "%s gains a level" when that is over 29 letters.</summary>
    public static string LevelMessage(string name, LevelGain gain, string className)
    {
        string suffix = gain.Level % 100 is 11 or 12 or 13 ? "th" : (gain.Level % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        string text = $"{name} is {gain.Level}{suffix} level {className}";
        return text.Length > 29 ? $"{name} gains a level" : text;
    }

    /// <summary>
    /// The item classes <paramref name="c"/> may use now: its classes, and a dual-class's former ones only once
    /// in use again (<see cref="Active"/>; the guide: the old class's gear is disabled until then).
    /// </summary>
    public ItemClasses UsableItemClasses(Character c) =>
        c.ItemClassesOf([.. c.Classes, .. c.Former.Where(f => Active(c, f)).Select(f => f.Class)]);

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
        if (c.Disciplines.Count == 0)
        {
            problems.Add("Choose a psionic discipline.");
        }
        if (NeedsSphere(c) && c.Sphere is null)
        {
            problems.Add("Choose a sphere.");
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
        foreach (CharacterClass k in c.Classes.Where(k => !Rules.AlignmentAllows(k, c.Alignment)))
        {
            problems.Add($"A {Rules.Class(k).Name.ToLowerInvariant()} can't have that alignment.");
        }
        return problems;
    }

    /// <summary>Level in class <paramref name="k"/> (0 if the character isn't of that class).</summary>
    public int LevelIn(Character c, CharacterClass k) => c.Classes.IndexOf(k) is >= 0 and var i ? Levels(c)[i] : 0;

    /// <summary>Level in each class: experience is divided evenly among them.</summary>
    public IReadOnlyList<int> Levels(Character c) =>
        [.. c.Classes.Select(k => Rules.Class(k).Levels.Count(xp => c.Experience / Math.Max(1, c.Classes.Count) >= xp))];

    /// <summary>The experience at which the next level comes, in whichever class is first (shared evenly, as <see cref="Levels"/>); null at the top of every class.</summary>
    public int? NextLevelExperience(Character c)
    {
        var levels = Levels(c);
        var next = c.Classes.Select((k, i) => Rules.Class(k).Levels is var table && levels[i] < table.Count ? table[levels[i]] * c.Classes.Count : (int?)null)
            .OfType<int>().ToList();
        return next.Count > 0 ? next.Min() : null;
    }
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
