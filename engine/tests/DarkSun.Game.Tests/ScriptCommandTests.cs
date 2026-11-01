using System.Buffers.Binary;
using DarkSun.Game.Characters;
using DarkSun.Game.Combat;
using DarkSun.Game.Rules;
using DarkSun.Game.Scripting;
using DarkSun.Game.World;

namespace DarkSun.Game.Tests;

/// <summary>The script commands and object fields traced from DSUN.EXE, against the OpenDS and Obsidian Edition notes.</summary>
public class ScriptCommandTests
{
    private sealed class OneScript(byte[] code) : IGplScripts
    {
        public byte[]? Load(int file, GplFileKind kind) => file == 1 ? code : null;
    }

    /// <summary>
    /// A <c>GPLX</c> with the shipped numbering for the fields tested: combat
    /// record 67 hp (+0), 71 sheet (+4, a pointer to the character record), 72
    /// id (+6), 73 status (+0x1C), 74 allegiance (+0x1D), 75 mind (+0x1E), 23
    /// Str (+0x22), 19 name (+0x28), 68 psionic points (+2), 89 special attack (+0x16); character record 87 size (+0x3D), 43 most
    /// hit points (+8), 29 Str (+0x1B); item 72 id (+0), 86 charges (+14), 85 slot (+17).
    /// </summary>
    internal static GplFieldMap Map()
    {
        var gplx = new byte[0x3DE + 99 + 10];
        for (int i = 0; i < 5 * 99; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(gplx.AsSpan(2 * i), -1);
        }
        void Field(int group, int field, int offset, byte type)
        {
            BinaryPrimitives.WriteInt16LittleEndian(gplx.AsSpan(2 * (group * 99 + field)), (short)offset);
            gplx[0x3DE + field] = type;
        }
        Field(GplFieldMap.Combat, 67, 0, 19);
        Field(GplFieldMap.Combat, 71, 4, 4);
        Field(GplFieldMap.Combat, 72, 6, 18);
        Field(GplFieldMap.Combat, 73, 0x1C, 16);
        Field(GplFieldMap.Combat, 74, 0x1D, 16);
        Field(GplFieldMap.Combat, 75, 0x1E, 16);
        Field(GplFieldMap.Combat, 23, 0x22, 16);
        Field(GplFieldMap.Combat, 19, 0x28, 0x80 | 18);
        Field(GplFieldMap.Combat, 68, 2, 19);
        Field(GplFieldMap.Combat, 89, 0x16, 19);
        Field(GplFieldMap.Character, 87, 0x3D, 16);
        Field(GplFieldMap.Character, 43, 8, 19);
        Field(GplFieldMap.Character, 29, 0x1B, 16);
        Field(GplFieldMap.Item, 72, 0, 18);
        Field(GplFieldMap.Item, 86, 14, 16);
        Field(GplFieldMap.Item, 85, 17, 16);
        return GplFieldMap.Parse(gplx);
    }

    private static (GameWorld World, WorldGplHost Host, GplMachine Machine) Run(GameWorld world, byte[]? code = null, int seed = 1,
        Func<int, Character?>? sheets = null, CharacterRules? rules = null)
    {
        var host = new WorldGplHost { Map = world, Fields = Map(), PartyCharacter = sheets ?? (_ => null), Rules = rules ?? CharacterRules.Default };
        var machine = new GplMachine(new OneScript(code ?? [GplOpcodes.EndFunction, 0x31]), host, [], new BorlandRandom((uint)seed));
        return (world, host, machine);
    }

    private static GplObjectRef Ref(int id, params byte[] path) => new(id, path);

    // -- object fields ----------------------------------------------------------------------------------

    [Fact]
    public void FieldsSeventyThreeToSeventyFiveAreStatusAllegianceAndMind()
    {
        var (world, host, machine) = Run(GameWorldTests.World());
        WorldObject templar = world.First(-100)!; // status 3, allegiance 1 in its record
        templar.Vitals.Status = Vitals.Dead;

        // GPLX: 73 is +0x1C, 74 +0x1D, 75 +0x1E (what the AI does), not the allegiance.
        Assert.Equal(Vitals.Dead, host.ReadField(machine, Ref(100, 73)));
        Assert.Equal(1, host.ReadField(machine, Ref(100, 74)));
        host.WriteField(machine, Ref(100, 75), 2); // SETRECORD obj(…).75 = 2 after REQUEST(18, …, 2): fight
        Assert.Equal(1, templar.Allegiance);
        Assert.Equal(2, templar.Mind);
        Assert.Equal(2, Combatant.FromCreature(templar).Mind);
        Assert.Equal("Templar", host.ReadFieldText(machine, Ref(100, 19)));

        // A party member is on the party's side.
        Assert.Equal(1, host.ReadField(machine, Ref(0x8000 | 0x25, 74)));
    }

