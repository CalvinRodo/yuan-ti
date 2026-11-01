using DarkSun.Game.Scripting;

namespace DarkSun.Game.Tests;

public class GplAssemblerTests
{
    // Field 3 takes a value in SEARCH (type 0x10).
    private static readonly byte[] FieldTypes = [0, 0, 0, 0x10];

    private const string Script = """
        ; a test script
          END_FUNCTION
        start:
          ACCUM <= gflag[83] == 0 && (lnum[2] + 1 > 3)
          IF(@done)
            lnum[300] = (((RAND(6)))) * -2
            PRINT_STRING(115, "Say \"hi\"\x7e")   ; a comment; with "quotes"
            obj_special(0x28).72.75 = name(-87)
            SEARCH(gname[5], range=[0,9], field[3](4)=7, field[1](2))
            MENU
              TITLE("Well?")
              ENTRY(text="Yes", func=@start, cond=1)
              SOURCE_LINE(4)
              ENTRY(text=<name>, func=@done, cond=lflag[0])
            MENU_END
          ELSE(@done)
            CALL_GLOBAL(@0064, 12)
        done:
          ENDIF
          LONG +=(gbignum[1], 100000)
          EXIT_GPL
        """;

    [Fact]
    public void AssemblesWhatTheDecoderReads()
    {
        byte[] code = GplAssembler.Assemble(Script);

        var lines = GplDecoder.Listing(code, FieldTypes).ToList();
        Assert.DoesNotContain(lines, l => l.Contains("??"));
        Assert.Contains(lines, l => l.EndsWith("ACCUM <= gflag[83] == 0 && (lnum[2] + 1 > 3)"));
        Assert.Contains(lines, l => l.EndsWith("lnum[300] = (((RAND(6)))) * -2"));
        Assert.Contains(lines, l => l.EndsWith("PRINT_STRING(115, \"Say \"hi\"~\")"));
        Assert.Contains(lines, l => l.EndsWith("SEARCH(gname[5], range=[0,9], field[3](4)=7, field[1](2))"));
        Assert.Contains(lines, l => l.EndsWith("ENTRY(text=\"Yes\", func=@0001, cond=1)"));
        Assert.Contains(lines, l => l.EndsWith("LONG +=(gbignum[1], 100000)"));
    }

    [Fact]
    public void StoresNumbersAsTheOriginalScriptsDo()
    {
        Assert.Equal([0x4F, 0x00, 0x73, 0x86, 0x02], GplAssembler.Assemble("PRINT_STRING(115, gstr[2])"));
        Assert.Equal([0x16, 0x8F, 0x01, 0x8E, 0x05], GplAssembler.Assemble("lflag[5] = 1"));
        Assert.Equal([0x16, 0x00, 0x01, 0x8E, 0x05], GplAssembler.Assemble("lflag[5] = 1:w"));
        Assert.Equal([0x0C, 0x8B, 0xFF, 0xFF, 0xFF, 0xDD], GplAssembler.Assemble("CHANGE_MONEY(-35:l)"));
        Assert.Equal([0x14, 0x00, 0x44, 0x00, 0x03], GplAssembler.Assemble("CALL_GLOBAL(@0044, 3)"));
        Assert.Equal([0x5D, 0x01, 0x2C], GplAssembler.Assemble("SOUND(300)"));
    }

    [Fact]
    public void SaysWhereLabelAddressesAreForCodeThatWillMove()
    {
        // ACCUM <= 1 (3 bytes); IF(@skip) (its address at 4); lflag[5] = 1; JUMP(@0010), a literal address, stays.
        byte[] code = GplAssembler.AssembleMovable("ACCUM <= 1\nIF(@skip)\nlflag[5] = 1\nskip:\nENDIF\nJUMP(@0010)", out int[] labels);

        Assert.Equal([4], labels);
        Assert.Equal(11, code[4] << 8 | code[5]);
    }

