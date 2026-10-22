using DarkSun.Game.Characters;
using DarkSun.Game.World;

namespace DarkSun.Game.Magic;

public enum SpellKind { Wizard, Priest, Psionic }

/// <summary>
/// A spell or psionic power (<c>SPIN</c> <see cref="Number"/> names and
/// describes it, <c>ICON</c> 20999 + n draws a spell, 3000 + n − 139 a power): its level and who may cast
/// it, from DSUN.EXE's table at <c>DS:019C</c> (7 bytes a spell, 1–138: the
/// level, a mask of caster codes, the name; read by <c>6868:0487</c>,
/// <c>612E:2574</c>). A power's <see cref="Discipline"/> follows the
/// <c>SPIN</c> order (139–144 psychokinesis, 145–158 psychometabolism,
/// 159–172 telepathy), as DSUN.EXE's "Kinetics", "Metabolic", "Telepath" do.
/// </summary>
public sealed record Spell(int Number, int Level, uint Casters, SpellKind Kind, Discipline? Discipline = null)
{
    public const int IconBase = 20999, PowerIconBase = 3000;

    public int Icon => Kind == SpellKind.Psionic ? PowerIconBase + Number - Spellcasting.FirstPower : IconBase + Number;
}

/// <summary>
/// Spells, memorizing and psionics for the party's characters. From DSUN.EXE:
/// the spells' levels and casters (<see cref="Spell"/>). A caster mask has
/// bit <c>code + 1</c> for each class code (<c>38D4:00ED</c>: 1–4 cleric of
/// air, earth, fire, water, 5–8 druid, 11 preserver, 13–16 ranger; the same
/// codes as items' classes). So it holds the spheres: Magical Stone is earth
/// only, Dust Devil air and earth, and at levels 4 and 5 the spells of no one
/// sphere are the druids'. The rest is the engine's, from AD&amp;D 2nd edition
/// where DSUN.EXE's tables (<c>DS:4B08</c>, <c>DS:4B11</c>: a character's
/// wizard and priest spells per level) aren't traced: spells per day by level
/// (<see cref="CharacterRules.WizardSlots"/>, …), priests' Wisdom bonus, the
/// spellbook's size by Intelligence, and psionic strength points and powers.
/// </summary>
public sealed class Spellcasting(CharacterRules rules)
{
    public const int FirstPower = 139, LastPower = 172, MaxSpellLevel = 5;

