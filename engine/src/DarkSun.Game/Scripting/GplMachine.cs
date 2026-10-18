using DarkSun.Game.Rules;

namespace DarkSun.Game.Scripting;

/// <summary>How a run ended, or paused.</summary>
/// <param name="Error">Why DSUN.EXE would have aborted the script (its <c>7372:17A7</c> path), or null.</param>
/// <param name="Suspended">The host paused the script for the player (see <see cref="GplMachine.Suspend"/>); <see cref="GplMachine.Resume"/> continues it.</param>
public sealed record GplRunResult(GplEntry Entry, int Instructions, string? Error, bool Suspended = false)
{
    public bool Ok => Error is null;
}

/// <summary>
/// The GPL interpreter, ported from DSUN.EXE segment <c>043D</c>
/// (<c>dsun-exe-reference.md</c> §2 and §4): the accumulator, call frames,
/// cross-file calls, the IF / COMPARE / WHILE machinery, variables, RAND and
/// the triggers. Commands that touch the world go to an <see cref="IGplHost"/>.
/// </summary>
/// <remarks>
/// Limits as in DSUN.EXE: 50 call frames, 16 nested files, IF nesting up to
/// 31, COMPARE nesting up to 7. Locals are cleared when a run starts and
/// whenever another file is loaded, including on return from a cross-file
/// call. Division by zero, which crashes DSUN.EXE, gives 0 here.
/// </remarks>
public sealed class GplMachine
{
    public const int MaxFrames = 50;
    public const int MaxFiles = 16;
    public const int MaxIfDepth = 31;
    public const int MaxCompareDepth = 7;
    public const int GameStartFile = 99;

    /// <summary>Where a new game begins (<c>main</c>, <c>1512:0001</c>): region 42 (<c>RGN2A.GFF</c>).</summary>
    public const int StartRegion = 42;

    /// <summary>The party's start position in <see cref="StartRegion"/>, in pixels (<c>DS:117E</c>, <c>DS:1180</c>).</summary>
    public const int StartX = 0x1E8, StartY = 0x98;

    private readonly IGplScripts _scripts;
    private readonly IGplHost _host;
    private readonly byte[] _fieldTypes;
    private readonly Dictionary<(int, GplFileKind), (byte[] Code, GplDecoder Decoder, Dictionary<int, GplInstruction> Cache)> _files = [];

    // Run state.
    private readonly List<GplEntry> _fileStack = [];
    private readonly int[] _pc = new int[MaxFrames + 1];
    private readonly int[] _frameIf = new int[MaxFrames + 1];
    private readonly int[] _frameCompare = new int[MaxFrames + 1];
    private readonly byte[] _conditions = new byte[MaxIfDepth + 2];
    private readonly int[] _compareValues = new int[MaxCompareDepth + 2];
    private readonly bool[] _compareMatched = new bool[MaxCompareDepth + 2];
    private (int File, GplFileKind Kind) _loaded = (-1, GplFileKind.Gpl);
    private byte[] _code = [];
    private GplDecoder _decoder = new([], []);
    private Dictionary<int, GplInstruction> _cache = [];
    private int _frame;
    private int _ifDepth;
    private int _compareDepth;
    private bool _done;
    private string? _error;
    private bool _afterString;
    private string _lastString = "";
    private bool _suspendRequested;
    private GplEntry _runEntry;
    private int _runCount;
    private int? _answer;

    public GplMachine(IGplScripts scripts, IGplHost host, byte[] fieldTypes, BorlandRandom random)
    {
        _scripts = scripts;
        _host = host;
        _fieldTypes = fieldTypes;
        Random = random;
    }

    public GplMemory Memory { get; } = new();

    public GplTriggers Triggers { get; } = new();

    public GplCreatureScripts Creatures { get; } = new();

    public BorlandRandom Random { get; }

    /// <summary>The accumulator (<c>3781:031B</c>), int32.</summary>
    public int Accumulator { get; set; }

    /// <summary>Script context (<c>37B7:0007</c>, <c>0005</c>, <c>0003</c>): who acts, and on what.</summary>
    public int Actor { get; set; }

