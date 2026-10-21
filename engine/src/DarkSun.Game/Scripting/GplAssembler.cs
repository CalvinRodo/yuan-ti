using System.Globalization;
using System.Text;

namespace DarkSun.Game.Scripting;

public sealed class GplAssemblyException(int line, string message) : Exception(line > 0 ? $"line {line}: {message}" : message)
{
    public int Line { get; } = line;
}

/// <summary>
/// Turns GPL source into bytecode, and bytecode into source. The source is
/// the listing's syntax (<see cref="GplDecoder.Listing"/>), one command per
/// line, without the address column, plus:
/// <list type="bullet">
/// <item><c>name:</c> a label; <c>@name</c> is its address, anywhere a number
/// goes (<c>IF(@skip)</c>, a trigger's address). <c>@1a2f</c> (hex digits only)
/// is a literal address. Addresses are always two bytes.</item>
/// <item><c>.org 0x1a2f</c>: pad with END_FUNCTION up to that address (an
/// error if the code is already past it). <see cref="Disassemble"/> puts one
/// before each entry point other scripts use, so they stay where the callers
/// expect them; to grow a function there, JUMP to new code at the end of the
/// file.</item>
/// <item><c>.bytes 16 0x2a …</c>: bytes as they are.</item>
/// <item>A menu in byte order: <c>MENU</c>, its debug-text commands,
/// <c>TITLE(expr)</c>, <c>ENTRY(text=…, func=…, cond=…)</c> lines,
/// <c>MENU_END</c>. The listing's <c>MENU(title=…)</c> works too.</item>
/// <item>Strings take <c>\"</c>, <c>\\</c> and <c>\xNN</c>; <c>;</c> starts a comment.</item>
/// <item>A REQUEST name (<see cref="GplRequests"/>) is its code: <c>REQUEST(SET_DOOR, -98, 0, 0)</c>
/// is <c>REQUEST(2, -98, 0, 0)</c>.</item>
/// </list>
/// Numbers are stored as the original scripts store them: int8 if they fit,
/// else a 15-bit word, int16 or int32, except that file numbers, menu
/// conditions and PRINT_STRING / PRINT_NUMBER's first argument are words
/// (<see cref="GplOpcodes.WordArguments"/>). A suffix picks the form:
/// <c>5:w</c> word, <c>5:b</c> int8, <c>5:s</c> int16, <c>5:l</c> int32. Strings are
/// packed. Indentation is free.
/// </summary>
public static class GplAssembler
{
    private const byte Pad = GplOpcodes.EndFunction;

    private static readonly (string Symbol, byte Code)[] Operators =
    [
        ("&&", 0xD5), ("||", 0xD6), ("==", 0xD7), ("!=", 0xD8), (">=", 0xDB), ("<=", 0xDC), ("&~", 0xDF),
        ("+", 0xD1), ("-", 0xD2), ("*", 0xD3), ("/", 0xD4), (">", 0xD9), ("<", 0xDA), ("&", 0xDD), ("|", 0xDE),
    ];

    private static readonly Dictionary<string, GplVarType> VarTypes = new()
    {
        ["lstr"] = GplVarType.LocalString, ["lnum"] = GplVarType.LocalNum, ["lbignum"] = GplVarType.LocalBigNum,
        ["gstr"] = GplVarType.GlobalString, ["gnum"] = GplVarType.GlobalNum, ["gname"] = GplVarType.EngineVar,
        ["gbignum"] = GplVarType.GlobalBigNum, ["gflag"] = GplVarType.GlobalFlag, ["lflag"] = GplVarType.LocalFlag,
    };

    // Longest first, so "BYTE +=" isn't read as a shorter mnemonic.
    private static readonly (string Mnemonic, byte Op)[] Mnemonics =
        [.. GplOpcodes.All.Select(o => (o.Value.Mnemonic, o.Key)).OrderByDescending(m => m.Mnemonic.Length)];

    /// <summary>Assemble <paramref name="source"/>; throws <see cref="GplAssemblyException"/> naming the line.</summary>
    public static byte[] Assemble(string source) => Assemble(source, out _);

