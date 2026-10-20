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

    /// <summary>
    /// <see cref="Creature"/> as the shipped creatures are: then a character
    /// record (<paramref name="hp"/> at +8, worth <paramref name="xp"/>) and the
    /// items it carries (load action 2, then 4), each with its <c>IT1R</c> and
    /// <c>NAME</c> references, and the 0xFF end header.
    /// </summary>
    private static byte[] ArmedCreature(int ojff, short combatHp, short hp, int xp, params (int Ojff, int Type, int Name, byte Slot)[] carried)
    {
        var b = new List<byte>(Creature(ojff, combatHp));
        void Header(int action, int block, int type, int index, int from, int length) =>
            b.AddRange([(byte)action, (byte)block, (byte)type, 0, (byte)index, (byte)(index >> 8), (byte)from, 0, (byte)length, 0]);
        Header(3, 0, CharacterRecord.Type, 0, 0, CharacterRecord.Length);
        var character = new byte[CharacterRecord.Length];
        BitConverter.GetBytes(xp).CopyTo(character, 4);
        BitConverter.GetBytes(hp).CopyTo(character, 8);
        b.AddRange(character);
        int block = 2;
        for (int i = 0; i < carried.Length; i++)
        {
            var (item, type, name, slot) = carried[i];
            int at = b.Count;
            Header(i == 0 ? 2 : 4, i == 0 ? 0 : block - 3, 1, 0, i == 0 ? 0x4F : 1, 21);
            var record = new byte[21];
            BitConverter.GetBytes((short)-item).CopyTo(record, 0);
            record[17] = slot;
            b.AddRange(record);
            Header(3, block, 5, type, 4, 0);
            Header(3, block, 3, name, 2, 0);
            block += 3;
        }
        b.Add(0xFF);
        b.AddRange(new byte[9]);
        return [.. b];
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

    /// <summary>
    /// A templar (20 hit points) at (10, 5), a torch on the ground at (2, 2),
    /// the party at (1, 8). <paramref name="armed"/>: the templar's <c>RDFF</c>
    /// as the shipped creatures have it, 30 hit points in its character record,
    /// worth 270 experience, carrying a torch in its pack and boots on its feet.
    /// </summary>
    private static (GameWorld World, IReadOnlyList<PartyMemberState> Party) World(bool armed = false)
    {
        var r = new Resources();
        r.Data[("RDFF", Templar)] = armed
            ? ArmedCreature(Templar, 20, 30, 270, (Torch, 0, 1, 255), (Boots, 2, 3, 13))
            : Creature(Templar, 20);
        r.Data[("RDFF", Torch)] = ItemRdff(Torch, 0, 1, 2);
        r.Data[("RDFF", Arrows)] = ItemRdff(Arrows, 1, 2, 1);
        r.Data[("RDFF", Boots)] = ItemRdff(Boots, 2, 3, 300);
        r.Data[("RDFF", GameWorld.DeadBody)] = ItemRdff(GameWorld.DeadBody, 3, 4, 0);
        r.Data[("OJFF", GameWorld.DeadBody)] = [0, 0, 8, 0, 15, 0, 0, 0, 0, 0, 0, 0, 7, 0, 0, 0]; // anchor (8, 15), BMP 7
        r.Data[("BMP ", 7)] = CinematicPlayerTests.Screen(3);
        // IT1R: type 0 nothing special; 1 stacks (flags +15 bit 2), ammunition (+9 = 11); 2 worn on the
        // feet (4) by fighters and thieves only (classes +16); 3 a container (flags bit 8).
        var types = new byte[4 * 20];
        types[20 + 15] = 0x02;
        types[20 + 9] = 11;
        types[40 + 9] = 4;
        types[40 + 16] = 0x20;
        types[40 + 17] = 0x04;
        types[60 + 15] = 0x08;
        r.Data[("IT1R", 1)] = types;
        var names = new byte[5 * 25];
        foreach (var (n, text) in new[] { (1, "Torch"), (2, "Arrow"), (3, "Boots"), (4, "Dead Body") })
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

        Assert.Equal(EquipResult.Worn, w.Equip(w[1]!, party[1].Items.Single(i => i.Rdff == Boots)));

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
    public void OnlyTheItemsClassesMayWearIt()
    {
        var (w, party) = World();
        w.Give(1, -Boots, 0, GameWorld.None);
        Item boots = party[0].Items.Single();
        var classes = new Dictionary<int, Data.Items.ItemClasses> { [0] = Data.Items.ItemClasses.Preserver };
        w.PartyClasses = slot => classes.TryGetValue(slot, out var c) ? c : null;

        Assert.False(w.CanUse(w[0]!, boots));
        Assert.Equal(EquipResult.WrongClass, w.Equip(w[0]!, boots));
        classes[0] = Data.Items.ItemClasses.Preserver | Data.Items.ItemClasses.Thief; // a multi-class preserver/thief
        Assert.Equal(EquipResult.Worn, w.Equip(w[0]!, boots));
        w.Give(1, -Torch, 0, GameWorld.None);
        Assert.Equal(EquipResult.NotWearable, w.Equip(w[0]!, party[0].Items.Single(i => i.Rdff == Torch)));
        Assert.True(w.CanUse(TemplarOf(w), boots)); // creatures aren't asked
    }

    [Fact]
    public void EquipAtWearsInThatSlotOnlyAndSwapsWhatWasThere()
    {
        var (w, party) = World();
        w.Give(2, -Boots, 0, GameWorld.None);
        Item first = party[0].Items[0], second = party[0].Items[1];
        const int Feet = 13;

        Assert.Equal(EquipResult.NotWearable, w.EquipAt(w[0]!, first, 0)); // slot 0 is the arms
        Assert.Equal(EquipResult.Worn, w.EquipAt(w[0]!, first, Feet));
        Assert.Equal(EquipResult.Worn, w.EquipAt(w[0]!, second, Feet));
        Assert.Equal((Equipment.Pack, Feet), (first.Slot, second.Slot)); // the first went back to the pack
    }

    [Fact]
    public void SplitTakesPartOfAStackIntoItsOwnEntry()
    {
        var (w, party) = World();
        w.Give(9, -Arrows, 0, GameWorld.None);
        w.Give(1, -Torch, 0, GameWorld.None);
        Item arrows = party[0].Items[0];

        Item part = w.Split(w[0]!, arrows, 4)!;

        Assert.Equal([(Arrows, 5), (Arrows, 4), (Torch, 1)], party[0].Items.Select(i => (i.Rdff, i.Quantity)));
        Assert.Same(part, party[0].Items[1]);
        Assert.Null(w.Split(w[0]!, arrows, 5)); // not all of it
        Assert.Null(w.Split(w[0]!, party[0].Items[2], 1)); // torches don't stack
    }

    [Fact]
    public void AStoreSellsAtValueAndBuysAtHalf()
    {
        var (w, party) = World();
        WorldObject merchant = TemplarOf(w);
        w.Give(1, -Boots, merchant.Index, GameWorld.None);
        Item boots = merchant.Items.Single();

        Assert.Null(Shop.Buy(w, merchant, w[0]!, boots, 299));
        Assert.Equal(700, Shop.Buy(w, merchant, w[0]!, boots, 1000));
        Item bought = party[0].Items.Single();

        Assert.Equal(150, Shop.Offer(bought));
        Assert.Equal(150, Shop.Sell(w, merchant, w[0]!, [bought]));
        Assert.Empty(party[0].Items);
        Assert.Contains(bought, merchant.Items);
        w.Give(5, -Arrows, 0, GameWorld.None);
        Assert.Equal(0, Shop.Offer(party[0].Items.Single())); // worth 1: not worth buying
    }

    [Fact]
    public void ADeadCreatureLeavesABodyWithWhatItCarried()
    {
        var (w, party) = World();
        WorldObject templar = TemplarOf(w);
        w.Give(1, -Boots, templar.Index, GameWorld.None);
        w.Give(5, -Arrows, templar.Index, GameWorld.None);
        Cell at = templar.Cell;

        w.Hurt(templar, 25);

        WorldObject body = w.First(-GameWorld.DeadBody)!;
        Assert.Equal(at, body.Cell);
        Assert.True(w.IsContainer(body));
        Assert.Equal([Boots, Arrows], body.Items.Select(i => i.Rdff));
        Assert.Contains(Verb.Open, ObjectVerbs.For(body, w.Items, new GplTriggers(), w.IsContainer(body)));
        Assert.False(w.PickUp(w[0]!, body)); // not while something is in it

        Assert.True(w.MoveItem(body, w[0]!, body.Items[1]));
        w.Give(3, -Arrows, 1, GameWorld.None);
        Assert.True(w.MoveItem(w[1]!, w[0]!, party[1].Items.Single())); // arrows join the arrows
        Assert.Equal(8, party[0].Items.Single().Quantity);
        Assert.True(w.MoveItem(body, w[0]!, body.Items[0]));
        Assert.True(w.PickUp(w[0]!, body)); // empty now

        var (w2, _) = World();
        WorldObject poor = TemplarOf(w2);
        w2.Hurt(poor, 25);
        Assert.Null(w2.First(-GameWorld.DeadBody)); // nothing carried, no body
        Assert.False(w2.IsContainer(w2.First(-Torch)!));
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
    public void CreaturesHaveTheirCharacterRecordsHitPointsAndWhatTheirRdffCarries()
    {
        var (w, _) = World(armed: true);
        WorldObject templar = TemplarOf(w);

        Assert.Equal((30, 30), (templar.Vitals.Hp, templar.Vitals.MaxHp)); // +8, not the combat record's 20
        Assert.Equal([(Torch, Equipment.Pack), (Boots, 13)], templar.Items.Select(i => (i.Rdff, i.Slot)));
        Assert.Equal("Boots", w.Items.NameOf(templar.Items[1].Record));

        w.Hurt(templar, 35);

        WorldObject body = Assert.Single(w.Objects, o => o.Id == -GameWorld.DeadBody);
        Assert.Equal([Torch, Boots], body.Items.Select(i => i.Rdff));
    }

    [Fact]
    public void KillingAnEnemyGivesItsExperienceShared()
    {
        var (w, _) = World(armed: true);
        var host = new WorldGplHost { Map = w };
        var hero = new Characters.Character { Name = "Rikus" };
        hero.Classes.Add(Characters.CharacterClass.Fighter);
        host.PartyCharacter = slot => slot == 0 ? hero : null;
        w.Defeated += c => host.AwardKill(w, c);
        WorldObject templar = TemplarOf(w);

        Assert.Equal(135, w.KillExperience(templar)); // 270 over the two members
        w.Hurt(templar, 31); // down (31 < 30 + 10) counts as a kill

        Assert.Equal(135, hero.Experience);
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