    public int Target { get; set; }

    public int Other { get; set; }

    /// <summary>The script and address being run.</summary>
    public GplEntry Current => _fileStack.Count > 0
        ? _fileStack[^1] with { Address = _pc[_frame] }
        : default;

    /// <summary>Most instructions one run may take, to stop runaway loops.</summary>
    public int InstructionLimit { get; set; } = 100_000;

    /// <summary>Called with each instruction before it runs, if set.</summary>
    public Action<GplEntry, GplInstruction>? Trace { get; set; }

    /// <summary>A script is paused waiting for the player; new runs and triggers are refused until it finishes.</summary>
    public bool IsSuspended { get; private set; }

    /// <summary>
    /// Pause the running script for input the host can't give at once
    /// (CONTINUE, ASK_YES_NO, MENU). The command that asked is undone and run
    /// again by <see cref="Resume"/>, where the host takes the player's answer
    /// with <see cref="TryTakeAnswer"/> instead of pausing again; this works
    /// for questions asked inside expressions, e.g. <c>lnum[0] = ((ASK_YES_NO))</c>.
    /// </summary>
    public void Suspend() => _suspendRequested = true;

    /// <summary>The answer passed to <see cref="Resume"/>, once, for the command that asked.</summary>
    public bool TryTakeAnswer(out int answer)
    {
        answer = _answer ?? 0;
        bool had = _answer.HasValue;
        _answer = null;
        return had;
    }

    // -- entry points (dsun-exe-reference.md §4) ---------------------------------

    /// <summary>A new game: clear the globals and triggers, then run <c>MAS 99</c>.</summary>
    public GplRunResult NewGame()
    {
        Memory.ClearGlobals();
        Triggers.Reset();
        Creatures.Clear();
        return Run(new GplEntry(GameStartFile, 0, GplFileKind.Mas));
    }

    /// <summary>
    /// Entering region <paramref name="region"/>: drop its predecessor's
    /// triggers and creature scripts, run <c>MAS</c> <paramref name="region"/>.
    /// (Creature scripts live in the per-object table <c>3972:08AA</c>, whose
    /// objects belong to the region; that they are cleared is inferred.)
    /// </summary>
    public GplRunResult EnterRegion(int region)
    {
        Triggers.ClearRegion();
        Creatures.Clear();
        return Run(new GplEntry(region, 0, GplFileKind.Mas));
    }

    /// <summary>
    /// A creature moved into cell (x, y) (<c>trigger_on_move</c>): runs the
    /// matching tile or box trigger. <paramref name="active"/>: it is the
    /// party's active member (box triggers with who 0 fire only for it).
    /// </summary>
    public GplRunResult? OnMove(int who, int x, int y, bool active)
    {
        if (IsSuspended || Triggers.MatchMove(x, y, active ? 0 : 1) is not { } entry)
        {
            return null;
        }
        SetContext(who, who, who);
        return Run(entry);
    }

    /// <summary>Pick up, talk to, attack, look at or use <paramref name="target"/>.</summary>
    public GplRunResult? OnObject(GplTriggerList list, int actor, int target)
    {
        if (IsSuspended || Triggers.MatchObject(list, _host.TriggerId(this, target)) is not { } entry)
        {
            return null;
        }
        SetContext(actor, target, target);
        return Run(entry);
    }

    /// <summary>Use <paramref name="first"/> with <paramref name="second"/>.</summary>
    public GplRunResult? OnUseWith(int actor, int first, int second)
    {
        if (IsSuspended || Triggers.MatchUseWith(_host.TriggerId(this, first), _host.TriggerId(this, second)) is not { } entry)
        {
            return null;
        }
        SetContext(actor, first, second);
        return Run(entry);
    }

    /// <summary>
    /// A creature's own script (no orders, line of sight): run with
    /// <paramref name="creature"/> as actor, target and other. Refused while
    /// another script is paused.
    /// </summary>
    public GplRunResult? RunFor(GplEntry entry, int creature)
    {
        if (IsSuspended)
        {
            return null;
        }
        SetContext(creature, creature, creature);
        return Run(entry);
    }

