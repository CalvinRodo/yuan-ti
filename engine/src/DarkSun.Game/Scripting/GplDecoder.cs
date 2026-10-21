using System.Text;

namespace DarkSun.Game.Scripting;

public sealed class GplDecodeException(string message) : Exception(message);

/// <summary>
/// Decodes GPL bytecode into <see cref="GplInstruction"/>s, and renders them as
/// the same text as <c>src/gpl_parser.py</c> (<c>make engine-compare</c> checks
/// both produce identical listings of every script). The grammar follows the
/// DSUN.EXE interpreter, <c>dsun-exe-reference.md</c> §2.
/// </summary>
public sealed class GplDecoder
{
    private const int MaxParentheses = 8;
    private const int MaxStringLength = 0x12B;
    private const int MaxMenuItems = 25;
    private const byte StringEndChar = 0x03;

    private static readonly HashSet<int> SpecialObjects = [0x25, 0x26, 0x27, 0x28, 0x2B, 0x2C];
    private static readonly HashSet<byte> MenuTextOps = [0x23, 0x28, 0x2E, 0x4B];
    private static readonly HashSet<byte> SearchExprTypes = [0x10, 0x12, 0x13, 0x14];
    private static readonly HashSet<byte> DepthBefore = [0x3F, 0x61, 0x64, 0x67];
    private static readonly HashSet<byte> DepthAfter = [0x17, 0x3E, 0x3F, 0x63];

    private static readonly Dictionary<byte, string> OperatorSymbols = new()
    {
        [0xD1] = "+", [0xD2] = "-", [0xD3] = "*", [0xD4] = "/", [0xD5] = "&&", [0xD6] = "||",
        [0xD7] = "==", [0xD8] = "!=", [0xD9] = ">", [0xDA] = "<", [0xDB] = ">=", [0xDC] = "<=",
        [0xDD] = "&", [0xDE] = "|", [0xDF] = "&~",
    };

    private static readonly Dictionary<GplVarType, string> VarNames = new()
    {
        [GplVarType.LocalString] = "lstr", [GplVarType.LocalNum] = "lnum", [GplVarType.LocalBigNum] = "lbignum",
        [GplVarType.GlobalString] = "gstr", [GplVarType.GlobalNum] = "gnum", [GplVarType.EngineVar] = "gname",
        [GplVarType.GlobalBigNum] = "gbignum", [GplVarType.GlobalFlag] = "gflag", [GplVarType.LocalFlag] = "lflag",
    };

    private readonly byte[] _data;
    private readonly byte[] _fieldTypes;
    private int _pos;

    /// <param name="fieldTypes"><c>GPLX</c> 1 bytes 0x3DE–0x440, needed to decode SEARCH.</param>
    public GplDecoder(byte[] data, byte[] fieldTypes)
    {
        _data = data;
        _fieldTypes = fieldTypes;
    }

    /// <summary><c>GPLX</c> 1 [0x3DE, 0x441): copied to <c>DS:537A</c> by DSUN.EXE.</summary>
    public static byte[] FieldTypes(ReadOnlySpan<byte> gplx) =>
        gplx.Length > 0x3DE ? gplx[0x3DE..Math.Min(gplx.Length, 0x3DE + 0x63)].ToArray() : [];

    /// <summary>Decode the command at <paramref name="address"/>.</summary>
    public GplInstruction DecodeAt(int address)
    {
        _pos = address;
        return Instruction(Byte(), address);
    }

    public bool IsValid(byte op) => GplOpcodes.All.ContainsKey(op);

    // -- text listing, as gpl_parser.py prints it ------------------------------

