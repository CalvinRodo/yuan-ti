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
}
