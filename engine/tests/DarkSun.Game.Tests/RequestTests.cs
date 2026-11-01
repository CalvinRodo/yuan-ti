using System.Buffers.Binary;
using DarkSun.Data.Regions;
using DarkSun.Game.Characters;
using DarkSun.Game.Combat;
using DarkSun.Game.Rules;
using DarkSun.Game.Scripting;
using DarkSun.Game.World;

namespace DarkSun.Game.Tests;

/// <summary>REQUEST codes that need the world or the view (<c>87B3:0000</c>).</summary>
public class RequestTests
{
    /// <summary>A <c>MONR</c> record: the region, then up to 10 (<c>OJFF</c>, strength) slots.</summary>
    private static byte[] Record(int region, params (int Ojff, int Strength)[] slots)
    {
        var b = new byte[MonsterTable.RecordSize];
        BinaryPrimitives.WriteInt16LittleEndian(b, (short)region);
        for (int i = 0; i < slots.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(2 + i * 4), (short)slots[i].Ojff);
            BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(4 + i * 4), (short)slots[i].Strength);
        }
        return b;
    }

    private static (GplMachine Machine, WorldGplHost Host) Run(string source, MonsterTable monsters, CombatFixes? fixes = null)
    {
        GameWorld world = GameWorldTests.World(); // region 5
        // Party strength 3: a fighter/thief (level 1 in each: 1 + 1) and a fighter (1).
        Character[] sheets =
        [
            new() { Name = "Rikus", Classes = [CharacterClass.Fighter, CharacterClass.Thief] },
            new() { Name = "Neeva", Classes = [CharacterClass.Fighter] },
        ];
        var host = new WorldGplHost { Map = world, Monsters = monsters, Fixes = fixes ?? new(), PartyCharacter = slot => slot < sheets.Length ? sheets[slot] : null };
        var scripts = new Scripts();
        scripts.Files[(1, GplFileKind.Gpl)] = GplAssembler.Assemble(source);
        var machine = new GplMachine(scripts, host, [], new BorlandRandom(1));
        Assert.True(machine.Run(new GplEntry(1, 0)).Ok);
        return (machine, host);
    }

    private static int Gnum(GplMachine m, int n) => m.Memory.Read(new GplVariable(GplVarType.GlobalNum, n));

    [Fact]
    public void RandomMonsterPicksFromTheRegionsTableAndSizesTheGroupToTheParty()
    {
        MonsterTable monsters = MonsterTable.Parse([
            .. Record(5, (0, 0), (151, 2)),             // region 5: one monster, strength 2, after an empty slot
            .. Record(MonsterTable.AnyRegion, (160, 3), (160, 1)),
        ]);
        var (m, _) = Run("""
              gnum[0] = ((REQUEST(8, 0, 0, 0)))
              gnum[1] = ((REQUEST(8, 0, 1, 0)))
              gnum[2] = ((REQUEST(8, 0, 0, 1)))
              gnum[3] = ((REQUEST(8, 0, 1, 1)))
              EXIT_GPL
            """, monsters);

        Assert.Equal(151, Gnum(m, 0)); // empty slots are never picked
        Assert.Equal(3, Gnum(m, 1));   // 2 × 3 / 2; the slot after it is empty (the EXE would divide by 0), so its own strength
        Assert.Equal(160, Gnum(m, 2)); // b ≠ 0: the table used anywhere
        Assert.Equal(6, Gnum(m, 3));   // 2 × 3 / 1: the next slot's strength, as 87B3:0CB4 divides, not its own 3; at most 6
    }

    [Fact]
    public void RandomMonsterCountUsesThePicksOwnStrengthWithTheFix()
    {
        var (m, _) = Run("""
              gnum[0] = ((REQUEST(8, 0, 0, 1)))
              gnum[1] = ((REQUEST(8, 0, 1, 1)))
              EXIT_GPL
            """, MonsterTable.Parse(Record(MonsterTable.AnyRegion, (160, 3), (160, 1))), new CombatFixes(MonsterCountFromItsSlot: true));

        Assert.Equal(160, Gnum(m, 0));
        Assert.Equal(2, Gnum(m, 1)); // 2 × 3 / 3, not 2 × 3 / 1
    }

    [Fact]
    public void RandomMonsterIsNoneWhereTheRegionHasNoTable()
    {
        var (m, _) = Run("""
              gnum[0] = ((REQUEST(8, 0, 0, 0)))
              gnum[1] = ((REQUEST(8, 0, 1, 0)))
              EXIT_GPL
            """, MonsterTable.Parse(Record(9, (151, 2))));

        Assert.Equal((0, 0), (Gnum(m, 0), Gnum(m, 1)));
    }

    [Fact]
    public void ViewRequestsReachTheView()
    {
        GameWorld world = GameWorldTests.World();
        int? played = null;
        bool cursor = false;
        var host = new WorldGplHost { Map = world, CinematicRequested = n => (played = n) == 100, MoveCursorRequested = () => cursor = true };
        var scripts = new Scripts();
        scripts.Files[(1, GplFileKind.Gpl)] = GplAssembler.Assemble("""
              REQUEST(MOVE_CURSOR, 0, 0, 0)
              REQUEST(6, 0, 100, 0)
              gnum[0] = 1
              REQUEST(6, 0, 300, 0)
              REQUEST(19, 0, 0, 0)
              gnum[1] = 1
              EXIT_GPL
            """);
        var m = new GplMachine(scripts, host, [], new BorlandRandom(1));

        GplRunResult run = m.Run(new GplEntry(1, 0));

        Assert.True(cursor);
        Assert.Equal("REQUEST(MOVE_CURSOR=15, 0, 0, 0)", host.Log[0].Command.ToString()); // the script log
        Assert.True(run.Suspended); // the cinematic plays inside the request
        Assert.Equal(0, Gnum(m, 0));
        run = m.Resume();
        Assert.True(run.Ok);
        Assert.Equal(300, played); // a picture the view can't show: the script goes straight on
        Assert.True(host.GameOverRequested);
        Assert.Equal((1, 1), (Gnum(m, 0), Gnum(m, 1)));
    }
}