    /// <summary>
    /// The listing <c>gpl_parser.py</c> prints for a script: address, indent
    /// (IF/WHILE/COMPARE blocks), and the command; decoding stops at the first
    /// error, which is shown as <c>?? 0xNN  ; message</c>.
    /// </summary>
    public static IEnumerable<string> Listing(byte[] data, byte[] fieldTypes)
    {
        var decoder = new GplDecoder(data, fieldTypes);
        int depth = 0;
        while (decoder._pos < data.Length)
        {
            int address = decoder._pos;
            byte op = decoder.Byte();
            if (DepthBefore.Contains(op) && depth > 0)
            {
                depth--;
            }
            string? text;
            string? error = null;
            try
            {
                text = Render(decoder.Instruction(op, address));
            }
            catch (GplDecodeException e)
            {
                text = null;
                error = e.Message;
            }
            if (text is null)
            {
                yield return $"{address:x5}  {new string(' ', 2 * depth)}?? 0x{op:X2}  ; {error}";
                yield break;
            }
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                yield return $"{(i == 0 ? address.ToString("x5") : "     ")}  {new string(' ', 2 * depth)}{lines[i]}";
            }
            if (DepthAfter.Contains(op))
            {
                depth++;
            }
        }
    }

    public static string Render(GplInstruction i) => Render(i, GplStyle.Listing);

    /// <summary>
    /// <paramref name="i"/> as text, in <paramref name="style"/>: the listing's,
    /// or the assembler's (<see cref="GplStyle.Source"/>: strings escaped,
    /// addresses as labels, menus in byte order).
    /// </summary>
    public static string Render(GplInstruction i, GplStyle style)
    {
        switch (i.Op)
        {
            case GplOpcodes.Menu:
                GplMenu menu = i.Menu!;
                if (style.IsSource)
                {
                    var source = new List<string> { "MENU" };
                    source.AddRange(menu.Before.Select(b => "  " + Render(b, style)));
                    source.Add($"  TITLE({Render(menu.Title, style)})");
                    foreach (GplMenuEntry e in menu.Entries)
                    {
                        source.AddRange(e.Before.Select(b => "  " + Render(b, style)));
                        source.Add($"  ENTRY(text={Render(e.Text, style)}, func={RenderAddress(e.Address, style)}, cond={Render(e.Condition, style, word: true)})");
                        source.AddRange(e.After.Select(b => "  " + Render(b, style)));
                    }
                    source.Add("MENU_END");
                    return string.Join('\n', source);
                }
                var lines = menu.Before.Select(b => Render(b, style)).ToList();
                lines.Add($"MENU(title={Render(menu.Title, style)})");
                foreach (GplMenuEntry e in menu.Entries)
                {
                    lines.AddRange(e.Before.Select(b => Render(b, style)));
                    lines.Add($"  ENTRY(text={Render(e.Text, style)}, func={RenderAddress(e.Address, style)}, cond={Render(e.Condition, style)})");
                    lines.AddRange(e.After.Select(b => Render(b, style)));
                }
                lines.Add("MENU_END");
                return string.Join('\n', lines);
            case GplOpcodes.LoadVar:
                return $"{Render(i.Target!, style)} = {Render(i.Value!, style)}";
            case GplOpcodes.LoadAccum:
                return $"ACCUM <= {Render(i.Value!, style)}";
            case GplOpcodes.Search:
                GplSearch s = i.Search!;
                IEnumerable<string> criteria = s.Criteria.Select(c =>
                    c.Value is null ? $"field[{c.Field}]({c.Mode})" : $"field[{c.Field}]({c.Mode})={Render(c.Value, style)}");
                return $"SEARCH({Render(s.Who, style)}, range=[{s.Low},{s.High}], {string.Join(", ", criteria)})";
            case GplOpcodes.SetRecord:
                return i.Record is null ? "SETRECORD" : $"SETRECORD {Render(i.Record)} = {Render(i.Value!, style)}";
        }
        string spec = GplOpcodes.All[i.Op].Operands ?? "";
        if (spec.Length == 0)
        {
            return i.Mnemonic;
        }
        // In source, another file's address is written @hex (no label: it isn't in this file).
        int other = style.IsSource && GplOpcodes.EntryArguments.TryGetValue(i.Op, out var entry) ? entry.Address : -1;
        IEnumerable<string> args = spec.Select((kind, n) =>
            n == other ? RenderAddress(i.Args[n], GplStyle.Source)
            : n == 0 && i.Op == GplOpcodes.Request && RequestName(i.Args[0], style) is { } name ? name
            : kind == 'A' ? RenderAddress(i.Args[n], style)
            : Render(i.Args[n], style, GplOpcodes.WordArguments.Contains((i.Op, n))));
        return $"{i.Mnemonic}({string.Join(", ", args)})";
    }

    /// <summary>A constant REQUEST code's name (<see cref="GplRequests"/>); in source, only if the number is stored as the assembler would store the name.</summary>
    private static string? RequestName(GplExpression e, GplStyle style) =>
        e.Items is [GplTerm { Operand: GplNumber n }] && (!style.IsSource || n.Form == GplAssembler.FormFor(n.Value, word: false))
            ? GplRequests.Name((int)n.Value)
            : null;

    private static string RenderAddress(GplExpression e, GplStyle style)
    {
        string text = Render(e, style, word: true);
        if (text.Length == 0 || !text.All(char.IsAsciiDigit))
        {
            return text;
        }
        int address = int.Parse(text);
        return style.Label?.Invoke(address) is { } label ? $"@{label}" : $"@{address:x4}";
    }

    public static string Render(GplExpression e) => Render(e, GplStyle.Listing);

    /// <param name="word">In source, whether a lone number is a 15-bit word unless marked (see <see cref="GplAssembler"/>).</param>
    public static string Render(GplExpression e, GplStyle style, bool word = false)
    {
        var parts = new List<string>();
        foreach (GplExprItem item in e.Items)
        {
            parts.Add(item switch
            {
                GplTerm t => Render(t.Operand, style, word && e.Items.Count == 1),
                GplOperator o => OperatorSymbols[o.Code],
                GplOpen => "(",
                _ => ")",
            });
        }
        return string.Join(' ', parts).Replace("( ", "(").Replace(" )", ")");
    }

    private static string Render(GplOperand o, GplStyle style, bool word) => o switch
    {
        GplNumber n => style.IsSource ? GplAssembler.NumberText(n, word) : n.Value.ToString(),
        GplAccumulator => "accum",
        GplName n => $"name(-{n.Number})",
        GplVariable v => Render(v),
        GplString s => s.Kind switch
        {
            GplStringKind.SpeakerName => "<name>",
            GplStringKind.Previous => "<last string>",
            _ => style.IsSource ? Quoted(s) : $"\"{Printable(s.Codes)}\"",
        },
        GplObjectRef r => Render(r),
        GplEmbedded e => $"(({Render(e.Instruction, style)}))",
        _ => throw new InvalidOperationException(o.ToString()),
    };

    private static string Render(GplVariable v)
    {
        if (v.Type == GplVarType.EngineVar)
        {
            return v.Index is >= 0x20 and < 0x2F ? $"gname[{v.Index - 0x20}]" : $"gname[?{v.Index}]";
        }
        return $"{VarNames[v.Type]}[{v.Index}]";
    }

    private static string Render(GplObjectRef r)
    {
        string name;
        if (r.IsSpecial)
        {
            int n = r.Id & 0x7FFF;
            name = SpecialObjects.Contains(n) ? $"obj_special(0x{n:x})" : $"obj_special(?0x{n:x})";
        }
        else
        {
            name = $"obj({r.Id})";
        }
        return name + string.Concat(r.Path.Select(p => $".{p}"));
    }

    private static string Render(GplTarget t, GplStyle style) => t.Field is not null ? Render(t.Field) : Render(t.Variable!);

    private static string Printable(byte[] codes) =>
        new([.. codes.Select(c => c is >= 0x20 and <= 0x7E ? (char)c : ' ')]);

    /// <summary>A string as the assembler reads it: <c>\"</c>, <c>\\</c>, <c>\xNN</c> for other codes; raw strings prefixed <c>r</c>.</summary>
    private static string Quoted(GplString s)
    {
        var b = new StringBuilder(s.Kind == GplStringKind.Raw ? "r\"" : "\"");
        foreach (byte c in s.Codes)
        {
            b.Append(c switch
            {
                (byte)'"' => "\\\"",
                (byte)'\\' => "\\\\",
                >= 0x20 and <= 0x7E => ((char)c).ToString(),
                _ => $"\\x{c:x2}",
            });
        }
        return b.Append('"').ToString();
    }

    // -- decoding ----------------------------------------------------------------

    private byte Byte()
    {
        if (_pos >= _data.Length)
        {
            throw new GplDecodeException($"read past end at 0x{_pos:x}");
        }
        return _data[_pos++];
    }

    /// <summary>Big-endian 16-bit.</summary>
    private int Word() => (Byte() << 8) | Byte();

    private int SignedWord() => (short)Word();

    private int? Peek() => _pos < _data.Length ? _data[_pos] : null;

    private GplInstruction Instruction(byte op, int address)
    {
        switch (op)
        {
            case GplOpcodes.Menu:
            {
                GplMenu menu = ReadMenu();
                return new GplInstruction(address, op, _pos - address) { Menu = menu };
            }
            case GplOpcodes.LoadVar:
            {
                GplExpression value = Expression();
                GplTarget target = Target();
                return new GplInstruction(address, op, _pos - address) { Value = value, Target = target };
            }
            case GplOpcodes.LoadAccum:
            {
                GplExpression value = Expression();
                return new GplInstruction(address, op, _pos - address) { Value = value };
            }
            case GplOpcodes.Search:
            {
                GplSearch search = ReadSearch();
                return new GplInstruction(address, op, _pos - address) { Search = search };
            }
            case GplOpcodes.SetRecord:
            {
                // Id 0x8000 matches no branch of the handler; nothing is read.
                if (_pos + 1 < _data.Length && _data[_pos] == 0x80 && _data[_pos + 1] == 0x00)
                {
                    return new GplInstruction(address, op, _pos - address);
                }
                GplObjectRef record = ObjectRef();
                GplExpression value = Expression();
                return new GplInstruction(address, op, _pos - address) { Record = record, Value = value };
            }
        }
        if (!GplOpcodes.All.TryGetValue(op, out GplOpcode? opcode))
        {
            throw new GplDecodeException($"invalid opcode 0x{op:x2}");
        }
        var args = new List<GplExpression>();
        foreach (char kind in opcode.Operands ?? "")
        {
            args.Add(kind == 'S' ? new GplExpression([new GplTerm(StringOperand())]) : Expression());
        }
        return new GplInstruction(address, op, _pos - address) { Args = args };
    }

    private GplExpression Expression()
    {
        var items = new List<GplExprItem>();
        int depth = 0;
        while (true)
        {
            if (Peek() == 0xE2)
            {
                _pos++;
                depth++;
                if (depth >= MaxParentheses)
                {
                    throw new GplDecodeException("parentheses nested too deep");
                }
                items.Add(new GplOpen());
                continue;
            }
            items.Add(new GplTerm(Operand()));
            while (depth > 0 && Peek() == 0xE1)
            {
                _pos++;
                depth--;
                items.Add(new GplClose());
            }
            if (Peek() is int next && OperatorSymbols.ContainsKey((byte)next))
            {
                _pos++;
                items.Add(new GplOperator((byte)next));
                continue;
            }
            return new GplExpression(items);
        }
    }

    private GplOperand Operand()
    {
        byte b = Byte();
        if (b < 0x80)
        {
            return new GplNumber((b << 8) | Byte()) { Form = GplNumber.Word15 };
        }
        switch (b)
        {
            case 0x80:
                return new GplAccumulator();
            case 0x8B:
                return new GplNumber((int)(((uint)Word() << 16) | (uint)Word())) { Form = GplNumber.Int32 };
            case 0x8C:
                byte op = Byte();
                return new GplEmbedded(Instruction(op, _pos - 1));
            case 0x8F:
                return new GplNumber((sbyte)Byte()) { Form = GplNumber.Int8 };
            case 0x90:
                return new GplNumber(SignedWord()) { Form = GplNumber.Int16 };
            case 0x91:
                return new GplName(Word());
            case 0x92:
                return StringOperand();
            case 0xB1:
                return ObjectRef();
        }
        if (b is >= 0x81 and <= 0x8E or >= 0xC1 and <= 0xCE)
        {
            return Variable(b);
        }
        throw new GplDecodeException($"bad operand byte 0x{b:x2}");
    }

    private GplVariable Variable(byte b)
    {
        bool wide = b >= 0xC0;
        int type = (wide ? b - 0x40 : b) & 0x7F;
        int index = wide ? Word() : Byte();
        if (!Enum.IsDefined((GplVarType)type))
        {
            throw new GplDecodeException($"bad variable type 0x{b:x2}");
        }
        return new GplVariable((GplVarType)type, index);
    }

    private GplObjectRef ObjectRef()
    {
        int id = Word();
        int count = Byte();
        var path = new byte[count];
        for (int i = 0; i < count; i++)
        {
            path[i] = Byte();
        }
        return new GplObjectRef(id, path);
    }

    private GplTarget Target()
    {
        byte code = Byte();
        int b = code & 0x7F;
        bool wide = (b & 0x40) != 0;
        int type = wide ? (b + 0xC0) & 0xFF : b;
        if (type >= 0x10)
        {
            return new GplTarget(null, ObjectRef()) { Code = code };
        }
        int index = wide ? Word() : Byte();
        if (!Enum.IsDefined((GplVarType)type))
        {
            throw new GplDecodeException($"bad assignment type 0x{b:x2}");
        }
        return new GplTarget(new GplVariable((GplVarType)type, index), null) { Code = code };
    }

    private GplString StringOperand()
    {
        switch (Peek())
        {
            case (int)GplStringKind.SpeakerName:
                _pos++;
                return new GplString(GplStringKind.SpeakerName, []);
            case (int)GplStringKind.Packed:
                _pos++;
                return new GplString(GplStringKind.Packed, Packed());
            case (int)GplStringKind.Raw:
            {
                _pos++;
                var codes = new List<byte>();
                while (codes.Count < MaxStringLength)
                {
                    byte c = (byte)(Byte() << 2); // gpl_map_raw_char, 043D:396B
                    if (c == StringEndChar)
                    {
                        break;
                    }
                    codes.Add(c);
                }
                return new GplString(GplStringKind.Raw, [.. codes]);
            }
            default:
                return new GplString(GplStringKind.Previous, []);
        }
    }

    /// <summary>7-bit packed text: 8 characters per 7 bytes, big-endian bit order; character 3 ends it.</summary>
    private byte[] Packed()
    {
        int acc = 0;
        int shift = 1;
        var codes = new List<byte>();
        while (codes.Count < MaxStringLength)
        {
            if (shift > 0)
            {
                acc = ((acc << 8) & 0xFF00) | Byte();
            }
            byte c = (byte)((acc >> shift) & 0x7F);
            if (c == StringEndChar)
            {
                break;
            }
            codes.Add(c);
            shift = shift < 7 ? shift + 1 : 0;
        }
        return [.. codes];
    }

    private GplSearch ReadSearch()
    {
        GplExpression who = Expression();
        byte low = Byte();
        byte high = Byte();
        var criteria = new List<GplSearchCriterion>();
        while (true)
        {
            if (Peek() == GplOpcodes.SearchQualifier)
            {
                _pos++;
            }
            byte field = Byte();
            byte mode = Byte();
            byte type = field < _fieldTypes.Length ? _fieldTypes[field] : (byte)0;
            GplExpression? value = mode is >= 4 and <= 6 && SearchExprTypes.Contains(type) ? Expression() : null;
            criteria.Add(new GplSearchCriterion(field, mode, value));
            if (Peek() != GplOpcodes.SearchQualifier)
            {
                return new GplSearch(who, low, high, criteria);
            }
        }
    }

    private GplMenu ReadMenu()
    {
        List<GplInstruction> before = MenuText();
        GplExpression title = Expression();
        var entries = new List<GplMenuEntry>();
        // gpl_parser.py counts listing lines here, not entries; the limit is the same in practice.
        int lines = 0;
        while (Peek() != GplOpcodes.MenuEnd && lines < MaxMenuItems)
        {
            List<GplInstruction> pre = MenuText();
            GplExpression text = Expression();
            GplExpression address = Expression();
            GplExpression condition = Expression();
            List<GplInstruction> post = MenuText();
            entries.Add(new GplMenuEntry(pre, text, address, condition, post));
            lines += pre.Count + 1 + post.Count;
        }
        if (Byte() != GplOpcodes.MenuEnd)
        {
            throw new GplDecodeException("MENU not terminated");
        }
        return new GplMenu(before, title, entries);
    }

    /// <summary>Debug-text commands that may surround menu parts (23, 28, 2E, 4B).</summary>
    private List<GplInstruction> MenuText()
    {
        var list = new List<GplInstruction>();
        while (Peek() is int p && MenuTextOps.Contains((byte)p))
        {
            int address = _pos;
            list.Add(Instruction(Byte(), address));
        }
        return list;
    }
}
