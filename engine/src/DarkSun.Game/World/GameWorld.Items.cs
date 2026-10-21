using DarkSun.Data.Items;
using DarkSun.Data.Regions;
using DarkSun.Game.Characters;
using DarkSun.Game.Magic;
using DarkSun.Game.Movement;
using DarkSun.Game.Scripting;

namespace DarkSun.Game.World;

/// <summary>
/// Items, damage and the other things scripts do to objects' contents
/// (<c>7372</c>, <c>612E:18DA</c>, <c>87B3</c>). Script values: a negative
/// number is a name (−<c>OJFF</c>), 0 or more an object index, 9999 none,
/// 32766 the whole party, and as an item 32767 any item (or, as a count, all).
/// </summary>
public sealed partial class GameWorld
{
    /// <summary>"Any item" as an item, "all" as a count.</summary>
    public const int Any = 0x7FFF;

    /// <summary>The whole party (32766).</summary>
    public const int WholeParty = DialogueGplHost.WholeParty;

    /// <summary>A new item from item <c>RDFF</c> <paramref name="rdff"/>, or null if it isn't one.</summary>
    /// <summary>An item as saved (<see cref="Item.Save"/>), its charges included.</summary>
    public Item? NewItem(ItemState s) => NewItem(s.Rdff, s.Quantity, s.Slot) is { } item
        ? (s.Charges is int charges ? WithCharges(item, charges) : item)
        : null;

    private static Item WithCharges(Item item, int charges)
    {
        item.Charges = charges;
        return item;
    }

    public Item? NewItem(int rdff, int quantity = 1, int slot = Equipment.Pack) =>
        _objectData.TryRead("RDFF", Math.Abs(rdff), out var data) && ItemRecord.FromRdff(data) is { } record
            ? new Item(Math.Abs(rdff), record, quantity, slot)
            : null;

    /// <summary>
    /// Give <paramref name="member"/> a new character's gear (<see cref="StartingGear"/>,
    /// <c>6ECD:1D48</c>): each item worn in its slot if its class may (<see cref="EquipAt"/>,
    /// as <c>1A0A:20EC</c>), else in the pack (<c>1A0A:1CD6</c>); a stack (the arrows) with its
    /// record's quantity. Returns what was given.
    /// </summary>
    public IReadOnlyList<Item> GiveStartingGear(WorldObject member, ItemClasses classes, bool thriKreen)
    {
        var given = new List<Item>();
        foreach (GearPiece piece in StartingGear.For(classes, thriKreen))
        {
            if (member.Items.Count >= Equipment.Capacity || NewItem(piece.Ojff) is not { } item)
            {
                continue;
            }
            if (Stacks(item.Record))
            {
                item.Quantity = Math.Max(1, (int)item.Record.Quantity);
            }
            member.Items.Add(item);
            if (EquipAt(member, item, piece.Slot) != EquipResult.Worn)
            {
                item.Slot = Equipment.Pack;
            }
            given.Add(item);
        }
        return given;
    }

    /// <summary>Do items of this record stack (one entry with a quantity)? <c>IT1R</c> flags bit 2 (<c>7372:0FEE</c>).</summary>
    public bool Stacks(ItemRecord record) => Items.TypeOf(record) is { Stacks: true };

    /// <summary>
    /// The "Dead Body" <c>OJFF</c> (an item, special 5) DSUN.EXE puts where a
    /// creature dies, holding what it carried (<c>612E</c>).
    /// </summary>
    public const int DeadBody = 1101;

    /// <summary>A party member's item classes (by slot; the session's characters), or null to let it use anything.</summary>
    public Func<int, ItemClasses?> PartyClasses { get; set; } = _ => null;

    /// <summary>Can <paramref name="obj"/> hold items: a creature, a party member, or an item (a container)?</summary>
    private static bool Holds(WorldObject? obj) => obj is { Kind: ObjectKind.Party or ObjectKind.Creature } or { Item: not null };

    /// <summary>The party members, in slot order.</summary>
    private IEnumerable<WorldObject> Members => Party.Where(p => !p.Hidden);

