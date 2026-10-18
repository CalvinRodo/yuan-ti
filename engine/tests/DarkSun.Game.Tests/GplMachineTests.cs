using DarkSun.Game.Rules;
using DarkSun.Game.Scripting;

namespace DarkSun.Game.Tests;

/// <summary>Builds GPL bytecode by hand.</summary>
internal sealed class Asm
{
    private readonly List<byte> _b = [];

    public int Here => _b.Count;

    public Asm Op(byte op)
    {
        _b.Add(op);
        return this;
    }

    public Asm Bytes(params byte[] bytes)
    {
        _b.AddRange(bytes);
        return this;
    }

    /// <summary>A 15-bit immediate (0..0x7FFF), or a 16-bit one (0x90) otherwise.</summary>
    public Asm Num(int v)
    {
        if (v is >= 0 and < 0x8000)
        {
            return Bytes((byte)(v >> 8), (byte)v);
        }
        return Bytes(0x90, (byte)(v >> 8), (byte)v);
    }

    public Asm Var(GplVarType type, int index) => Bytes((byte)(0x80 | (byte)type), (byte)index);

    /// <summary>A 15-bit address to fill in later; returns its position.</summary>
    public int Placeholder()
    {
        int at = Here;
        Num(0);
        return at;
    }

    public void Patch(int at, int address)
    {
        _b[at] = (byte)(address >> 8);
        _b[at + 1] = (byte)address;
    }

    /// <summary>LOAD_VAR: variable = number.</summary>
    public Asm Set(GplVarType type, int index, int value) => Op(0x16).Num(value).Bytes((byte)type, (byte)index);

    /// <summary>LOAD_ACCUM with an expression written by <paramref name="expr"/>.</summary>
    public Asm Accum(Action<Asm> expr)
    {
        Op(0x18);
        expr(this);
        return this;
    }

    public byte[] ToArray() => [.. _b];
}

internal sealed class Scripts : IGplScripts
{
    public Dictionary<(int, GplFileKind), byte[]> Files { get; } = [];

    public byte[]? Load(int file, GplFileKind kind) => Files.GetValueOrDefault((file, kind));
}

public class GplMachineTests
{
    private static (GplMachine Machine, LoggingGplHost Host, Scripts Scripts) Make(byte[] gpl1)
    {
        var scripts = new Scripts();
        scripts.Files[(1, GplFileKind.Gpl)] = gpl1;
        var host = new LoggingGplHost();
        return (new GplMachine(scripts, host, [], new BorlandRandom(1)), host, scripts);
    }

    private static GplRunResult Run(GplMachine m) => m.Run(new GplEntry(1, 0));

    [Fact]
    public void OperatorsApplyLeftToRightWithoutPrecedence()
    {
        var a = new Asm().Accum(e => e.Num(2).Op(0xD1).Num(3).Op(0xD3).Num(4)).Op(0x31);
        var (m, _, _) = Make(a.ToArray());

        Assert.True(Run(m).Ok);
        Assert.Equal(20, m.Accumulator);
    }

    [Fact]
    public void ParenthesesEvaluateFirst()
    {
        var a = new Asm().Accum(e => e.Num(2).Op(0xD1).Op(0xE2).Num(3).Op(0xD3).Num(4).Op(0xE1)).Op(0x31);
        var (m, _, _) = Make(a.ToArray());

        Run(m);
        Assert.Equal(14, m.Accumulator);
    }

    /// <summary>IF; lnum[0] = 1; ELSE; lnum[1] = 1; ENDIF, with the accumulator set to <paramref name="acc"/>.</summary>
    private static (int, int) IfElse(int acc)
    {
        var a = new Asm().Accum(e => e.Num(acc));
        a.Op(0x3E);
        int toElse = a.Placeholder();
        a.Set(GplVarType.LocalNum, 0, 1).Op(0x3F);
        int toEnd = a.Placeholder();
        a.Patch(toElse, a.Here);
        a.Set(GplVarType.LocalNum, 1, 1);
        a.Patch(toEnd, a.Here);
        a.Op(0x67).Op(0x31);
        var (m, _, _) = Make(a.ToArray());
        Run(m);
        return (m.Memory.Read(new GplVariable(GplVarType.LocalNum, 0)), m.Memory.Read(new GplVariable(GplVarType.LocalNum, 1)));
    }

    [Fact]
    public void IfElseFollowTheAccumulator()
    {
        Assert.Equal((1, 0), IfElse(1));
        Assert.Equal((0, 1), IfElse(0));
    }

    [Fact]
    public void ElseTestsOnlyTheLowByte()
    {
        // DSUN.EXE keeps (byte)acc for ELSE: 256 runs the IF branch and the ELSE branch.
        Assert.Equal((1, 1), IfElse(256));
    }