    /// <summary>
    /// Assemble <paramref name="source"/>, and say which line (1-based) each
    /// command starts at: <paramref name="lines"/> maps a command's address
    /// to its line (a menu's to its <c>MENU</c> line). For the debugger.
    /// </summary>
    public static byte[] Assemble(string source, out IReadOnlyDictionary<int, int> lines)
    {
        var map = new Dictionary<int, int>();
        lines = map;
        var w = new Writer();
        string[] sourceLines = source.Split('\n');
        bool inMenu = false;
        int menuLine = 0;
        for (int n = 0; n < sourceLines.Length; n++)
        {
            w.Line = n + 1;
            string text = StripComment(sourceLines[n]).Trim();
            if (text.Length == 0)
            {
                continue;
            }
            try
            {
                text = TakeLabel(text, w);
                if (text.Length == 0)
                {
                    continue;
                }
                if (!inMenu && !text.StartsWith(".org", StringComparison.Ordinal))
                {
                    map.TryAdd(w.Address, w.Line);
                }
                if (inMenu)
                {
                    inMenu = MenuLine(text, w);
                }
                else if (text == "MENU" || text.StartsWith("MENU(", StringComparison.Ordinal))
                {
                    w.Bytes.Add(GplOpcodes.Menu);
                    if (text != "MENU")
                    {
                        var c = new Cursor(text, 5);
                        c.Expect("title=");
                        Expression(c, w);
                        c.Expect(")");
                        c.End();
                    }
                    inMenu = true;
                    menuLine = w.Line;
                }
                else if (text.StartsWith('.'))
                {
                    Directive(text, w);
                }
                else
                {
                    var c = new Cursor(text, 0);
                    Instruction(c, w);
                    c.End();
                }
            }
            catch (FormatException e)
            {
                throw new GplAssemblyException(w.Line, e.Message);
            }
        }
        if (inMenu)
        {
            throw new GplAssemblyException(menuLine, "MENU without MENU_END");
        }
        w.ResolveFixups();
        return [.. w.Bytes];
    }

    /// <summary>
    /// Source for <paramref name="data"/> that assembles back to the same bytes:
    /// labels at the addresses its jumps, branches and menus go to, and
    /// <c>.org</c> before each of <paramref name="entries"/> (addresses other
    /// scripts call: triggers, CALL_GLOBAL). A command whose bytes the
    /// assembler wouldn't write the same way (a longer number encoding than it
    /// needs) is kept as <c>.bytes</c>, with its text in a comment; so is
    /// anything after the first byte that doesn't decode.
    /// </summary>
    public static string Disassemble(byte[] data, byte[] fieldTypes, IEnumerable<int>? entries = null, string? title = null)
    {
        var decoder = new GplDecoder(data, fieldTypes);
        var instructions = new List<GplInstruction>();
        int end = 0;
        while (end < data.Length)
        {
            try
            {
                GplInstruction i = decoder.DecodeAt(end);
                instructions.Add(i);
                end += i.Length;
            }
            catch (GplDecodeException)
            {
                break;
            }
        }
        var starts = instructions.Select(i => i.Address).ToHashSet();
        var pinned = (entries ?? []).Where(starts.Contains).ToHashSet();
        var labels = new SortedSet<int>(pinned);
        foreach (GplInstruction i in instructions)
        {
            foreach (int target in Targets(i))
            {
                if (starts.Contains(target))
                {
                    labels.Add(target);
                }
            }
        }
        string Label(int address) => $"L{address:x4}";
        var style = new GplStyle(true, a => labels.Contains(a) ? Label(a) : null);
        var known = labels.ToDictionary(Label, a => a);

        var o = new StringBuilder();
        if (title is not null)
        {
            o.Append("; ").Append(title).Append('\n');
        }
        o.Append("; Assembled when the game loads it; see GplAssembler for the syntax.\n");
        int depth = 0;
        foreach (GplInstruction i in instructions)
        {
            if (pinned.Contains(i.Address))
            {
                o.Append(CultureInfo.InvariantCulture, $"\n.org 0x{i.Address:x4}  ; entry point used by other scripts\n");
            }
            if (labels.Contains(i.Address))
            {
                o.Append(Label(i.Address)).Append(":\n");
            }
            if (i.Op is 0x3F or 0x61 or 0x64 or 0x67 && depth > 0)
            {
                depth--;
            }
            string indent = new(' ', 2 + 2 * depth);
            string text = GplDecoder.Render(i, style);
            ReadOnlySpan<byte> original = data.AsSpan(i.Address, i.Length);
            if (AssemblesTo(text, known, i.Address, original))
            {
                foreach (string line in text.Split('\n'))
                {
                    o.Append(indent).Append(line).Append('\n');
                }
            }
            else
            {
                o.Append(indent).Append(Bytes(original)).Append('\n');
                foreach (string line in text.Split('\n'))
                {
                    o.Append(indent).Append("; ").Append(line).Append('\n');
                }
            }
            if (i.Op is 0x17 or 0x3E or 0x3F or 0x63)
            {
                depth++;
            }
        }
        if (end < data.Length)
        {
            o.Append("\n; not decoded\n");
            for (int at = end; at < data.Length; at += 16)
            {
                o.Append(Bytes(data.AsSpan(at, Math.Min(16, data.Length - at)))).Append('\n');
            }
        }
        return o.ToString();
    }

