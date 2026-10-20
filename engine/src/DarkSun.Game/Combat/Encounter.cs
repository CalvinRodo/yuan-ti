using DarkSun.Game.Characters;
using DarkSun.Game.Movement;
using DarkSun.Game.Rules;
using DarkSun.Game.World;

namespace DarkSun.Game.Combat;

/// <summary>
/// A fight starting in a region (FIGHT, <c>7372:030B</c>; or the party attacking
/// a creature): the party members still standing, each as its character sheet
/// makes it (<see cref="PartyCombatant"/>), and the creatures of a fighting
/// allegiance (&amp; 3) within <see cref="CombatRules.SightRange"/> of one of
/// them (<c>612E:0000</c>), as <see cref="Combatant.FromCreature"/> reads them.
/// A creature attacked that wasn't fighting turns hostile (allegiance 2).
/// </summary>
public static class Encounter
{
    /// <summary>
    /// The battle, or null when there is no enemy. A creature that falls leaves
    /// the map at once (<see cref="GameWorld.Fell"/>: its body, the party's
    /// experience for it).
    /// </summary>
    public static Battle? Start(GameWorld world, Func<int, Character?> sheets, ICombatDice dice, CharacterRules characterRules,
        CombatRules? rules = null, SpellRules? spells = null, CreatureRules? creatures = null, WorldObject? attacked = null)
    {
        rules ??= CombatRules.Default;
        if (attacked is { Kind: not ObjectKind.Party } && (attacked.Allegiance & 3) == 0)
        {
            attacked.Allegiance = 2;
        }
        List<Combatant> all = [];
        List<WorldObject> party = [.. world.Party.Where(p => !p.Hidden && p.Vitals.Alive)];
        foreach (WorldObject member in party)
        {
            Combatant c = sheets(member.Index) is { } sheet
                ? PartyCombatant.From(sheet, member, world.Items, characterRules)
                : Combatant.FromCreature(member, world.Items);
            c.Side = 1;
            c.HomeSide = 1;
            all.Add(c);
        }
        foreach (WorldObject o in world.Objects.Where(o => o.Kind == ObjectKind.Creature && !o.Hidden && o.Vitals.Alive
            && (o.Allegiance & 3) != 0 && (o == attacked || party.Any(p => Battle.Distance(p.Cell, o.Cell) <= rules.SightRange))))
        {
            Combatant c = Combatant.FromCreature(o, world.Items);
            c.Side = o.Allegiance;
            all.Add(c);
        }
        if (!all.Any(c => c.Fights && all.Any(c.IsEnemyOf)))
        {
            return null;
        }
        var battle = new Battle(all, new RegionCombatMap(world.Region), dice, rules, spells, creatures)
        {
            Catalog = world.Items,
            Difficulty = world.Difficulty,
            NewItem = rdff => world.NewItem(rdff, 1, 3),
        };
        battle.Defeated += c =>
        {
            if (c.Source is { Kind: not ObjectKind.Party } o)
            {
                world.Fell(o);
            }
        };
        return battle;
    }

    /// <summary>
    /// After the fight: the party members' sheets get what they used
    /// (<see cref="Battle.SyncSheets"/>) and the party gets up
    /// (<see cref="Battle.Finish"/>). True: the party has fallen.
    /// </summary>
    public static bool End(Battle battle)
    {
        battle.SyncSheets();
        return battle.Finish();
    }
}
