using DarkSun.Data.Items;
using DarkSun.Game.Characters;
using DarkSun.Game.Magic;
using DarkSun.Game.World;

namespace DarkSun.Game.Combat;

/// <summary>
/// A party member as a combatant: what DSUN.EXE keeps in its character and
/// combat records (<c>8E77</c>: THAC0, saves, attacks) worked out from the
/// character sheet, and the weapons it wears.
/// </summary>
public static class PartyCombatant
{
    /// <summary>
    /// <paramref name="c"/>, standing as <paramref name="member"/> (whose hit
    /// points it shares): its abilities with race adjustments; the best THAC0,
    /// the saves and attacks of the classes in use
    /// (<see cref="CharacterCreation.ActiveLevels"/>); armour class from what it
    /// wears, without Dexterity (the battle adds that), shields apart; the
    /// weapon in hand (slot 3), a missile weapon (slot 2) and its ammunition
    /// (slot 1); the spells it knows at the levels with casts left (and those
    /// casts, <see cref="Combatant.CastsLeft"/>), its powers, ranks and
    /// psionic points. Without a weapon it fights as the new-character
    /// template has it (<see cref="CharacterRules.UnarmedDice"/>: its attacks a
    /// round of 1d1), or with its race's <see cref="RaceRules.NaturalAttacks"/>
    /// (a thri-kreen's four claws of 1d4 a round and a bite of 1d4 + 1); its
    /// race's <see cref="RaceRules.Move"/>. The tables are all <paramref name="rules"/>'.
    /// </summary>
    public static Combatant From(Character c, WorldObject member, ItemCatalog catalog, CharacterRules rules)
    {
        var creation = new CharacterCreation(rules, CharacterNames.Empty, new Random(0));
        var magic = new Spellcasting(rules);
        IReadOnlyList<(CharacterClass Class, int Level)> active = creation.ActiveLevels(c);
        int[] stats = [.. Enum.GetValues<Ability>().Select(a => creation.Adjusted(c, a))];
        Weapon? WornIn(int slot) =>
            member.Items.FirstOrDefault(i => i.Slot == slot) is { } item && catalog.TypeOf(item.Record) is { IsWeapon: true } type
                ? new Weapon(type, item.Record.Bonus, item)
                : null;
        int shields = member.Items.Where(i => i.Equipped && catalog.TypeOf(i.Record) is { Weapon: var w } && (w & WeaponKind.Shield) != 0)
            .Sum(i => catalog.TypeOf(i.Record)!.ArmorClass + Math.Max(0, (int)i.Record.Bonus));
        int dex = stats[(int)Ability.Dex];
        static int LevelKey(SpellKind kind, int level) => (int)kind * 16 + level;
        Dictionary<int, int> castsLeft = magic.Levels(c).Where(l => magic.CastsLeft(c, l.Kind, l.Level) > 0)
            .ToDictionary(l => LevelKey(l.Kind, l.Level), l => magic.CastsLeft(c, l.Kind, l.Level));
        Dictionary<int, int> spellLevels = magic.Castable(c).ToDictionary(s => s.Number, s => LevelKey(s.Kind, s.Level));
        int psp = magic.Psp(c);
        RaceRules? race = rules.Races.FirstOrDefault(r => r.Race == c.Race);
        int halves = CharacterSheet.AttackHalves(active, rules);
        return new Combatant
        {
            Name = c.Name.Length > 0 ? c.Name : member.Name,
            Side = 1,
            HomeSide = 1,
            Vitals = member.Vitals,
            Cell = member.Cell,
            Facing = member.Facing,
            PartyMember = true,
            ComputerControlled = c.ComputerControl,
            ArmorClass = CharacterSheet.ArmorClass(dex, member.Items, catalog, rules) - CharacterSheet.DexterityDefence(dex, rules),
            ShieldClass = shields,
            Thac0 = CharacterSheet.Thac0(active, rules),
            Move = race?.Move ?? 12,
            Stats = stats,
            Level = Math.Max(1, active.Select(a => a.Level).DefaultIfEmpty(1).Max()),
            Race = (int)c.Race + 1,
            Alignment = (int)c.Alignment + 1,
            Classes = [.. active.Select(a => (Spellcasting.ClassCode(a.Class, c.Sphere), a.Level))],
            Saves = CharacterSheet.Saves(active, c.Race, stats[(int)Ability.Con], rules),
            AttackHalves = halves,
            NaturalAttacks = race?.NaturalAttacks is { Count: > 0 } natural ? [.. natural] : [new(halves, rules.UnarmedDice, rules.UnarmedSides, 0)],
            Weapon = WornIn(3),
            MissileWeapon = WornIn(2),
            Ammunition = member.Items.FirstOrDefault(i => i.Slot == 1)?.Quantity ?? 0,
            ThiefLevel = magic.Level(c, CharacterClass.Thief),
            Spells = [.. spellLevels.Keys],
            SpellLevels = spellLevels,
            CastsLeft = castsLeft,
            Powers = [.. c.Powers],
            PowerRanks = Combatant.PowerRanksOf(c),
            Psp = psp,
            PspAtStart = psp,
            Items = member.Items,
            Source = member,
            Sheet = c,
        };
    }
}