    /// <summary>
    /// GIVE(count, item, to, from) (<c>043D:15FE</c> → <c>7372:0D93</c>): move
    /// up to <paramref name="count"/> of <paramref name="item"/> from one
    /// holder to another; from 9999 makes new ones, to 9999 destroys them.
    /// Returns how many moved.
    /// </summary>
    public int Give(int count, int item, int to, int from) => Transfer(First(from), First(to), to == None, count, item);

    /// <summary>
    /// TAKE(count, item, from, to) (<c>043D:1DE9</c>, <c>7372:0D1D</c>): from
    /// the whole party, member by member until enough; from a member, the
    /// active member first.
    /// </summary>
    public int Take(int count, int item, int from, int to)
    {
        if (from == WholeParty)
        {
            int total = 0;
            foreach (WorldObject member in Members.ToList())
            {
                if (total >= count)
                {
                    break;
                }
                // DSUN.EXE asks each member for the whole count again; asking for what is left keeps to the count.
                total += TakeFrom(count - total, item, member.Index, to);
            }
            return total;
        }
        return TakeFrom(count, item, from, to);
    }

    private int TakeFrom(int count, int item, int from, int to)
    {
        int moved = 0;
        if (from is >= 0 and < PartySize && this[ActiveMember] is { } active)
        {
            moved = Transfer(active, First(to), to == None, count, item);
        }
        if (moved < count)
        {
            moved += Transfer(First(from), First(to), to == None, count - moved, item);
        }
        return moved;
    }

    /// <summary><c>7372:0D93</c>: the move itself, one entry at a time.</summary>
    private int Transfer(WorldObject? from, WorldObject? to, bool destroy, int count, int item)
    {
        if (from is null)
        {
            return Holds(to) ? Create(to!, Math.Abs(item), count) : 0;
        }
        if (!destroy && !Holds(to))
        {
            return 0;
        }
        int moved = 0;
        while (moved < count)
        {
            Item? found = from.Items.FirstOrDefault(i => i.Id == item || item == Any);
            if (found is null)
            {
                break;
            }
            if (destroy)
            {
                // One at a time: a stack loses one, a single item goes.
                if (found.Quantity < 2)
                {
                    from.Items.Remove(found);
                }
                else
                {
                    found.Quantity--;
                }
                moved++;
            }
            else if (count - moved < found.Quantity && found.Quantity > 1)
            {
                // Part of a stack: new ones for the receiver, fewer for the giver.
                int made = Create(to!, found.Rdff, count - moved);
                found.Quantity -= made;
                moved += made;
                if (made == 0)
                {
                    break;
                }
            }
            else
            {
                if (to!.Items.Count >= Equipment.Capacity)
                {
                    break;
                }
                from.Items.Remove(found);
                found.Slot = Equipment.Pack;
                to.Items.Add(found);
                moved += Math.Max(1, found.Quantity);
            }
        }
        return moved;
    }

    /// <summary><c>7372:0FEE</c>: new items for <paramref name="to"/>'s pack: one stack, or one entry each.</summary>
    private int Create(WorldObject to, int rdff, int count)
    {
        int made = 0;
        while (made < count && to.Items.Count < Equipment.Capacity && NewItem(rdff) is { } item)
        {
            if (Stacks(item.Record))
            {
                item.Quantity = count - made;
            }
            to.Items.Add(item);
            made += item.Quantity;
        }
        return made;
    }

    /// <summary>
    /// DROP(count, item, who) (<c>043D:12A5</c>, <c>7372:0370</c>): the
    /// matching creatures put items on the map where they stand. 1 if as many
    /// as asked were dropped (or the count was "all"), else 0.
    /// </summary>
    public int Drop(int count, int item, int who)
    {
        if (who == Any)
        {
            return 0;
        }
        IEnumerable<WorldObject> holders = who == WholeParty ? Members : Select(who);
        int dropped = 0;
        foreach (WorldObject holder in holders.Where(h => h.Kind is ObjectKind.Party or ObjectKind.Creature).ToList())
        {
            foreach (Item carried in holder.Items.Where(i => i.Id == item || item == Any).ToList())
            {
                if (dropped >= count)
                {
                    break;
                }
                int n = Math.Min(Math.Max(1, carried.Quantity), count - dropped);
                if (DropItem(holder, carried, n) is not null)
                {
                    dropped += n;
                }
            }
        }
        return dropped >= count || count == Any ? 1 : 0;
    }

