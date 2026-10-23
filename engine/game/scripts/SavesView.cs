using System.Globalization;
using DarkSun.Game.Saves;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// DSUN.EXE's LOAD and SAVE screen (<c>7CBC:022D</c>): <c>WIND</c> 3009 over
/// <c>BMP</c> 3009 at (0, 10), its banner (2056: SAVE, or <c>ICON</c> 6030
/// LOAD), the action button (2057: SAVE, or 6031 LOAD), EXIT (2058), ten slot
/// bars (2059–2068, <c>ICON</c> 18100) and the name bar (<c>EBOX</c> 4001).
/// The original keeps ten games; here every saved game is listed, newest first,
/// ten at a time (the arrows, the wheel, Page Up and Down), each with its name
/// and when it was saved; the autosave (entering a region) and the quick save
/// (F5) are AUTOSAVE and QUICKSAVE. Saving names the game in the name bar: the
/// first slot is a new game, another replaces that one (after asking).
/// DELETE (<c>ICON</c> 2097) removes one, after asking in the original's
/// question window. The selected game's date, time, region and party are
/// written beside the list. Enter does the action (a double click too),
/// Esc or EXIT leaves. On the main menu (L) it loads; in the game the game
/// menu's LOAD/SAVE GAME opens it over the map.
/// </summary>
public partial class SavesView : RetroScreen
{
    public const string ScenePath = "res://scenes/saves.tscn";

    public enum Mode { Load, Save }

    /// <summary>Shown when the screen opens (why the last load failed).</summary>
    public static string? Message { get; set; }

    /// <summary>
    /// In the game: save as name (the first argument) to the file of that key
    /// (the second; null: a new file); null if saved, else why not. Null on the main menu.
    /// </summary>
    public Func<string, string?, string?>? SaveTo { get; set; }

    /// <summary>In the game: load this save over the game in progress. On the main menu the screen starts the map with it.</summary>
    public Action<SaveGame>? LoadSave { get; set; }

    /// <summary>The screen closed (in the game).</summary>
    public Action? Closed { get; set; }

    public bool IsOpen => Visible;

    private const int WindowNumber = 3009, WindowTop = 10;
    private const int Banner = 2056, ActionButton = 2057, ExitButton = 2058, FirstSlot = 2059, Slots = 10, NameBar = 4001;
    private const int LoadBanner = 6030, LoadIcon = 6031, SaveBanner = 2056, SaveIcon = 2057, DeleteIcon = 2097, SlotIcon = 18100;
    private const int PreviousIcon = 16105, NextIcon = 16104, SelectedFrame = 3;

    /// <summary>The text: names grey-white, the selected one and the name bar yellow, the details the status grey.</summary>
    private static readonly Color Face = StoneUi.Face, Yellow = StoneUi.Yellow, Dim = StoneUi.Dim;

    /// <summary>A line of the list: a saved game (or one that can't be read), or the new game to save as.</summary>
    private sealed record Entry(string? Path, SaveGame? Save, string? Error, string Title)
    {
        public bool IsNew => Path is null;

        public string? File => Path is null ? null : System.IO.Path.GetFileNameWithoutExtension(Path);

        public bool Special => File is RegionView.AutoSave or RegionView.QuickSave;
    }

    private StoneUi _ui = null!;
    private UiWindowView _window = null!;
    private QuestionWindow _ask = null!;
    private readonly TextureButton[] _slots = new TextureButton[Slots];
    private readonly Label[] _names = new Label[Slots];
    private TextureButton _action = null!, _delete = null!, _previous = null!, _next = null!;
    private Label _page = null!, _status = null!;
    private readonly Label[] _details = new Label[5];
    private LineEdit _name = null!;
    private Texture2D? _slotNormal, _slotSelected;
    private List<Entry> _entries = [];
    private int _selected, _top;
    private Mode _mode;
    private Action? _afterSave;
    private bool _replaceAsked;

