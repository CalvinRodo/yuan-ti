using Godot;

namespace DarkSun.Engine;

/// <summary>One key (or click) and what it does; <see cref="Action"/> is the input action it shows, if any.</summary>
public sealed record HelpLine(string Keys, string What, string? Action = null)
{
    /// <summary>The keys bound to <paramref name="action"/> now (so a remapping shows), or <paramref name="fallback"/>.</summary>
    public static HelpLine For(string action, string what, string? fallback = null)
    {
        string keys = KeyHelp.Keys(action);
        return new HelpLine(keys.Length > 0 ? keys : fallback ?? "(none)", what, action);
    }
}

public sealed record HelpSection(string Title, params HelpLine[] Lines);

/// <summary>
/// The key help (an autoload): <c>?</c> on any screen shows every key and
/// click that screen takes, from <see cref="For"/>; <c>?</c>, Esc or a click
/// closes it. While it is open the screen gets no keys. Keys from
/// <see cref="Controls"/> are shown as they are bound now. A screen's keys
/// are listed here, in one place: when a screen learns a key, add it to
/// <see cref="For"/> (<c>--check-help</c> fails if an action in
/// <see cref="Controls.All"/> isn't listed anywhere).
/// </summary>
public partial class KeyHelp : CanvasLayer
{
    private const int FontSize = 10;

    private static readonly Color Heading = new(0.95f, 0.8f, 0.55f);
    private static readonly Color KeyColor = new(1f, 0.9f, 0.7f);
    private static readonly Color Dim = new(0.7f, 0.65f, 0.6f);

    private Control _root = null!;
    private Label _title = null!;
    private HFlowContainer _sections = null!;

    public bool IsOpen => _root.Visible;