    [Fact]
    public void CompareRunsTheMatchingCase()
    {
        var a = new Asm().Op(0x17).Num(2);
        a.Op(0x27).Num(1);
        int next1 = a.Placeholder();
        a.Set(GplVarType.LocalNum, 0, 1).Op(0x29);
        int end1 = a.Placeholder();
        a.Patch(next1, a.Here);
        a.Op(0x27).Num(2);
        int next2 = a.Placeholder();
        a.Set(GplVarType.LocalNum, 1, 1).Op(0x29);
        int end2 = a.Placeholder();
        a.Patch(next2, a.Here);
        a.Set(GplVarType.LocalNum, 2, 1);
        a.Patch(end1, a.Here);
        a.Patch(end2, a.Here);
        a.Op(0x61).Op(0x31);
        var (m, _, _) = Make(a.ToArray());

        Assert.True(Run(m).Ok);
        Assert.Equal([0, 1, 0], Enumerable.Range(0, 3).Select(i => m.Memory.Read(new GplVariable(GplVarType.LocalNum, i))));
    }

    [Fact]
    public void WhileLoopsAndWordAddChangesTheVariable()
    {
        var a = new Asm();
        int loop = a.Here;
        a.Accum(e => e.Var(GplVarType.LocalNum, 0).Op(0xDA).Num(5));
        a.Op(0x63);
        int exit = a.Placeholder();
        a.Op(0x7A).Var(GplVarType.LocalNum, 0).Num(1); // WORD += lnum[0], 1
        a.Op(0x64).Num(loop);
        a.Patch(exit, a.Here);
        a.Op(0x31);
        var (m, _, _) = Make(a.ToArray());

        Assert.True(Run(m).Ok);
        Assert.Equal(5, m.Memory.Read(new GplVariable(GplVarType.LocalNum, 0)));
    }

    [Fact]
    public void ByteCommandsChangeOneByteOfAWord()
    {
        var a = new Asm().Set(GplVarType.GlobalNum, 0, 0x01FF).Op(0x05).Var(GplVarType.GlobalNum, 0).Op(0x31); // BYTE_INC
        var (m, _, _) = Make(a.ToArray());

        Run(m);
        Assert.Equal(0x0100, m.Memory.Read(new GplVariable(GplVarType.GlobalNum, 0)));
    }

    [Fact]
    public void LocalCallsReturn()
    {
        var a = new Asm().Op(0x13);
        int sub = a.Placeholder();
        a.Set(GplVarType.LocalNum, 1, 2).Op(0x31);
        a.Patch(sub, a.Here);
        a.Set(GplVarType.LocalNum, 0, 1).Op(0x15);
        var (m, _, _) = Make(a.ToArray());

        Assert.True(Run(m).Ok);
        Assert.Equal((1, 2), (m.Memory.Read(new GplVariable(GplVarType.LocalNum, 0)), m.Memory.Read(new GplVariable(GplVarType.LocalNum, 1))));
    }

    [Fact]
    public void CrossFileCallsReturnAndClearLocals()
    {
        var caller = new Asm().Set(GplVarType.LocalNum, 0, 5).Op(0x14).Num(1).Num(2) // CALL_GLOBAL @1, file 2
            .Accum(e => e.Var(GplVarType.LocalNum, 0)).Op(0x31);
        var callee = new Asm().Op(0x19).Set(GplVarType.GlobalNum, 0, 7).Op(0x19);
        var (m, _, scripts) = Make(caller.ToArray());
        scripts.Files[(2, GplFileKind.Gpl)] = callee.ToArray();

        Assert.True(Run(m).Ok);
        Assert.Equal(7, m.Memory.Read(new GplVariable(GplVarType.GlobalNum, 0)));
        Assert.Equal(0, m.Accumulator); // the caller's locals were cleared by the reload
    }

    [Fact]
    public void FlagsAreBits()
    {
        var a = new Asm().Set(GplVarType.GlobalFlag, 9, 1).Set(GplVarType.GlobalFlag, 10, 5).Set(GplVarType.GlobalFlag, 10, 0).Op(0x31);
        var (m, _, _) = Make(a.ToArray());

        Run(m);
        Assert.Equal(0b10, m.Memory.Area(GplArea.GlobalFlags)[1]);
        Assert.Equal(1, m.Memory.Read(new GplVariable(GplVarType.GlobalFlag, 9)));
    }

    [Fact]
    public void RandScalesTheBorlandSequence()
    {
        var a = new Asm().Op(0x52).Num(32767).Op(0x31);
        var (m, _, _) = Make(a.ToArray());

        Run(m);
        Assert.Equal(346, m.Accumulator); // rand() * 32768 / 32768
    }