    // DS:019C, spells 1–138: (level, caster mask).
    private static readonly (int Level, uint Casters)[] Table =
    [
        (1, 0x081001), (1, 0x081001), (1, 0x081001), (1, 0x081001), (1, 0x081001), (1, 0x081001), (1, 0x081001), (1, 0x081001),
        (1, 0x081001), (1, 0x081001), (1, 0x081001), (1, 0x081001), (2, 0x081001), (2, 0x081001), (2, 0x081001), (2, 0x081001),
        (2, 0x081001), (2, 0x081001), (2, 0x081001), (2, 0x081001), (2, 0x081001), (2, 0x081001), (2, 0x081001), (2, 0x081001),
        (2, 0x081001), (3, 0x081001), (3, 0x081001), (3, 0x081001), (3, 0x081001), (3, 0x081001), (3, 0x081001), (3, 0x081001),
        (3, 0x081001), (3, 0x081001), (3, 0x081001), (3, 0x081001), (3, 0x081001), (3, 0x081001), (3, 0x081001), (3, 0x081001),
        (4, 0x081001), (4, 0x081001), (4, 0x081001), (4, 0x081001), (4, 0x081001), (4, 0x081001), (4, 0x081001), (4, 0x081001),
        (4, 0x081001), (4, 0x081001), (4, 0x081001), (4, 0x081001), (4, 0x081001), (4, 0x081001), (4, 0x081001), (4, 0x081001),
        (5, 0x081001), (5, 0x081001), (5, 0x081001), (5, 0x081001), (5, 0x081001), (5, 0x081001), (5, 0x081001), (5, 0x081001),
        (5, 0x081001), (5, 0x081001), (5, 0x081001), (5, 0x081001), (5, 0x081001), (1, 0x13C3FE), (1, 0x13C3FE), (1, 0x13C3FE),
        (1, 0x13C3FE), (1, 0x13C3FE), (1, 0x13C3FE), (1, 0x10808A), (1, 0x13C3FE), (1, 0x13C3FE), (1, 0x13C3FE), (1, 0x13C3FE),
        (2, 0x13C3FE), (2, 0x13C3FE), (2, 0x13C3FE), (2, 0x10C0CE), (2, 0x13C3FE), (2, 0x110112), (2, 0x13C3FE), (2, 0x110112),
        (2, 0x110112), (2, 0x13C3FE), (3, 0x104046), (3, 0x110112), (3, 0x10808A), (3, 0x120222), (3, 0x13C3FE), (3, 0x13C3FE),
        (3, 0x13C3FE), (3, 0x13C3FE), (3, 0x13C3FE), (3, 0x13C3FE), (3, 0x13C3FE), (3, 0x13C3FE), (3, 0x110112), (3, 0x13C3FE),
        (3, 0x13C3FE), (3, 0x13C3FE), (3, 0x104046), (4, 0x1003C2), (4, 0x120222), (4, 0x1003C2), (4, 0x1003C2), (4, 0x10808A),
        (4, 0x1003C2), (4, 0x1003C2), (4, 0x120222), (4, 0x10C0CE), (4, 0x110112), (4, 0x1003C2), (4, 0x1003C2), (4, 0x1003C2),
        (4, 0x110112), (4, 0x1003C2), (4, 0x104046), (5, 0x104046), (5, 0x110112), (5, 0x10808A), (5, 0x120222), (5, 0x1003C2),
        (5, 0x1003C2), (5, 0x104046), (5, 0x1003C2), (5, 0x110112), (5, 0x104046), (5, 0x10808A), (5, 0x1282AA), (5, 0x1003C2),
        (5, 0x1003C2), (5, 0x110112),
    ];

    /// <summary>Every spell (1–138) and power (139–172).</summary>
    public static IReadOnlyList<Spell> All { get; } =
    [
        .. Table.Select((t, i) => new Spell(i + 1, t.Level, t.Casters, i + 1 < 70 ? SpellKind.Wizard : SpellKind.Priest)),
        .. Enumerable.Range(FirstPower, LastPower - FirstPower + 1).Select(n => new Spell(n, 1, 0, SpellKind.Psionic,
            n <= 144 ? Discipline.Psychokinesis : n <= 158 ? Discipline.Psychometabolism : Discipline.Telepathy)),
    ];

    public static Spell? Get(int number) => number >= 1 && number <= All.Count ? All[number - 1] : null;

    public CharacterRules Rules { get; } = rules;

    private CharacterCreation Creation => new(Rules, CharacterNames.Empty, new Random(0));

    /// <summary>The character's level in <paramref name="k"/> (0 without the class, or a dual-class's former class not in use yet).</summary>
    public int Level(Character c, CharacterClass k) =>
        Creation.ActiveLevels(c).Where(a => a.Class == k).Select(a => a.Level).DefaultIfEmpty(0).Max();

    /// <summary>
    /// A class's code (<c>38D4:00ED</c>, character record +0x21): 1–4 cleric of
    /// air, earth, fire, water; 5–8 druid; 9 fighter; 10 gladiator; 11
    /// preserver; 12 psionicist; 13–16 ranger; 17 thief.
    /// </summary>
    public static int ClassCode(CharacterClass k, Sphere? sphere)
    {
        int s = (int)(sphere ?? Sphere.Air);
        return k switch
        {
            CharacterClass.Cleric => 1 + s,
            CharacterClass.Druid => 5 + s,
            CharacterClass.Fighter => 9,
            CharacterClass.Gladiator => 10,
            CharacterClass.Preserver => 11,
            CharacterClass.Psionicist => 12,
            CharacterClass.Ranger => 13 + s,
            _ => 17,
        };
    }

    /// <summary>The caster bits of the character's classes (bit <c>code + 1</c>).</summary>
    public static uint CasterBits(Character c)
    {
        uint bits = 0;
        foreach (CharacterClass k in c.Classes.Concat(c.Former.Select(f => f.Class)))
        {
            bits |= 1u << (ClassCode(k, c.Sphere) + 1);
        }
        return bits;
    }

