using DarkSun.Data.Animation;
using DarkSun.Game.Characters;
using DarkSun.Game.Combat;
using DarkSun.Game.Movement;
using DarkSun.Game.Rules;
using DarkSun.Game.Saves;
using DarkSun.Game.Scripting;
using DarkSun.Game.World;

namespace DarkSun.Game.Tests;

public class PartyCollapseTests
{
    private static GameWorld Party()
    {
        GameWorld world = GameWorldTests.World();
        foreach (WorldObject member in world.Party)
        {
            member.Walker!.Remove();
            member.Vitals.Hp = member.Vitals.MaxHp = 20;
            member.Vitals.Status = Vitals.Well;
        }
        foreach (WorldObject member in world.Party)
        {
            Assert.True(member.Walker!.Teleport(new Cell(7 - member.Index, 8), world.Region.IsBlocked));
        }
        return world;
    }

    private static void Walk(GameWorld world, int from, int ticks)
    {
        for (int tick = from; tick < from + ticks; tick++)
        {
            foreach (WorldObject member in world.Party.Where(p => !p.Collapsed))
            {
                member.Walker!.Update(tick, world.Region.IsBlocked);
            }
            world.UpdateCollapsedParty();
            world.UpdateFollowers();
        }
    }

    /// <summary>Ticks until every follower has gathered into the leader.</summary>
    private static int TicksToGather(GameWorld world)
    {
        for (int tick = 0; tick < 4000; tick += 10)
        {
            if (world.Party.Skip(1).All(p => p.Collapsed))
            {
                return tick;
            }
            Walk(world, tick, 10);
        }
        return int.MaxValue;
    }

    [Fact]
    public void AQuickCollapseGathersFasterAndLeavesTheirPaceAsItWas()
    {
        GameWorld walking = Party();
        walking.CollapseParty(0);
        GameWorld quick = Party();
        quick.CollapseParty(0, speed: 3);

        int slow = TicksToGather(walking), fast = TicksToGather(quick);

        Assert.True(fast < slow && fast * 2 < slow, $"quick {fast} ticks, walking {slow}");
        Assert.All(quick.Party, p => Assert.Equal(1, p.Walker!.Speed));
    }

    [Fact]
    public void TheOthersFollowTheLeaderAndKeepCloseOnFreeCells()
    {
        GameWorld world = Party();
        world.FollowLeader(0);
        world[0]!.Walker!.WalkTo(new Cell(16, 8), world.Region.IsBlocked);

        Walk(world, 0, 4000);

        Assert.Equal(new Cell(16, 8), world[0]!.Cell);
        Assert.All(world.Party.Skip(1), p => Assert.True(Math.Max(Math.Abs(p.Cell.X - 16), Math.Abs(p.Cell.Y - 8)) <= 2, $"member {p.Index} at {p.Cell}"));
        Assert.Equal(4, world.Party.Select(p => p.Cell).Distinct().Count());
        Assert.All(world.Party, p => Assert.False(p.Walker!.IsWalking));
    }

    [Fact]
    public void WithoutFollowingTheOthersStayWhereTheyAre()
    {
        GameWorld world = Party();
        world[0]!.Walker!.WalkTo(new Cell(16, 8), world.Region.IsBlocked);

        Walk(world, 0, 4000);

        Assert.Equal(new Cell(16, 8), world[0]!.Cell);
        Assert.All(world.Party.Skip(1), p => Assert.Equal(new Cell(7 - p.Index, 8), p.Cell));
    }

    [Fact]
    public void CollapsingEndsFollowing()
    {
        GameWorld world = Party();
        world.FollowLeader(0);
        world.CollapseParty(0);

        Assert.Null(world.FollowedLeader);
    }

    [Fact]
    public void FollowersWalkIntoTheLeaderThenTravelWithoutOccupyingCells()
    {
        GameWorld world = Party();
        world.CollapseParty(0);
        Assert.All(world.Party, p => Assert.False(p.Collapsed));
        Assert.Contains(world.Party, p => p.Index != 0 && p.Walker!.IsWalking);

        Walk(world, 0, 2000);

        Assert.Equal(0, world.CollapsedLeader);
        Assert.All(world.Party.Skip(1), p => Assert.True(p.Collapsed));
        Assert.All(world.Party, p => Assert.Equal(world[0]!.Cell, p.Cell));
        Assert.DoesNotContain(world.Occupancy.Cells, c => c.Value is 1 or 2 or 3);
        Assert.Equal(4, world.Select(DialogueGplHost.WholeParty).Count());
        Assert.All(world.Party, p => Assert.False(p.Hidden));

        world[0]!.Walker!.WalkTo(new Cell(9, 8), world.Region.IsBlocked);
        Walk(world, 2000, 2000);
        Assert.All(world.Party, p => Assert.Equal(new Cell(9, 8), p.Cell));
        Assert.Equal(0, world.Occupancy.OwnerOf(new Cell(9, 8)));
    }