    /// <summary>
    /// The addresses in each <c>GPL </c> file that <paramref name="scripts"/>
    /// enter it at (triggers, CALL_GLOBAL, with constant arguments): what
    /// <see cref="Disassemble"/> needs to keep in place.
    /// </summary>
    public static Dictionary<int, HashSet<int>> EntryPoints(IEnumerable<byte[]> scripts, byte[] fieldTypes)
    {
        var entries = new Dictionary<int, HashSet<int>>();
        foreach (byte[] data in scripts)
        {
            var decoder = new GplDecoder(data, fieldTypes);
            for (int at = 0; at < data.Length;)
            {
                GplInstruction i;
                try
                {
                    i = decoder.DecodeAt(at);
                }
                catch (GplDecodeException)
                {
                    break;
                }
                at += i.Length;
                if (GplOpcodes.EntryArguments.TryGetValue(i.Op, out var arg) && i.Args.Count > arg.File
                    && Constant(i.Args[arg.Address]) is int address && Constant(i.Args[arg.File]) is int file)
                {
                    (entries.TryGetValue(file, out var set) ? set : entries[file] = []).Add((ushort)address);
                }
            }
        }
        return entries;
    }

    /// <summary>Addresses in this file that <paramref name="i"/> jumps or calls to (constant ones only).</summary>
    private static IEnumerable<int> Targets(GplInstruction i)
    {
        if (i.Menu is { } menu)
        {
            foreach (GplMenuEntry e in menu.Entries)
            {
                if (Constant(e.Address) is int a)
                {
                    yield return a;
                }
            }
        }
        if (i.Op == GplOpcodes.CallGlobal)
        {
            yield break;
        }
        string spec = GplOpcodes.All.TryGetValue(i.Op, out GplOpcode? op) ? op.Operands ?? "" : "";
        for (int n = 0; n < spec.Length && n < i.Args.Count; n++)
        {
            if (spec[n] == 'A' && Constant(i.Args[n]) is int a)
            {
                yield return a;
            }
        }
    }

    private static int? Constant(GplExpression e) => e.Items is [GplTerm { Operand: GplNumber n }] ? n.Value : null;

    private static bool AssemblesTo(string text, Dictionary<string, int> labels, int address, ReadOnlySpan<byte> original)
    {
        try
        {
            var w = new Writer { Base = address };
            foreach (var (k, v) in labels)
            {
                w.Labels[k] = v;
            }
            bool inMenu = false;
            foreach (string line in text.Split('\n'))
            {
                string t = line.Trim();
                if (inMenu)
                {
                    inMenu = MenuLine(t, w);
                }
                else if (t == "MENU")
                {
                    w.Bytes.Add(GplOpcodes.Menu);
                    inMenu = true;
                }
                else
                {
                    var c = new Cursor(t, 0);
                    Instruction(c, w);
                    c.End();
                }
            }
            w.ResolveFixups();
            return original.SequenceEqual(w.Bytes.ToArray());
        }
        catch (Exception e) when (e is FormatException or GplAssemblyException)
        {
            return false;
        }
    }

    private static string Bytes(ReadOnlySpan<byte> bytes)
    {
        var b = new StringBuilder(".bytes");
        foreach (byte x in bytes)
        {
            b.Append(CultureInfo.InvariantCulture, $" 0x{x:x2}");
        }
        return b.ToString();
    }

    // -- lines -------------------------------------------------------------------

