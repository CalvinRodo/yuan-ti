using DarkSun.Data.Items;
using DarkSun.Game.Scripting;

namespace DarkSun.Game.World;

/// <summary>What the player can do to an object from its right-click menu.</summary>
public enum Verb { Talk, Get, Use, UseWith, Attack, Look, Open }

/// <summary>
/// The verbs an object offers. Talk, Get and Use follow DSUN.EXE's verb
/// window (<c>68BD:0040</c>, flags from <c>68BD:0C6D</c>): Get for an item
/// whose type doesn't have <c>IT1R</c> +8 bit 0x20, Use for a door or an item
/// whose type has flag 1 (a door offers only Use), and each of the three
/// when the object has that trigger. DSUN.EXE runs the look trigger on every
/// right-click and has an INFO button; here that is Look, always offered.
/// Attack (creatures outside the party, or an attack trigger) and Use with
/// (a use-with trigger naming the object) are reached elsewhere in the
/// original (combat, the inventory) and added here, as is Open for a
/// container (<see cref="GameWorld.IsContainer"/>; DSUN.EXE's VIEW INVENTORY).
/// </summary>
public static class ObjectVerbs
{
    public static IReadOnlyList<Verb> For(WorldObject obj, ItemCatalog items, GplTriggers triggers, bool container = false)
    {
        bool Has(GplTriggerList list) => triggers.MatchObject(list, obj.Id) is not null;
        ItemType? type = obj.Item is { } item ? items.TypeOf(item) : null;
        bool door = obj.IsDoor;
        var verbs = new List<Verb>();
        if (Has(GplTriggerList.TalkTo))
        {
            verbs.Add(Verb.Talk);
        }
        if (container)
        {
            verbs.Add(Verb.Open);
        }
        if ((obj.Item is not null && !door && type is not { Fixed: true }) || Has(GplTriggerList.Pickup))
        {
            verbs.Add(Verb.Get);
        }
        if (door || type is { Kind: var kind } && (kind & ItemTypeFlags.Usable) != 0 || Has(GplTriggerList.Use))
        {
            verbs.Add(Verb.Use);
        }
        if (UseWithPartners(obj, triggers).Any())
        {
            verbs.Add(Verb.UseWith);
        }
        if ((obj.Kind == ObjectKind.Creature && obj.Record is not null) || Has(GplTriggerList.Attack))
        {
            verbs.Add(Verb.Attack);
        }
        verbs.Add(Verb.Look);
        return verbs;
    }

    /// <summary>The ids of the objects a use-with trigger pairs <paramref name="obj"/> with.</summary>
    public static IEnumerable<int> UseWithPartners(WorldObject obj, GplTriggers triggers) =>
        triggers.Records(GplTriggerList.UseWith)
            .Where(t => (short)t.A == (short)obj.Id || (short)t.B == (short)obj.Id)
            .Select(t => (short)t.A == (short)obj.Id ? (short)t.B : (short)t.A)
            .Select(id => (int)id);
}