    [Fact]
    public void DisassemblesToSourceThatAssemblesToTheSameBytes()
    {
        byte[] code = [.. GplAssembler.Assemble(Script), 0x26, 0xEE];

        string source = GplAssembler.Disassemble(code, FieldTypes, entries: [1]);

        Assert.Contains(".org 0x0001", source);
        Assert.Contains("L0001:", source);
        Assert.Contains("IF(@L", source);
        Assert.Contains("CALL_GLOBAL(@0064, 12)", source);
        Assert.Contains("PRINT_STRING(115, \"Say \\\"hi\\\"~\")", source);
        Assert.Contains(".bytes 0x26 0xee", source);
        Assert.Equal(code, GplAssembler.Assemble(source));
    }

    [Fact]
    public void RequestCodesHaveNames()
    {
        byte[] code = GplAssembler.Assemble("REQUEST(SET_DOOR, -98, 0, 0)");

        Assert.Equal(GplAssembler.Assemble("REQUEST(2, -98, 0, 0)"), code);
        Assert.EndsWith("REQUEST(SET_DOOR, -98, 0, 0)", Assert.Single(GplDecoder.Listing(code, FieldTypes)));
        string source = GplAssembler.Disassemble(code, FieldTypes);
        Assert.Contains("REQUEST(SET_DOOR, -98, 0, 0)", source);
        Assert.Equal(code, GplAssembler.Assemble(source));
        // A code without a name, or stored in a longer form than the name would be, stays a number.
        Assert.Contains("REQUEST(99, 0, 0, 0)", GplAssembler.Disassemble(GplAssembler.Assemble("REQUEST(99, 0, 0, 0)"), FieldTypes));
        Assert.Contains("REQUEST(2:w, 0, 0, 0)", GplAssembler.Disassemble(GplAssembler.Assemble("REQUEST(2:w, 0, 0, 0)"), FieldTypes));
    }

    [Fact]
    public void OrgKeepsEntryPointsInPlace()
    {
        byte[] code = GplAssembler.Assemble("END_FUNCTION\n.org 0x0004\nentry: EXIT_GPL\nJUMP(@entry)");

        Assert.Equal([0x19, 0x19, 0x19, 0x19, 0x31, 0x12, 0x00, 0x04], code);
        var e = Assert.Throws<GplAssemblyException>(() => GplAssembler.Assemble("ENDIF\nLOG(\"too long\")\n.org 0x0002\nEXIT_GPL"));
        Assert.Equal(3, e.Line);
        Assert.Contains("JUMP", e.Message);
    }

    [Theory]
    [InlineData("EXIT_GPL\nIF(@nowhere)", 2, "no label nowhere")]
    [InlineData("FROB(1)", 1, "unknown command 'FROB'")]
    [InlineData("REQUEST(OPEN_SESAME, 0, 0, 0)", 1, "unknown operand 'OPEN_SESAME'")]
    [InlineData("GOXY(1, 2)", 1, "expected ','")]
    [InlineData("lnum[1] == 2", 1, "expected '='")]
    [InlineData("MENU\n  TITLE(\"x\")", 1, "MENU without MENU_END")]
    [InlineData("x:\nx:", 2, "defined twice")]
    [InlineData("a:", 1, "looks like a hex address")]
    [InlineData("lnum[0] = ((RAND(6))) * 2", 1, "put the command in parentheses")]
    [InlineData("LOG(\"caf\u00e9\")", 1, "can't be stored")]
    public void SaysWhereItIsWrong(string source, int line, string message)
    {
        var e = Assert.Throws<GplAssemblyException>(() => GplAssembler.Assemble(source));

        Assert.Equal(line, e.Line);
        Assert.Contains(message, e.Message);
    }

    [Fact]
    public void FindsEntryPointsOtherScriptsUse()
    {
        byte[] mas = GplAssembler.Assemble("CREATE_TALKTO_TRIGGER(@0010, 12, name(-87))\nCALL_GLOBAL(@0020, 12)\nCALL_GLOBAL(@0030, 5)");

        var entries = GplAssembler.EntryPoints([mas], []);

        Assert.Equal([0x10, 0x20], entries[12].Order());
        Assert.Equal([0x30], entries[5]);
    }
}