    /// <summary>
    /// Who acts and on what, also as the engine variables scripts read them
    /// (<c>gname[6]</c> actor, <c>[7]</c> target, <c>[8]</c> other).
    /// </summary>
    private void SetContext(int actor, int target, int other)
    {
        (Actor, Target, Other) = (actor, target, other);
        _host.WriteEngineVariable(this, 0x26, actor);
        _host.WriteEngineVariable(this, 0x27, target);
        _host.WriteEngineVariable(this, 0x28, other);
    }

    // -- running -----------------------------------------------------------------

    /// <summary>
    /// <c>gpl_run</c> (<c>043D:0001</c>): run a script from an address until
    /// it ends. File 0 means no script, as in DSUN.EXE.
    /// </summary>
    public GplRunResult Run(GplEntry entry)
    {
        if (entry.File == 0)
        {
            return new GplRunResult(entry, 0, null);
        }
        if (IsSuspended)
        {
            return new GplRunResult(entry, 0, "another script is waiting for the player");
        }
        _fileStack.Clear();
        _frame = 0;
        _ifDepth = 0;
        _compareDepth = 0;
        _done = false;
        _error = null;
        Memory.ClearLocals();
        _runEntry = entry;
        _runCount = 0;
        CallFile(entry);
        return Continue();
    }

    /// <summary>
    /// Continue a suspended script: the command that paused runs again and
    /// gets <paramref name="answer"/> (ASK_YES_NO: 1 yes, 0 no; MENU: the
    /// option's index or −1; CONTINUE: anything).
    /// </summary>
    public GplRunResult Resume(int answer = 0)
    {
        if (!IsSuspended)
        {
            throw new InvalidOperationException("no script is suspended");
        }
        IsSuspended = false;
        _answer = answer;
        return Continue();
    }

    private GplRunResult Continue()
    {
        int count = _runCount;
        GplEntry entry = _runEntry;
        while (!_done && _frame >= 1)
        {
            if (++count > InstructionLimit)
            {
                Abort($"more than {InstructionLimit} instructions");
                break;
            }
            int pc = _pc[_frame];
            if (pc >= _code.Length)
            {
                // The loader appends EXIT_GPL (0x31) after every file.
                _done = true;
                break;
            }
            GplInstruction instruction;
            try
            {
                instruction = Decode(pc);
            }
            catch (GplDecodeException e)
            {
                Abort(e.Message);
                break;
            }
            (int frame, int ifDepth, int compareDepth, int files) = (_frame, _ifDepth, _compareDepth, _fileStack.Count);
            _pc[_frame] = pc + instruction.Length;
            Trace?.Invoke(Current with { Address = pc }, instruction);
            Execute(instruction);
            if (_suspendRequested && !_done)
            {
                // Undo the command so that Resume runs it again with the answer.
                _suspendRequested = false;
                (_frame, _ifDepth, _compareDepth) = (frame, ifDepth, compareDepth);
                if (_fileStack.Count != files)
                {
                    _fileStack.RemoveRange(files, _fileStack.Count - files);
                    Load(_fileStack[^1].File, _fileStack[^1].Kind, clearLocals: false);
                }
                _pc[_frame] = pc;
                IsSuspended = true;
                _runCount = count - 1;
                return new GplRunResult(entry, count, null, Suspended: true);
            }
            _answer = null;
        }
        _suspendRequested = false;
        return new GplRunResult(entry, count, _error);
    }

    private GplInstruction Decode(int pc)
    {
        if (!_cache.TryGetValue(pc, out GplInstruction? instruction))
        {
            instruction = _decoder.DecodeAt(pc);
            _cache[pc] = instruction;
        }
        return instruction;
    }

    private void Abort(string reason)
    {
        _error ??= $"{Current.Kind} {Current.File} @{Current.Address:x4}: {reason}";
        _done = true;
    }