    [Fact]
    public void ExpandingRestoresAllFourMembersWithoutOverlaps()
    {
        GameWorld world = Party();
        world.CollapseParty(0);
        Walk(world, 0, 2000);

        world.ExpandParty();

        Assert.Null(world.CollapsedLeader);
        Assert.All(world.Party, p => Assert.False(p.Collapsed));
        Assert.Equal(4, world.Party.Select(p => p.Cell).Distinct().Count());
        Assert.All(world.Party, p => Assert.Equal(p.Index, world.Occupancy.OwnerOf(p.Cell)));
    }

    [Fact]
    public void CancellingWhileFollowersAreWalkingInDoesNotLetThemOverlapTheLeader()
    {
        GameWorld world = Party();
        world.CollapseParty(0);
        Walk(world, 0, 10);

        world.ExpandParty();
        Walk(world, 10, 2000);

        Assert.Equal(4, world.Party.Select(p => p.Cell).Distinct().Count());
        Assert.All(world.Party, p => Assert.Equal(p.Index, world.Occupancy.OwnerOf(p.Cell)));
    }

    [Fact]
    public void FollowersGatherAtTheLeaderAfterItMoves()
    {
        GameWorld world = Party();
        world.CollapseParty(0);
        Walk(world, 0, 10);
        world[0]!.Walker!.WalkTo(new Cell(9, 8), world.Region.IsBlocked);

        Walk(world, 10, 4000);

        Assert.All(world.Party.Skip(1), p => Assert.True(p.Collapsed));
        Assert.All(world.Party, p => Assert.Equal(new Cell(9, 8), p.Cell));
        Assert.Equal(0, world.Occupancy.OwnerOf(new Cell(9, 8)));
    }

    [Fact]
    public void TeleportingACreatureDoesNotExpandTheParty()
    {
        GameWorld world = Party();
        world.CollapseParty(0);
        Walk(world, 0, 2000);

        world.Teleport(-100, world.Region.Number, 12, 5);

        Assert.Equal(0, world.CollapsedLeader);
        Assert.All(world.Party.Skip(1), p => Assert.True(p.Collapsed));
    }

    [Fact]
    public void TeleportingTheWholePartyKeepsItsCollapsedState()
    {
        GameWorld world = Party();
        world.CollapseParty(0);
        Walk(world, 0, 2000);

        world.Teleport(GameWorld.WholeParty, world.Region.Number, 12, 8);

        Assert.Equal(0, world.CollapsedLeader);
        Assert.All(world.Party.Skip(1), p => Assert.True(p.Collapsed));
        Assert.All(world.Party, p => Assert.Equal(world[0]!.Cell, p.Cell));
        Assert.DoesNotContain(world.Occupancy.Cells, c => c.Value is 1 or 2 or 3);
    }

    [Fact]
    public void CombatSpreadsOutThePartyAndIncludesEveryMember()
    {
        GameWorld world = Party();
        world.CollapseParty(0);
        Walk(world, 0, 2000);
        WorldObject foe = world.First(-100)!;
        foe.Allegiance = 2;
        foe.Vitals.Status = Vitals.Well;
        foe.Vitals.Hp = foe.Vitals.MaxHp = 10;

        Battle battle = Encounter.Start(world, _ => null, new ScriptedDice(), CharacterRules.Default)!;

        Assert.NotNull(battle);
        Assert.Equal(4, battle.Combatants.Count(c => c.PartyMember));
        Assert.Null(world.CollapsedLeader);
        Assert.Equal(4, world.Party.Select(p => p.Cell).Distinct().Count());
    }

    [Fact]
    public void SavingAndRestoringKeepsTheCollapsedParty()
    {
        GameWorld world = Party();
        world.CollapseParty(0);
        Walk(world, 0, 2000);
        var machine = new GplMachine(new Scripts(), new LoggingGplHost(), [], new BorlandRandom(1));
        SaveGame save = SaveGame.Capture("collapsed", machine, world.EngineVariables, world, new RegionMemory(),
            world.Party.Select(p => new PartyMemberSave(1, p.Cell.X, p.Cell.Y, Direction.East)), 0, []);
        string path = Path.Combine(Path.GetTempPath(), $"yuan-ti-collapse-{Guid.NewGuid():N}.json");
        try
        {
            save.Write(path);
            save = SaveGame.Read(path);
        }
        finally
        {
            File.Delete(path);
        }

        GameWorld restored = Party();
        restored.RestoreCollapsedParty(save.CollapsedLeader!.Value,
            save.Party.Select((member, slot) => (member, slot)).Where(p => p.member.Collapsed).Select(p => p.slot));

        Assert.Equal(0, restored.CollapsedLeader);
        Assert.All(restored.Party.Skip(1), p => Assert.True(p.Collapsed));
        Assert.All(restored.Party, p => Assert.Equal(restored[0]!.Cell, p.Cell));
        Assert.DoesNotContain(restored.Occupancy.Cells, c => c.Value is 1 or 2 or 3);
    }
}
