using System.Text;
using DarkSun.Game.Scripting;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// The script debugger, over the region view (F6): a script's source with
/// its breakpoints and the command about to run, the call stack, the
/// variables, and the breakpoint list.
/// <list type="bullet">
/// <item>Click the gutter left of a line (or Ctrl+B on the caret's line) to
/// set or clear a breakpoint: the game pauses before running that command.
/// "Break on next" pauses at the first command of the next script the game
/// runs (creature scripts excepted: they run every half second).</item>
/// <item>Paused, F8 continues, F10 steps over (calls run through), F11 steps
/// into, Shift+F11 steps out of the function; Stop drops the script.</item>
/// <item>Scripts show as assembler source (<see cref="GplSource"/>): a mod's
/// own <c>.gpl</c> file if it replaced the script, else what
/// <c>ModTool export</c> writes. Type <c>GPL 12</c> or <c>MAS 42</c> to open one.</item>
/// <item>Variables lists every one that isn't 0, changes since the last
/// pause in yellow; type <c>gnum[5] = 3</c> to set one.</item>
/// </list>
/// Breakpoints are kept between runs (<see cref="Session.Breakpoints"/>).
/// </summary>
public partial class ScriptDebugger : PanelContainer
{
    private const int FontSize = 9;
    private const double RefreshInterval = 0.25;

    private static readonly Color Dim = new(0.65f, 0.6f, 0.55f);
    private static readonly Color Changed = new(1f, 0.85f, 0.3f);

    private readonly Dictionary<GplEntry, GplSource?> _sources = [];
    private readonly List<GplEntry> _breakpointItems = [];
    private GplMachine? _sourcesFor;
    private Dictionary<string, string> _valuesAtPause = [];
    private Dictionary<string, string> _valuesAtLastPause = [];
    private Dictionary<string, string> _values = [];
    private GplEntry? _pausedAt;
    private GplSource? _shown;
    private int _frame;
    private bool _syncing;
    private bool _dirty = true;
    private double _sinceRefresh;

    /// <summary>Development (<c>--demo</c>): a paused script steps over a command every <see cref="DemoStep"/> seconds, as F10 would, for recordings.</summary>
    private readonly bool _demo = OS.GetCmdlineUserArgs().Contains("--demo");
    private double _sinceStep;
    private const double DemoStep = 0.7;

    private Label _status = null!;
    private Button _continue = null!, _over = null!, _into = null!, _out = null!, _stop = null!;
    private CheckBox _breakNext = null!;
    private LineEdit _goto = null!;
    private Label _origin = null!;
    private CodeEdit _code = null!;
    private ItemList _stack = null!;
    private LineEdit _filter = null!;
    private RichTextLabel _variables = null!;
    private LineEdit _set = null!;
    private Label _setResult = null!;
    private ItemList _breakpoints = null!;

    /// <summary>The machine to debug (the session's; a new game or a load makes a new one).</summary>
    public Func<GplMachine> Machine { get; set; } = () => Session.Scripts;

    /// <summary>A step or Continue ran the script on: the view reports the result as it does any run.</summary>
    public Action<GplRunResult>? Ran { get; set; }

    /// <summary>Stop: the view drops the paused script.</summary>
    public Action? Stop { get; set; }

    /// <summary>What to show when nothing is paused and no script was chosen: the region's own script.</summary>
    public Func<GplEntry?>? Default { get; set; }

    /// <summary>The mod file that replaces a script, if any.</summary>
    public Func<GplEntry, string?> ModSource { get; set; } = e =>
        Session.Mods.ReplacementPath("GPLDATA.GFF", e.Kind == GplFileKind.Mas ? "MAS " : "GPL ", e.File);