    private static string StripComment(string line)
    {
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '\\' && quoted)
            {
                i++;
            }
            else if (line[i] == '"')
            {
                quoted = !quoted;
            }
            else if (line[i] == ';' && !quoted)
            {
                return line[..i];
            }
        }
        return line;
    }

    private static string TakeLabel(string text, Writer w)
    {
        int colon = text.IndexOf(':');
        if (colon <= 0 || !IsIdentifier(text.AsSpan(0, colon)))
        {
            return text;
        }
        string name = text[..colon];
        if (IsHex(name))
        {
            throw new FormatException($"label {name} looks like a hex address; give it a letter that isn't a-f");
        }
        if (!w.Labels.TryAdd(name, w.Address))
        {
            throw new FormatException($"label {name} defined twice");
        }
        return text[(colon + 1)..].Trim();
    }

    private static void Directive(string text, Writer w)
    {
        var c = new Cursor(text, 0);
        if (c.TryTake(".org"))
        {
            int address = c.Integer();
            c.End();
            if (address < w.Address)
            {
                throw new FormatException(
                    $".org 0x{address:x4}: the code before it is {w.Address - address} bytes too long (ends at 0x{w.Address:x4}); "
                    + "move the extra code to the end of the file and JUMP to it");
            }
            while (w.Address < address)
            {
                w.Bytes.Add(Pad);
            }
        }
        else if (c.TryTake(".bytes"))
        {
            while (!c.AtEnd)
            {
                int b = c.Integer();
                if (b is < 0 or > 255)
                {
                    throw new FormatException($"{b} is not a byte");
                }
                w.Bytes.Add((byte)b);
            }
        }
        else
        {
            throw new FormatException($"unknown directive {text.Split(' ')[0]}");
        }
    }

    /// <summary>One line inside MENU … MENU_END; false once it has ended.</summary>
    private static bool MenuLine(string text, Writer w)
    {
        var c = new Cursor(text, 0);
        if (c.TryTake("MENU_END"))
        {
            c.End();
            w.Bytes.Add(GplOpcodes.MenuEnd);
            return false;
        }
        if (c.TryTake("TITLE("))
        {
            Expression(c, w);
            c.Expect(")");
        }
        else if (c.TryTake("ENTRY("))
        {
            c.Expect("text=");
            Expression(c, w);
            c.Expect(",");
            c.Expect("func=");
            Expression(c, w);
            c.Expect(",");
            c.Expect("cond=");
            Expression(c, w, word: true);
            c.Expect(")");
        }
        else
        {
            int at = w.Bytes.Count;
            Instruction(c, w);
            if (w.Bytes[at] is not (0x23 or 0x28 or 0x2E or 0x4B))
            {
                throw new FormatException("only TITLE, ENTRY, MENU_END and the debug-text commands (SOURCE_TRACE, OP_28, SOURCE_LINE, LOCAL_SUB_TRACE) go in a menu");
            }
        }
        c.End();
        return true;
    }

    // -- commands ----------------------------------------------------------------

    private static void Instruction(Cursor c, Writer w)
    {
        c.SkipSpaces();
        if (c.TryTake("ACCUM"))
        {
            c.Expect("<=");
            w.Bytes.Add(GplOpcodes.LoadAccum);
            Expression(c, w);
            return;
        }
        if (c.TryWord("SETRECORD"))
        {
            w.Bytes.Add(GplOpcodes.SetRecord);
            if (!c.AtEnd && c.Peek() != ')')
            {
                byte[] record = ObjectRef(c);
                c.Expect("=");
                w.Bytes.AddRange(record);
                Expression(c, w);
            }
            return;
        }
        if (c.TryTake("SEARCH("))
        {
            Search(c, w);
            return;
        }
        if (c.Peek() is >= 'a' and <= 'z')
        {
            // LOAD_VAR: the value comes before the target in the bytes.
            byte[] target = Target(c);
            c.SkipSpaces();
            if (c.Peek() != '=' || c.Peek(1) == '=')
            {
                throw new FormatException("expected '=' after the variable");
            }
            c.Expect("=");
            w.Bytes.Add(GplOpcodes.LoadVar);
            Expression(c, w);
            w.Bytes.AddRange(target);
            return;
        }
        foreach (var (mnemonic, op) in Mnemonics)
        {
            if (!c.Rest.StartsWith(mnemonic, StringComparison.Ordinal)
                || (c.Rest.Length > mnemonic.Length && c.Rest[mnemonic.Length] is not ('(' or ' ' or ')')))
            {
                continue;
            }
            c.TryTake(mnemonic);
            if (GplOpcodes.All[op].Operands is not { } spec)
            {
                throw new FormatException($"{mnemonic} is written differently");
            }
            w.Bytes.Add(op);
            if (spec.Length == 0)
            {
                return;
            }
            c.Expect("(");
            for (int n = 0; n < spec.Length; n++)
            {
                if (n > 0)
                {
                    c.Expect(",");
                }
                if (spec[n] == 'S')
                {
                    StringOperand(c, w, prefixed: false);
                }
                else
                {
                    Expression(c, w, GplOpcodes.WordArguments.Contains((op, n)));
                }
            }
            c.Expect(")");
            return;
        }
        throw new FormatException($"unknown command '{c.Rest.ToString().Split('(', ' ')[0]}'");
    }

    private static void Search(Cursor c, Writer w)
    {
        w.Bytes.Add(GplOpcodes.Search);
        Expression(c, w);
        c.Expect(",");
        c.Expect("range=[");
        w.Bytes.Add(Byte(c.Integer()));
        c.Expect(",");
        w.Bytes.Add(Byte(c.Integer()));
        c.Expect("]");
        bool first = true;
        while (c.TryTake(","))
        {
            c.Expect("field[");
            byte field = Byte(c.Integer());
            c.Expect("](");
            byte mode = Byte(c.Integer());
            c.Expect(")");
            if (!first)
            {
                w.Bytes.Add(GplOpcodes.SearchQualifier);
            }
            w.Bytes.Add(field);
            w.Bytes.Add(mode);
            if (c.TryTake("="))
            {
                Expression(c, w);
            }
            first = false;
        }
        if (first)
        {
            throw new FormatException("SEARCH needs at least one field");
        }
        c.Expect(")");
    }

    // -- operands ----------------------------------------------------------------

    /// <param name="word">A word argument: a lone number is a 15-bit word.</param>
    private static void Expression(Cursor c, Writer w, bool word = false)
    {
        int depth = 0;
        bool first = true;
        while (true)
        {
            c.SkipSpaces();
            while (c.Peek() == '(' && !IsEmbedded(c))
            {
                c.TryTake("(");
                w.Bytes.Add(0xE2);
                depth++;
                c.SkipSpaces();
            }
            byte? embedded = Operand(c, w, word && first && depth == 0 && LoneOperand(c));
            first = false;
            c.SkipSpaces();
            bool closed = false;
            while (depth > 0 && c.TryTake(")"))
            {
                w.Bytes.Add(0xE1);
                depth--;
                closed = true;
                c.SkipSpaces();
            }
            if (Operator(c) is byte code)
            {
                if (embedded is byte op && !closed && EndsWithExpression(op))
                {
                    throw new FormatException(
                        "an operator right after ((command)) would be read as part of the command's last argument; put the command in parentheses: (((command)))");
                }
                w.Bytes.Add(code);
                continue;
            }
            if (depth > 0)
            {
                throw new FormatException("missing ')'");
            }
            return;
        }
    }

    /// <summary>Whether the operand at <paramref name="c"/> is the whole expression (no operator follows it).</summary>
    private static bool LoneOperand(Cursor c)
    {
        var probe = new Cursor(c.Text, c.Position);
        try
        {
            Operand(probe, new Writer(), word: false);
        }
        catch (FormatException)
        {
            return false;
        }
        probe.SkipSpaces();
        return Operator(probe) is null;
    }

    /// <summary>Whether command <paramref name="op"/> ends by reading an expression, which would take in a following operator.</summary>
    private static bool EndsWithExpression(byte op) =>
        op is GplOpcodes.LoadAccum or GplOpcodes.Search or GplOpcodes.SetRecord
        || (GplOpcodes.All.TryGetValue(op, out GplOpcode? o) && o.Operands is { Length: > 0 } spec && spec[^1] is 'E' or 'A');

    private static byte? Operator(Cursor c)
    {
        foreach (var (symbol, code) in Operators)
        {
            if (c.TryTake(symbol))
            {
                return code;
            }
        }
        return null;
    }

    /// <summary>Whether the <c>((</c> here starts an embedded command rather than two parentheses.</summary>
    private static bool IsEmbedded(Cursor c)
    {
        if (c.Peek() != '(' || c.Peek(1) != '(' || c.Matching(c.Position) is not int close
            || close < c.Position + 3 || c.Text[close - 1] != ')' || c.Matching(c.Position + 1) != close - 1)
        {
            return false;
        }
        try
        {
            var inner = new Cursor(c.Text[(c.Position + 2)..(close - 1)], 0);
            Instruction(inner, new Writer());
            inner.End();
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <returns>The opcode, if the operand is an embedded command.</returns>
    private static byte? Operand(Cursor c, Writer w, bool word)
    {
        c.SkipSpaces();
        char p = c.Peek();
        if (p == '(' && IsEmbedded(c))
        {
            int close = c.Matching(c.Position)!.Value;
            var inner = new Cursor(c.Text[(c.Position + 2)..(close - 1)], 0);
            w.Bytes.Add(0x8C);
            var sub = w.Sub();
            Instruction(inner, sub);
            inner.End();
            w.Append(sub);
            c.Position = close + 1;
            return sub.Bytes[0];
        }
        if (p == '@')
        {
            c.TryTake("@");
            string name = c.Identifier();
            if (IsHex(name))
            {
                int address = int.Parse(name, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                Word(w, address <= 0x7FFF ? address : throw new FormatException($"address {name} is past 7fff"));
            }
            else
            {
                w.Fixup(name);
            }
            return null;
        }
        if (p == '-' || char.IsAsciiDigit(p))
        {
            long value = c.Integer();
            if (c.Peek() == ':' && Suffixes.IndexOf(c.Peek(1)) is int s and >= 0)
            {
                c.Position += 2;
                Number(w, value, Forms[s]);
            }
            else
            {
                Number(w, value, FormFor(value, word));
            }
            return null;
        }
        if (p == '"' || p == '<')
        {
            StringOperand(c, w, prefixed: true);
            return null;
        }
        if (c.TryWord("accum"))
        {
            w.Bytes.Add(0x80);
            return null;
        }
        if (c.TryTake("name(-"))
        {
            int n = c.Integer();
            c.Expect(")");
            w.Bytes.Add(0x91);
            Word(w, n);
            return null;
        }
        if (c.Rest.StartsWith("obj", StringComparison.Ordinal))
        {
            w.Bytes.Add(0xB1);
            w.Bytes.AddRange(ObjectRef(c));
            return null;
        }
        if (RequestCode(c) is int request)
        {
            Number(w, request, FormFor(request, word));
            return null;
        }
        var (type, index) = Variable(c);
        if (index < 0x100)
        {
            w.Bytes.Add((byte)(0x80 | (byte)type));
            w.Bytes.Add((byte)index);
        }
        else
        {
            w.Bytes.Add((byte)(0xC0 + (byte)type));
            Word(w, index);
        }
        return null;
    }

    /// <summary>A REQUEST name (<see cref="GplRequests"/>) at <paramref name="c"/>, taken, as its code.</summary>
    private static int? RequestCode(Cursor c)
    {
        var probe = new Cursor(c.Text, c.Position);
        if (!char.IsAsciiLetterUpper(probe.Peek()) || !GplRequests.TryCode(probe.Identifier(), out int code))
        {
            return null;
        }
        c.Position = probe.Position;
        return code;
    }

    private static (GplVarType Type, int Index) Variable(Cursor c)
    {
        string name = c.Identifier();
        if (!VarTypes.TryGetValue(name, out GplVarType type))
        {
            throw new FormatException($"unknown operand '{name}'");
        }
        c.Expect("[");
        bool raw = c.TryTake("?");
        int index = c.Integer();
        c.Expect("]");
        if (type == GplVarType.EngineVar && !raw)
        {
            index += 0x20;
        }
        if (index is < 0 or > 0xFFFF)
        {
            throw new FormatException($"variable index {index} out of range");
        }
        return (type, index);
    }

    /// <summary>LOAD_VAR's target: a variable, encoded as in expressions, or an object field.</summary>
    private static byte[] Target(Cursor c)
    {
        if (c.Rest.StartsWith("obj", StringComparison.Ordinal))
        {
            return [0xB2, .. ObjectRef(c)];
        }
        var (type, index) = Variable(c);
        return index < 0x100 ? [(byte)(0x80 | (byte)type), (byte)index] : [(byte)(0xC0 + (byte)type), (byte)(index >> 8), (byte)index];
    }

    private static byte[] ObjectRef(Cursor c)
    {
        c.SkipSpaces();
        int id;
        if (c.TryTake("obj_special("))
        {
            c.TryTake("?");
            id = 0x8000 | c.Integer();
        }
        else
        {
            c.Expect("obj(");
            id = c.Integer();
        }
        c.Expect(")");
        if (id is < 0 or > 0xFFFF)
        {
            throw new FormatException($"object id {id} out of range");
        }
        var path = new List<byte>();
        while (c.Peek() == '.' && char.IsAsciiDigit(c.Peek(1)))
        {
            c.TryTake(".");
            path.Add(Byte(c.Integer()));
        }
        return [(byte)(id >> 8), (byte)id, (byte)path.Count, .. path];
    }

    /// <summary>A string; <paramref name="prefixed"/> in an expression (byte 0x92 first).</summary>
    private static void StringOperand(Cursor c, Writer w, bool prefixed)
    {
        c.SkipSpaces();
        if (prefixed)
        {
            w.Bytes.Add(0x92);
        }
        if (c.TryTake("<name>"))
        {
            w.Bytes.Add((byte)GplStringKind.SpeakerName);
            return;
        }
        if (c.TryTake("<last string>"))
        {
            return;
        }
        if (c.Peek() == 'r' && c.Peek(1) == '"')
        {
            throw new FormatException("raw strings (r\"…\") can't be assembled; DSUN.EXE never ends them");
        }
        c.Expect("\"");
        var codes = new List<byte>();
        while (true)
        {
            char ch = c.Peek() != '\0' ? c.Next() : throw new FormatException("unterminated string");
            if (ch == '"')
            {
                break;
            }
            if (ch == '\\')
            {
                char e = c.Next();
                if (e == 'x')
                {
                    codes.Add(byte.Parse(c.Take(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    continue;
                }
                ch = e;
            }
            codes.Add((byte)ch);
        }
        if (codes.Count > 0x12B)
        {
            throw new FormatException($"string too long ({codes.Count} characters, at most 299)");
        }
        if (codes.FirstOrDefault(b => b >= 0x80 || b == 3) is var bad and not 0)
        {
            throw new FormatException($"character 0x{bad:x2} can't be stored in a string");
        }
        w.Bytes.Add((byte)GplStringKind.Packed);
        Pack(w, codes);
    }

    /// <summary>7 bits per character, big-endian, ending with character 3; the last byte padded with zeros.</summary>
    private static void Pack(Writer w, List<byte> codes)
    {
        int acc = 0, bits = 0;
        foreach (byte code in codes.Append((byte)3))
        {
            acc = (acc << 7) | code;
            bits += 7;
            while (bits >= 8)
            {
                bits -= 8;
                w.Bytes.Add((byte)(acc >> bits));
            }
        }
        if (bits > 0)
        {
            w.Bytes.Add((byte)(acc << (8 - bits)));
        }
    }

    private const string Suffixes = "wbsl";
    private static readonly byte[] Forms = [GplNumber.Word15, GplNumber.Int8, GplNumber.Int16, GplNumber.Int32];

    /// <summary>The form the assembler gives <paramref name="value"/> unless told otherwise; <paramref name="word"/>: a word argument.</summary>
    internal static byte FormFor(long value, bool word) => value switch
    {
        >= 0 and <= 0x7FFF when word => GplNumber.Word15,
        >= sbyte.MinValue and <= sbyte.MaxValue => GplNumber.Int8,
        >= 0 and <= 0x7FFF => GplNumber.Word15,
        >= short.MinValue and < 0 => GplNumber.Int16,
        _ => GplNumber.Int32,
    };

    /// <summary><paramref name="n"/> as source: with a suffix when it isn't stored in the form the assembler would pick.</summary>
    internal static string NumberText(GplNumber n, bool word) =>
        n.Form == FormFor(n.Value, word) ? n.Value.ToString(CultureInfo.InvariantCulture)
        : $"{n.Value}:{Suffixes[Array.IndexOf(Forms, n.Form)]}";

    private static void Number(Writer w, long value, byte form)
    {
        switch (form)
        {
            case GplNumber.Int8 when value is >= sbyte.MinValue and <= sbyte.MaxValue:
                w.Bytes.Add(0x8F);
                w.Bytes.Add((byte)(sbyte)value);
                break;
            case GplNumber.Word15 when value is >= 0 and <= 0x7FFF:
                Word(w, (int)value);
                break;
            case GplNumber.Int16 when value is >= short.MinValue and <= short.MaxValue:
                w.Bytes.Add(0x90);
                Word(w, (int)value);
                break;
            case GplNumber.Int32 when value is >= int.MinValue and <= uint.MaxValue:
                w.Bytes.Add(0x8B);
                Word(w, (int)(value >> 16));
                Word(w, (int)value);
                break;
            default:
                throw new FormatException($"{value} doesn't fit in that form");
        }
    }

    private static void Word(Writer w, int value)
    {
        w.Bytes.Add((byte)(value >> 8));
        w.Bytes.Add((byte)value);
    }

    private static byte Byte(int value) =>
        value is >= 0 and <= 255 ? (byte)value : throw new FormatException($"{value} is not a byte");

    private static bool IsIdentifier(ReadOnlySpan<char> s) =>
        s.Length > 0 && (char.IsAsciiLetter(s[0]) || s[0] == '_') && !s.ContainsAnyExcept(IdentifierChars);

    private static readonly System.Buffers.SearchValues<char> IdentifierChars =
        System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_");

    private static bool IsHex(string s) => s.Length > 0 && s.All(char.IsAsciiHexDigit);

    // -- output ------------------------------------------------------------------

    private sealed class Writer
    {
        public List<byte> Bytes { get; } = [];

        public Dictionary<string, int> Labels { get; } = [];

        public List<(int At, string Label, int Line)> Fixups { get; } = [];

        /// <summary>Address of byte 0 of <see cref="Bytes"/>.</summary>
        public int Base { get; init; }

        public int Line { get; set; }

        public int Address => Base + Bytes.Count;

        /// <summary>A two-byte label address, filled in at the end.</summary>
        public void Fixup(string label)
        {
            Fixups.Add((Bytes.Count, label, Line));
            Bytes.Add(0);
            Bytes.Add(0);
        }

        /// <summary>A writer for an embedded command, continuing at the current address.</summary>
        public Writer Sub() => new() { Base = Address, Line = Line };

        public void Append(Writer sub)
        {
            int at = Bytes.Count;
            Bytes.AddRange(sub.Bytes);
            Fixups.AddRange(sub.Fixups.Select(f => (f.At + at, f.Label, f.Line)));
        }

        public void ResolveFixups()
        {
            foreach (var (at, label, line) in Fixups)
            {
                if (!Labels.TryGetValue(label, out int address))
                {
                    throw new GplAssemblyException(line, $"no label {label}");
                }
                if (address > 0x7FFF)
                {
                    throw new GplAssemblyException(line, $"label {label} is past 0x7FFF");
                }
                Bytes[at] = (byte)(address >> 8);
                Bytes[at + 1] = (byte)address;
            }
        }
    }

    private sealed class Cursor(string text, int position)
    {
        public string Text { get; } = text;

        public int Position { get; set; } = position;

        public bool AtEnd
        {
            get
            {
                SkipSpaces();
                return Position >= Text.Length;
            }
        }

        public ReadOnlySpan<char> Rest => Text.AsSpan(Position);

        public char Peek(int ahead = 0) => Position + ahead < Text.Length ? Text[Position + ahead] : '\0';

        public char Next() => Position < Text.Length ? Text[Position++] : throw new FormatException("unexpected end of line");

        public string Take(int n)
        {
            if (Position + n > Text.Length)
            {
                throw new FormatException("unexpected end of line");
            }
            string s = Text.Substring(Position, n);
            Position += n;
            return s;
        }

        public void SkipSpaces()
        {
            while (Position < Text.Length && Text[Position] is ' ' or '\t' or '\r')
            {
                Position++;
            }
        }

        public bool TryTake(string s)
        {
            SkipSpaces();
            if (!Rest.StartsWith(s, StringComparison.Ordinal))
            {
                return false;
            }
            Position += s.Length;
            return true;
        }

        /// <summary><paramref name="s"/> as a whole word (not the start of a longer one).</summary>
        public bool TryWord(string s)
        {
            SkipSpaces();
            if (!Rest.StartsWith(s, StringComparison.Ordinal) || (Rest.Length > s.Length && (char.IsAsciiLetterOrDigit(Rest[s.Length]) || Rest[s.Length] == '_')))
            {
                return false;
            }
            Position += s.Length;
            return true;
        }

        public void Expect(string s)
        {
            if (!TryTake(s))
            {
                throw new FormatException($"expected '{s}' at '{Excerpt()}'");
            }
        }

        public void End()
        {
            if (!AtEnd)
            {
                throw new FormatException($"unexpected '{Excerpt()}'");
            }
        }

        public string Identifier()
        {
            SkipSpaces();
            int start = Position;
            while (Position < Text.Length && (char.IsAsciiLetterOrDigit(Text[Position]) || Text[Position] == '_'))
            {
                Position++;
            }
            return start < Position ? Text[start..Position] : throw new FormatException($"expected a name at '{Excerpt()}'");
        }

        /// <summary>A decimal or 0x hex integer, optionally negative.</summary>
        public int Integer()
        {
            SkipSpaces();
            int start = Position;
            bool negative = Peek() == '-';
            if (negative)
            {
                Position++;
            }
            bool hex = Peek() == '0' && Peek(1) is 'x' or 'X';
            if (hex)
            {
                Position += 2;
            }
            int digits = Position;
            while (Position < Text.Length && (hex ? char.IsAsciiHexDigit(Text[Position]) : char.IsAsciiDigit(Text[Position])))
            {
                Position++;
            }
            if (digits == Position)
            {
                Position = start;
                throw new FormatException($"expected a number at '{Excerpt()}'");
            }
            long value = long.Parse(Text.AsSpan(digits, Position - digits), hex ? NumberStyles.HexNumber : NumberStyles.None, CultureInfo.InvariantCulture);
            value = negative ? -value : value;
            return value is >= int.MinValue and <= uint.MaxValue ? (int)value : throw new FormatException($"{value} doesn't fit in 32 bits");
        }

        /// <summary>The index of the ')' that closes the '(' at <paramref name="open"/>, skipping strings.</summary>
        public int? Matching(int open)
        {
            int depth = 0;
            bool quoted = false;
            for (int i = open; i < Text.Length; i++)
            {
                char ch = Text[i];
                if (quoted)
                {
                    if (ch == '\\')
                    {
                        i++;
                    }
                    else if (ch == '"')
                    {
                        quoted = false;
                    }
                }
                else if (ch == '"')
                {
                    quoted = true;
                }
                else if (ch == '(')
                {
                    depth++;
                }
                else if (ch == ')' && --depth == 0)
                {
                    return i;
                }
            }
            return null;
        }

        private string Excerpt() => Position >= Text.Length ? "end of line" : Text[Position..Math.Min(Text.Length, Position + 20)];
    }
}
