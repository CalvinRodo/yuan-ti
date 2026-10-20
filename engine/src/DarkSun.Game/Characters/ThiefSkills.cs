using DarkSun.Game.Combat;

namespace DarkSun.Game.Characters;

/// <summary>
/// The thief skills as DSUN.EXE rolls them (<c>REQUEST(3, who, skill,
/// situation)</c>, <c>87B3:028D</c>): no code rolls one on its own, only the
/// scripts (open locks, find/remove traps, climb walls, pick pockets, hear
/// noise) and the traps (<c>REQUEST(7)</c>). A d100 (<c>rand() % 100</c>) must
/// come under the chance plus the situation (<c>87B3:0488</c>). The tables are
/// <see cref="CharacterRules.ThiefSkills"/>.
/// </summary>
public static class ThiefSkills
{
    public const int PickPockets = 0, OpenLocks = 1, FindTraps = 2, MoveSilently = 3, HideInShadows = 4, HearNoise = 5,
        ClimbWalls = 6, ReadLanguages = 7;

    /// <summary>What the situation step adds to rule a skill out, or make it certain.</summary>
    public const int Certain = 1000;

    /// <summary>The item <see cref="CharacterRules.SilentItemWorks"/> is about, and where it is worn (<c>87B3:05FF</c>).</summary>
    public const int SilentItem = 2005, SilentItemSlot = 6;

    /// <summary>Someone's state for a skill check: its thief level, Dexterity, what it wears (slot, item id) and its conditions.</summary>
    /// <param name="Okay">In Okay condition (combat record +0x1C = 1); anything else has no skills.</param>
    /// <param name="ConditionLevel">The level of a condition's effect (Enlarge's), 0 without it.</param>
    public sealed record Who(Race Race, int ThiefLevel, int Dex, bool Okay, IReadOnlyList<(int Slot, int Id)> Worn,
        IReadOnlySet<Condition> Conditions, Func<Condition, int>? ConditionLevel = null);

    /// <summary>
    /// The chance (<c>87B3:030B</c>): 0 without thief levels (unless finding
    /// traps under Detect Traps), not Okay, or hearing noise with something worn
    /// in <see cref="CharacterRules.NoHearingSlots"/>; else the skill's base +
    /// <see cref="CharacterRules.ThiefSkillPerLevel"/> a level + the race's
    /// adjustment, Dexterity's, less the gear penalty.
    /// </summary>
    public static int Chance(CharacterRules rules, Who who, int skill)
    {
        if (!Eligible(rules, who, skill))
        {
            return 0;
        }
        ThiefSkill s = rules.ThiefSkills[skill];
        IReadOnlyList<int>? race = rules.Races.FirstOrDefault(r => r.Race == who.Race)?.ThiefSkills;
        int chance = s.Base + (race is not null && skill < race.Count ? race[skill] : 0) + who.ThiefLevel * rules.ThiefSkillPerLevel;
        if (s.DexBelow > who.Dex)
        {
            chance -= (s.DexBelow - who.Dex) * 5;
        }
        if (who.Dex > s.DexAbove)
        {
            chance += (who.Dex - s.DexAbove) * 5;
        }
        if (who.Dex > s.DexWellAbove)
        {
            chance -= (who.Dex - s.DexWellAbove) * 3;
        }
        if (who.Worn.Any(w => rules.GearSlots.Contains(w.Slot)))
        {
            chance -= s.GearPenalty;
        }
        return chance;
    }

    private static bool Eligible(CharacterRules rules, Who who, int skill) =>
        skill >= 0 && skill < rules.ThiefSkills.Count
        && (who.ThiefLevel > 0 || skill == FindTraps && who.Conditions.Contains(Condition.DetectTraps))
        && !(skill == HearNoise && who.Worn.Any(w => rules.NoHearingSlots.Contains(w.Slot)))
        && who.Okay;

    /// <summary>
    /// The situation the script gives, as conditions change it (<c>87B3:04F5</c>):
    /// Berserk, Confused, Afraid and Paralyzed rule every skill out, Blind all but
    /// hearing noise, Slowed picking pockets, Mirror Image hiding, Fire Shield
    /// picking pockets and hiding, Graft Weapon picking pockets, opening locks and
    /// climbing, Feeblemind reading languages. Enlarge divides hiding's by
    /// (100 + 10 × its level) % and multiplies climbing's. Detect Traps makes
    /// finding traps certain, Invisible hiding.
    /// </summary>
    public static int Situation(CharacterRules rules, Who who, int skill, int situation)
    {
        bool Has(Condition c) => who.Conditions.Contains(c);
        if (Has(Condition.Berserk) || Has(Condition.Confused) || Has(Condition.Afraid) || Has(Condition.Paralyzed)
            || Has(Condition.Slowed) && skill == PickPockets
            || Has(Condition.Blind) && skill != HearNoise
            || Has(Condition.MirrorImages) && skill == HideInShadows
            || Has(Condition.FireShield) && skill is PickPockets or HideInShadows
            || Has(Condition.GraftWeapon) && skill is PickPockets or OpenLocks or ClimbWalls
            || Has(Condition.Feebleminded) && skill == ReadLanguages)
        {
            return situation - Certain;
        }
        if (Has(Condition.Enlarged))
        {
            int scale = (who.ConditionLevel?.Invoke(Condition.Enlarged) ?? 0) * 10 + 100;
            situation = skill switch
            {
                HideInShadows => situation * 100 / scale,
                ClimbWalls => scale * situation / 100,
                _ => situation,
            };
        }
        if (rules.SilentItemWorks && who.Worn.Contains((SilentItemSlot, SilentItem)))
        {
            if (skill == MoveSilently)
            {
                return situation + Certain;
            }
            if (skill == HearNoise)
            {
                return situation - Certain;
            }
        }
        return Has(Condition.DetectTraps) && skill == FindTraps || Has(Condition.Invisible) && skill == HideInShadows
            ? situation + Certain
            : situation;
    }

    /// <summary>
    /// What the d100 must come under: the chance plus the situation; with no
    /// chance at all, the situation as the script gave it (<c>87B3:030B</c>
    /// returns before <c>87B3:04F5</c> can change it).
    /// </summary>
    public static int Target(CharacterRules rules, Who who, int skill, int situation) =>
        Eligible(rules, who, skill) ? Chance(rules, who, skill) + Situation(rules, who, skill, situation) : situation;

    /// <summary>The roll (<c>87B3:0488</c>): <paramref name="roll"/> (0–32767) % 100 under <see cref="Target"/>.</summary>
    public static bool Succeeds(CharacterRules rules, Who who, int skill, int situation, int roll) =>
        roll % 100 < Target(rules, who, skill, situation);
}