    public override void _Ready()
    {
        Visible = false;
        SetAnchorsAndOffsetsPreset(LayoutPreset.RightWide);
        GrowHorizontal = GrowDirection.Begin;
        OffsetLeft = -320;
        AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.06f, 0.05f, 0.94f),
            BorderColor = new Color(0.6f, 0.42f, 0.25f),
            BorderWidthLeft = 1,
            ContentMarginLeft = 4, ContentMarginTop = 3, ContentMarginRight = 3, ContentMarginBottom = 3,
        });
        var mono = new SystemFont { FontNames = ["DejaVu Sans Mono", "Consolas", "Menlo", "Liberation Mono", "monospace"] };

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 2);
        AddChild(column);

        var title = new HBoxContainer();
        column.AddChild(title);
        title.AddChild(Small(new Label { Text = "Script debugger" }, color: new Color(0.95f, 0.8f, 0.55f)));
        title.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        title.AddChild(Small(new Label { Text = "F6 hides" }, color: Dim));

        _status = Small(new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart });
        column.AddChild(_status);

        var buttons = new HFlowContainer();
        buttons.AddThemeConstantOverride("h_separation", 2);
        column.AddChild(buttons);
        _continue = AddButton(buttons, "Continue", "F8: run until the next breakpoint", () => Go(GplStep.None));
        _over = AddButton(buttons, "Over", "F10: the next command here; calls run through", () => Go(GplStep.Over));
        _into = AddButton(buttons, "Into", "F11: the next command, into calls", () => Go(GplStep.Into));
        _out = AddButton(buttons, "Out", "Shift+F11: until this function returns", () => Go(GplStep.Out));
        _stop = AddButton(buttons, "Stop", "Drop the paused script, as if it had ended", () => Stop?.Invoke());
        _breakNext = Small(new CheckBox { Text = "Break on next", TooltipText = "Pause at the first command of the next script the game runs (not creature scripts)" });
        _breakNext.Toggled += on => Machine().BreakOnNextRun = on;
        buttons.AddChild(_breakNext);

        var open = new HBoxContainer();
        column.AddChild(open);
        _goto = Small(new LineEdit { PlaceholderText = "GPL 12 or MAS 42, Enter", SizeFlagsHorizontal = SizeFlags.ExpandFill });
        _goto.TextSubmitted += OpenTyped;
        open.AddChild(_goto);
        AddButton(open, "Break", "Ctrl+B: set or clear a breakpoint on the caret's line (or click left of the line number)",
            () => ToggleAt(_code.GetCaretLine()));
        AddButton(open, "Here", "Back to the paused command (or the region's script)", () =>
        {
            _frame = 0;
            ShowCurrent();
        });
        _origin = Small(new Label { ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis }, color: Dim);
        column.AddChild(_origin);

        _code = new CodeEdit
        {
            Editable = false,
            GuttersDrawBreakpointsGutter = true,
            GuttersDrawExecutingLines = true,
            GuttersDrawLineNumbers = true,
            HighlightCurrentLine = true,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 3,
            SyntaxHighlighter = Highlighter(),
            ContextMenuEnabled = false,
        };
        _code.AddThemeFontOverride("font", mono);
        _code.AddThemeFontSizeOverride("font_size", FontSize);
        _code.BreakpointToggled += line => OnGutterToggled((int)line);
        column.AddChild(_code);

        var tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 2 };
        tabs.AddThemeFontSizeOverride("font_size", FontSize);
        column.AddChild(tabs);

        var variables = new VBoxContainer { Name = "Variables" };
        variables.AddThemeConstantOverride("separation", 1);
        tabs.AddChild(variables);
        _filter = Small(new LineEdit { PlaceholderText = "filter: gflag, gnum[1…", ClearButtonEnabled = true });
        _filter.TextChanged += _ => ShowVariables();
        variables.AddChild(_filter);
        _variables = new RichTextLabel { BbcodeEnabled = true, SizeFlagsVertical = SizeFlags.ExpandFill, SelectionEnabled = true };
        _variables.AddThemeFontOverride("normal_font", mono);
        _variables.AddThemeFontSizeOverride("normal_font_size", FontSize);
        variables.AddChild(_variables);
        var set = new HBoxContainer();
        variables.AddChild(set);
        _set = Small(new LineEdit { PlaceholderText = "gnum[5] = 3, Enter", SizeFlagsHorizontal = SizeFlags.ExpandFill });
        _set.TextSubmitted += SetVariable;
        set.AddChild(_set);
        _setResult = Small(new Label(), color: Dim);
        set.AddChild(_setResult);

        _stack = Small(new ItemList { Name = "Stack" });
        _stack.ItemSelected += i =>
        {
            _frame = (int)i;
            ShowCurrent();
        };
        tabs.AddChild(_stack);

        var breaks = new VBoxContainer { Name = "Breakpoints" };
        tabs.AddChild(breaks);
        _breakpoints = Small(new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill });
        _breakpoints.ItemSelected += i => Show(_breakpointItems[(int)i]);
        breaks.AddChild(_breakpoints);
        var breakButtons = new HBoxContainer();
        breaks.AddChild(breakButtons);
        AddButton(breakButtons, "Remove", "Clear the selected breakpoint", () =>
        {
            foreach (int i in _breakpoints.GetSelectedItems())
            {
                Session.Breakpoints.Remove(_breakpointItems[i]);
            }
        });
        AddButton(breakButtons, "Clear all", "Clear every breakpoint", Session.Breakpoints.Clear);

        Session.Breakpoints.Changed += OnBreakpointsChanged;
    }

    public override void _ExitTree() => Session.Breakpoints.Changed -= OnBreakpointsChanged;

    /// <summary>Scripts ran: show what changed (at once if one paused, else a few times a second).</summary>
    public void ScriptsRan()
    {
        _dirty = true;
        GplMachine m = Machine();
        if (m.PausedFor is not null)
        {
            if (!Visible)
            {
                Visible = true;
            }
            Refresh();
        }
    }

    /// <summary>The script on show (for the headless check).</summary>
    public GplSource? Shown => _shown;

    /// <summary>Line <paramref name="line"/> (1-based) is marked as the one about to run.</summary>
    public bool IsExecuting(int line) => _code.IsLineExecuting(line - 1);

    public bool IsBreakpointShown(int line) => _code.IsLineBreakpointed(line - 1);

    /// <summary>Where a click in the breakpoint gutter of <paramref name="line"/> (1-based) lands, in viewport coordinates (for the check).</summary>
    public Vector2 GutterPoint(int line)
    {
        Rect2I r = _code.GetRectAtLineColumn(line - 1, 0);
        return _code.GetGlobalTransformWithCanvas() * new Vector2(4, r.Position.Y + r.Size.Y / 2f);
    }

    public void Toggle()
    {
        Visible = !Visible;
        if (Visible)
        {
            Refresh();
        }
    }

    public override void _Process(double delta)
    {
        _sinceRefresh += delta;
        if (Visible && _dirty && _sinceRefresh >= RefreshInterval)
        {
            Refresh();
        }
        if (_demo && Visible && Machine().PausedFor is not null && (_sinceStep += delta) >= DemoStep)
        {
            _sinceStep = 0;
            Go(GplStep.Over);
        }
    }

    public override void _Input(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false })
        {
            return;
        }
        if (e.IsActionPressed(Controls.ToggleScriptDebugger, exactMatch: true))
        {
            Toggle();
        }
        else if (Machine().PausedFor is null)
        {
            return;
        }
        else if (e.IsActionPressed(Controls.DebugContinue, exactMatch: true))
        {
            Go(GplStep.None);
        }
        else if (e.IsActionPressed(Controls.DebugStepOver, exactMatch: true))
        {
            Go(GplStep.Over);
        }
        else if (e.IsActionPressed(Controls.DebugStepInto, exactMatch: true))
        {
            Go(GplStep.Into);
        }
        else if (e.IsActionPressed(Controls.DebugStepOut, exactMatch: true))
        {
            Go(GplStep.Out);
        }
        else
        {
            return;
        }
        GetViewport().SetInputAsHandled();
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (Visible && _code.HasFocus() && e is InputEventKey { Pressed: true, Keycode: Key.B, CtrlPressed: true })
        {
            ToggleAt(_code.GetCaretLine());
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>Everything: status, buttons, the script, the stack, the variables, the breakpoints.</summary>
    public void Refresh()
    {
        _dirty = false;
        _sinceRefresh = 0;
        GplMachine m = Machine();
        if (m != _sourcesFor)
        {
            _sources.Clear();
            _sourcesFor = m;
        }
        _breakNext.SetPressedNoSignal(m.BreakOnNextRun);
        bool paused = m.PausedFor is not null;
        foreach (Button b in new[] { _continue, _over, _into, _out, _stop })
        {
            b.Disabled = !paused;
        }

        IReadOnlyList<GplFrame> stack = m.CallStack;
        _values = GplVariables.NonZero(m).ToDictionary(w => w.Name, w => w.Value);
        _values["accumulator"] = m.Accumulator.ToString();
        _values["actor"] = m.Actor.ToString();
        _values["target"] = m.Target.ToString();
        _values["other"] = m.Other.ToString();
        if (paused)
        {
            GplEntry at = m.Current;
            if (_pausedAt != at)
            {
                // A new pause: show where, and what changed since the last one.
                _pausedAt = at;
                _valuesAtPause = _valuesAtLastPause;
                _valuesAtLastPause = _values;
                _frame = 0;
                _shown = null;
            }
            _status.Text = $"Paused ({m.PausedFor}) before {GplBreakpoints.Format(at)}"
                + (LineOf(at) is int line ? $", line {line}" : "") + ".";
        }
        else
        {
            _pausedAt = null;
            _status.Text = m.IsSuspended
                ? "A script is waiting for the player."
                : $"Running freely. {Session.Breakpoints.Count} breakpoint{(Session.Breakpoints.Count == 1 ? "" : "s")}.";
        }

        _stack.Clear();
        foreach (GplFrame f in stack)
        {
            int index = _stack.AddItem((f.Depth == stack[0].Depth ? "" : "returns to ")
                + GplBreakpoints.Format(f.At) + (LineOf(f.At) is int l ? $"  line {l}" : ""));
            _stack.SetItemTooltip(index, f.Depth == stack[0].Depth ? "the command about to run" : "where this caller goes on");
        }
        if (_frame < stack.Count)
        {
            _stack.Select(_frame);
        }

        ShowVariables();
        if (_shown is null)
        {
            ShowCurrent(); // a new pause, or first opened
        }
        else
        {
            MarkLines(); // keep the script the player chose
        }
        ShowBreakpointList();
    }

    // -- the script ----------------------------------------------------------------

    /// <summary>The paused command (or the selected caller), else the region's script.</summary>
    private void ShowCurrent()
    {
        GplMachine m = Machine();
        IReadOnlyList<GplFrame> stack = m.CallStack;
        if (m.PausedFor is not null && _frame < stack.Count)
        {
            Show(stack[_frame].At);
        }
        else if (Default?.Invoke() is { } fallback)
        {
            Show(fallback);
        }
    }

    /// <summary>Show <paramref name="at"/>'s script, with the line of its address under the caret.</summary>
    private void Show(GplEntry at, bool scroll = true)
    {
        GplSource? source = Source(at);
        if (source is null)
        {
            _origin.Text = $"No {GplBreakpoints.Script(at)} script.";
            return;
        }
        if (_shown != source)
        {
            _shown = source;
            _syncing = true;
            _code.Text = source.Text;
            _syncing = false;
            _origin.Text = (source.Origin == "disassembled" ? "disassembled (as ModTool export writes it)" : source.Origin)
                + $", {source.LineOf.Count} commands";
            _origin.TooltipText = source.Origin;
        }
        MarkLines();
        if (scroll && source.LineAt(at.Address) is int line)
        {
            _code.SetCaretLine(line - 1);
            _code.CenterViewportToCaret();
        }
    }

    private void MarkLines()
    {
        if (_shown is null)
        {
            return;
        }
        _syncing = true;
        _code.ClearBreakpointedLines();
        _code.ClearExecutingLines();
        foreach (GplEntry b in Session.Breakpoints.All)
        {
            if (b with { Address = 0 } == _shown.Script && _shown.LineOf.TryGetValue(b.Address, out int line))
            {
                _code.SetLineAsBreakpoint(line - 1, true);
            }
        }
        GplMachine m = Machine();
        IReadOnlyList<GplFrame> stack = m.CallStack;
        if (m.PausedFor is not null)
        {
            for (int f = 0; f < stack.Count; f++)
            {
                // The command about to run, and where each caller goes on.
                if (stack[f].At with { Address = 0 } == _shown.Script && _shown.LineAt(stack[f].At.Address) is int line)
                {
                    _code.SetLineAsExecuting(line - 1, f == 0 || f == _frame);
                }
            }
        }
        _syncing = false;
    }

    private GplSource? Source(GplEntry at)
    {
        GplEntry script = at with { Address = 0 };
        if (!_sources.TryGetValue(script, out GplSource? source))
        {
            source = GplSource.For(Machine(), script.File, script.Kind, ModSource(script));
            _sources[script] = source;
        }
        return source;
    }

    private int? LineOf(GplEntry at) => Source(at)?.LineAt(at.Address);

    private void OpenTyped(string text)
    {
        string[] parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && GplBreakpoints.TryParse($"{parts[0]} {parts[1]} @0", out GplEntry at) && Source(at) is not null)
        {
            Show(at);
            _goto.Text = "";
        }
        else
        {
            _origin.Text = $"No script {text.Trim()}: type GPL 12 or MAS 42.";
        }
    }

    // -- breakpoints -----------------------------------------------------------------

    /// <summary>The gutter was clicked: CodeEdit flipped the line; make it a breakpoint at that line's command.</summary>
    private void OnGutterToggled(int line)
    {
        if (_syncing)
        {
            return;
        }
        ToggleAt(line);
    }

    private void ToggleAt(int line)
    {
        if (_shown?.AddressAt(line + 1) is int address)
        {
            Session.Breakpoints.Toggle(_shown.Script with { Address = address });
        }
        MarkLines(); // also undoes a flip on a line with no command after it
    }

    private void OnBreakpointsChanged()
    {
        MarkLines();
        ShowBreakpointList();
    }

    private void ShowBreakpointList()
    {
        _breakpoints.Clear();
        _breakpointItems.Clear();
        foreach (GplEntry b in Session.Breakpoints.All.OrderBy(e => e.Kind).ThenBy(e => e.File).ThenBy(e => e.Address))
        {
            string text = GplBreakpoints.Format(b);
            if (Source(b) is { } source && source.LineAt(b.Address) is int line)
            {
                text += $"  {line}: {source.Text.Split('\n')[line - 1].Trim()}";
            }
            _breakpoints.AddItem(text);
            _breakpointItems.Add(b);
        }
    }

    // -- variables --------------------------------------------------------------------

    private void ShowVariables()
    {
        string filter = _filter.Text.Trim();
        // Changes are shown while paused, against the pause before.
        Dictionary<string, string> since = Machine().PausedFor is not null ? _valuesAtPause : [];
        var text = new StringBuilder();
        void Section(string title, IEnumerable<KeyValuePair<string, string>> items)
        {
            var shown = items.Where(kv => filter.Length == 0 || kv.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (shown.Count == 0)
            {
                return;
            }
            text.Append($"[color=#{Dim.ToHtml(false)}]{title}[/color]\n");
            foreach (var (name, value) in shown)
            {
                bool changed = since.Count > 0 && since.GetValueOrDefault(name, "0") != value;
                string escaped = value.Replace("[", "[lb]");
                text.Append(changed ? $"  {name} = [color=#{Changed.ToHtml(false)}]{escaped}[/color]\n" : $"  {name} = {escaped}\n");
            }
        }
        string[] context = ["accumulator", "actor", "target", "other"];
        Section("context", context.Select(k => KeyValuePair.Create(k, _values.GetValueOrDefault(k, "0"))));
        Section("locals (this file)", _values.Where(kv => kv.Key.StartsWith('l')));
        Section("globals (not 0)", _values.Where(kv => kv.Key.StartsWith('g') && !kv.Key.StartsWith("gname")));
        Section("engine", _values.Where(kv => kv.Key.StartsWith("gname")));
        // Gone back to 0 since the pause: still worth seeing.
        Section("now 0", since.Where(kv => !_values.ContainsKey(kv.Key)).Select(kv => KeyValuePair.Create(kv.Key, "0")));
        _variables.Text = text.ToString();
    }

    private void SetVariable(string text)
    {
        string? error = GplVariables.Set(Machine(), text);
        _setResult.Text = error ?? "set";
        _setResult.TooltipText = error ?? "";
        if (error is null)
        {
            _set.Text = "";
            Refresh();
        }
    }

    // -- running on ---------------------------------------------------------------------

    /// <summary>Continue the paused script (<see cref="GplStep.None"/>) or step it, as the buttons do.</summary>
    public void Go(GplStep step)
    {
        GplMachine m = Machine();
        if (m.PausedFor is null)
        {
            return;
        }
        Ran?.Invoke(m.Continue(step));
        Refresh();
    }

    // -- building ----------------------------------------------------------------------

    private static CodeHighlighter Highlighter()
    {
        var h = new CodeHighlighter
        {
            NumberColor = new Color(0.7f, 0.85f, 1f),
            SymbolColor = new Color(0.8f, 0.75f, 0.7f),
            FunctionColor = new Color(1f, 0.8f, 0.5f),
            MemberVariableColor = new Color(0.75f, 0.9f, 0.75f),
        };
        h.AddColorRegion(";", "", new Color(0.5f, 0.55f, 0.5f), lineOnly: true);
        h.AddColorRegion("\"", "\"", new Color(0.85f, 0.65f, 0.55f));
        foreach (string mnemonic in GplOpcodes.All.Values.Select(o => o.Mnemonic).Where(n => n.All(c => char.IsLetterOrDigit(c) || c == '_')))
        {
            h.AddKeywordColor(mnemonic, new Color(1f, 0.8f, 0.5f));
        }
        foreach (string v in new[] { "lnum", "lbignum", "lflag", "lstr", "gnum", "gbignum", "gflag", "gstr", "gname" })
        {
            h.AddKeywordColor(v, new Color(0.6f, 0.85f, 0.95f));
        }
        return h;
    }

    private static Button AddButton(Container parent, string text, string tip, Action pressed)
    {
        var b = Small(new Button { Text = text, TooltipText = tip, FocusMode = FocusModeEnum.None });
        b.Pressed += pressed;
        parent.AddChild(b);
        return b;
    }

    private static T Small<T>(T control, Color? color = null) where T : Control
    {
        control.AddThemeFontSizeOverride("font_size", FontSize);
        if (color is { } c)
        {
            control.AddThemeColorOverride("font_color", c);
        }
        return control;
    }
}