    [Fact]
    public void WorldCommandsGoToTheHost()
    {
        var a = new Asm().Op(0x5D).Num(12).Op(0x31); // SOUND(12)
        var (m, host, _) = Make(a.ToArray());

        Run(m);
        Assert.Equal("SOUND(12)", Assert.Single(host.Log).Command.ToString());
    }

    [Fact]
    public void RunawayLoopsStop()
    {
        var a = new Asm();
        a.Op(0x12).Num(0);
        var (m, _, _) = Make(a.ToArray());
        m.InstructionLimit = 1000;

        Assert.False(Run(m).Ok);
    }

    [Fact]
    public void FileZeroIsNoScript() => Assert.True(Make([]).Machine.Run(new GplEntry(0, 0)).Ok);
}

public class GplTriggerTests
{
    [Fact]
    public void TileTriggersFireForEveryMover()
    {
        var t = new GplTriggers();
        t.AddTile(GplTriggerList.MoveTile, 10, 20, 0x40, 7, 0);

        // Byte +0A is never written for tiles and starts as 0xFF.
        Assert.Equal(new GplEntry(7, 0x40), t.MatchMove(10, 20, who: 1));
        Assert.Null(t.MatchMove(10, 21, who: 0));
    }

    [Fact]
    public void BoxWhoZeroFiresOnlyForTheActiveMember()
    {
        var t = new GplTriggers();
        t.AddBox(GplTriggerList.MoveBox, 5, 5, 3, 2, 0x10, 9, who: 0);

        Assert.Equal(new GplEntry(9, 0x10), t.MatchMove(7, 6, who: 0));
        Assert.Null(t.MatchMove(7, 6, who: 1));
        Assert.Null(t.MatchMove(8, 6, who: 0)); // x = 5..7
    }

    [Fact]
    public void SameKeyReplaces()
    {
        var t = new GplTriggers();
        t.AddObject(GplTriggerList.TalkTo, 1, 59, -322, global: false);
        t.AddObject(GplTriggerList.TalkTo, 2, 60, -322, global: false);

        Assert.Equal(1, t.Count(GplTriggerList.TalkTo));
        Assert.Equal(new GplEntry(60, 2), t.MatchObject(GplTriggerList.TalkTo, -322));
    }

    [Fact]
    public void RegionChangeKeepsOnlyGlobalObjectTriggers()
    {
        var t = new GplTriggers();
        t.AddObject(GplTriggerList.TalkTo, 1, 2, -7, global: true);
        t.AddObject(GplTriggerList.TalkTo, 1, 2, -8, global: false);
        t.AddTile(GplTriggerList.MoveTile, 1, 1, 1, 2, 0);

        t.ClearRegion();

        Assert.NotNull(t.MatchObject(GplTriggerList.TalkTo, -7));
        Assert.Null(t.MatchObject(GplTriggerList.TalkTo, -8));
        Assert.Equal(0, t.Count(GplTriggerList.MoveTile));
    }

    [Fact]
    public void AFullTableOverwritesTheRecordAtTheInsertionPoint()
    {
        var t = new GplTriggers();
        for (int i = 0; i < GplTriggers.Capacity; i++)
        {
            t.AddObject(GplTriggerList.Use, 1, 1, i * 2, global: false);
        }
        t.AddObject(GplTriggerList.Use, 5, 5, 1, global: false);

        Assert.Equal(new GplEntry(5, 5), t.MatchObject(GplTriggerList.Use, 1));
        Assert.Null(t.MatchObject(GplTriggerList.Use, 2));
        Assert.Equal(GplTriggers.Capacity, t.Count(GplTriggerList.Use));
    }

    [Fact]
    public void ScriptsMadeByTheGameStartScriptSurviveRegions()
    {
        var scripts = new Scripts();
        scripts.Files[(99, GplFileKind.Mas)] = new Asm().Op(0x6E).Num(5).Num(3).Bytes(0x91, 0, 7).Op(0x31).ToArray();
        scripts.Files[(2, GplFileKind.Mas)] = new Asm().Op(0x6E).Num(6).Num(3).Bytes(0x91, 0, 8).Op(0x31).ToArray();
        scripts.Files[(3, GplFileKind.Mas)] = new Asm().Op(0x31).ToArray();
        var m = new GplMachine(scripts, new LoggingGplHost(), [], new BorlandRandom(1));

        m.NewGame();
        m.EnterRegion(2);
        Assert.NotNull(m.Triggers.MatchObject(GplTriggerList.TalkTo, -8));
        m.EnterRegion(3);

        Assert.NotNull(m.Triggers.MatchObject(GplTriggerList.TalkTo, -7));
        Assert.Null(m.Triggers.MatchObject(GplTriggerList.TalkTo, -8));
    }
}