    // -- spells ---------------------------------------------------------------------------

    /// <summary>
    /// How many spells of <paramref name="kind"/> and <paramref name="level"/>
    /// (1–5) the character may memorize: the wizard table at its preserver
    /// level; the priest table at its best cleric or druid level (or the
    /// ranger's), plus Wisdom's bonus where it has a spell of that level already.
    /// </summary>
    public int Slots(Character c, SpellKind kind, int level)
    {
        static int At(IReadOnlyList<IReadOnlyList<int>> table, int classLevel, int spellLevel) =>
            classLevel < 1 || table.Count == 0 ? 0
                : table[Math.Min(classLevel, table.Count) - 1] is var row && spellLevel <= row.Count ? row[spellLevel - 1] : 0;
        if (kind == SpellKind.Wizard)
        {
            return At(Rules.WizardSlots, Level(c, CharacterClass.Preserver), level);
        }
        if (kind != SpellKind.Priest)
        {
            return 0;
        }
        // 6798:071C: each priest class's slots added up; a cleric's or druid's Wisdom bonus with them.
        int priest = Math.Max(Level(c, CharacterClass.Cleric), Level(c, CharacterClass.Druid));
        int slots = At(Rules.PriestSlots, priest, level) + At(Rules.RangerSlots, Level(c, CharacterClass.Ranger), level);
        if (priest > 0 && (slots > 0 || !Rules.WisdomBonusNeedsSlots))
        {
            int wis = Creation.Adjusted(c, Ability.Wis);
            slots += Rules.WisdomBonusSpells.Where(b => wis >= b.Wisdom).Sum(b => b.Level == level ? 1 : 0);
        }
        return slots;
    }

    /// <summary>Spell levels the character can memorize at all, by kind.</summary>
    public IEnumerable<(SpellKind Kind, int Level)> Levels(Character c) =>
        from kind in new[] { SpellKind.Wizard, SpellKind.Priest }
        from level in Enumerable.Range(1, MaxSpellLevel)
        where Slots(c, kind, level) > 0
        select (kind, level);

    /// <summary>May the character's classes cast <paramref name="s"/> (DSUN.EXE's caster mask)?</summary>
    public static bool Allows(Character c, Spell s) => s.Kind != SpellKind.Psionic && (s.Casters & CasterBits(c)) != 0;

    /// <summary>
    /// The spells the character may memorize at <paramref name="kind"/> and
    /// <paramref name="level"/>: a priest's every spell its classes and sphere
    /// allow; a preserver's those in its spellbook.
    /// </summary>
    public IEnumerable<Spell> Memorizable(Character c, SpellKind kind, int level) =>
        Slots(c, kind, level) == 0 ? []
            : All.Where(s => s.Kind == kind && s.Level == level && Allows(c, s) && (kind != SpellKind.Wizard || c.Spellbook.Contains(s.Number)));

    /// <summary>The wizard spells the character could add to its spellbook at <paramref name="level"/>.</summary>
    public IEnumerable<Spell> Learnable(Character c, int level) =>
        Slots(c, SpellKind.Wizard, level) == 0 ? []
            : All.Where(s => s.Kind == SpellKind.Wizard && s.Level == level && Allows(c, s) && !c.Spellbook.Contains(s.Number));

    /// <summary>
    /// How many spells of a level the spellbook may hold, by Intelligence
    /// (<see cref="CharacterRules.SpellbookSizes"/>); with no sizes listed, as many as there are (DSUN.EXE).
    /// </summary>
    public int SpellbookSize(Character c)
    {
        if (Rules.SpellbookSizes.Count == 0)
        {
            return All.Count;
        }
        int intelligence = Creation.Adjusted(c, Ability.Int);
        return Rules.SpellbookSizes.Where(b => intelligence >= b.Intelligence).Select(b => b.Spells).DefaultIfEmpty(0).Max();
    }