    [Fact]
    public void ASheetFieldIsReachedThroughTheCombatRecord()
    {
        var (_, host, machine) = Run(GameWorldTests.World(size: 0x11));
        Assert.Equal(0x11, host.ReadField(machine, Ref(100, 71, 87))); // obj(…).71.87: its character record's size
        host.WriteField(machine, Ref(100, 71, 43), 33);
        Assert.Equal(33, host.ReadField(machine, Ref(100, 71, 43)));
    }

    [Fact]
    public void APartyMembersSheetComesFromTheCharacter()
    {
        var c = new Character { Name = "Barek", Race = Race.Human, Classes = [CharacterClass.Fighter], Rolled = [17, 12, 15, 10, 10, 10] };
        var (world, host, machine) = Run(GameWorldTests.World(), sheets: slot => slot == 0 ? c : null);
        world.ActiveMember = 0;
        world[0]!.Vitals.MaxHp = 20;

        Assert.Equal(17, host.ReadField(machine, Ref(0x8000 | 0x25, 23)));
        Assert.Equal(17, host.ReadField(machine, Ref(0x8000 | 0x25, 71, 29)));
        Assert.Equal(20, host.ReadField(machine, Ref(0x8000 | 0x25, 71, 43)));
        host.WriteField(machine, Ref(0x8000 | 0x25, 71, 29), 19); // the magic pool's new ability
        Assert.Equal(19, c.Rolled[0]);
        host.WriteField(machine, Ref(0x8000 | 0x25, 71, 43), 25);
        Assert.Equal((25, 25), (world[0]!.Vitals.MaxHp, c.Hp));
    }

    [Fact]
    public void PsionicPointsWrittenOnAPartyMemberReachTheSheet()
    {
        var c = new Character { Name = "Kel", Race = Race.Human, Classes = [CharacterClass.Psionicist], Rolled = [10, 10, 16, 16, 16, 10] };
        var (world, host, machine) = Run(GameWorldTests.World(), sheets: slot => slot == 0 ? c : null);
        world.ActiveMember = 0;
        int most = new Magic.Spellcasting(CharacterRules.Default).MaxPsp(c);

        host.WriteField(machine, Ref(0x8000 | 0x25, 68), 0); // GPL 49: emptied, then filled to the most
        Assert.Equal(most, c.PspSpent);
        host.WriteField(machine, Ref(0x8000 | 0x25, 68), most);
        Assert.Equal(0, c.PspSpent);
        Assert.Equal(most, host.ReadField(machine, Ref(0x8000 | 0x25, 68)));
        Assert.False(world[0]!.Knows(68)); // not kept on the object, where it would go stale
    }

    [Fact]
    public void ACreaturesPsionicPointsAndSpecialAttackAsScriptsSetThemGoIntoAFight()
    {
        var (world, host, machine) = Run(GameWorldTests.World());
        WorldObject templar = world.First(-100)!;

        host.WriteField(machine, Ref(100, 68), 9);
        host.WriteField(machine, Ref(100, 89), 0x0105);
        Combatant c = Combatant.FromCreature(templar);
        Assert.Equal(9, c.Psp);
        Assert.Equal(5, c.Type); // +0x16, the low byte of the word

        host.WriteField(machine, Ref(100, 89), 0); // GPL 81 on Balkazar
        Assert.Equal(0, Combatant.FromCreature(templar).Type);
    }