    /// <summary><c>gpl_call_file</c> (<c>043D:02F3</c>).</summary>
    private void CallFile(GplEntry entry)
    {
        if (_done)
        {
            return;
        }
        if (_fileStack.Count >= MaxFiles)
        {
            Abort("cross-file calls nested too deep");
            return;
        }
        _fileStack.Add(entry);
        if (!Load(entry.File, entry.Kind))
        {
            Abort($"no {entry.Kind} {entry.File}");
            return;
        }
        PushFrame(entry.Address);
    }

    /// <summary><c>gpl_return_file</c> (<c>043D:0384</c>), END_FUNCTION.</summary>
    private void ReturnFile()
    {
        _fileStack.RemoveAt(_fileStack.Count - 1);
        if (_fileStack.Count == 0)
        {
            _done = true;
            return;
        }
        GplEntry caller = _fileStack[^1];
        if (!Load(caller.File, caller.Kind))
        {
            Abort($"no {caller.Kind} {caller.File}");
            return;
        }
        PopFrame();
    }

    /// <summary>Make a file current (<c>043D:03F8</c>); a different file clears the locals.</summary>
    private bool Load(int file, GplFileKind kind, bool clearLocals = true)
    {
        if (_loaded == (file, kind))
        {
            return true;
        }
        if (!_files.TryGetValue((file, kind), out var loaded))
        {
            byte[]? code = _scripts.Load(file, kind);
            if (code is null)
            {
                return false;
            }
            loaded = (code, new GplDecoder(code, _fieldTypes), []);
            _files[(file, kind)] = loaded;
        }
        if (clearLocals)
        {
            Memory.ClearLocals();
        }
        (_code, _decoder, _cache) = loaded;
        _loaded = (file, kind);
        return true;
    }

    private void PushFrame(int address)
    {
        if (_frame + 1 > MaxFrames - 1)
        {
            Abort("call frames nested too deep");
            return;
        }
        _frame++;
        _frameIf[_frame] = _ifDepth;
        _frameCompare[_frame] = _compareDepth;
        _pc[_frame] = address;
    }

    private void PopFrame()
    {
        _ifDepth = _frameIf[_frame];
        _compareDepth = _frameCompare[_frame];
        _frame--;
        if (_frame < 0)
        {
            Abort("returned past the first frame");
        }
    }

    private void Jump(int address) => _pc[_frame] = (ushort)address;

    // -- commands ------------------------------------------------------------------

