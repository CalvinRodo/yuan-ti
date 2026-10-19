using DarkSun.Game.Rules;
using DarkSun.Game.Scripting;

namespace DarkSun.Game.Tests;

public class GplDebuggerTests
{
    // GPL 1 calls a local function, then GPL 2 at its start.
    private const string One = """
        ; GPL 1
          lnum[0] = 1
          CALL_LOCAL(@plus)
          CALL_GLOBAL(0, 2)
          gnum[1] = lnum[0]
          EXIT_GPL
        plus:
          lnum[0] = lnum[0] + 10
          LOCAL_RET
        """;

    private const string Two = """
          gnum[2] = 7
          END_FUNCTION
        """;

    private static (GplMachine Machine, GplSource One) Make()
    {
        var scripts = new Scripts();
        scripts.Files[(1, GplFileKind.Gpl)] = GplAssembler.Assemble(One);
        scripts.Files[(2, GplFileKind.Gpl)] = GplAssembler.Assemble(Two);
        var m = new GplMachine(scripts, new LoggingGplHost(), [], new BorlandRandom(1));
        return (m, GplSource.For(m, 1, GplFileKind.Gpl)!);
    }

    /// <summary>The first line that has <paramref name="text"/>.</summary>
    private static int L(GplSource source, string text) => source.Text.Split('\n').ToList().FindIndex(l => l.Contains(text)) + 1;

    private static GplEntry At(GplSource source, int line) => source.Script with { Address = source.AddressAt(line)!.Value };

    private static int Line(GplMachine m, GplSource source) => source.LineAt(m.Current.Address)!.Value;

    private static int Num(GplMachine m, GplVarType type, int i) => m.Memory.Read(new GplVariable(type, i));

    [Fact]
    public void ABreakpointPausesBeforeTheCommandAndContinueRunsOn()
    {
        var (m, source) = Make();
        int callGlobal = L(source, "CALL_GLOBAL");
        m.Breakpoints.Add(At(source, callGlobal));

        GplRunResult paused = m.Run(new GplEntry(1, 0));

        Assert.True(paused.Paused && paused.Suspended && paused.Ok);
        Assert.Equal("breakpoint", m.PausedFor);
        Assert.True(m.IsSuspended);
        Assert.Equal(callGlobal, Line(m, source));
        Assert.Equal(11, Num(m, GplVarType.LocalNum, 0)); // the lines before it ran
        Assert.Equal(0, Num(m, GplVarType.GlobalNum, 2)); // it didn't
        Assert.Throws<InvalidOperationException>(() => m.Resume(0));

        GplRunResult done = m.Continue();

        Assert.True(done.Ok && !done.Suspended);
        Assert.Null(m.PausedFor);
        Assert.Equal(7, Num(m, GplVarType.GlobalNum, 2));
        Assert.Equal(0, Num(m, GplVarType.GlobalNum, 1)); // locals are cleared on return from another file
    }

    [Fact]
    public void StepIntoOverAndOut()
    {
        var (m, source) = Make();
        m.Breakpoints.Add(At(source, L(source, "CALL_LOCAL")));
        m.Run(new GplEntry(1, 0));

        m.Continue(GplStep.Into);
        Assert.Equal(L(source, "+ 10"), Line(m, source)); // into the function
        Assert.Equal(2, m.CallStack.Count);
        Assert.Equal(At(source, L(source, "CALL_GLOBAL")), m.CallStack[1].At); // returns to CALL_GLOBAL

        m.Continue(GplStep.Out);
        Assert.Equal(L(source, "CALL_GLOBAL"), Line(m, source)); // back after the call
        Assert.Single(m.CallStack);

        m.Continue(GplStep.Over); // over the cross-file call
        Assert.Equal(L(source, "gnum[1]"), Line(m, source));
        Assert.Equal(7, Num(m, GplVarType.GlobalNum, 2));

        Assert.True(m.Continue(GplStep.Over).Paused); // EXIT_GPL
        Assert.False(m.Continue(GplStep.Over).Paused); // the run ends: stepping ends with it
        Assert.Equal(GplStep.None, m.Step);
        Assert.False(m.Run(new GplEntry(2, 0)).Paused);
    }

