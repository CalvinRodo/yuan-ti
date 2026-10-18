namespace DarkSun.Game.Scripting;

/// <summary>Variable kinds, as encoded in GPL operands (<c>dsun-exe-reference.md</c> §2).</summary>
public enum GplVarType : byte
{
    LocalString = 0x01,
    LocalNum = 0x02,
    LocalBigNum = 0x05,
    GlobalString = 0x06,
    GlobalNum = 0x07,
    EngineVar = 0x09,
    GlobalBigNum = 0x0A,
    GlobalFlag = 0x0D,
    LocalFlag = 0x0E,
}

/// <summary>String operand kinds (<c>gpl_read_string</c>, <c>043D:2BB7</c>).</summary>
public enum GplStringKind : byte
{
    /// <summary>Not a string kind: DSUN.EXE reuses its last string and reads nothing.</summary>
    Previous = 0,
    SpeakerName = 0x01,
    Raw = 0x02,
    Packed = 0x05,
}

/// <summary>An operand inside an expression.</summary>
public abstract record GplOperand;

/// <summary>A number: 15-bit, int8, int16 or int32 immediate.</summary>
public sealed record GplNumber(int Value) : GplOperand;

public sealed record GplAccumulator : GplOperand;

/// <summary><c>name(-n)</c>: an object by number; its value is −n.</summary>
public sealed record GplName(int Number) : GplOperand;

public sealed record GplVariable(GplVarType Type, int Index) : GplOperand;

/// <summary>A string; <see cref="Codes"/> are the character codes as stored.</summary>
public sealed record GplString(GplStringKind Kind, byte[] Codes) : GplOperand
{
    public string Text => new([.. Codes.Select(c => (char)c)]);
}

/// <summary>An object's field: word id (top bit: a special object), then field bytes.</summary>
public sealed record GplObjectRef(int Id, byte[] Path) : GplOperand
{
    public bool IsSpecial => Id >= 0x8000;
}

/// <summary>A command run inside an expression; its value is the accumulator afterwards.</summary>
public sealed record GplEmbedded(GplInstruction Instruction) : GplOperand;

/// <summary>Expression items in byte order: operands, operators and parentheses.</summary>
public abstract record GplExprItem;

public sealed record GplTerm(GplOperand Operand) : GplExprItem;

/// <summary>Operator byte 0xD1–0xDF.</summary>
public sealed record GplOperator(byte Code) : GplExprItem;

public sealed record GplOpen : GplExprItem;

public sealed record GplClose : GplExprItem;

/// <summary>
/// Operands joined by operators, applied left to right without precedence;
/// parentheses group (at most 7 deep).
/// </summary>
public sealed record GplExpression(IReadOnlyList<GplExprItem> Items);

/// <summary>An assignment target: a numeric or string variable, or an object field.</summary>
public sealed record GplTarget(GplVariable? Variable, GplObjectRef? Field);

/// <summary>One SEARCH criterion: field, mode, and a value for some field types.</summary>
public sealed record GplSearchCriterion(byte Field, byte Mode, GplExpression? Value);

public sealed record GplSearch(GplExpression Who, byte Low, byte High, IReadOnlyList<GplSearchCriterion> Criteria);

public sealed record GplMenuEntry(
    IReadOnlyList<GplInstruction> Before, GplExpression Text, GplExpression Address, GplExpression Condition,
    IReadOnlyList<GplInstruction> After);

public sealed record GplMenu(IReadOnlyList<GplInstruction> Before, GplExpression Title, IReadOnlyList<GplMenuEntry> Entries);

/// <summary>A decoded command. Which of the optional parts are set depends on the opcode.</summary>
public sealed record GplInstruction(int Address, byte Op, int Length)
{
    public string Mnemonic => GplOpcodes.All.TryGetValue(Op, out GplOpcode? o) ? o.Mnemonic : $"OP_{Op:X2}";

    /// <summary>Operands of the generic commands, in order; strings are wrapped as single-term expressions.</summary>
    public IReadOnlyList<GplExpression> Args { get; init; } = [];

    /// <summary>LOAD_VAR: the value; LOAD_ACCUM: the expression.</summary>
    public GplExpression? Value { get; init; }

    public GplTarget? Target { get; init; }

    public GplSearch? Search { get; init; }

    public GplMenu? Menu { get; init; }

    /// <summary>SETRECORD: the field (null for id 0x8000, which reads nothing).</summary>
    public GplObjectRef? Record { get; init; }
}
