namespace DarkSun.Game.Scripting;

/// <summary>Where the debugger stops next (<see cref="GplMachine.Step"/>).</summary>
public enum GplStep
{
    /// <summary>Only at breakpoints.</summary>
    None,

    /// <summary>At the next command, in this script or any it calls; set while no script runs, at the first command of the next run.</summary>
    Into,

    /// <summary>At the next command in this function or its callers: calls, menus and cross-file calls run through.</summary>
    Over,

    /// <summary>When this function returns.</summary>
    Out,
}

/// <summary>A call frame, innermost first: the script and the address it runs (or, for callers, returns to).</summary>
public readonly record struct GplFrame(GplEntry At, int Depth);

/// <summary>
/// Breakpoints: script addresses where <see cref="GplMachine"/> pauses before
/// running the command. Kept apart from the machine so they survive a new
/// game or a loaded save, which make a new one.
/// </summary>
public sealed class GplBreakpoints
{
    private readonly HashSet<GplEntry> _at = [];

    public event Action? Changed;

    public IReadOnlyCollection<GplEntry> All => _at;

    public int Count => _at.Count;

    public bool Contains(GplEntry at) => _at.Contains(at);

    public void Add(GplEntry at)
    {
        if (_at.Add(at))
        {
            Changed?.Invoke();
        }
    }

    public void Remove(GplEntry at)
    {
        if (_at.Remove(at))
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Add or remove; true if it is now set.</summary>
    public bool Toggle(GplEntry at)
    {
        bool set = _at.Add(at) || !_at.Remove(at);
        Changed?.Invoke();
        return set;
    }

    public void Clear()
    {
        if (_at.Count > 0)
        {
            _at.Clear();
            Changed?.Invoke();
        }
    }

    /// <summary>One per line, as <c>GPL 12 @0040</c> or <c>MAS 42 @0000</c>.</summary>
    public string Write() => string.Join('\n', _at.OrderBy(e => e.Kind).ThenBy(e => e.File).ThenBy(e => e.Address).Select(Format));

    /// <summary>Lines written by <see cref="Write"/>; lines that don't parse are skipped.</summary>
    public void Read(string text)
    {
        _at.Clear();
        foreach (string line in text.Split('\n'))
        {
            if (TryParse(line, out GplEntry at))
            {
                _at.Add(at);
            }
        }
        Changed?.Invoke();
    }

    public static string Format(GplEntry e) => $"{Script(e)} @{e.Address:x4}";

    /// <summary>The script alone: <c>GPL 12</c>, <c>MAS 42</c>.</summary>
    public static string Script(GplEntry e) => $"{(e.Kind == GplFileKind.Mas ? "MAS" : "GPL")} {e.File}";

    /// <summary><c>GPL 12 @0040</c>, <c>mas 42 @0</c> (address in hex).</summary>
    public static bool TryParse(string text, out GplEntry at)
    {
        at = default;
        string[] parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !parts[2].StartsWith('@'))
        {
            return false;
        }
        GplFileKind? kind = parts[0].ToUpperInvariant() switch { "GPL" => GplFileKind.Gpl, "MAS" => GplFileKind.Mas, _ => null };
        if (kind is null || !int.TryParse(parts[1], out int file)
            || !int.TryParse(parts[2][1..], System.Globalization.NumberStyles.HexNumber, null, out int address))
        {
            return false;
        }
        at = new GplEntry(file, address, kind.Value);
        return true;
    }
}

/// <summary>
/// A script as the debugger shows it: assembler source (<see cref="GplAssembler"/>
/// syntax), with the line each command starts on. A script a mod replaces
/// with a <c>.gpl</c> file shows that file, if it still assembles to the code
/// the game loaded; any other script shows what <c>ModTool export</c> would
/// write for it, so it can be copied into a mod as it is.
/// </summary>
public sealed class GplSource
{
    private readonly Dictionary<int, int> _addressOf = [];
    private readonly int[] _starts;

    private GplSource(GplEntry script, string text, IReadOnlyDictionary<int, int> lineOf, string origin)
    {
        Script = script;
        Text = text;
        LineOf = lineOf;
        Origin = origin;
        foreach (var (address, line) in lineOf)
        {
            _addressOf[line] = address;
        }
        _starts = [.. _addressOf.Keys.Order()];
    }