    [Fact]
    public void GetpartyMakesTheMemberOther()
    {
        // GETPARTY(9999); GETXY(gname[8]): the scripts walk the party this way (the arena gate's check).
        var (world, host, machine) = Run(GameWorldTests.World());
        world[0]!.Walker!.Teleport(new Movement.Cell(3, 7), world.Region.IsBlocked);
        byte[] code = new Asm().Op(0x34).Num(9999).Op(0x09).Var(GplVarType.EngineVar, 0x28).Op(0x31).ToArray();
        (_, host, machine) = Run(world, code);

        Assert.Null(machine.Run(new GplEntry(1, 0)).Error);
        Assert.Equal(0, host.ReadEngineVariable(machine, 0x28));
        Assert.Equal((3, 7), (host.ReadEngineVariable(machine, 0x21), host.ReadEngineVariable(machine, 0x22)));
    }

    // -- skills and rolls -------------------------------------------------------------------------------

    private static ThiefSkills.Who Thief(int level, int dex, Race race = Race.Human, params Condition[] conditions) =>
        new(race, level, dex, true, [], new HashSet<Condition>(conditions));

    [Fact]
    public void ThiefSkillsFollowTheTables()
    {
        CharacterRules r = CharacterRules.Default;
        // Open locks: 18 + 10 (dwarf) + 4 a level; Dexterity 17 is 2 over 15: +10.
        Assert.Equal(18 + 10 + 4 + 10, ThiefSkills.Chance(r, Thief(1, 17, Race.Dwarf), ThiefSkills.OpenLocks));
        // Pick pockets at Dexterity 9: 3 under 12, −15; Dexterity 22: +30 over 16, −3 over 21.
        Assert.Equal(28 + 4 - 15, ThiefSkills.Chance(r, Thief(1, 9), ThiefSkills.PickPockets));
        Assert.Equal(28 + 4 + 30 - 3, ThiefSkills.Chance(r, Thief(1, 22), ThiefSkills.PickPockets));
        // Anything on the legs: −10 moving silently, and no hearing at all.
        var legs = Thief(1, 13) with { Worn = [(6, 99)] };
        Assert.Equal(28 + 4 - 10, ThiefSkills.Chance(r, legs, ThiefSkills.MoveSilently));
        Assert.Equal(0, ThiefSkills.Chance(r, legs, ThiefSkills.HearNoise));
        // No thief levels: nothing, but Detect Traps makes finding traps certain for anyone.
        Assert.Equal(0, ThiefSkills.Chance(r, Thief(0, 12), ThiefSkills.FindTraps));
        Assert.True(ThiefSkills.Target(r, Thief(0, 12, Race.Human, Condition.DetectTraps), ThiefSkills.FindTraps, 0) > 1000);
        // 87B3:04F5: Slowed stops only picking pockets; Blind all but hearing noise.
        Assert.True(ThiefSkills.Target(r, Thief(5, 12, Race.Human, Condition.Slowed), ThiefSkills.PickPockets, 0) < 0);
        Assert.True(ThiefSkills.Target(r, Thief(5, 12, Race.Human, Condition.Slowed), ThiefSkills.OpenLocks, 0) > 0);
        Assert.True(ThiefSkills.Target(r, Thief(5, 12, Race.Human, Condition.Blind), ThiefSkills.HearNoise, 0) > 0);
        Assert.True(ThiefSkills.Target(r, Thief(5, 12, Race.Human, Condition.Blind), ThiefSkills.ClimbWalls, 0) < 0);
    }

    [Fact]
    public void APartySkillCheckRollsForTheFirstSlotUnlessFixed()
    {
        var thief = new Character { Race = Race.Human, Classes = [CharacterClass.Thief], Rolled = [10, 17, 10, 10, 10, 10], Experience = 1_000_000 };
        Character Sheet(int slot) => slot == 3 ? thief : new Character { Race = Race.Human, Classes = [CharacterClass.Fighter] };
        // The first slot has no thief levels: 0 chance, so a party check with situation −100 fails as the original has it…
        var (world, host, machine) = Run(GameWorldTests.World(), sheets: Sheet);
        Assert.Equal(0, host.SkillCheck(machine, world, 0x7FFE, ThiefSkills.ClimbWalls, 0));
        // …and with the fix the thief climbs (78 + 4 a level).
        var fixedRules = CharacterRules.Default with { PartySkillCheckBest = true };
        (world, host, machine) = Run(GameWorldTests.World(), sheets: Sheet, rules: fixedRules);
        Assert.Equal(1, host.SkillCheck(machine, world, 0x7FFE, ThiefSkills.ClimbWalls, 0));
    }

