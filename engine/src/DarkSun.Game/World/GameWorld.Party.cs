using DarkSun.Game.Movement;

namespace DarkSun.Game.World;

public sealed partial class GameWorld
{
    public int? CollapsedLeader { get; private set; }

    /// <summary>Gather the other party members into this member, then travel as one.</summary>
    public void CollapseParty(int leader)
    {
        if (this[leader] is not { Kind: ObjectKind.Party, Hidden: false, Walker: { } walker })
        {
            throw new ArgumentException("The party leader must be on the map.", nameof(leader));
        }
        ExpandParty();
        CollapsedLeader = leader;
        walker.Stop();
        foreach (WorldObject member in Party.Where(p => p.Index != leader && !p.Hidden && p.Walker is not null))
        {
            member.Walker!.Join(walker, Region.IsBlocked);
        }
    }

    /// <summary>Called after walking: gather arrivals and keep collapsed members at the leader's script-visible cell.</summary>
    public void UpdateCollapsedParty()
    {
        if (CollapsedLeader is not { } index || this[index] is not { Walker: { } leader } main)
        {
            return;
        }
        if (main.Hidden || !main.Vitals.Alive)
        {
            ExpandParty();
            return;
        }
        foreach (WorldObject member in Party.Where(p => p.Index != index && !p.Hidden && p.Walker is not null))
        {
            Walker walker = member.Walker!;
            bool arrived = !member.Collapsed && walker.Cell == leader.Cell && !walker.IsWalking;
            if (arrived || member.Collapsed && walker.Cell != leader.Cell)
            {
                member.Collapsed = true;
                walker.Carry(leader.Cell);
                Changed?.Invoke(member, null);
            }
        }
    }

    /// <summary>Spread gathered members onto free cells before independent movement or combat.</summary>
    public void ExpandParty()
    {
        if (CollapsedLeader is null)
        {
            return;
        }
        foreach (WorldObject member in Party.Where(p => p.Index != CollapsedLeader))
        {
            if (member.Walker is not { } walker || member.Hidden)
            {
                continue;
            }
            walker.Stop();
            Cell cell = NearestCell(walker.Cell, c => walker.CanStand(c, Region.IsBlocked))
                ?? throw new InvalidOperationException($"No free cell to expand party member {member.Index + 1}.");
            if (!walker.Teleport(cell, Region.IsBlocked))
            {
                throw new InvalidOperationException($"Could not expand party member {member.Index + 1} at {cell}.");
            }
            member.Collapsed = false;
            Changed?.Invoke(member, null);
        }
        CollapsedLeader = null;
    }

    /// <summary>Restore packed members without making them walk in from their temporary load positions.</summary>
    public void RestoreCollapsedParty(int leader, IEnumerable<int> collapsed)
    {
        CollapseParty(leader);
        foreach (int slot in collapsed)
        {
            if (slot != leader && this[slot] is { Kind: ObjectKind.Party, Walker: { } walker } member)
            {
                member.Collapsed = true;
                walker.Carry(this[leader]!.Cell);
                Changed?.Invoke(member, null);
            }
        }
    }
}