    /// <summary>The script (address 0).</summary>
    public GplEntry Script { get; }

    public string Text { get; }

    /// <summary>Command address → line (1-based).</summary>
    public IReadOnlyDictionary<int, int> LineOf { get; }

    /// <summary>Where the text came from: the mod file's path, or "disassembled".</summary>
    public string Origin { get; }

    /// <summary>The command on <paramref name="line"/>, or the first one after it (labels, comments, blank lines); null past the last.</summary>
    public int? AddressAt(int line)
    {
        int i = Array.BinarySearch(_starts, line);
        i = i < 0 ? ~i : i;
        return i < _starts.Length ? _addressOf[_starts[i]] : null;
    }

    /// <summary>The line of the command at <paramref name="address"/>, or of the last one before it (an address inside a command).</summary>
    public int? LineAt(int address)
    {
        if (LineOf.TryGetValue(address, out int line))
        {
            return line;
        }
        int? best = null;
        int bestAddress = -1;
        foreach (var (a, l) in LineOf)
        {
            if (a < address && a > bestAddress)
            {
                (best, bestAddress) = (l, a);
            }
        }
        return best;
    }

    /// <summary>Source for <paramref name="file"/>, from <paramref name="modSource"/> (the mod file replacing it, if any) or disassembled; null if there is no such script.</summary>
    public static GplSource? For(GplMachine machine, int file, GplFileKind kind, string? modSource = null)
    {
        if (machine.Code(file, kind) is not { } code)
        {
            return null;
        }
        var script = new GplEntry(file, 0, kind);
        if (modSource is not null && modSource.EndsWith(GplModFormat.Extension, StringComparison.OrdinalIgnoreCase) && File.Exists(modSource))
        {
            string text = File.ReadAllText(modSource);
            try
            {
                if (GplAssembler.Assemble(text, out var lines).AsSpan().SequenceEqual(code))
                {
                    return new GplSource(script, text, lines, modSource);
                }
            }
            catch (GplAssemblyException)
            {
            }
            // Edited since the game loaded it: show what is running.
        }
        string listing = GplAssembler.Disassemble(code, machine.FieldTypes, title: GplBreakpoints.Script(script));
        try
        {
            if (GplAssembler.Assemble(listing, out var lines).AsSpan().SequenceEqual(code))
            {
                return new GplSource(script, listing, lines, "disassembled");
            }
        }
        catch (GplAssemblyException)
        {
        }
        // Shouldn't happen (every script round-trips); the decoder's listing, addresses first.
        var plain = GplDecoder.Listing(code, machine.FieldTypes).ToList();
        var map = new Dictionary<int, int>();
        for (int n = 0; n < plain.Count; n++)
        {
            if (int.TryParse(plain[n].AsSpan(0, Math.Min(5, plain[n].Length)), System.Globalization.NumberStyles.HexNumber, null, out int a))
            {
                map.TryAdd(a, n + 1);
            }
        }
        return new GplSource(script, string.Join('\n', plain), map, "listing");
    }
}

/// <summary>A variable as the debugger lists it.</summary>
public readonly record struct GplWatch(string Name, string Value, bool Local);

/// <summary>Reading and setting script variables by name, as the source writes them (<c>gnum[5]</c>, <c>lflag[3]</c>, <c>gname[6]</c>).</summary>
public static class GplVariables
{
    private static readonly (string Name, GplVarType Type, GplArea Area, int Size)[] Kinds =
    [
        ("lnum", GplVarType.LocalNum, GplArea.LocalNums, 2),
        ("lbignum", GplVarType.LocalBigNum, GplArea.LocalBigNums, 4),
        ("lflag", GplVarType.LocalFlag, GplArea.LocalFlags, 0),
        ("lstr", GplVarType.LocalString, GplArea.LocalStrings, GplMemory.StringSize),
        ("gnum", GplVarType.GlobalNum, GplArea.GlobalNums, 2),
        ("gbignum", GplVarType.GlobalBigNum, GplArea.GlobalBigNums, 4),
        ("gflag", GplVarType.GlobalFlag, GplArea.GlobalFlags, 0),
        ("gstr", GplVarType.GlobalString, GplArea.GlobalStrings, GplMemory.StringSize),
    ];

