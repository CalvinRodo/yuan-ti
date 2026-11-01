using DarkSun.Game.World;

namespace DarkSun.Game.Combat;

/// <summary>
/// The six abilities as DSUN.EXE works them out into a combat record (+0x22,
/// what fights and <c>STAT_ROLL</c> read; <c>80F5:2BB8</c>), from the
/// character's own (record +0x1B), the effects on it and the items it carries.
/// Two sets start from the character's: one that adds, one of floors.
/// <list type="bullet">
/// <item>Effects (the table at <c>cs:2F00</c>): Strength Enhanced (63) makes
/// Strength at least 24; Borrowed, Lent, Adrenalin Control, Strength and
/// Weakened (64–68) add their value to it, kept within 3–24 after each;
/// Feebleminded (19) makes Intelligence and Wisdom 3 whatever else.</item>
/// <item>Items: every item carried, worn or in the pack or a container (the
/// holder's whole tree, <c>1A0A:1A2D</c>; unlike armour class, nothing asks
/// whether it is worn), whose record +0x0F is 0xFA–0xFF, works on ability
/// +0x0F − 0xFA (Str, Dex, Con, Int, Wis, Cha) by its charges (+0x0E; the
/// jump table at <c>cs:2EF4</c> sends all six to one case): below 16 it adds
/// them, 16–31 makes the ability at least that, 32 and up takes away
/// charges − 31. The shipped ones: Belt of Might Str ≥ 24; Draketooth Str +3,
/// Helm of Might Str +1; El's Ring Dex +3; Steadfast Ring Con +3; Ring of
/// Insight Wis +2; Dag's Dagger Cha +1.</item>
/// </list>
/// With any floor at all, each ability is the higher of its two; else the
/// sum. None goes above 25. With <c>wornOnly</c>
/// (<see cref="Characters.CharacterRules.AbilityItemsWhenWorn"/>) only the items
/// worn count.
/// </summary>
public static class AbilityScores
{
    public const int Highest = 25, EffectHighest = 24, Least = 3, Feebleminded = 3;

    /// <summary>Item record +0x0F values from here on work on ability value − this.</summary>
    public const int FirstAbilityItem = 0xFA;

    private const int FloorFrom = 16, LossFrom = 32;

    public static int[] Of(IReadOnlyList<int> own, IEnumerable<ActiveEffect> effects, IEnumerable<Item> items, bool wornOnly = false)
    {
        int[] sum = [.. own.Take(6)], floor = [.. own.Take(6)];
        bool floors = false, feebleminded = false;
        foreach (ActiveEffect e in effects)
        {
            switch (e.Condition)
            {
                case Condition.Feebleminded:
                    feebleminded = true;
                    break;
                case Condition.StrengthEnhanced:
                    floors = true;
                    floor[0] = Math.Max(floor[0], EffectHighest);
                    break;
                case >= Condition.StrengthBorrowed and <= Condition.Weakened:
                    sum[0] = Math.Clamp(sum[0] + e.Value, Least, EffectHighest);
                    break;
            }
        }
        foreach (Item item in wornOnly ? items.Where(i => i.Equipped) : Carried(items))
        {
            int ability = item.Record.Flags - FirstAbilityItem, by = item.Charges & 0xFF;
            if (ability is < 0 or > 5)
            {
                continue;
            }
            if (by < FloorFrom)
            {
                sum[ability] += by;
            }
            else if (by < LossFrom)
            {
                floors = true;
                floor[ability] = Math.Max(floor[ability], by);
            }
            else
            {
                sum[ability] -= by - (LossFrom - 1);
            }
        }
        int[] scores = new int[6];
        for (int i = 0; i < 6; i++)
        {
            scores[i] = feebleminded && i is 3 or 4 ? Feebleminded : Math.Min(Highest, floors ? Math.Max(sum[i], floor[i]) : sum[i]);
        }
        return scores;
    }

    private static IEnumerable<Item> Carried(IEnumerable<Item> items) => items.SelectMany(i => Carried(i.Contents).Prepend(i));
}
