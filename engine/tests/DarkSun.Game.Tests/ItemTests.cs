using DarkSun.Data.Gff;
using DarkSun.Data.Images;
using DarkSun.Data.Regions;
using DarkSun.Game.Movement;
using DarkSun.Game.Rules;
using DarkSun.Game.Scripting;
using DarkSun.Game.World;

namespace DarkSun.Game.Tests;

public class ItemTests
{
    private const int Width = 20, Height = 10;
    private const int Templar = 100, Torch = 200, Arrows = 300, Boots = 400;

    private sealed class Resources : IResourceSource
    {
        public Dictionary<(string, int), byte[]> Data { get; } = [];

        public bool Contains(string tag, int number) => Data.ContainsKey((tag, number));

        public bool TryRead(string tag, int number, out ReadOnlyMemory<byte> data)
        {
            bool found = Data.TryGetValue((tag, number), out byte[]? bytes);
            data = bytes ?? [];
            return found;
        }
    }

    private static byte[] Creature(int ojff, short hp)
    {
        var data = new byte[CombatRecord.HeaderSize + CombatRecord.Size];
        (data[0], data[2], data[8]) = (1, 2, CombatRecord.Size);
        int r = CombatRecord.HeaderSize;
        (data[r], data[r + 1]) = ((byte)hp, (byte)(hp >> 8));
        data[r + 6] = (byte)-ojff;
        data[r + 7] = 0xFF;
        data[r + 0x1C] = 1;
        data[r + 0x1D] = 2;
        System.Text.Encoding.Latin1.GetBytes("Templar").CopyTo(data, r + 40);
        return data;
    }

    /// <summary>An item <c>RDFF</c>: record of <c>IT1R</c> type <paramref name="type"/>, worth <paramref name="value"/>, named <c>NAME</c> <paramref name="name"/>.</summary>
    private static byte[] ItemRdff(int ojff, int type, int name, int value)
    {
        var item = new byte[10 + 21 + 1];
        (item[0], item[2], item[8]) = (1, 1, 21);
        int r = 10;
        (item[r], item[r + 1]) = ((byte)-ojff, 0xFF);
        (item[r + 6], item[r + 7]) = ((byte)value, (byte)(value >> 8));
        item[r + 10] = (byte)type;
        item[r + 18] = (byte)name;
        item[^1] = 0xFF;
        return item;
    }

    private static readonly ImageFrame Frame = new(10, 20, new byte[200], new bool[200]);

    /// <summary>A templar (20 hit points) at (10, 5), a torch on the ground at (2, 2), the party at (1, 8).</summary>
    private static (GameWorld World, IReadOnlyList<PartyMemberState> Party) World()
    {
        var r = new Resources();
        r.Data[("RDFF", Templar)] = Creature(Templar, 20);
        r.Data[("RDFF", Torch)] = ItemRdff(Torch, 0, 1, 2);
        r.Data[("RDFF", Arrows)] = ItemRdff(Arrows, 1, 2, 1);
        r.Data[("RDFF", Boots)] = ItemRdff(Boots, 2, 3, 300);
        // IT1R: type 0 nothing special; 1 stacks (flags +15 bit 2), ammunition (+9 = 11); 2 worn on the feet (4).
        var types = new byte[3 * 20];
        types[20 + 15] = 0x02;
        types[20 + 9] = 11;
        types[40 + 9] = 4;
        r.Data[("IT1R", 1)] = types;
        var names = new byte[4 * 25];
        foreach (var (n, text) in new[] { (1, "Torch"), (2, "Arrow"), (3, "Boots") })
        {
            System.Text.Encoding.Latin1.GetBytes(text).CopyTo(names, n * 25);
        }
        r.Data[("NAME", 1)] = names;
        RegionObject[] objects =
        [
            new(0, Templar, 10 * 16 + 8, 6 * 16 - 1, 0, 0, false, [Frame], 0),
            new(1, Torch, 2 * 16 + 8, 3 * 16 - 1, 0, 0, false, [Frame], 0),
        ];
        var party = Enumerable.Range(0, GameWorld.PartySize).Select(_ => new PartyMemberState()).ToList();
        var world = new GameWorld(Region.Create(5, Width, Height, new byte[Width * Height], objects), r, party: party);
        var spec = new CharacterSpec(WalkerTests.Script(), [(10, 20), (12, 22), (14, 24)]);
        for (int i = 0; i < 2; i++)
        {
            world.AddPartyMember(i, spec, new Cell(1, 8));
        }
        return (world, party);
    }

