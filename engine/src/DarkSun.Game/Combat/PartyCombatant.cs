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
    /// (slot 1); its memorized spells not yet cast, its powers, ranks and
    /// psionic points. Without a weapon it fights as the new-character
    /// template has it (<c>38D4:0048</c>: its attacks a round of 1d1); a thri-kreen
    /// with four claws of 1d4 a round and a bite of 1d4 + 1 (8 and 2 halves;
    /// <c>8E77:038F</c>, <c>6ECD:1344</c>), and Move 15 (others 12).
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
        List<int> ready = [.. c.Memorized];
        foreach (int cast in c.Cast)
        {
            ready.Remove(cast);
        }
        int psp = magic.Psp(c);
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
            ArmorClass = CharacterSheet.ArmorClass(dex, member.Items, catalog) - CharacterSheet.DexterityDefence(dex),
            ShieldClass = shields,
            Thac0 = CharacterSheet.Thac0(active),
            Move = c.Race == Race.ThriKreen ? 15 : 12,
            Stats = stats,
            Level = Math.Max(1, active.Select(a => a.Level).DefaultIfEmpty(1).Max()),
            Race = (int)c.Race + 1,
            Alignment = (int)c.Alignment + 1,
            Classes = [.. active.Select(a => (Spellcasting.ClassCode(a.Class, c.Sphere), a.Level))],
            Saves = CharacterSheet.Saves(active, c.Race, stats[(int)Ability.Con]),
            AttackHalves = CharacterSheet.AttackHalves(active),
            NaturalAttacks = c.Race == Race.ThriKreen ? [new(8, 1, 4, 0), new(2, 1, 4, 1)] : [new(CharacterSheet.AttackHalves(active), 1, 1, 0)],
            Weapon = WornIn(3),
            MissileWeapon = WornIn(2),
            Ammunition = member.Items.FirstOrDefault(i => i.Slot == 1)?.Quantity ?? 0,
            ThiefLevel = magic.Level(c, CharacterClass.Thief),
            Spells = ready,
            SpellsAtStart = [.. ready],
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