    [Fact]
    public void StatRollIsADTwentyUnderTheAbility()
    {
        var strong = new Character { Race = Race.Human, Classes = [CharacterClass.Fighter], Rolled = [25, 10, 10, 10, 10, 10] };
        var weak = new Character { Race = Race.Human, Classes = [CharacterClass.Fighter], Rolled = [1, 10, 10, 10, 10, 10] };
        int Rolls(Character c, int seed)
        {
            // ACCUM <= STAT_ROLL(0, difficulty 1, Str)
            var (world, host, machine) = Run(GameWorldTests.World(), sheets: slot => slot == 0 ? c : null, seed: seed);
            byte[] code = new Asm().Op(0x59).Num(0).Num(1).Num(0).Op(0x31).ToArray();
            (_, _, machine) = Run(world, code, seed, slot => slot == 0 ? c : null);
            machine.Run(new GplEntry(1, 0));
            return machine.Accumulator;
        }
        int strongPasses = Enumerable.Range(1, 40).Sum(s => Rolls(strong, s)), weakPasses = Enumerable.Range(1, 40).Sum(s => Rolls(weak, s));
        Assert.InRange(strongPasses, 30, 40); // only a 19 fails
        Assert.InRange(weakPasses, 0, 6); // only a 0 passes
    }

    // -- traps, things, text ---------------------------------------------------------------------------

    /// <summary>A world with a trap item (RDFF 2039): +0x0F = <paramref name="flags"/> (4: four d6 on its own cell).</summary>
    private static GameWorld TrapWorld(byte flags = 4) => GameWorldTests.World(more: data =>
    {
        var trap = new byte[10 + 21 + 1];
        (trap[0], trap[2], trap[8]) = (1, 1, 21);
        BinaryPrimitives.WriteInt16LittleEndian(trap.AsSpan(10 + 10), 9999);
        trap[10 + 15] = flags;
        trap[^1] = 0xFF;
        data[("RDFF", 2039)] = trap;
        data[("OJFF", 2039)] = [0, 0, 8, 0, 15, 0, 0, 0, 0, 0, 0, 0, 7, 0, 0, 0];
        data[("BMP ", 7)] = CinematicPlayerTests.Screen(3);
    });

    /// <summary>REQUEST(7, name(−2039), the member's cell): the hit points the member loses.</summary>
    private static int SetOffTrap(GameWorld world, Func<int, Character?>? sheets = null)
    {
        WorldObject member = world[0]!;
        member.Vitals.Hp = member.Vitals.MaxHp = 40;
        var (_, _, machine) = Run(world, new Asm().Op(0x22).Num(7).Num(-2039).Num(member.Cell.X).Num(member.Cell.Y).Op(0x31).ToArray(), sheets: sheets);
        Assert.Null(machine.Run(new GplEntry(1, 0)).Error);
        return 40 - member.Vitals.Hp;
    }

    [Fact]
    public void ATrapGoesOffOnItsCell()
    {
        GameWorld world = TrapWorld();

        Assert.InRange(SetOffTrap(world), 4, 24); // no thieves: 4d6
        Assert.Empty(world.Select(-2039)); // spent
    }

    [Fact]
    public void DetectTrapsCastOnTheMapAvoidsATrapUnlessItHidesFromIt()
    {
        // Detect Traps on the active member (slot 1), cast on the map; the party's find traps roll is slot 0's, who has none.
        var fighter = new Character { Race = Race.Human, Classes = [CharacterClass.Fighter] };
        var detecting = new Character { Race = Race.Human, Classes = [CharacterClass.Fighter] };
        detecting.Lasting.Add(new LastingEffect(85, Condition.DetectTraps, 5, EndsAt: 600));
        Character? Sheet(int slot) => slot == 1 ? detecting : fighter;
        GameWorld Trap(byte flags = 4)
        {
            GameWorld w = TrapWorld(flags);
            w.ActiveMember = 1;
            return w;
        }

        GameWorld world = Trap();
        Assert.Equal(0, SetOffTrap(world, Sheet));
        Assert.NotEmpty(world.Select(-2039)); // left lying there

        Assert.InRange(SetOffTrap(Trap(4 | 0x80), Sheet), 4, 24); // its +0x0F hides it from the spell

        world = Trap();
        world.Clock = 600; // the spell is over
        Assert.InRange(SetOffTrap(world, Sheet), 4, 24);
    }