    public override void _Ready()
    {
        Layer = 100;
        ProcessMode = ProcessModeEnum.Always;
        Controls.Register();
        _root = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
        _root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _root.GuiInput += e =>
        {
            // A click closes it; the wheel scrolls, and at the top or bottom it comes here too.
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left or MouseButton.Right or MouseButton.Middle })
            {
                Close();
            }
        };
        AddChild(_root);
        var shade = new ColorRect { Color = new Color(0, 0, 0, 0.6f), MouseFilter = Control.MouseFilterEnum.Ignore };
        shade.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(shade);

        var margin = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        foreach (string side in new[] { "left", "right", "top", "bottom" })
        {
            margin.AddThemeConstantOverride($"margin_{side}", 16);
        }
        _root.AddChild(margin);
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.1f, 0.07f, 0.05f, 0.96f),
            BorderColor = new Color(0.6f, 0.42f, 0.25f),
            BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
            ContentMarginLeft = 8, ContentMarginTop = 6, ContentMarginRight = 8, ContentMarginBottom = 6,
        });
        margin.AddChild(panel);
        var column = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        column.AddThemeConstantOverride("separation", 4);
        panel.AddChild(column);
        var top = new HBoxContainer();
        column.AddChild(top);
        _title = Text("", Heading, FontSize + 2);
        top.AddChild(_title);
        top.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        top.AddChild(Text("? or Esc closes", Dim));
        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        column.AddChild(scroll);
        _sections = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _sections.AddThemeConstantOverride("h_separation", 16);
        _sections.AddThemeConstantOverride("v_separation", 8);
        scroll.AddChild(_sections);
        if (OS.GetCmdlineUserArgs().Contains("--show-keys"))
        {
            // Open on the first screen (for screenshots).
            GetTree().CreateTimer(0.5).Timeout += Open;
        }
    }

    public override void _Input(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true } key)
        {
            return;
        }
        bool question = key.Unicode == '?';
        if (IsOpen)
        {
            if (!key.Echo && (question || e.IsActionPressed(Controls.Back)))
            {
                Close();
            }
            GetViewport().SetInputAsHandled(); // the screen underneath gets no keys meanwhile
        }
        else if (question && !key.Echo && !Typing())
        {
            Open();
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>A text field has the keyboard: <c>?</c> is a character there.</summary>
    private bool Typing() => GetViewport().GuiGetFocusOwner() switch
    {
        LineEdit => true,
        TextEdit t => t.Editable,
        _ => false,
    };

    public void Open()
    {
        Node? scene = GetTree().CurrentScene;
        var (title, sections) = For(scene);
        _title.Text = $"Keys: {title}";
        foreach (Node child in _sections.GetChildren())
        {
            child.QueueFree();
        }
        foreach (HelpSection section in sections)
        {
            var box = new VBoxContainer { CustomMinimumSize = new Vector2(280, 0) };
            box.AddThemeConstantOverride("separation", 0);
            box.AddChild(Text(section.Title, Heading));
            var grid = new GridContainer { Columns = 2 };
            grid.AddThemeConstantOverride("h_separation", 8);
            grid.AddThemeConstantOverride("v_separation", 0);
            foreach (HelpLine line in section.Lines)
            {
                grid.AddChild(Text(line.Keys, KeyColor));
                var what = Text(line.What, null);
                what.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                what.CustomMinimumSize = new Vector2(170, 0);
                what.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                grid.AddChild(what);
            }
            box.AddChild(grid);
            _sections.AddChild(box);
        }
        _root.Visible = true;
    }

    public void Close() => _root.Visible = false;

    private static Label Text(string text, Color? color, int size = FontSize)
    {
        var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", size);
        if (color is { } c)
        {
            label.AddThemeColorOverride("font_color", c);
        }
        return label;
    }

    // -- what each screen takes ------------------------------------------------------

    private static HelpSection Everywhere => new("Everywhere",
        new HelpLine("?", "This list of keys"));

    /// <summary>The screen's name and its keys.</summary>
    public static (string Title, IReadOnlyList<HelpSection> Sections) For(Node? scene) => scene switch
    {
        RegionView => ("the map", [.. RegionSections, Everywhere]),
        MainMenu => ("main menu", [MainMenuSection, Buttons, Everywhere]),
        PartyView => ("View Character", [PartySection, Everywhere]),
        CharacterGenView => ("Character Generation", [CharacterGenSection, Everywhere]),
        SavesView => ("Load Saved Game", [SavesSection, Everywhere]),
        ModsView => ("mods", [ModsSection, Everywhere]),
        IntroView => ("opening", [new HelpSection("Opening", new HelpLine("Any key, click", "Skip to the main menu")), Everywhere]),
        _ => ("this screen", [Buttons, Everywhere]),
    };

    private static HelpSection Buttons => new("Buttons",
        new HelpLine("Click", "Press a button"),
        new HelpLine("Tab, arrows", "Move between buttons"),
        new HelpLine("Enter, Space", "Press the highlighted button"));

    private static HelpSection SavesSection => Lists("Saved games", "the saved games", "Load or Delete the save");

    private static HelpSection ModsSection => Lists("Mods", "the mods", "Turn a mod on or off, or move it up or down");

    private static HelpSection Lists(string title, string what, string press) => new(title,
        new HelpLine("Click", press),
        new HelpLine("Tab, arrows", $"Move through {what}"),
        new HelpLine("Enter, Space", "Press the highlighted button"),
        HelpLine.For(Controls.Back, "Back to the main menu", "Esc"));

    private static HelpSection MainMenuSection => new("Main menu",
        new HelpLine("S", "Start a new game"),
        new HelpLine("C", "Create characters"),
        new HelpLine("L", "Load a saved game"),
        new HelpLine("E", "Exit"),
        new HelpLine("M", "Mods: turn them on and off, change their order"));

    private static HelpSection PartySection => new("View Character",
        new HelpLine("Click a box", "Select a character (or an empty box)"),
        new HelpLine("Right-click a box", "NEW / ADD, or EDIT / DROP, as a menu"),
        new HelpLine("Click NEW, EDIT", "Make a character, or change one"),
        new HelpLine("Click ADD, DROP", "Bring back a character made before, or take one out"),
        HelpLine.For(Controls.Back, "Keep the party and go back (also RETURN)", "Esc"));

    private static HelpSection CharacterGenSection => new("Character Generation",
        new HelpLine("Click the body", "Next race and gender (right-click: previous)"),
        new HelpLine("Click an ability", "Raise it (right-click: lower)"),
        new HelpLine("Click HP, alignment", "Raise or change it (right-click: back)"),
        new HelpLine("Click the die", "Roll new abilities and hit points"),
        new HelpLine("Click a class", "Choose it or drop it"),
        new HelpLine("Click the icon", "Change the map icon"),
        new HelpLine("Click a discipline", "Choose the psionic discipline (or sphere)"),
        new HelpLine("Click the name", "Type a name"),
        new HelpLine("Click DONE, EXIT", "Keep the character, or leave without it"),
        HelpLine.For(Controls.Back, "Leave without the character (not while typing)", "Esc"));

    private static IEnumerable<HelpSection> RegionSections =>
    [
        new HelpSection("Party",
            new HelpLine("Click", "The selected character walks there"),
            new HelpLine("Shift+click", "The whole party walks there"),
            new HelpLine("Right-click", "What can be done with the thing under the pointer (done at once if only one)"),
            HelpLine.For(Controls.NextCharacter, "Select the next character"),
            HelpLine.For(Controls.ToggleInventory, "The party's items: wear, give, drop"),
            HelpLine.For(Controls.AddCharacter, "Add a character (testing)"),
            HelpLine.For(Controls.ChangeSprite, "Change the selected character's sprite (testing)")),
        new HelpSection("Verb menu and conversations",
            new HelpLine("1–6", "Choose a verb by its place in the menu"),
            new HelpLine("T G U L", "Talk, Get, Use, Look"),
            new HelpLine("Esc, click outside", "Close the menu; cancel Use with…"),
            new HelpLine("Click", "Use with…: the second thing"),
            new HelpLine("Space, Enter", "Conversation: MORE (the next page, go on, the choices)"),
            new HelpLine("Click the text", "Conversation: the same as MORE; the wheel scrolls"),
            new HelpLine("1–9, click", "Conversation: pick a choice"),
            new HelpLine("Y, N", "Conversation: answer Yes or No"),
            new HelpLine("Double-click", "In a shop: buy or sell; in the items: wear or take off")),
        new HelpSection("View",
            HelpLine.For(Controls.PanUp, "Scroll up"),
            HelpLine.For(Controls.PanDown, "Scroll down"),
            HelpLine.For(Controls.PanLeft, "Scroll left"),
            HelpLine.For(Controls.PanRight, "Scroll right"),
            new HelpLine("Middle-drag", "Scroll (the camera follows again after the next click)"),
            HelpLine.For(Controls.ZoomIn, "Zoom in (or mouse wheel up)"),
            HelpLine.For(Controls.ZoomOut, "Zoom out (or mouse wheel down)")),
        new HelpSection("Game",
            HelpLine.For(Controls.QuickSave, "Quick save"),
            HelpLine.For(Controls.QuickLoad, "Load the quick save"),
            HelpLine.For(Controls.Back, "Back to the main menu", "Esc")),
        new HelpSection("Debugging",
            HelpLine.For(Controls.ToggleMapFlags, "Map flags: blocked, line of sight, occupied, triggers"),
            HelpLine.For(Controls.ToggleScriptLog, "Script log"),
            HelpLine.For(Controls.ToggleGrid, "Cell grid"),
            HelpLine.For(Controls.ToggleObjectNumbers, "Object numbers"),
            HelpLine.For(Controls.NextRegion, "Next region"),
            HelpLine.For(Controls.PreviousRegion, "Previous region")),
        new HelpSection("Script debugger",
            HelpLine.For(Controls.ToggleScriptDebugger, "Open or close it"),
            HelpLine.For(Controls.DebugContinue, "Paused: continue"),
            HelpLine.For(Controls.DebugStepOver, "Paused: step over"),
            HelpLine.For(Controls.DebugStepInto, "Paused: step into"),
            HelpLine.For(Controls.DebugStepOut, "Paused: step out"),
            new HelpLine("Click by a line", "Set or clear a breakpoint (left of the line number)"),
            new HelpLine("Ctrl+B", "Set or clear a breakpoint on the caret's line")),
    ];

    /// <summary>Every screen's sections, for <c>--check-help</c>.</summary>
    public static IEnumerable<HelpSection> AllSections =>
    [
        .. RegionSections, MainMenuSection, PartySection, CharacterGenSection, Buttons, Everywhere, SavesSection, ModsSection,
    ];

    /// <summary>Actions in <see cref="Controls.All"/> no screen's help lists.</summary>
    public static IEnumerable<string> Unlisted()
    {
        var listed = AllSections.SelectMany(s => s.Lines).Select(l => l.Action).OfType<string>().ToHashSet();
        return Controls.All.Where(a => !listed.Contains(a));
    }

    // -- key names ----------------------------------------------------------------------

    private static readonly Dictionary<Key, string> Names = new()
    {
        [Key.Equal] = "=", [Key.Minus] = "-", [Key.KpAdd] = "Num +", [Key.KpSubtract] = "Num -",
        [Key.Pageup] = "PgUp", [Key.Pagedown] = "PgDn", [Key.Escape] = "Esc",
        [Key.Left] = "←", [Key.Right] = "→", [Key.Up] = "↑", [Key.Down] = "↓",
    };

    /// <summary>The keys bound to <paramref name="action"/>, as <c>F5</c>, <c>Shift+F11</c>, <c>↑ / W</c>.</summary>
    public static string Keys(string action) =>
        InputMap.HasAction(action)
            ? string.Join(" / ", InputMap.ActionGetEvents(action).OfType<InputEventKey>().Select(KeyName).Distinct())
            : "";

    private static string KeyName(InputEventKey k)
    {
        Key code = k.Keycode;
        if (code == Key.None)
        {
            // Bound by position: name the key this keyboard layout has there.
            code = DisplayServer.GetName() == "headless" ? k.PhysicalKeycode : DisplayServer.KeyboardGetKeycodeFromPhysical(k.PhysicalKeycode);
        }
        string name = Names.TryGetValue(code, out string? shortName) ? shortName : OS.GetKeycodeString(code);
        return (k.CtrlPressed ? "Ctrl+" : "") + (k.AltPressed ? "Alt+" : "") + (k.ShiftPressed ? "Shift+" : "") + name;
    }
}