    /// <summary>Add <paramref name="number"/> to the spellbook: "IS LEARNED", or why not.</summary>
    public string Learn(Character c, int number)
    {
        if (Get(number) is not { Kind: SpellKind.Wizard } s || !Learnable(c, s.Level).Contains(s))
        {
            return c.Spellbook.Contains(number) ? "YOU ALREADY KNOW THIS SPELL" : "CANNOT LEARN THIS SPELL";
        }
        if (c.Spellbook.Count(n => Get(n)?.Level == s.Level) >= SpellbookSize(c))
        {
            return "THE SPELLBOOK IS FULL AT THIS LEVEL";
        }
        c.Spellbook.Add(number);
        return "IS LEARNED!";
    }

    public int MemorizedAt(Character c, SpellKind kind, int level) =>
        c.Memorized.Count(n => Get(n) is { } s && s.Kind == kind && s.Level == level);

    /// <summary>Memorize a copy of <paramref name="number"/> if a slot of its level is free.</summary>
    public bool Memorize(Character c, int number)
    {
        if (Get(number) is not { } s || !Memorizable(c, s.Kind, s.Level).Contains(s) || MemorizedAt(c, s.Kind, s.Level) >= Slots(c, s.Kind, s.Level))
        {
            return false;
        }
        c.Memorized.Add(number);
        return true;
    }

    /// <summary>Forget one memorized copy (one not yet cast first).</summary>
    public bool Forget(Character c, int number)
    {
        if (!c.Memorized.Remove(number))
        {
            return false;
        }
        if (c.Cast.Count(n => n == number) > c.Memorized.Count(n => n == number))
        {
            c.Cast.Remove(number);
        }
        return true;
    }

    /// <summary>The memorized copies of <paramref name="number"/> not yet cast.</summary>
    public static int Ready(Character c, int number) => c.Memorized.Count(n => n == number) - c.Cast.Count(n => n == number);

    // -- psionics -------------------------------------------------------------------------

    /// <summary>Psionic strength points at full: the psionicist's by level (<see cref="CharacterRules.PsionicistPsp"/>), others' wild talent.</summary>
    public int MaxPsp(Character c)
    {
        int level = Level(c, CharacterClass.Psionicist);
        return level < 1 ? Rules.WildTalentPsp
            : Rules.PsionicistPsp[Math.Min(level, Rules.PsionicistPsp.Count) - 1] + Rules.PspPerLevelAfter * Math.Max(0, level - Rules.PsionicistPsp.Count);
    }

    public int Psp(Character c) => Math.Max(0, MaxPsp(c) - c.PspSpent);

    /// <summary>How many powers the character may know: a psionicist's grow with its level, others have one (their wild talent).</summary>
    public int PowersAllowed(Character c)
    {
        int level = Level(c, CharacterClass.Psionicist);
        return level < 1 ? 1 : Rules.PowersAtFirstLevel + level - 1;
    }

    /// <summary>The powers of the character's disciplines.</summary>
    public static IEnumerable<Spell> PowersOf(Character c, Discipline d) =>
        All.Where(s => s.Kind == SpellKind.Psionic && s.Discipline == d && c.Disciplines.Contains(d));

    /// <summary>Learn power <paramref name="number"/>: "IS LEARNED", or why not.</summary>
    public string Pick(Character c, int number)
    {
        if (Get(number) is not { Kind: SpellKind.Psionic, Discipline: { } d } || !c.Disciplines.Contains(d))
        {
            return "NOT IN THIS CHARACTER'S DISCIPLINES";
        }
        if (c.Powers.Contains(number))
        {
            return "ALREADY KNOWN";
        }
        if (c.Powers.Count >= PowersAllowed(c))
        {
            return "NO MORE POWERS TO PICK";
        }
        c.Powers.Add(number);
        return "IS LEARNED";
    }

    /// <summary>The highest a power's rank goes (<c>6698:0685</c>).</summary>
    public const int MaxPowerRank = 30;

    /// <summary>The items that teach a spell or power when used: <c>RDFF</c> 1400–1499 (<c>91F7:03D0</c>).</summary>
    public static bool Teaches(int rdff) => rdff is >= 1400 and <= 1499;