    /// <summary>
    /// Put <paramref name="quantity"/> of <paramref name="item"/> on the map at
    /// <paramref name="holder"/>'s cell (<c>7372:04E4</c>): a new object of the
    /// item's <c>OJFF</c>. Null if it can't be made.
    /// </summary>
    public WorldObject? DropItem(WorldObject holder, Item item, int quantity)
    {
        if (Spawn(item.Rdff, holder.Cell) is not { } ground)
        {
            return null;
        }
        if (quantity < item.Quantity)
        {
            item.Quantity -= quantity;
        }
        else
        {
            holder.Items.Remove(item);
            quantity = item.Quantity;
        }
        ground.WriteField(82, quantity);
        return ground;
    }

    /// <summary>
    /// Get: <paramref name="taker"/> picks up the item <paramref name="ground"/>
    /// lying on the map; it leaves the map. False if it isn't an item, the
    /// pack is full, or it still holds something (a container is emptied first,
    /// so nothing in it is lost).
    /// </summary>
    public bool PickUp(WorldObject taker, WorldObject ground)
    {
        if (ground.Item is null || ground.Source is not { } source || taker.Items.Count >= Equipment.Capacity
            || ground.Items.Count > 0 || NewItem(source.Ojff) is not { } item)
        {
            return false;
        }
        int quantity = ground.ReadField(82);
        item.Quantity = quantity > 0 ? quantity : Math.Max(1, (int)ground.Item.Quantity);
        if (!Stacks(item.Record))
        {
            item.Quantity = 1;
        }
        taker.Items.Add(item);
        Remove(ground);
        return true;
    }

    /// <summary>
    /// USE on an item that teaches (<see cref="Spellcasting.Teaches"/>,
    /// <c>91F7:03D0</c>): <paramref name="character"/> learns its spell or power
    /// (<see cref="Spellcasting.LearnFromItem"/>) and the item goes from
    /// <paramref name="holder"/>; null when the item teaches nothing.
    /// <paramref name="name"/> names a spell by number for the message.
    /// </summary>
    public string? LearnFrom(WorldObject holder, Item item, Character character, Func<int, string> name)
    {
        if (!Spellcasting.Teaches(item.Rdff))
        {
            return null;
        }
        int number = item.Record.Flags;
        string message = Spellcasting.LearnFromItem(character, number, name(number), out bool consumed);
        if (consumed)
        {
            holder.Items.Remove(item);
        }
        return message;
    }

    /// <summary>
    /// May <paramref name="holder"/> use <paramref name="item"/>: its type's
    /// classes share a bit with a party member's (<c>6466:0854</c>)? Others
    /// (creatures, members without a character) may use anything.
    /// </summary>
    public bool CanUse(WorldObject holder, Item item) =>
        Items.TypeOf(item.Record) is not { } type || holder.Kind != ObjectKind.Party
        || PartyClasses(holder.Index) is not { } classes || type.UsableBy(classes);

    /// <summary>Wear <paramref name="item"/> in a free slot of its placement, if <paramref name="holder"/>'s class may (<see cref="CanUse"/>).</summary>
    public EquipResult Equip(WorldObject holder, Item item)
    {
        if (Items.TypeOf(item.Record) is not { Placement: not ItemPlacement.None } type)
        {
            return EquipResult.NotWearable;
        }
        if (!CanUse(holder, item))
        {
            return EquipResult.WrongClass;
        }
        if (Equipment.FreeSlot(type.Placement, holder.Items) is not int slot)
        {
            return EquipResult.NoFreeSlot;
        }
        item.Slot = slot;
        return EquipResult.Worn;
    }

    /// <summary>
    /// Wear <paramref name="item"/> in equipment slot <paramref name="slot"/>,
    /// which must take its placement (<see cref="Equipment.Placements"/>);
    /// what was there goes back to the pack.
    /// </summary>
    public EquipResult EquipAt(WorldObject holder, Item item, int slot)
    {
        if (Items.TypeOf(item.Record) is not { Placement: not ItemPlacement.None } type
            || slot is < 0 or >= Equipment.Slots || Equipment.Placements[slot] != type.Placement)
        {
            return EquipResult.NotWearable;
        }
        if (!CanUse(holder, item))
        {
            return EquipResult.WrongClass;
        }
        foreach (Item there in holder.Items.Where(i => i.Slot == slot && i != item))
        {
            there.Slot = Equipment.Pack;
        }
        item.Slot = slot;
        return EquipResult.Worn;
    }