    private static WorldObject TemplarOf(GameWorld w) => w.First(-Templar)!;

    [Fact]
    public void GiveFromNobodyMakesItemsAndArrowsStack()
    {
        var (w, party) = World();

        Assert.Equal(2, w.Give(2, -Torch, 0, GameWorld.None));
        Assert.Equal(20, w.Give(20, -Arrows, 0, GameWorld.None));

        Assert.Equal([Torch, Torch, Arrows], party[0].Items.Select(i => i.Rdff));
        Assert.Equal(20, party[0].Items[2].Quantity);
        Assert.Same(party[0].Items, w[0]!.Items); // the party's carried things are kept for the game
        Assert.Equal(0, w.Give(1, -12345, 0, GameWorld.None)); // no such item
    }

    [Fact]
    public void GiveAndTakeMoveItemsBetweenHolders()
    {
        var (w, party) = World();
        WorldObject templar = TemplarOf(w);
        w.Give(3, -Torch, templar.Index, GameWorld.None);
        w.Give(10, -Arrows, templar.Index, GameWorld.None);

        Assert.Equal(2, w.Give(2, -Torch, 1, templar.Index));
        Assert.Equal(4, w.Give(4, -Arrows, 1, templar.Index)); // part of a stack

        Assert.Equal(2, party[1].Items.Count(i => i.Rdff == Torch));
        Assert.Equal(4, party[1].Items.Single(i => i.Rdff == Arrows).Quantity);
        Assert.Equal(6, templar.Items.Single(i => i.Rdff == Arrows).Quantity);

        // TAKE from the whole party to nobody: gone, one by one.
        Assert.Equal(2, w.Take(GameWorld.Any, -Torch, GameWorld.WholeParty, GameWorld.None));
        Assert.DoesNotContain(party[1].Items, i => i.Rdff == Torch);
        Assert.Equal(1, w.Take(1, -Arrows, 1, GameWorld.None));
        Assert.Equal(3, party[1].Items.Single(i => i.Rdff == Arrows).Quantity);
    }

    [Fact]
    public void SearchCountsMatchesAndTellsWhatIsWorn()
    {
        var (w, party) = World();
        w.Give(2, -Torch, 0, GameWorld.None);
        w.Give(1, -Torch, 1, GameWorld.None);
        w.Give(1, -Boots, 1, GameWorld.None);
        GplSearchTest Is(int field, int value) => new(field, 4, value);

        Assert.Equal(3, w.Search(GameWorld.WholeParty, [Is(72, -Torch), new(72, 3, 0)])); // how many torches
        Assert.Equal(1, w.Search(GameWorld.WholeParty, [Is(72, -Boots)])); // has boots
        Assert.Equal(0, w.Search(0, [Is(72, -Boots)]));
        Assert.Equal(0, w.Search(1, [Is(72, -Boots), Is(85, 13)])); // not worn yet

        Assert.True(w.Equip(w[1]!, party[1].Items.Single(i => i.Rdff == Boots)));

        Assert.Equal(13, party[1].Items.Single(i => i.Rdff == Boots).Slot); // the feet
        Assert.Equal(1, w.Search(1, [Is(72, -Boots), Is(85, 13)]));
        Assert.Equal(0, w.Search(-Templar, [Is(72, -Boots)])); // a name isn't searched
    }

    [Fact]
    public void GetPutsTheItemInThePackAndTakesItOffTheMap()
    {
        var (w, party) = World();
        WorldObject torch = w.First(-Torch)!;

        Assert.True(w.PickUp(w[0]!, torch));

        Assert.Equal(Torch, Assert.Single(party[0].Items).Rdff);
        Assert.Null(w[torch.Index]);
        Assert.DoesNotContain(w.Snapshot().Objects, o => o.Index == torch.Index);
    }