    /// <summary>
    /// Using an item that teaches (<c>91F7:03D0</c>): <paramref name="number"/>
    /// is its record +0x0F (the spell's record index + 1, so a <c>SPIN</c>
    /// number). A spell goes into the spellbook ("YOU LEARN THE SPELL", or "YOU
    /// ALREADY KNOW THIS SPELL"); a power is learned at rank 1 ("NAME LEARNED",
    /// <c>6698:07C5</c>) or, if known, rises a rank to at most 30 ("NAME
    /// ENHANCED TO n", <c>6698:0685</c>). Neither checks the class, discipline
    /// or how many it may know. Anything else: "CANNOT LEARN FROM THIS ITEM".
    /// <paramref name="consumed"/>: the item is used up (<c>7BEB:0A06</c>).
    /// </summary>
    public static string LearnFromItem(Character c, int number, string name, out bool consumed)
    {
        consumed = true;
        if (number >= 1 && number < FirstPower)
        {
            if (c.Spellbook.Contains(number))
            {
                consumed = false;
                return "YOU ALREADY KNOW THIS SPELL";
            }
            c.Spellbook.Add(number);
            return "YOU LEARN THE SPELL";
        }
        if (number is >= FirstPower and <= LastPower)
        {
            if (!c.Powers.Contains(number))
            {
                c.Powers.Add(number);
                c.PowerRanks.Remove(number);
                return $"{name} LEARNED";
            }
            int rank = Math.Min(MaxPowerRank, c.PowerRank(number) + 1);
            c.PowerRanks[number] = rank;
            return $"{name} ENHANCED TO {rank}";
        }
        consumed = false;
        return "CANNOT LEARN FROM THIS ITEM";
    }

    // -- casting out of combat --------------------------------------------------------------

    /// <summary>
    /// Cast memorized spell (or use power) <paramref name="number"/> on
    /// <paramref name="target"/> outside combat. Only the healing ones do
    /// anything yet (cure light, serious and critical wounds, raise dead,
    /// cell adjustment); the rest need combat. Returns what happened, as
    /// DSUN.EXE's messages put it.
    /// </summary>
    public string CastOutside(Character caster, int number, Vitals target, Random random)
    {
        if (Get(number) is not { } s)
        {
            return "NOTHING HAPPENS";
        }
        bool power = s.Kind == SpellKind.Psionic;
        if (power ? !caster.Powers.Contains(number) : Ready(caster, number) < 1)
        {
            return power ? "CAN'T START THAT PSIONIC" : "NO SUCH SPELL TO CAST";
        }
        if (power && Psp(caster) < Rules.PowerCost)
        {
            return "YOU NEED TO REST";
        }
        int Dice(int n, int sides, int plus) => Enumerable.Range(0, n).Sum(_ => random.Next(1, sides + 1)) + plus;
        int? heal = number switch
        {
            72 => Dice(1, 8, 0),      // cure light wounds: 1–8
            113 => Dice(2, 8, 1),     // cure serious wounds: 3–17
            128 => Dice(3, 8, 3),     // cure critical wounds: 6–27
            152 => Dice(2, 4, 0),     // cell adjustment
            _ => null,
        };
        string result;
        if (number == 136) // raise dead
        {
            if (target.Status is < Vitals.Dead or >= Vitals.Destroyed)
            {
                return "NOTHING HAPPENS";
            }
            (target.Status, target.Hp) = (Vitals.Well, 1);
            result = "RAISED FROM THE DEAD";
        }
        else if (heal is int hp)
        {
            if (target.Status >= Vitals.Dead || target.Hp >= target.MaxHp)
            {
                return "NOTHING HAPPENS";
            }
            int before = target.Hp;
            target.Hp = Math.Min(target.MaxHp, target.Hp + hp);
            if (target.Status == Vitals.Down && target.Hp > 0)
            {
                target.Status = Vitals.Well;
            }
            result = $"HEALED {target.Hp - before}";
        }
        else
        {
            return "ONLY IN COMBAT";
        }
        if (power)
        {
            caster.PspSpent += Rules.PowerCost;
        }
        else
        {
            caster.Cast.Add(number);
        }
        return result;
    }

    /// <summary>A rest: every memorized spell is ready again and psionic strength is full.</summary>
    public static void Rest(Character c)
    {
        c.Cast.Clear();
        c.PspSpent = 0;
    }
}
