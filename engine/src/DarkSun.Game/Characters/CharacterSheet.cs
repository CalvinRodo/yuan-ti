using DarkSun.Data.Items;
using DarkSun.Game.World;

namespace DarkSun.Game.Characters;

/// <summary>
/// What the character sheet shows that is worked out rather than stored:
/// armour class and THAC0, AD&amp;D 2nd edition's. DSUN.EXE's are now traced
/// (<c>dsun-exe-reference.md</c> §8): its THAC0 is <see cref="ClassRules.Thac0"/> (<c>8E77:0416</c>);
/// its armour class (<c>612E:2113</c>) also gives gladiators −1 per 5 levels,
/// ignores shields from behind and lets spells cap the body armour:
/// <list type="bullet">
/// <item>armour class: 10, less each worn item's <c>IT1R</c> armour class
/// (Athas's armour comes in pieces: chest, arms, legs, a shield, a helm;
/// the shipped pieces give 1–4) and the bonus of each worn piece that has
/// one, plus Dexterity's defensive adjustment;</item>
/// <item>THAC0: the best of the classes' at the character's levels
/// (<see cref="ClassRules.Thac0"/>: warriors 21 − level, priests 20 − 2 per
/// 3 levels, rogues and psionicists 20 − 1 per 2 levels, wizards 20 − 1 per
/// 3 levels).</item>
/// </list>
/// The tables come from <see cref="CharacterRules"/> (<c>rules/characters.json</c>);
/// each method takes them, <see cref="CharacterRules.Default"/> when left out.
/// </summary>
public static class CharacterSheet
{
    public const int BaseArmorClass = 10;

    /// <summary>Dexterity's defensive adjustment (added to armour class: lower is better).</summary>
    public static int DexterityDefence(int dex, CharacterRules? rules = null) =>
        (rules ?? CharacterRules.Default).Abilities.DexDefenceOf(dex);

    /// <summary>Armour class wearing <paramref name="items"/> (the worn ones count) with Dexterity <paramref name="dex"/>.</summary>
    public static int ArmorClass(int dex, IEnumerable<Item> items, ItemCatalog catalog, CharacterRules? rules = null)
    {
        int ac = BaseArmorClass + DexterityDefence(dex, rules);
        foreach (Item item in items.Where(i => i.Equipped))
        {
            if (catalog.TypeOf(item.Record) is { ArmorClass: > 0 } type)
            {
                ac -= type.ArmorClass + Math.Max(0, (int)item.Record.Bonus);
            }
        }
        return ac;
    }

    private static ClassRules? Of(CharacterClass c, CharacterRules? rules) =>
        (rules ?? CharacterRules.Default).Classes.FirstOrDefault(k => k.Class == c);

    /// <summary>THAC0 of one class at <paramref name="level"/>; 20 for a class the rules don't have.</summary>
    public static int Thac0(CharacterClass c, int level, CharacterRules? rules = null) => Of(c, rules)?.Thac0(level) ?? 20;

    /// <summary>The best THAC0 of <paramref name="c"/>'s classes at <paramref name="levels"/> (one per class); 20 with none.</summary>
    public static int Thac0(Character c, IReadOnlyList<int> levels, CharacterRules? rules = null) =>
        c.Classes.Select((k, i) => Thac0(k, i < levels.Count ? levels[i] : 1, rules)).DefaultIfEmpty(20).Min();

    /// <summary>The best THAC0 of the classes in use (<see cref="CharacterCreation.ActiveLevels"/>: a dual-class's former class counts once the new one passes it).</summary>
    public static int Thac0(IEnumerable<(CharacterClass Class, int Level)> active, CharacterRules? rules = null) =>
        active.Select(a => Thac0(a.Class, a.Level, rules)).DefaultIfEmpty(20).Min();

    // -- saving throws (8E77:059B) ----------------------------------------------------------

    /// <summary>
    /// The five saving throws (<c>8E77:059B</c>; paralysation, rod, petrification,
    /// breath, spell): for each class in use, its <see cref="ClassRules.Saves"/>
    /// at its level, the lowest kept. A dwarf's or halfling's
    /// (<see cref="RaceRules.ConstitutionSaves"/>) Con × 2 ÷ 7 comes off a
    /// warrior class's five, as the original's test on the group counter has it
    /// (likely meant for rod and spell; <see cref="CharacterRules.ConstitutionSavesWarriorsOnly"/>).
    /// <see cref="CharacterRules.NoClassSave"/> each with no class.
    /// </summary>
    public static int[] Saves(IEnumerable<(CharacterClass Class, int Level)> active, Race race, int con, CharacterRules? rules = null)
    {
        rules ??= CharacterRules.Default;
        bool conSaves = rules.Races.FirstOrDefault(r => r.Race == race)?.ConstitutionSaves == true;
        int[] saves = [.. Enumerable.Repeat(rules.NoClassSave, 5)];
        foreach (var (k, level) in active)
        {
            if (Of(k, rules) is not { Saves: { } rows } kind)
            {
                continue;
            }
            for (int i = 0; i < Math.Min(5, rows.Count); i++)
            {
                int save = rows[i].At(level);
                if (conSaves && (kind.Warrior || !rules.ConstitutionSavesWarriorsOnly))
                {
                    save -= con * 2 / 7;
                }
                saves[i] = Math.Min(saves[i], save);
            }
        }
        return saves;
    }

    /// <summary>
    /// Attacks a round in halves (<c>8E77:038F</c>, character record +0x2A):
    /// <see cref="CharacterRules.BaseAttackHalves"/> (2), then by the best
    /// warrior class level (<see cref="CharacterRules.AttackSteps"/>: 3 from
    /// level 1, 4 from 7, 5 from 13).
    /// </summary>
    public static int AttackHalves(IEnumerable<(CharacterClass Class, int Level)> active, CharacterRules? rules = null)
    {
        rules ??= CharacterRules.Default;
        int warrior = active.Where(a => Of(a.Class, rules)?.Warrior == true).Select(a => a.Level).DefaultIfEmpty(0).Max();
        return rules.AttackSteps.Where(s => warrior >= s.WarriorLevel).Select(s => s.Halves).DefaultIfEmpty(rules.BaseAttackHalves).Last();
    }
}