    [Fact]
    public void DamageHurtsDownsAndKillsAsTheOriginalCounts()
    {
        var (w, party) = World();
        WorldObject templar = TemplarOf(w);

        w.Damage(-Templar, 5, percent: false);
        Assert.Equal((15, Vitals.Well), (templar.Vitals.Hp, templar.Status));

        w.Damage(-Templar, 20, percent: false); // 15 hp, 20 damage: down, not dead
        Assert.Equal((0, Vitals.Down), (templar.Vitals.Hp, templar.Status));
        Assert.Null(w[templar.Index]); // out of the region

        party[0].Vitals.Hp = 10;
        w.Damage(0, 25, percent: false);
        Assert.Equal(Vitals.Dead, party[0].Vitals.Status);
        Assert.NotNull(w[0]); // a fallen member stays
        party[1].Vitals.Hp = 10;
        party[1].Vitals.MaxHp = 10;
        w.Damage(GameWorld.WholeParty, 1000, percent: true); // 1000 % of 10 hit points
        Assert.Equal(Vitals.Dead, party[1].Vitals.Status);

        w.Rest(raiseDead: false);
        Assert.Equal(Vitals.Dead, party[1].Vitals.Status);
        w.Rest(raiseDead: true);
        Assert.Equal((Vitals.Well, 10), (party[1].Vitals.Status, party[1].Vitals.Hp));
    }

    [Fact]
    public void ARegionRemembersWhatItsCreaturesCarry()
    {
        var (w, _) = World();
        WorldObject templar = TemplarOf(w);
        w.Give(5, -Arrows, templar.Index, GameWorld.None);
        w.Damage(-Templar, 4, percent: false);
        RegionState left = w.Snapshot();

        var (again, _) = World();
        again.Restore(left);

        WorldObject back = TemplarOf(again);
        Assert.Equal(5, Assert.Single(back.Items).Quantity);
        Assert.Equal(16, back.Vitals.Hp);
    }

    [Fact]
    public void ScriptsGiveSearchAwardAndPay()
    {
        var (w, party) = World();
        var host = new WorldGplHost { Map = w };
        var hero = new Characters.Character { Name = "Rikus" };
        hero.Classes.Add(Characters.CharacterClass.Fighter);
        hero.Classes.Add(Characters.CharacterClass.Thief);
        host.PartyCharacter = slot => slot == 0 ? hero : null;
        var scripts = new Scripts();
        scripts.Files[(1, GplFileKind.Gpl)] = GplAssembler.Assemble("""
              GIVE(3, name(-200), 32766, 9999)
              lnum[0] = ((SEARCH(32766, range=[77,80], field[72](4)=name(-200), field[72](3))))
              gnum[0] = lnum[0]
              AWARD(32766, 1001)
              CHANGE_MONEY(250)
              CHANGE_MONEY(-50)
              gnum[1] = ((REQUEST(20, 0, 0, 0)))
              EXIT_GPL
            """);
        // GPLX field types (as GPLDATA.GFF has them) for the fields SEARCH names: 72 0x12, 82 0x13, 85 0x10.
        var fieldTypes = new byte[99];
        (fieldTypes[72], fieldTypes[82], fieldTypes[85]) = (0x12, 0x13, 0x10);
        var m = new GplMachine(scripts, host, fieldTypes, new BorlandRandom(1));

        Assert.True(m.Run(new GplEntry(1, 0)).Ok);

        Assert.Equal(3, party[0].Items.Count); // GIVE to the party: the first member (it resolves to one holder)
        Assert.Equal(3, m.Memory.Read(new GplVariable(GplVarType.GlobalNum, 0)));
        Assert.Equal(1000, hero.Experience); // 500 a class
        Assert.Equal(200, host.Money);
        Assert.Equal(1, m.Memory.Read(new GplVariable(GplVarType.GlobalNum, 1))); // the templar
    }

    [Fact]
    public void ShopWaitsForTheShopToClose()
    {
        var (w, _) = World();
        var host = new WorldGplHost { Map = w };
        var scripts = new Scripts();
        scripts.Files[(1, GplFileKind.Gpl)] = GplAssembler.Assemble("""
              SHOP(name(-100))
              gnum[0] = 1
              EXIT_GPL
            """);
        var m = new GplMachine(scripts, host, [], new BorlandRandom(1));

        Assert.True(m.Run(new GplEntry(1, 0)).Suspended);
        Assert.Equal(TemplarOf(w).Index, Assert.IsType<GplShopPrompt>(host.Dialogue.Prompt).Merchant);

        Assert.True(m.Resume(0).Ok);
        Assert.Equal(1, m.Memory.Read(new GplVariable(GplVarType.GlobalNum, 0)));
    }
}