    /// <summary>
    /// Take <paramref name="count"/> off a stack into an entry of its own in
    /// <paramref name="holder"/>'s pack. Null if it isn't a stack of more, or
    /// the pack is full.
    /// </summary>
    public Item? Split(WorldObject holder, Item item, int count)
    {
        if (!Stacks(item.Record) || count < 1 || count >= item.Quantity || holder.Items.Count >= Equipment.Capacity
            || NewItem(item.Rdff, count) is not { } part)
        {
            return null;
        }
        item.Quantity -= count;
        holder.Items.Insert(holder.Items.IndexOf(item) + 1, part);
        return part;
    }

    // -- containers -----------------------------------------------------------------------

    /// <summary>
    /// Is <paramref name="obj"/> a container on the map: an item whose type
    /// has flag 8 (chests, bags, skeletons; <c>6527:12BD</c>), a dead body, or
    /// any item that holds something?
    /// </summary>
    public bool IsContainer(WorldObject obj) =>
        obj.Item is { } item && obj.Kind == ObjectKind.Thing
        && (Items.TypeOf(item) is { IsContainer: true } || obj.Id == -DeadBody || obj.Items.Count > 0);

    /// <summary>
    /// Move <paramref name="item"/> (a whole entry) from <paramref name="from"/>
    /// to <paramref name="to"/>, into the pack; a stack joins one of the same
    /// item. False if <paramref name="to"/> is full.
    /// </summary>
    public bool MoveItem(WorldObject from, WorldObject to, Item item)
    {
        if (!from.Items.Contains(item))
        {
            return false;
        }
        if (Stacks(item.Record) && to.Items.FirstOrDefault(i => i.Rdff == item.Rdff && !i.Equipped) is { } same)
        {
            same.Quantity += item.Quantity;
            from.Items.Remove(item);
            return true;
        }
        if (to.Items.Count >= Equipment.Capacity)
        {
            return false;
        }
        from.Items.Remove(item);
        item.Slot = Equipment.Pack;
        to.Items.Add(item);
        return true;
    }

    /// <summary>
    /// <paramref name="creature"/> has died: a dead body (<see cref="DeadBody"/>)
    /// where it lay takes everything it carried (<c>612E</c>). Null if it
    /// carried nothing or there is no room for the body.
    /// </summary>
    public WorldObject? LeaveBody(WorldObject creature)
    {
        if (creature.Items.Count == 0 || Spawn(DeadBody, creature.Cell) is not { } body)
        {
            return null;
        }
        foreach (Item item in creature.Items)
        {
            item.Slot = Equipment.Pack;
        }
        body.Items = [.. creature.Items];
        creature.Items = [];
        return body;
    }

    // -- making and removing objects ------------------------------------------------------

    /// <summary>
    /// A new object of <c>OJFF</c> <paramref name="ojff"/> at the free index its
    /// kind takes, standing on <paramref name="cell"/>: drawn on its own, like a
    /// clone. Null if there is no room or no such object.
    /// </summary>
    public WorldObject? Spawn(int ojff, Cell cell)
    {
        bool creature = IsCreature(ojff);
        int first = creature ? PartySize : MaxCreatures, last = creature ? MaxCreatures : MaxObjects;
        int index = Enumerable.Range(first, last - first).FirstOrDefault(n => _objects[n] is null, -1);
        int x = cell.X * Region.TileSize + Region.TileSize / 2, y = (cell.Y + 1) * Region.TileSize - 1;
        if (index < 0 || Region.MakeObject(_objectData, ojff, x, y) is not { } source)
        {
            return null;
        }
        WorldObject obj = MakeObject(index, source);
        obj.IsClone = true;
        obj.FixedCell = cell;
        _objects[index] = obj;
        Changed?.Invoke(obj, null);
        return obj;
    }