    /// <summary>The engine variables <c>gname[0–14]</c>.</summary>
    public const int EngineCount = 15;

    /// <summary>How many variables of a kind there are: <c>gnum</c> 200, <c>gflag</c> 808, …</summary>
    public static int Count(GplMemory memory, string kind)
    {
        if (kind == "gname")
        {
            return EngineCount;
        }
        var k = Kinds.First(k => k.Name == kind);
        int bytes = memory.Area(k.Area).Length;
        return k.Size == 0 ? bytes * 8 : bytes / k.Size;
    }

    /// <summary>Every variable that isn't 0 or empty (all engine variables), locals first.</summary>
    public static List<GplWatch> NonZero(GplMachine machine)
    {
        var list = new List<GplWatch>();
        foreach (var k in Kinds)
        {
            int count = Count(machine.Memory, k.Name);
            for (int i = 0; i < count; i++)
            {
                string value = Read(machine, k.Type, i);
                if (value is not ("0" or "\"\""))
                {
                    list.Add(new GplWatch($"{k.Name}[{i}]", value, k.Name[0] == 'l'));
                }
            }
        }
        for (int n = 0; n < EngineCount; n++)
        {
            list.Add(new GplWatch($"gname[{n}]", machine.ReadEngine(n).ToString(), false));
        }
        return list;
    }

    private static string Read(GplMachine machine, GplVarType type, int index)
    {
        var v = new GplVariable(type, index);
        return type is GplVarType.LocalString or GplVarType.GlobalString
            ? $"\"{machine.Memory.ReadString(GplMemory.Locate(v)!)}\""
            : machine.Memory.Read(v).ToString();
    }

    /// <summary>The value of <c>kind[index]</c>, e.g. <c>gnum[5]</c>; null if there's no such variable.</summary>
    public static string? Get(GplMachine machine, string name) =>
        Parse(machine, name, out string kind, out int index) is null
            ? kind == "gname" ? machine.ReadEngine(index).ToString() : Read(machine, Kinds.First(k => k.Name == kind).Type, index)
            : null;

    /// <summary>
    /// <c>gnum[5] = 3</c>, <c>lstr[0] = "Ral"</c>, <c>gname[6] = -12</c>: set a
    /// variable as LOAD_VAR would. Returns null, or why it couldn't.
    /// </summary>
    public static string? Set(GplMachine machine, string assignment)
    {
        int eq = assignment.IndexOf('=');
        if (eq < 0)
        {
            return "write it as name[index] = value";
        }
        if (Parse(machine, assignment[..eq], out string kind, out int index) is { } error)
        {
            return error;
        }
        string value = assignment[(eq + 1)..].Trim();
        if (kind is "lstr" or "gstr")
        {
            if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
            {
                return "a string goes in quotes";
            }
            var at = GplMemory.Locate(new GplVariable(Kinds.First(k => k.Name == kind).Type, index))!;
            machine.Memory.WriteString(at, value[1..^1]);
            return null;
        }
        if (!int.TryParse(value, out int number)
            && !(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value[2..], System.Globalization.NumberStyles.HexNumber, null, out number)))
        {
            return $"{value} is not a number";
        }
        if (kind == "gname")
        {
            machine.WriteEngine(index, number);
        }
        else
        {
            machine.Memory.Assign(new GplVariable(Kinds.First(k => k.Name == kind).Type, index), number);
        }
        return null;
    }

    private static string? Parse(GplMachine machine, string name, out string kind, out int index)
    {
        name = name.Trim();
        int open = name.IndexOf('[');
        kind = open > 0 ? name[..open].Trim().ToLowerInvariant() : name;
        index = -1;
        if (open < 0 || !name.EndsWith(']') || !int.TryParse(name[(open + 1)..^1], out index))
        {
            return $"{name}: write it as name[index], e.g. gnum[5]";
        }
        string k = kind;
        if (k != "gname" && !Kinds.Any(x => x.Name == k))
        {
            return $"no variables called {kind} (lnum, lbignum, lflag, lstr, gnum, gbignum, gflag, gstr, gname)";
        }
        int count = Count(machine.Memory, kind);
        return index < 0 || index >= count ? $"{kind} goes from 0 to {count - 1}" : null;
    }
}