    private void Execute(GplInstruction i)
    {
        switch (i.Op)
        {
            case GplOpcodes.StringEnd:
                if (_afterString)
                {
                    _afterString = false;
                }
                else
                {
                    Abort("STRING_END outside a string");
                }
                return;
            case GplOpcodes.LoadVar:
                Accumulator = Evaluate(i.Value!).Value;
                Assign(i.Target!, Accumulator);
                return;
            case GplOpcodes.LoadAccum:
                Accumulator = Evaluate(i.Value!).Value;
                return;
            case GplOpcodes.Menu:
                RunMenu(i.Menu!);
                return;
            case GplOpcodes.SetRecord:
                if (i.Record is not null)
                {
                    _host.WriteField(this, i.Record, Evaluate(i.Value!).Value);
                }
                return;
            case GplOpcodes.Search:
            {
                Result who = Evaluate(i.Search!.Who);
                Host(i, [who]);
                return;
            }
            case 0x52: // RAND: draws before evaluating n (gpl_op_52_RAND)
            {
                int r = Random.Next();
                int n = Evaluate(i.Args[0]).Value;
                Accumulator = (short)(unchecked(r * (n + 1)) / 0x8000);
                return;
            }
        }

        Result[] a = [.. i.Args.Select(Evaluate)];
        int V(int n) => a[n].Value;
        switch (i.Op)
        {
            case 0x01: Modify(a[0], 4, v => V(1) == 0 ? 0 : v / V(1)); break;
            case 0x02: Modify(a[0], 1, v => v - 1); break;
            case 0x03: Modify(a[0], 2, v => v - 1); break;
            case 0x04: Modify(a[0], 4, v => v - 1); break;
            case 0x05: Modify(a[0], 1, v => v + 1); break;
            case 0x06: Modify(a[0], 2, v => v + 1); break;
            case 0x07: Modify(a[0], 4, v => v + 1); break;
            case 0x0A: CopyString(a[0], a[1], i); break;
            case 0x0E: Accumulator = Accumulator == 0 ? 1 : 0; break;
            case 0x1E or 0x1F: Accumulator = unchecked(-V(0)); break; // NAMETONUM, NUMTONAME: acc = −value
            case 0x11: Modify(a[0], 4, v => unchecked(v * V(1))); break;
            case GplOpcodes.Jump: Jump(V(0)); break;
            case GplOpcodes.CallLocal: PushFrame((ushort)V(0)); break;
            case GplOpcodes.CallGlobal: CallFile(new GplEntry(V(1), (ushort)V(0))); break;
            case GplOpcodes.LocalReturn: PopFrame(); break;
            case GplOpcodes.Compare:
                Accumulator = V(0);
                if (++_compareDepth > MaxCompareDepth)
                {
                    Abort("COMPARE nested too deep");
                    break;
                }
                _compareMatched[_compareDepth] = false;
                _compareValues[_compareDepth] = Accumulator;
                break;
            case GplOpcodes.EndFunction: ReturnFile(); break;
            case 0x27: // IFCOMPARE value, next case
                if (_compareValues[Math.Max(_compareDepth, 0)] == V(0))
                {
                    _compareMatched[Math.Max(_compareDepth, 0)] = true;
                }
                else
                {
                    Jump(V(1));
                }
                break;
            case 0x29: // ORELSE: leave the COMPARE once a case has matched
                if (_compareMatched[Math.Max(_compareDepth, 0)])
                {
                    Jump(V(0));
                }
                break;
            case 0x31: _done = true; break;
            case 0x3E: // IF: remembers the accumulator's low byte for ELSE
                if (++_ifDepth > MaxIfDepth)
                {
                    Abort("IF nested too deep");
                    break;
                }
                _conditions[_ifDepth] = (byte)Accumulator;
                if (Accumulator == 0)
                {
                    Jump(V(0));
                }
                break;
            case 0x3F: // ELSE
                if (_conditions[Math.Max(_ifDepth, 0)] != 0)
                {
                    Jump(V(0));
                }
                break;
            case 0x61:
                if (--_compareDepth < 0)
                {
                    Abort("CMPEND without COMPARE");
                }
                break;
            case 0x63: // WHILE
                if (Accumulator == 0)
                {
                    Jump(V(0));
                }
                break;
            case 0x64: Jump(V(0)); break; // WEND
            case 0x67:
                if (--_ifDepth < 0)
                {
                    Abort("ENDIF without IF");
                }
                break;
            case 0x76: Modify(a[0], 1, v => v + V(1)); break;
            case 0x77: Modify(a[0], 1, v => v - V(1)); break;
            case 0x78: Modify(a[0], 1, v => v * V(1)); break;
            case 0x79: Modify(a[0], 1, v => V(1) == 0 ? 0 : v / V(1)); break;
            case 0x7A: Modify(a[0], 2, v => v + V(1)); break;
            case 0x7B: Modify(a[0], 2, v => v - V(1)); break;
            case 0x7C: Modify(a[0], 2, v => v * V(1)); break;
            case 0x7D: Modify(a[0], 2, v => V(1) == 0 ? 0 : v / V(1)); break;
            case 0x7E: Modify(a[0], 4, v => unchecked(v + V(1))); break;
            case 0x7F: Modify(a[0], 4, v => unchecked(v - V(1))); break;

            // Triggers (dsun-exe-reference.md §4).
            case 0x1B: Creatures.SetSight(V(2), 1, new GplEntry(V(1), (ushort)V(0)), V(3)); break;
            case 0x1C: Creatures.SetSight(V(2), 2, new GplEntry(V(1), (ushort)V(0)), V(3)); break;
            case 0x1D: Creatures.ClearSight(V(0)); Host(i, a); break;
            case 0x6F: Creatures.SetNoOrders(V(2), new GplEntry(V(1), (ushort)V(0))); break;
            case 0x65: AddObjectTrigger(GplTriggerList.Attack, a); break;
            case 0x66: AddObjectTrigger(GplTriggerList.Look, a); break;
            case 0x6C: AddObjectTrigger(GplTriggerList.Pickup, a); break;
            case 0x6D: AddObjectTrigger(GplTriggerList.Use, a); break;
            case 0x6E: AddObjectTrigger(GplTriggerList.TalkTo, a); break;
            case 0x68: Triggers.AddTile(GplTriggerList.MoveTile, V(0), V(1), V(2), V(3), V(4)); break;
            case 0x69: Triggers.AddTile(GplTriggerList.DoorTile, V(0), V(1), V(2), V(3), V(4)); break;
            case 0x6A: Triggers.AddBox(GplTriggerList.MoveBox, V(0), V(1), V(2), V(3), V(4), V(5), V(6)); break;
            case 0x6B: Triggers.AddBox(GplTriggerList.DoorBox, V(0), V(1), V(2), V(3), V(4), V(5), V(6)); break;
            case 0x70: Triggers.AddUseWith(V(0), V(1), V(2), V(3), InGameStartScript); break;

            case 0x20: Accumulator = (V(0) & V(1)) == V(1) ? 1 : 0; break; // BITSNOOP
            case 0x35: Host(i, a); _done = true; break; // FIGHT: combat starts, the script ends (gpl_exit)

            // Debug and source-trace commands: no effect on the game.
            case 0x23 or 0x28 or 0x2C or 0x2E or 0x4B: break;

            default:
                Host(i, a);
                break;
        }
    }