    /// <summary>Take <paramref name="obj"/> out of the region for good (picked up, dead, replaced).</summary>
    public void Remove(WorldObject obj)
    {
        if (obj.Kind == ObjectKind.Party || _objects[obj.Index] != obj)
        {
            return;
        }
        obj.Hidden = true;
        obj.Walker?.Remove();
        _objects[obj.Index] = null;
        Changed?.Invoke(obj, null);
    }

    /// <summary>
    /// <c>REQUEST(9, who, ojff, b)</c> (<c>87B3:0F1C</c>): replace each
    /// matching object with a new object of <c>OJFF</c> <paramref name="ojff"/>
    /// where it stood (where it first stood if <paramref name="atStart"/>);
    /// the new one becomes "other" (<c>gname[8]</c>).
    /// </summary>
    public void Transform(int who, int ojff, bool atStart)
    {
        foreach (WorldObject old in Select(who).ToList())
        {
            Cell at = atStart && old.Source is { } s
                ? new Cell(s.EtabX / Region.TileSize, Math.Max(0, s.EtabY - 1) / Region.TileSize)
                : old.Cell;
            Remove(old);
            if (Spawn(ojff, at) is { } made)
            {
                SetEngineVariable(0x28, made.Index);
            }
        }
    }

    // -- damage and rest ------------------------------------------------------------------

    /// <summary>
    /// DAMAGE(who, amount) and PHYS_DAMAGE(who, percent) (<c>043D:0C8A</c>,
    /// <c>7372:12AA</c> / <c>1342</c>): hurt each match (32766: each party
    /// member); PHYS_DAMAGE takes that percentage of the creature's hit points.
    /// </summary>
    public void Damage(int who, int amount, bool percent)
    {
        if (who == Any)
        {
            return;
        }
        IEnumerable<WorldObject> targets = who == WholeParty ? Members : Select(who);
        foreach (WorldObject t in targets.Where(t => t.Kind is ObjectKind.Party or ObjectKind.Creature).ToList())
        {
            Hurt(t, percent ? (int)((long)amount * t.Vitals.MaxHp / 100) : amount);
        }
    }

    /// <summary>A creature (not a party member) went down, died or was destroyed: <see cref="KillExperience"/> is due.</summary>
    public event Action<WorldObject>? Defeated;

    /// <summary>
    /// What each party member gets for <paramref name="creature"/> falling
    /// (<c>612E:25F2</c>): for an enemy (allegiance 2), its character record's
    /// experience value divided by the party slots in use (the fallen
    /// included, <c>6868:02E9</c>), cut to 16 bits; 0 for anyone else.
    /// <see cref="WorldGplHost.AwardKill"/> then gives it as AWARD does.
    /// </summary>
    public int KillExperience(WorldObject creature)
    {
        int slots = Party.Count();
        return creature is { Allegiance: 2, Character: { } c } && slots > 0 ? (short)(c.ExperienceValue / slots) : 0;
    }

    /// <summary>
    /// <c>612E:18DA</c>: less than the hit points takes that many; up to 9
    /// more leaves the creature down (status 3, 0 hit points); more kills it
    /// (5), 10 000 or more destroys it (8). A creature that is no longer
    /// alive leaves the map, leaving a dead body with what it carried unless
    /// destroyed (<see cref="LeaveBody"/>); a party member stays where it fell.
    /// </summary>
    public void Hurt(WorldObject target, int damage)
    {
        if (damage < 1)
        {
            return;
        }
        Vitals v = target.Vitals;
        if (damage < v.Hp)
        {
            v.Hp -= damage;
            return;
        }
        v.Status = damage < v.Hp + 10 ? Vitals.Down : damage < 10000 ? Vitals.Dead : Vitals.Destroyed;
        v.Hp = 0;
        Fell(target);
    }

    /// <summary>
    /// <paramref name="target"/>'s hit points are gone (here or in a battle): a
    /// creature down, dead or destroyed counts as defeated (experience,
    /// <see cref="Defeated"/>) and leaves the map, its body with what it carried
    /// unless destroyed; a party member stays where it fell.
    /// </summary>
    public void Fell(WorldObject target)
    {
        if (target.Kind != ObjectKind.Party && target.Vitals.Status >= Vitals.Down && _objects[target.Index] == target)
        {
            Defeated?.Invoke(target);
            if (target.Vitals.Status < Vitals.Destroyed)
            {
                LeaveBody(target);
            }
            Remove(target);
        }
        else
        {
            Changed?.Invoke(target, null);
        }
    }

