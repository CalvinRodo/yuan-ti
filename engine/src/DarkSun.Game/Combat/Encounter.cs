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
/// the party's side (<c>612E:281F</c>, <c>612E:0000</c>: the party members
/// and creatures of allegiance &amp; 1), as <see cref="Combatant.FromCreature"/>
/// reads them. Party members a script set watching (<see cref="Watching"/>,
/// REQUEST 14's arena bout) stay out. A creature attacked that wasn't fighting turns hostile (allegiance 2).
/// </summary>
public static class Encounter
{
    /// <summary>
    /// The battle, or null when there is no enemy. A creature that falls leaves
    /// the map at once (<see cref="GameWorld.Fell"/>: its body, the party's
    /// experience for it); with <paramref name="fallsWhenShown"/> the caller
    /// does that as it plays the battle's <see cref="Fell"/> back, since the
    /// battle runs ahead of what the screen shows.
    /// </summary>
    public static Battle? Start(GameWorld world, Func<int, Character?> sheets, ICombatDice dice, CharacterRules characterRules,
        CombatRules? rules = null, SpellRules? spells = null, CreatureRules? creatures = null, WorldObject? attacked = null,
        bool fallsWhenShown = false)
    {
        rules ??= CombatRules.Default;
        world.ExpandParty();
        world.StopFollowing();
        if (attacked is { Kind: not ObjectKind.Party } && (attacked.Allegiance & 3) == 0)
        {
            attacked.Allegiance = 2;
        }
        List<Combatant> all = [];
        List<WorldObject> party = [.. world.Party.Where(p => !p.Hidden && p.Vitals.Alive)];
        foreach (WorldObject member in party.Where(m => !Watching(m)))
        {
            Combatant c = sheets(member.Index) is { } sheet
                ? PartyCombatant.From(sheet, member, world.Items, characterRules)
                : Combatant.FromCreature(member, world.Items);
            c.Side = 1;
            c.HomeSide = 1;
            all.Add(c);
        }
        // 612E:281F: the range is from each of the party's side, party members and creatures of allegiance & 1 alike.
        List<WorldObject> anchors = [.. party.Where(m => !Watching(m)),
            .. world.Objects.Where(o => o.Kind == ObjectKind.Creature && !o.Hidden && o.Vitals.Alive && (o.Allegiance & 1) != 0)];
        foreach (WorldObject o in world.Objects.Where(o => o.Kind == ObjectKind.Creature && !o.Hidden && o.Vitals.Alive
            && (o.Allegiance & 3) != 0 && (o == attacked || anchors.Any(p => Battle.Distance(p.Cell, o.Cell) <= rules.SightRange))))
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
            Characters = characterRules,
            Difficulty = world.Difficulty,
            NewItem = rdff => world.NewItem(rdff, 1, 3),
        };
        if (!fallsWhenShown)
        {
            battle.Defeated += c =>
            {
                if (c.Source is { Kind: not ObjectKind.Party } o)
                {
                    world.Fell(o);
                }
            };
        }
        return battle;
    }

    /// <summary>
    /// A party member a script took out of fights (allegiance without the
    /// fighting bits: REQUEST 14's 4). The party's allegiance is otherwise
    /// left 0 here, standing for the original's 1.
    /// </summary>
    public static bool Watching(WorldObject member) => member.Allegiance != 0 && (member.Allegiance & 3) == 0;

    /// <summary>
    /// After the fight: the party members' sheets get what they used
    /// (<see cref="Battle.SyncSheets"/>), those who watched it may fight again,
    /// the opening bout's creatures leave (<c>87B3:11AC</c>),
    /// and the party gets up (<see cref="Battle.Finish"/>). True: the party
    /// fought and has fallen.
    /// </summary>
    public static bool End(Battle battle, GameWorld? world = null)
    {
        battle.SyncSheets();
        if (world?[world.ActiveMember]?.Allegiance == GameWorld.WatchingAllegiance)
        {
            foreach (WorldObject creature in world.Objects.Where(o => o.Id is -296 or -171 or -269).ToList())
            {
                world.Remove(creature);
            }
        }
        foreach (WorldObject member in world?.Party ?? [])
        {
            if (Watching(member))
            {
                member.Allegiance = 0; // the original's 1; 0 is the party's fighting allegiance here
            }
        }
        return battle.Finish();
    }
}