    /// <summary>Triggers made by <c>MAS 99</c> are global: they survive region changes.</summary>
    private bool InGameStartScript => _loaded == (GameStartFile, GplFileKind.Mas);

    private void AddObjectTrigger(GplTriggerList list, Result[] a) =>
        Triggers.AddObject(list, a[0].Value, a[1].Value, a[2].Value, InGameStartScript);

    private void Host(GplInstruction i, Result[] a)
    {
        var command = new GplCommand(i, Current, [.. a.Select(r => r.Value)], [.. a.Select(r => r.Text)],
            [.. a.Select(r => r.Location)]);
        if (_host.Execute(this, command) is int value)
        {
            Accumulator = value;
        }
    }

    private void RunMenu(GplMenu menu)
    {
        string title = Evaluate(menu.Title).Text ?? "";
        var options = new List<GplMenuOption>();
        foreach (GplMenuEntry e in menu.Entries)
        {
            Result text = Evaluate(e.Text);
            int address = Evaluate(e.Address).Value;
            bool enabled = Evaluate(e.Condition).Value != 0;
            options.Add(new GplMenuOption(text.Text ?? text.Value.ToString(), (ushort)address, enabled));
        }
        int choice = _host.ChooseMenu(this, title, options);
        if (!_suspendRequested && choice >= 0 && choice < options.Count)
        {
            // The chosen option's address is entered as a subroutine.
            PushFrame(options[choice].Address);
        }
    }

    // -- values ---------------------------------------------------------------------

    /// <summary>An evaluated expression: its value, its text if it was a string, and the last variable it read.</summary>
    private readonly record struct Result(int Value, string? Text, GplLocation? Location);

    /// <summary>
    /// Operands joined by operators applied left to right, without precedence;
    /// parentheses evaluate first (<c>gpl_eval_expr</c>, <c>043D:398F</c>).
    /// </summary>
    private Result Evaluate(GplExpression e)
    {
        var stack = new Stack<(int? Value, byte? Op)>();
        int? value = null;
        byte? op = null;
        string? text = null;
        GplLocation? location = null;
        foreach (GplExprItem item in e.Items)
        {
            switch (item)
            {
                case GplOpen:
                    stack.Push((value, op));
                    (value, op) = (null, null);
                    break;
                case GplClose:
                    int inner = value ?? 0;
                    (value, op) = stack.Count > 0 ? stack.Pop() : (null, null);
                    value = value is int v ? Apply(v, op, inner) : inner;
                    break;
                case GplOperator o:
                    op = o.Code;
                    break;
                case GplTerm t:
                    Result r = Operand(t.Operand);
                    text = r.Text;
                    location = r.Location ?? location;
                    value = value is int left ? Apply(left, op, r.Value) : r.Value;
                    break;
            }
        }
        while (stack.Count > 0)
        {
            int inner = value ?? 0;
            (value, op) = stack.Pop();
            value = value is int v ? Apply(v, op, inner) : inner;
        }
        return new Result(value ?? 0, e.Items.Count == 1 ? text : null, location);
    }