    /// <summary>
    /// <c>REQUEST(1)</c> / <c>REQUEST(4)</c> (<c>87B3:073D</c>): the party
    /// rests. Members who are down (status 2 or 3) get up; with
    /// <paramref name="raiseDead"/> (REQUEST 1) the dead too. Those standing
    /// are healed to full (the original's spell and healing details aren't traced).
    /// </summary>
    /// <summary>The party has rested (<see cref="Rest"/>): spells and psionic strength come back.</summary>
    public event Action? Rested;

    public void Rest(bool raiseDead)
    {
        foreach (WorldObject member in Party)
        {
            Vitals v = member.Vitals;
            if (raiseDead || v.Status is 2 or 3)
            {
                v.Status = Vitals.Well;
            }
            if (v.Status == Vitals.Well)
            {
                v.Hp = v.MaxHp;
            }
        }
        Rested?.Invoke();
    }

    /// <summary>
    /// <c>REQUEST(20, who, mask)</c> (<c>87B3:13B9</c>): how many creatures
    /// are in the region (not the party) whose allegiance shares a bit with
    /// <paramref name="mask"/>, or all with mask 0.
    /// </summary>
    public int CountCreatures(int mask) =>
        Objects.Count(o => o.Kind == ObjectKind.Creature && !o.Hidden && (mask == 0 || (mask & o.Allegiance) != 0));

    // -- SEARCH (043D:08F8) ---------------------------------------------------------------

    /// <summary>
    /// SEARCH(who, range, criteria…) (<c>043D:08F8</c>): over the items
    /// <paramref name="who"/> carries (32766: each party member's). Criteria
    /// with op 4 (<c>=</c>; 5 and 6 taken as <c>&gt;</c> and <c>&lt;</c>,
    /// unused by the shipped scripts) pick items; the last criterion's op says
    /// what comes back, combined over members as the original does: 0 the
    /// smallest value of its field, 1 the largest, 2 the total, 3 how many
    /// items (assumed: <c>0826:0344</c> is not traced), 4–6 1 if any item
    /// matches. The range bytes (77–80, 0–0 in the scripts) are not traced and
    /// not used.
    /// </summary>
    public int Search(int who, IReadOnlyList<GplSearchTest> criteria)
    {
        if (criteria.Count == 0)
        {
            return 0;
        }
        int op = criteria[^1].Op;
        long result = op == 0 ? int.MaxValue : 0;
        IEnumerable<WorldObject> holders = who == WholeParty ? Members : who >= 0 && this[who] is { } one ? [one] : [];
        foreach (WorldObject holder in holders)
        {
            long value = SearchOne(holder, criteria, op);
            result = op switch
            {
                0 => Math.Min(result, value),
                1 => Math.Max(result, value),
                2 or 3 => result + value,
                _ => result | value,
            };
        }
        return (int)Math.Clamp(result, int.MinValue, int.MaxValue);
    }

    private static long SearchOne(WorldObject holder, IReadOnlyList<GplSearchTest> criteria, int op)
    {
        var tests = criteria.Where(c => c.Op is >= 4 and <= 6).ToList();
        var matches = holder.Items.Where(i => tests.All(t => t.Op switch
        {
            4 => i.Field(t.Field) == t.Value,
            5 => i.Field(t.Field) > t.Value,
            _ => i.Field(t.Field) < t.Value,
        })).ToList();
        int field = criteria[^1].Field;
        return op switch
        {
            0 => matches.Count == 0 ? int.MaxValue : matches.Min(i => (long)i.Field(field)),
            1 => matches.Count == 0 ? 0 : matches.Max(i => (long)i.Field(field)),
            2 => matches.Sum(i => (long)i.Field(field)),
            3 => matches.Sum(i => (long)Math.Max(1, i.Quantity)),
            _ => matches.Count > 0 ? 1 : 0,
        };
    }
}