    [Fact]
    public void StepIntoACrossFileCallShowsTheOtherScript()
    {
        var (m, source) = Make();
        m.Breakpoints.Add(At(source, L(source, "CALL_GLOBAL")));
        m.Run(new GplEntry(1, 0));

        m.Continue(GplStep.Into);

        Assert.Equal(new GplEntry(2, 0), m.Current);
        Assert.Equal([2, 1], m.CallStack.Select(f => f.At.File));
    }

    [Fact]
    public void BreakOnNextRunSkipsCreatureScripts()
    {
        var (m, _) = Make();
        m.BreakOnNextRun = true;

        Assert.False(m.RunFor(new GplEntry(2, 0), 5)!.Paused);
        GplRunResult run = m.Run(new GplEntry(1, 0));

        Assert.True(run.Paused);
        Assert.Equal(new GplEntry(1, 0), m.Current);
        Assert.False(m.BreakOnNextRun);
    }

    [Fact]
    public void AbandonDropsThePausedScript()
    {
        var (m, _) = Make();
        m.BreakOnNextRun = true;
        m.Run(new GplEntry(1, 0));

        m.Abandon();

        Assert.False(m.IsSuspended);
        Assert.Empty(m.CallStack);
        Assert.True(m.Run(new GplEntry(2, 0)).Ok);
    }

    [Fact]
    public void TheSourceMapsLinesToCommands()
    {
        var (_, source) = Make();

        Assert.Equal("disassembled", source.Origin);
        Assert.Equal(0, source.AddressAt(1)); // a comment line: the first command after it
        Assert.All(source.LineOf, kv => Assert.Equal(kv.Value, source.LineAt(kv.Key)));
        Assert.Null(source.AddressAt(10_000));
    }

    [Fact]
    public void AModsSourceIsShownWhenItMatchesTheCode()
    {
        string path = Path.Combine(Path.GetTempPath(), $"darksun-{Guid.NewGuid():N}.gpl");
        try
        {
            File.WriteAllText(path, One);
            var (m, _) = Make();
            GplSource source = GplSource.For(m, 1, GplFileKind.Gpl, path)!;
            Assert.Equal(path, source.Origin);
            Assert.Equal(2, source.LineAt(0)); // lnum[0] = 1, after the comment
            Assert.Equal(8, source.LineAt(source.AddressAt(7)!.Value)); // "plus:" is line 7, its command line 8

            File.WriteAllText(path, One.Replace("+ 10", "+ 20")); // edited since it was loaded
            Assert.Equal("disassembled", GplSource.For(m, 1, GplFileKind.Gpl, path)!.Origin);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void VariablesCanBeReadAndSetByName()
    {
        var (m, _) = Make();

        Assert.Null(GplVariables.Set(m, "gnum[5] = 3"));
        Assert.Null(GplVariables.Set(m, "gflag[9] = 1"));
        Assert.Null(GplVariables.Set(m, "gstr[1] = \"Ral\""));
        Assert.Null(GplVariables.Set(m, "gname[6] = -16"));

        Assert.Equal("3", GplVariables.Get(m, "gnum[5]"));
        Assert.Equal("\"Ral\"", GplVariables.Get(m, "gstr[1]"));
        Assert.Equal("-16", GplVariables.Get(m, "gname[6]"));
        var shown = GplVariables.NonZero(m);
        Assert.Contains(new GplWatch("gflag[9]", "1", false), shown);
        Assert.DoesNotContain(shown, w => w.Name == "gnum[4]");
        Assert.NotNull(GplVariables.Set(m, "gnum[200] = 1"));
        Assert.NotNull(GplVariables.Set(m, "foo[1] = 1"));
        Assert.NotNull(GplVariables.Set(m, "gnum[1] = x"));
    }

    [Fact]
    public void BreakpointsReadBackWhatTheyWrite()
    {
        var b = new GplBreakpoints();
        b.Add(new GplEntry(12, 0x40));
        b.Add(new GplEntry(42, 0, GplFileKind.Mas));

        var back = new GplBreakpoints();
        back.Read(b.Write() + "\nnonsense");

        Assert.Equal("GPL 12 @0040\nMAS 42 @0000", b.Write());
        Assert.Equal(b.All.ToHashSet(), back.All.ToHashSet());
        Assert.False(back.Toggle(new GplEntry(12, 0x40)));
        Assert.Equal(1, back.Count);
    }
}
