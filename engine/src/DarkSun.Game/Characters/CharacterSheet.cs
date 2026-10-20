using DarkSun.Data.Items;
using DarkSun.Game.World;

namespace DarkSun.Game.Characters;

/// <summary>
/// What the character sheet shows that is worked out rather than stored:
/// armour class and THAC0, AD&amp;D 2nd edition's. DSUN.EXE's are now traced
/// (<c>dsun-exe-reference.md</c> §8): its THAC0 is the same (<c>8E77:0416</c>);
/// its armour class (<c>612E:2113</c>) also gives gladiators −1 per 5 levels,
/// ignores shields from behind and lets spells cap the body armour:
/// <list type="bullet">
/// <item>armour class: 10, less each worn item's <c>IT1R</c> armour class
/// (Athas's armour comes in pieces: chest, arms, legs, a shield, a helm;
/// the shipped pieces give 1–4) and the bonus of each worn piece that has
/// one, plus Dexterity's defensive adjustment;</item>
/// <item>THAC0: the best of the classes' tables at the character's levels
/// (warriors 21 − level, priests 20 − 2 per 3 levels, rogues and
/// psionicists 20 − 1 per 2 levels, wizards 20 − 1 per 3 levels).</item>
/// </list>
/// </summary>
public static class CharacterSheet
{
    public const int BaseArmorClass = 10;

    /// <summary>Dexterity's defensive adjustment (added to armour class: lower is better).</summary>
    public static int DexterityDefence(int dex) => dex switch
    {
        <= 3 => 4,
        4 => 3,
        5 => 2,
        6 => 1,
        <= 14 => 0,
        15 => -1,
        16 => -2,
        17 => -3,
        <= 20 => -4,
        <= 23 => -5,
        _ => -6,
    };

    /// <summary>Armour class wearing <paramref name="items"/> (the worn ones count) with Dexterity <paramref name="dex"/>.</summary>
    public static int ArmorClass(int dex, IEnumerable<Item> items, ItemCatalog catalog)
    {
        int ac = BaseArmorClass + DexterityDefence(dex);
        foreach (Item item in items.Where(i => i.Equipped))
        {
            if (catalog.TypeOf(item.Record) is { ArmorClass: > 0 } type)
            {
                ac -= type.ArmorClass + Math.Max(0, (int)item.Record.Bonus);
            }
        }
        return ac;
    }

    /// <summary>THAC0 of one class at <paramref name="level"/>.</summary>
    public static int Thac0(CharacterClass c, int level)
    {
        int l = Math.Max(1, level) - 1;
        return c switch
        {
            CharacterClass.Fighter or CharacterClass.Gladiator or CharacterClass.Ranger => 20 - l,
            CharacterClass.Cleric or CharacterClass.Druid => 20 - 2 * (l / 3),
            CharacterClass.Preserver => 20 - l / 3,
            _ => 20 - l / 2,
        };
    }

    /// <summary>The best THAC0 of <paramref name="c"/>'s classes at <paramref name="levels"/> (one per class); 20 with none.</summary>
    public static int Thac0(Character c, IReadOnlyList<int> levels) =>
        c.Classes.Select((k, i) => Thac0(k, i < levels.Count ? levels[i] : 1)).DefaultIfEmpty(20).Min();

    /// <summary>The best THAC0 of the classes in use (<see cref="CharacterCreation.ActiveLevels"/>: a dual-class's former class counts once the new one passes it).</summary>
    public static int Thac0(IEnumerable<(CharacterClass Class, int Level)> active) =>
        active.Select(a => Thac0(a.Class, a.Level)).DefaultIfEmpty(20).Min();

    // -- saving throws (8E77:059B) ----------------------------------------------------------

    /// <summary>The four class groups (<c>40B1:0010</c>): priest, warrior, wizard, rogue.</summary>
    public static int GroupOf(CharacterClass c) => c switch
    {
        CharacterClass.Cleric or CharacterClass.Druid => 0,
        CharacterClass.Fighter or CharacterClass.Gladiator or CharacterClass.Ranger => 1,
        CharacterClass.Preserver => 2,
        _ => 3,
    };

    // 40B1:006C + 15·group + 3·save: (base, rate × 100, max) for paralysation, rod, petrification, breath, spell.
    private static readonly (int Base, int Rate, int Max)[][] SaveTable =
    [
        [(10, 45, 2), (14, 45, 6), (13, 45, 5), (16, 45, 8), (15, 45, 7)],
        [(14, 69, 3), (16, 69, 5), (15, 69, 4), (17, 82, 4), (17, 69, 6)],
        [(14, 30, 8), (11, 40, 3), (13, 40, 5), (15, 40, 7), (12, 40, 4)],
        [(13, 25, 8), (14, 50, 4), (12, 25, 7), (16, 25, 11), (15, 50, 5)],
    ];

    /// <summary>
    /// The five saving throws (<c>8E77:059B</c>; paralysation, rod, petrification,
    /// breath, spell): for each class in use, base − min(max, rate × (level − 1)
    /// ÷ 100) of its group, the lowest kept. A dwarf's or halfling's Con × 2 ÷ 7
    /// comes off the warrior group's five, as the original's test on the group
    /// counter has it (likely meant for rod and spell). 20 each with no class.
    /// </summary>
    public static int[] Saves(IEnumerable<(CharacterClass Class, int Level)> active, Race race, int con)
    {
        int[] saves = [20, 20, 20, 20, 20];
        foreach (var (k, level) in active)
        {
            int group = GroupOf(k);
            for (int i = 0; i < 5; i++)
            {
                var (b, rate, max) = SaveTable[group][i];
                int save = b - Math.Min(max, rate * (Math.Max(1, level) - 1) / 100);
                if (group == 1 && race is Race.Dwarf or Race.Halfling)
                {
                    save -= con * 2 / 7;
                }
                saves[i] = Math.Min(saves[i], save);
            }
        }
        return saves;
    }

    /// <summary>
    /// Attacks a round in halves (<c>8E77:038F</c>, character record +0x2A): 2,
    /// 3 from warrior level 1, 4 from 7, 5 from 13.
    /// </summary>
    public static int AttackHalves(IEnumerable<(CharacterClass Class, int Level)> active)
    {
        int warrior = active.Where(a => GroupOf(a.Class) == 1).Select(a => a.Level).DefaultIfEmpty(0).Max();
        return warrior >= 13 ? 5 : warrior >= 7 ? 4 : warrior >= 1 ? 3 : 2;
    }
}