    public override void _Ready()
    {
        base._Ready();
        if (Session.Install is null)
        {
            GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, "res://scenes/main.tscn");
            return;
        }
        _ui = StoneUi.Load();
        Build();
        if (SaveTo is not null)
        {
            // In the game: hidden until the game menu opens it, over the map.
            Visible = false;
            return;
        }
        Open(Mode.Load);
        if (Message is { } message)
        {
            _status.Text = message;
            Message = null;
        }
        if (OS.GetCmdlineUserArgs().Contains("--check-saves") || (DisplayServer.GetName() == "headless" && _status.Text.Length > 0))
        {
            if (_status.Text.Length > 0)
            {
                GD.Print($"saves: {_status.Text}");
            }
            foreach (Entry e in _entries)
            {
                GD.Print(e.Save is not null
                    ? $"save {System.IO.Path.GetFileName(e.Path)}: {e.Title} · {Describe(e.Save)}"
                    : $"save {System.IO.Path.GetFileName(e.Path)}: can't be read: {e.Error}");
            }
            GetTree().Quit();
        }
    }

    /// <summary>Open the screen to load or to save; <paramref name="afterSave"/> runs once a game is saved.</summary>
    public void Open(Mode mode, Action? afterSave = null)
    {
        _mode = mode;
        _afterSave = afterSave;
        bool saving = mode == Mode.Save;
        _window.Button(Banner)!.TextureNormal = _ui.Icon(saving ? SaveBanner : LoadBanner);
        _ui.SetIcon(_action, saving ? SaveIcon : LoadIcon);
        _name.Editable = saving;
        _name.FocusMode = saving ? FocusModeEnum.All : FocusModeEnum.None;
        _status.Text = "";
        _ask.Close();
        Reload(selectFile: null);
        Visible = true;
        if (saving)
        {
            // Unless it was closed, or opened to load, meanwhile.
            Callable.From(() =>
            {
                if (Visible && _mode == Mode.Save)
                {
                    _name.GrabFocus();
                }
            }).CallDeferred();
        }
    }

    public void Close()
    {
        _ask.Close();
        if (SaveTo is null)
        {
            GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, MainMenu.ScenePath);
            return;
        }
        _name.ReleaseFocus();
        Visible = false;
        Closed?.Invoke();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!Visible)
        {
            return;
        }
        if (e.IsActionPressed(Controls.Back))
        {
            if (_ask.Visible)
            {
                _ask.Close();
            }
            else
            {
                Close();
            }
        }
        else if (_ask.Visible)
        {
            // The question takes its own clicks; keys go nowhere else meanwhile.
        }
        else if (e.IsActionPressed("ui_up", true) || e.IsActionPressed("ui_down", true))
        {
            Select(_selected + (e.IsActionPressed("ui_up", true) ? -1 : 1));
        }
        else if (e is InputEventKey { Pressed: true, Keycode: Key.Pageup or Key.Pagedown } page)
        {
            Select(_selected + (page.Keycode == Key.Pageup ? -Slots : Slots));
        }
        else if (e.IsActionPressed("ui_accept"))
        {
            Act();
        }
        else if (e is InputEventKey { Pressed: true, Keycode: Key.Delete } && !_name.HasFocus())
        {
            Delete();
        }
        else if (e is not InputEventKey { Pressed: true } key || key.Unicode == '?')
        {
            return;
        }
        // Every other key stops here: the map under the screen gets none meanwhile (F9 would load a game under it).
        GetViewport().SetInputAsHandled();
    }

    // -- for the checks -----------------------------------------------------------------

    /// <summary>The list's lines as shown.</summary>
    public IReadOnlyList<string> Titles => [.. _entries.Select(e => e.Title)];

    /// <summary>Type <paramref name="name"/> into the name bar.</summary>
    public void Type(string name) => _name.Text = name;

    /// <summary>Select the line titled <paramref name="title"/>; false if there is none.</summary>
    public bool SelectTitle(string title)
    {
        int at = _entries.FindIndex(e => e.Title == title);
        if (at >= 0)
        {
            Select(at);
        }
        return at >= 0;
    }

    /// <summary>Press the window's button <paramref name="number"/> (2057 SAVE or LOAD, 2058 EXIT).</summary>
    public void Press(int number) => _window.Button(number)?.EmitSignal(BaseButton.SignalName.Pressed);

    // -- building -----------------------------------------------------------------------

    private void Build()
    {
        _window = UiWindowView.Build(WindowNumber, _ui.Palette)!;
        _window.Position = new Vector2(0, WindowTop);
        Canvas.AddChild(_window);
        _window.Button(Banner)!.MouseFilter = MouseFilterEnum.Ignore;
        _slotNormal = _ui.Icon(SlotIcon);
        _slotSelected = _ui.Icon(SlotIcon, SelectedFrame);
        for (int i = 0; i < Slots; i++)
        {
            int slot = i;
            TextureButton bar = _window.Button(FirstSlot + i)!;
            _slots[i] = bar;
            _names[i] = StoneUi.Text(bar, new Vector2(3, -1), bar.Size.X - 6, Face);
            bar.Pressed += () => Select(_top + slot);
            bar.GuiInput += e => SlotInput(slot, e);
        }
        _action = _window.Button(ActionButton)!;
        _action.Pressed += Act;
        _window.Button(ExitButton)!.Pressed += Close;

        // Beside the original's two buttons: DELETE, the list's pages, the selected game's details.
        _delete = Add(_ui.Button(DeleteIcon, new Vector2(216, 70)));
        _delete.Pressed += Delete;
        _previous = Add(_ui.Button(PreviousIcon, new Vector2(220, 91)));
        _previous.Pressed += () => Scroll(-Slots);
        _next = Add(_ui.Button(NextIcon, new Vector2(277, 91)));
        _next.Pressed += () => Scroll(Slots);
        _page = StoneUi.Text(_window, new Vector2(229, 90), 48, Dim, HorizontalAlignment.Center);
        for (int i = 0; i < _details.Length; i++)
        {
            _details[i] = StoneUi.Text(_window, new Vector2(214, 104 + 8 * i), 66, Dim);
        }

        // The name bar: the selected game's name; typed into when saving.
        Rect2 bar2 = _window.RectOf("EBOX", NameBar);
        if (_window.TextBox(NameBar) is { } original)
        {
            original.Visible = false;
        }
        _name = new LineEdit
        {
            Position = bar2.Position + new Vector2(2, -2),
            Size = new Vector2(bar2.Size.X - 4, bar2.Size.Y + 2),
            MaxLength = 40,
            ContextMenuEnabled = false,
            Flat = true,
            CaretBlink = true,
            SelectAllOnFocus = true,
            PlaceholderText = "",
        };
        GameFonts.Use(_name, Yellow);
        var none = new StyleBoxEmpty();
        foreach (string style in new[] { "normal", "focus", "read_only" })
        {
            _name.AddThemeStyleboxOverride(style, none);
        }
        _name.AddThemeColorOverride("font_uneditable_color", Colors.White);
        _name.AddThemeColorOverride("caret_color", Yellow);
        _name.AddThemeColorOverride("selection_color", new Color(Dim, 0.6f));
        _name.TextSubmitted += _ => Act();
        _name.TextChanged += _ => _replaceAsked = false;
        _window.AddChild(_name);
        _name.Size = new Vector2(bar2.Size.X - 4, GameFonts.Size + 2); // once in the tree, where the game font's smaller size counts

        _status = StoneUi.Text(Canvas, new Vector2(4, WindowTop + 180), Width - 8, Dim, HorizontalAlignment.Center);
        _ask = new QuestionWindow(Canvas, _ui.Palette);
    }

    /// <summary>Put a button in the window.</summary>
    private TextureButton Add(TextureButton button)
    {
        _window.AddChild(button);
        return button;
    }

    // -- the list -----------------------------------------------------------------------

    /// <summary>Read the saves folder again, selecting the game saved in <paramref name="selectFile"/> (else the first line).</summary>
    private void Reload(string? selectFile)
    {
        bool saving = _mode == Mode.Save;
        _entries = [];
        if (saving)
        {
            _entries.Add(new Entry(null, null, null, "New saved game"));
        }
        foreach (var (path, save, error) in Session.ListSaves())
        {
            var entry = new Entry(path, save, error, "");
            entry = entry with { Title = TitleOf(entry) };
            // Saving: only the named games can be replaced; the autosave and quick save keep their own.
            if (!saving || (save is not null && !entry.Special))
            {
                _entries.Add(entry);
            }
        }
        int at = selectFile is null ? -1 : _entries.FindIndex(e => e.File == selectFile);
        _top = 0;
        Select(Math.Max(0, at));
    }

    /// <summary>
    /// A game's name in the list: AUTOSAVE and QUICKSAVE, else the name it was
    /// saved as; one the game named itself (the region and the time, as games
    /// were saved before they had names) by when it was saved.
    /// </summary>
    private static string TitleOf(Entry e) => e.File switch
    {
        RegionView.AutoSave => "AUTOSAVE",
        RegionView.QuickSave => "QUICKSAVE",
        _ when e.Save is { } s && (s.Name.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(s.Name, @"^\d+: ")) => ShortDate(s.Saved),
        _ => e.Save?.Name ?? e.File ?? "",
    };

    private void Select(int index)
    {
        _selected = Math.Clamp(index, 0, Math.Max(0, _entries.Count - 1));
        if (_selected < _top)
        {
            _top = _selected;
        }
        else if (_selected >= _top + Slots)
        {
            _top = _selected - Slots + 1;
        }
        _replaceAsked = false;
        if (_mode == Mode.Save)
        {
            _name.Text = Selected is { IsNew: false } e ? e.Title : DefaultName();
            if (_name.HasFocus())
            {
                _name.SelectAll();
            }
        }
        Refresh();
    }

    private void Scroll(int by)
    {
        int last = Math.Max(0, _entries.Count - Slots);
        _top = Math.Clamp(_top + by, 0, last);
        if (_selected < _top || _selected >= _top + Slots)
        {
            Select(Math.Clamp(_selected, _top, _top + Slots - 1));
            return;
        }
        Refresh();
    }

    private Entry? Selected => _selected < _entries.Count ? _entries[_selected] : null;

    private void Refresh()
    {
        bool saving = _mode == Mode.Save;
        for (int i = 0; i < Slots; i++)
        {
            Entry? e = _top + i < _entries.Count ? _entries[_top + i] : null;
            bool selected = e is not null && _top + i == _selected;
            TextureButton bar = _slots[i];
            bar.TextureNormal = selected ? _slotSelected : _slotNormal;
            // As the original's load screen: an empty slot can't be chosen.
            bar.Disabled = e is null;
            _names[i].Text = e?.Title ?? "";
            _names[i].LabelSettings = GameFonts.Settings(selected ? Yellow : e is { Save: null, IsNew: false } ? Dim : Face);
        }
        Entry? chosen = Selected;
        _action.Disabled = chosen is null || (!saving && chosen.Save is null);
        _delete.Disabled = chosen is null || chosen.IsNew;
        int pages = Math.Max(1, (_entries.Count + Slots - 1) / Slots);
        _page.Text = pages > 1 ? $"{_top / Slots + 1}/{pages}" : "";
        _previous.Visible = _next.Visible = pages > 1;
        _previous.Disabled = _top == 0;
        _next.Disabled = _top + Slots >= _entries.Count;
        string[] lines = chosen switch
        {
            { Save: { } s } => DetailsOf(s),
            { IsNew: true } => ["Type a name,", "then SAVE."],
            { Error: { } } => ["Can't be read."],
            _ => saving ? [] : ["No saved", "games yet."],
        };
        for (int i = 0; i < _details.Length; i++)
        {
            _details[i].Text = i < lines.Length ? lines[i] : "";
        }
        if (!saving)
        {
            _name.Text = chosen?.Title ?? "";
        }
    }

    private void SlotInput(int slot, InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel:
                Scroll(wheel.ButtonIndex == MouseButton.WheelUp ? -1 : 1);
                break;
            case InputEventMouseButton { Pressed: true, DoubleClick: true, ButtonIndex: MouseButton.Left } when _top + slot < _entries.Count:
                Select(_top + slot);
                Act();
                break;
            default:
                return;
        }
        _slots[slot].AcceptEvent();
    }

    /// <summary>When it was saved, short: "Oct 22 22:59" this year, else "Oct 22 2025".</summary>
    private static string ShortDate(DateTimeOffset saved)
    {
        DateTime local = saved.LocalDateTime;
        return local.ToString(local.Year == DateTime.Now.Year ? "MMM d HH:mm" : "MMM d yyyy", CultureInfo.InvariantCulture);
    }

    private static string[] DetailsOf(SaveGame s)
    {
        DateTime local = s.Saved.LocalDateTime;
        var lines = new List<string>
        {
            local.ToString("MMM d yyyy", CultureInfo.InvariantCulture),
            local.ToString("HH:mm", CultureInfo.InvariantCulture),
            $"Region {s.Region}",
            s.Party.Count == 1 ? "1 in party" : $"{s.Party.Count} in party",
        };
        if (s.Mods.Count > 0)
        {
            lines.Add(s.Mods.Count == 1 ? "1 mod" : $"{s.Mods.Count} mods");
        }
        return [.. lines];
    }

    private static string Describe(SaveGame save) =>
        $"region {save.Region} · {save.Saved.LocalDateTime:g} · {save.Party.Count} characters"
        + (save.Mods.Count > 0 ? $" · mods: {string.Join(", ", save.Mods)}" : "");

    /// <summary>A name for a new game: SAVE 1, SAVE 2, …, the first no game has.</summary>
    private string DefaultName()
    {
        var taken = _entries.Select(e => e.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int n = 1;
        while (taken.Contains($"Save {n}"))
        {
            n++;
        }
        return $"Save {n}";
    }

    // -- the buttons --------------------------------------------------------------------

    /// <summary>LOAD the selected game, or SAVE as the name bar says (asking first before replacing one).</summary>
    private void Act()
    {
        if (Selected is not { } e || _ask.Visible)
        {
            return;
        }
        if (_mode == Mode.Load)
        {
            if (e.Save is not { } save)
            {
                _status.Text = e.Error is { } why ? $"Can't load it: {why}" : "";
                return;
            }
            _status.Text = "LOADING GAME";
            if (LoadSave is { } load)
            {
                Close();
                load(save);
            }
            else
            {
                RegionView.PendingLoad = save;
                GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, RegionView.ScenePath);
            }
            return;
        }
        string name = _name.Text.Trim();
        if (name.Length == 0)
        {
            name = DefaultName();
        }
        if (!e.IsNew && !_replaceAsked)
        {
            _ask.Ask("REPLACE SAVE?", null, ("REPLACE", () =>
            {
                _replaceAsked = true;
                Act();
            }), ("CANCEL", null));
            return;
        }
        if (SaveTo?.Invoke(name, e.File) is { } refused)
        {
            _status.Text = refused.Length > 0 ? refused : "Couldn't save the game.";
            return;
        }
        Action? then = _afterSave;
        Close();
        then?.Invoke();
    }

    private void Delete()
    {
        if (Selected is not { IsNew: false, Path: { } path } e || _ask.Visible)
        {
            return;
        }
        _ask.Ask("DELETE SAVE?", null, ("DELETE", () =>
        {
            try
            {
                File.Delete(path);
                _status.Text = $"Deleted {e.Title}.";
            }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException)
            {
                _status.Text = $"Couldn't delete it: {x.Message}";
            }
            int at = _selected;
            Reload(selectFile: null);
            Select(Math.Min(at, _entries.Count - 1));
        }), ("CANCEL", null));
    }
}
