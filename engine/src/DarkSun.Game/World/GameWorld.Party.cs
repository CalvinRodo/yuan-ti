using DarkSun.Game.Movement;

namespace DarkSun.Game.World;

public sealed partial class GameWorld
{
    public int? CollapsedLeader { get; private set; }

    /// <summary>
    /// Gather the other party members into this member, then travel as one.
    /// They walk in <paramref name="speed"/> times as fast as they walk (the
    /// original's members gather quickly).
    /// </summary>
    public void CollapseParty(int leader, int speed = 1)
    {
        if (this[leader] is not { Kind: ObjectKind.Party, Hidden: false, Walker: { } walker })
        {
            throw new ArgumentException("The party leader must be on the map.", nameof(leader));
        }
        ExpandParty();
        StopFollowing();
        CollapsedLeader = leader;
        walker.Stop();
        foreach (WorldObject member in Party.Where(p => p.Index != leader && !p.Hidden && p.Walker is not null))
        {
            member.Walker!.Speed = Math.Max(1, speed);
            member.Walker.Join(walker, Region.IsBlocked);
        }
    }

    /// <summary>The member the others follow (<see cref="FollowLeader"/>), or null when each walks alone.</summary>
    public int? FollowedLeader { get; private set; }

    /// <summary>
    /// As in DSUN.EXE, the others follow this member wherever it walks: each
    /// keeps to the free cell next to it nearest to them (the chase target
    /// chooser), until they are ordered elsewhere.
    /// </summary>
    public void FollowLeader(int leader) => FollowedLeader = leader;

    /// <summary>Each member walks alone again (a fight, a collapse, members ordered on their own).</summary>
    public void StopFollowing() => FollowedLeader = null;

    /// <summary>Called after walking: send each follower toward a free cell next to the leader once it has fallen behind.</summary>
    public void UpdateFollowers()
    {
        if (FollowedLeader is not { } index || CollapsedLeader is not null)
        {
            return;
        }
        if (this[index] is not { Kind: ObjectKind.Party, Hidden: false, Walker: { } leader } main || !main.Vitals.Alive)
        {
            StopFollowing();
            return;
        }
        var taken = new HashSet<Cell>();
        foreach (WorldObject member in Party.Where(p => p.Index != index && !p.Hidden && !p.Collapsed && p.Vitals.Alive && p.Walker is not null)
            .OrderBy(p => Distance(p.Walker!.Cell, leader.Cell)))
        {
            Walker walker = member.Walker!;
            if (Distance(walker.Cell, leader.Cell) <= 1 && !walker.IsWalking)
            {
                continue;
            }
            if (_followGoals.TryGetValue(member.Index, out Cell goal) && walker.IsWalking
                && Distance(goal, leader.Cell) <= 1 && !taken.Contains(goal))
            {
                taken.Add(goal);
                continue;
            }
            if (FollowCell(walker, leader, taken) is { } next)
            {
                taken.Add(next);
                _followGoals[member.Index] = next;
                walker.WalkTo(next, Region.IsBlocked);
            }
        }
    }

    private readonly Dictionary<int, Cell> _followGoals = [];

    private static int Distance(Cell a, Cell b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    /// <summary>The cell next to the leader (or one further out when those are full) that the follower can stand on, nearest to it.</summary>
    private Cell? FollowCell(Walker follower, Walker leader, HashSet<Cell> taken)
    {
        for (int ring = 1; ring <= 2; ring++)
        {
            Cell? best = null;
            int bestDistance = int.MaxValue;
            for (int dy = -ring; dy <= ring; dy++)
            {
                for (int dx = -ring; dx <= ring; dx++)
                {
                    var c = new Cell(leader.Cell.X + dx, leader.Cell.Y + dy);
                    if (Distance(c, leader.Cell) != ring || taken.Contains(c) || !follower.CanStand(c, Region.IsBlocked))
                    {
                        continue;
                    }
                    int d = Math.Abs(c.X - follower.Cell.X) + Math.Abs(c.Y - follower.Cell.Y);
                    if (d < bestDistance)
                    {
                        (best, bestDistance) = (c, d);
                    }
                }
            }
            if (best is not null)
            {
                return best;
            }
        }
        return null;
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
                walker.Speed = 1;
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
            walker.Speed = 1;
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