    private static int Apply(int a, byte? op, int b) => op switch
    {
        0xD1 => unchecked(a + b),
        0xD2 => unchecked(a - b),
        0xD3 => unchecked(a * b),
        0xD4 => b == 0 ? 0 : (a == int.MinValue && b == -1 ? a : a / b),
        0xD5 => a != 0 && b != 0 ? 1 : 0,
        0xD6 => a != 0 || b != 0 ? 1 : 0,
        0xD7 => a == b ? 1 : 0,
        0xD8 => a != b ? 1 : 0,
        0xD9 => a > b ? 1 : 0,
        0xDA => a < b ? 1 : 0,
        0xDB => a >= b ? 1 : 0,
        0xDC => a <= b ? 1 : 0,
        0xDD => a & b,
        0xDE => a | b,
        0xDF => a & ~b,
        _ => b,
    };

    private Result Operand(GplOperand o)
    {
        switch (o)
        {
            case GplNumber n:
                return new Result(n.Value, null, null);
            case GplAccumulator:
                return new Result(Accumulator, null, null);
            case GplName n:
                return new Result(-n.Number, null, null);
            case GplString s:
                _afterString = true;
                string text = s.Kind switch
                {
                    GplStringKind.SpeakerName => _host.SpeakerName(this),
                    GplStringKind.Previous => _lastString,
                    _ => s.Text,
                };
                _lastString = text;
                return new Result(0, text, null);
            case GplVariable v when v.Type is GplVarType.LocalString or GplVarType.GlobalString:
            {
                GplMemoryLocation at = GplMemory.Locate(v)!;
                return new Result(0, Memory.ReadString(at), at);
            }
            case GplVariable v when v.Type == GplVarType.EngineVar:
                return new Result(v.Index is >= 0x20 and < 0x2F ? _host.ReadEngineVariable(this, v.Index) : 0,
                    null, new GplEngineLocation(v.Index));
            case GplVariable v:
                return new Result(Memory.Read(v), null, GplMemory.Locate(v));
            case GplObjectRef r:
                return new Result(_host.ReadField(this, r), null, new GplFieldLocation(r));
            case GplEmbedded e:
                Execute(e.Instruction);
                return new Result(Accumulator, null, null);
            default:
                throw new InvalidOperationException(o.ToString());
        }
    }

    private void Assign(GplTarget target, int value)
    {
        if (target.Field is not null)
        {
            _host.WriteField(this, target.Field, value);
        }
        else
        {
            Memory.Assign(target.Variable!, value);
        }
    }

    /// <summary>BYTE/WORD/LONG commands: change 1, 2 or 4 bytes where the operand's last variable lives.</summary>
    private void Modify(Result operand, int width, Func<int, int> change)
    {
        switch (operand.Location)
        {
            case GplMemoryLocation m:
                Memory.WriteInt(m, width, change(Memory.ReadInt(m, width)));
                break;
            case GplEngineLocation g:
                _host.WriteEngineVariable(this, g.Index, change(_host.ReadEngineVariable(this, g.Index)));
                break;
            case GplFieldLocation f:
                _host.WriteField(this, f.Field, change(_host.ReadField(this, f.Field)));
                break;
        }
    }

    private void CopyString(Result destination, Result source, GplInstruction i)
    {
        if (destination.Location is GplMemoryLocation m && m.Area is GplArea.LocalStrings or GplArea.GlobalStrings)
        {
            Memory.WriteString(m, source.Text ?? source.Value.ToString());
        }
        else
        {
            Host(i, [destination, source]);
        }
    }
}