    [Fact]
    public void SetthingWalksTheItemsAndTheThingReadsThem()
    {
        GameWorld world = GameWorldTests.World();
        WorldObject member = world[0]!;
        Item torch = world.NewItem(200, slot: 3)!;
        Item spare = world.NewItem(200)!;
        member.Items.Add(torch);
        member.Items.Add(spare);
        var (_, host, machine) = Run(world);
        var thing = Ref(0x8000 | 0x2B, 85);

        Assert.Equal(1, host.Execute(machine, Command("SETTHING", 0, 9999)));
        Assert.Equal(3, host.ReadField(machine, thing));
        Assert.Equal(1, host.Execute(machine, Command("SETTHING", 0, 9998)));
        Assert.Equal(-1, host.ReadField(machine, thing)); // the pack (255), as the field's signed byte reads it
        host.WriteField(machine, Ref(0x8000 | 0x2B, 86), 0); // a wand's charges
        Assert.Equal(0, spare.Charges);
        Assert.Equal(0, host.Execute(machine, Command("SETTHING", 0, 9997)));
    }

    [Fact]
    public void SetOtherTakesTheThingSoOtherReadsTheItem()
    {
        // GPL 137's search for a weapon in hand: SETTHING(gname[5], n); IF .85 == 3: SET_OTHER(gname[11]); then obj_special(0x2c).72.
        GameWorld world = GameWorldTests.World();
        world[0]!.Items.Add(world.NewItem(200, slot: 3)!);
        var (_, host, machine) = Run(world);

        Assert.Equal(1, host.Execute(machine, Command("SETTHING", 0, 9999)));
        int thing = host.ReadEngineVariable(machine, 0x2B);
        Assert.Equal(-200, thing);
        Assert.Equal(1, host.Execute(machine, Command("SET_OTHER", thing)));
        Assert.Equal(-200, host.ReadField(machine, Ref(0x8000 | 0x2C, 72)));
        Assert.Equal(3, host.ReadField(machine, Ref(0x8000 | 0x2C, 85)));
    }

    [Fact]
    public void AnItemUsedFromAPackIsTheTargetScriptsReadAndWrite()
    {
        // USE on the Wand of Metal Detection (GPL 85): obj_special(0x27).86 > 0, then BYTE_DEC it.
        // The script gets the wand in gname[7] by its id, which nothing on the map has.
        GameWorld world = GameWorldTests.World();
        Item wand = world.NewItem(200)!;
        wand.Charges = 25;
        world[0]!.Items.Add(wand);
        var (_, host, machine) = Run(world);
        host.WriteEngineVariable(machine, 0x27, wand.Id);
        var charges = Ref(0x8000 | 0x27, 86);
        Assert.Equal(0, host.ReadField(machine, charges));

        host.UsedItem = wand;
        Assert.Equal(25, host.ReadField(machine, charges));
        Assert.Equal(-200, host.ReadField(machine, Ref(0x8000 | 0x27, 72))); // the Parting Staff's name(-30044) test
        host.WriteField(machine, charges, 24);
        Assert.Equal(24, wand.Charges);
    }

    private static GplCommand Command(string mnemonic, params int[] values)
    {
        byte op = GplOpcodes.All.First(o => o.Value.Mnemonic == mnemonic).Key;
        var instruction = new GplInstruction(0, op, 1);
        return new GplCommand(instruction, new GplEntry(1, 0), values, [.. values.Select(_ => (string?)null)], [.. values.Select(_ => (GplLocation?)null)]);
    }

    [Fact]
    public void InputStringWaitsForTheWordAndStringCompareChecksIt()
    {
        // INPUT_STRING(gstr[0]); STRING_COMPARE(gstr[0], gstr[1]); gnum[0] = ACCUM
        byte[] code = new Asm().Op(0x42).Var(GplVarType.GlobalString, 0)
            .Op(0x5A).Var(GplVarType.GlobalString, 0).Var(GplVarType.GlobalString, 1)
            .Op(0x16).Bytes(0x80).Bytes((byte)GplVarType.GlobalNum, 0).Op(0x31).ToArray();
        var host = new DialogueGplHost();
        var machine = new GplMachine(new OneScript(code), host, [], new BorlandRandom(1));
        machine.Memory.WriteString(GplMemory.Locate(new GplVariable(GplVarType.GlobalString, 1))!, "honor");

        Assert.True(machine.Run(new GplEntry(1, 0)).Suspended);
        Assert.IsType<GplTextPrompt>(host.Dialogue.Prompt);
        Assert.Null(machine.ResumeWith("honor").Error);
        Assert.Equal(1, machine.Memory.Read(new GplVariable(GplVarType.GlobalNum, 0)));
    }

    [Fact]
    public void EndAndCloseArentPrintedEndWaitsAndCloseClears()
    {
        var host = new DialogueGplHost();
        var machine = new GplMachine(new OneScript([GplOpcodes.EndFunction, 0x31]), host, [], new BorlandRandom(1));
        GplCommand Print(string text)
        {
            var instruction = new GplInstruction(0, 0x4F, 1);
            return new GplCommand(instruction, new GplEntry(1, 0), [115, 0], [null, text], [null, null]);
        }

        host.Execute(machine, Print("Gladiators: Step forward, into the arena! "));
        host.Execute(machine, Print("END"));
        Assert.IsType<GplContinuePrompt>(host.Dialogue.Prompt);
        Assert.Equal(["Gladiators: Step forward, into the arena! "], host.Dialogue.Text.Select(t => t.Text));
        host.Execute(machine, Print("CLOSE"));
        Assert.True(host.Dialogue.Text.Count == 0);
    }

    // -- repairs, the clock -----------------------------------------------------------------------------

    [Fact]
    public void RepairsApplyOnlyWhereTheirBytesAre()
    {
        GplRepair guard = GplRepairs.All.Single(r => r.Id == "deadtriggers.portcullis-guard");
        byte[] code = new byte[0x40];
        guard.Expect.CopyTo(code, guard.Offset);
        byte[] repaired = GplRepairs.Apply(195, code, [guard]);
        Assert.Equal(guard.Replace, repaired.AsSpan(guard.Offset, guard.Replace.Length).ToArray());
        Assert.Equal(new byte[3], code.AsSpan(0, 3).ToArray()); // the original is left as it was

        byte[] other = new byte[0x40];
        Assert.Same(other, GplRepairs.Apply(195, other, [guard]));
        Assert.Equal(27, GplRepairs.All.Count(r => r.OnByDefault));
    }

    [Fact]
    public void ARepairLongerThanWhatItReplacesRunsAsADetourAfterTheFile()
    {
        // 0: lnum[0] = 1; EXIT. The repair: ACCUM <= 1; IF(@+11) lnum[0] = 2; ENDIF, the IF's address counted from the detour.
        byte[] code = Convert.FromHexString("168f01820031");
        var repair = new GplRepair("test", 1, 0, Convert.FromHexString("168f018200"), Convert.FromHexString("188f013e000b168f02820067"), true, "")
        {
            Relocate = [4],
        };
        byte[] repaired = GplRepairs.Apply(1, code, [repair]);

        // 0: JUMP(@7), never-reached EXIT_GPLs; 6: EXIT, 7: the detour (IF @0x12), JUMP(@5) back.
        Assert.Equal("1200073131" + "31" + "31" + "188f013e0012168f02820067" + "120005", Convert.ToHexString(repaired).ToLowerInvariant());
        var m = new GplMachine(new OneScript(repaired), new LoggingGplHost(), [], new BorlandRandom(1));
        Assert.True(m.Run(new GplEntry(1, 0)).Ok);
        Assert.Equal(2, m.Memory.Read(new GplVariable(GplVarType.LocalNum, 0)));
    }

    [Fact]
    public void TheClockRunsOnTheMapAndPassTimeAddsMinutes()
    {
        GameWorld world = GameWorldTests.World();
        var scripts = new GplCreatureScripts();
        world.Update(0, scripts);
        world.Update(64 * 10, scripts); // 1DF3:29E8: one a 64 ticks of the 96 Hz timer
        Assert.Equal(10, world.Clock);

        var (_, host, machine) = Run(world, new Asm().Op(0x30).Num(5).Op(0x31).ToArray());
        machine.Run(new GplEntry(1, 0));
        Assert.Equal(10 + 5 * 60, world.Clock);
    }
}
