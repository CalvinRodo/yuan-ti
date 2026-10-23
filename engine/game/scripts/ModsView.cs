using DarkSun.Data.Mods;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// The mod list, from the main menu (M), in the stone of DSUN.EXE's LOAD and
/// SAVE window (<c>WIND</c> 3009 over <c>BMP</c> 3009; the original has no
/// such screen): a MODS banner, every mod found in its slot bars in priority
/// order (highest first), four at a time, each by its friendly name
/// (<see cref="Mod.DisplayName"/>) and ON or OFF; under them the selected
/// mod's details: name, version and author, the folder it is in, its
/// description over as many lines as it takes (the wheel or Page Up and Down
/// scroll it), what it replaces and any file that couldn't be used. Down the
/// right: TURN ON / TURN OFF (renames its folder with a leading <c>_</c>; a
/// mod that comes with the game, such as the tabletop rules, is a setting
/// instead), RAISE and LOWER its priority (writes <c>load_order.txt</c>; only
/// among mods in the same folder), FOLDER (opens the mods folder), RELOAD
/// and EXIT. The name bar along the bottom says what the button under the
/// pointer does. Changes apply at once: the mods are loaded again (every
/// changed resource is built when the screen opens). Up and Down choose a
/// mod, Space or Enter turns it on or off, Esc goes back. Run with
/// <c>-- --check-mods</c> to print the list and its problems and quit (exit
/// code 1 if there are problems).
/// </summary>
public partial class ModsView : RetroScreen
{
    public const string ScenePath = "res://scenes/mods.tscn";

    private const int WindowNumber = 3009, WindowTop = 10, BannerButton = 2056, ExitButton = 2058, FirstSlot = 2059, Slots = 4, AllSlots = 10;
    private const int NameBar = 4001, SlotIcon = 18100, SelectedFrame = 3, PreviousIcon = 16105, NextIcon = 16104;
    private const int ButtonX = 216, ButtonWidth = 62;

    /// <summary>The details' place: the panel under the four slot bars.</summary>
    private static readonly Rect2 DetailsArea = new(48, 77, 158, 64);

    private const string Hint = "Highest priority first. Changes apply at once.";

    private StoneUi _ui = null!;
    private UiWindowView _window = null!;
    private readonly TextureButton[] _slots = new TextureButton[Slots];
    private readonly Label[] _names = new Label[Slots], _states = new Label[Slots];
    private TextureButton _toggle = null!, _raise = null!, _lower = null!, _previous = null!, _next = null!, _up = null!, _down = null!;
    private Label _page = null!, _state = null!, _status = null!;
    private Control _detailsClip = null!;
    private VBoxContainer _details = null!;
    private Texture2D? _slotNormal, _slotSelected;
    private string? _selected;
    private int _top;
    private string _said = "";

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
        IReadOnlyList<ModProblem> problems = Verify();
        if (OS.GetCmdlineUserArgs().Contains("--check-mods"))
        {
            ModSet mods = Session.Mods;
            GD.Print($"mods: {mods.All.Count} found in {string.Join(", ", mods.Roots)}; {mods.Mods.Count} on, {mods.Count} resources");
            var counts = mods.CountsByMod();
            foreach (Mod mod in mods.All)
            {
                GD.Print($"  {(mod.Enabled ? "on " : "off")} {mod.DisplayName} ({mod.Name}): {counts.GetValueOrDefault(mod.Name)} resources");
            }
            foreach (ModProblem p in problems)
            {
                GD.Print($"  problem: {p}");
            }
            bool rulesOk = CheckTabletopRules();
            GetTree().Quit(problems.Count > 0 || !rulesOk ? 1 : 0);
            return;
        }
        _selected = Session.Mods.All.FirstOrDefault()?.Name;
        Refresh();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        var all = Session.Mods.All;
        int at = all.ToList().FindIndex(m => m.Name == _selected);
        if (e.IsActionPressed(Controls.Back))
        {
            Back();
        }
        else if ((e.IsActionPressed("ui_up", true) || e.IsActionPressed("ui_down", true)) && all.Count > 0)
        {
            Select(all[Math.Clamp(at + (e.IsActionPressed("ui_up", true) ? -1 : 1), 0, all.Count - 1)].Name);
        }
        else if (e is InputEventKey { Pressed: true, Keycode: Key.Pageup or Key.Pagedown } page)
        {
            ScrollDetails(page.Keycode == Key.Pageup ? -DetailsArea.Size.Y + 9 : DetailsArea.Size.Y - 9);
        }
        else if (e.IsActionPressed("ui_accept") || e is InputEventKey { Pressed: true, Keycode: Key.Space })
        {
            Toggle();
        }
        else
        {
            return;
        }
        GetViewport().SetInputAsHandled();
    }

    /// <summary>
    /// The tabletop rules come with the game, off: turned on (for this run, not
    /// saved), the game plays by them (a gladiator needs 4500 for level 3), and
    /// off again by DSUN.EXE's (4000).
    /// </summary>
    private static bool CheckTabletopRules()
    {
        const string Name = "Tabletop";
        if (Session.Mods.All.FirstOrDefault(m => m.Name == Name) is not { Bundled: true } tabletop)
        {
            GD.Print("  problem: the tabletop rules mod isn't there");
            return false;
        }
        bool wasOn = tabletop.Enabled;
        int Gladiator3() => Session.Rules.Class(DarkSun.Game.Characters.CharacterClass.Gladiator).Levels[2];
        Settings.SetBundledMod(Name, false);
        Session.ReloadMods();
        int original = Gladiator3();
        Settings.SetBundledMod(Name, true);
        Session.ReloadMods();
        int tabletopRules = Gladiator3();
        Settings.SetBundledMod(Name, wasOn);
        Session.ReloadMods();
        bool ok = (original, tabletopRules) == (4000, 4500);
        GD.Print($"  tabletop rules: {(wasOn ? "on" : "off")}; a gladiator's level 3 at {original} without them, {tabletopRules} with them{(ok ? "" : " (problem)")}");
        return ok;
    }

    private static IReadOnlyList<ModProblem> Verify() =>
        Session.Mods.Count > 0 ? Session.VerifyMods() : Session.Mods.Problems;

    private void Back() => GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, MainMenu.ScenePath);

    // -- building -----------------------------------------------------------------------

    private void Build()
    {
        _window = UiWindowView.Build(WindowNumber, _ui.Palette)!;
        _window.Position = new Vector2(0, WindowTop);
        Canvas.AddChild(_window);
        Rect2 bannerAt = _window.RectOf("BUTN", BannerButton);
        _window.Button(BannerButton)!.Visible = false;
        _window.Button(2057)!.Visible = false;
        _window.AddChild(_ui.Banner("MODS", new Vector2(bannerAt.Position.X, bannerAt.Position.Y)));

        // The first four slot bars list the mods; the panel under them holds the details.
        _slotNormal = _ui.Icon(SlotIcon);
        _slotSelected = _ui.Icon(SlotIcon, SelectedFrame);
        for (int i = 0; i < AllSlots; i++)
        {
            TextureButton bar = _window.Button(FirstSlot + i)!;
            if (i >= Slots)
            {
                bar.Visible = false;
                continue;
            }
            int slot = i;
            _slots[i] = bar;
            _names[i] = StoneUi.Text(bar, new Vector2(3, -1), bar.Size.X - 34, StoneUi.Face);
            _states[i] = StoneUi.Text(bar, new Vector2(bar.Size.X - 30, -1), 27, StoneUi.Face, HorizontalAlignment.Right);
            bar.Pressed += () =>
            {
                if (_top + slot < Session.Mods.All.Count)
                {
                    Select(Session.Mods.All[_top + slot].Name);
                }
            };
            bar.GuiInput += e =>
            {
                if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel)
                {
                    ScrollList(wheel.ButtonIndex == MouseButton.WheelUp ? -1 : 1);
                    bar.AcceptEvent();
                }
            };
        }
        _detailsClip = new Control { Position = DetailsArea.Position, Size = DetailsArea.Size, ClipContents = true, MouseFilter = MouseFilterEnum.Stop };
        _details = new VBoxContainer { Size = new Vector2(DetailsArea.Size.X, 0), MouseFilter = MouseFilterEnum.Ignore };
        _details.AddThemeConstantOverride("separation", 0);
        _detailsClip.AddChild(_details);
        _detailsClip.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel)
            {
                ScrollDetails(wheel.ButtonIndex == MouseButton.WheelUp ? -9 : 9);
                _detailsClip.AcceptEvent();
            }
        };
        _window.AddChild(_detailsClip);
        // Arrows at the details' right when there is more to read.
        _up = Add(_ui.UpDownArrow(up: true, DetailsArea.Position + new Vector2(DetailsArea.Size.X - 9, 0)));
        _up.Pressed += () => ScrollDetails(-DetailsArea.Size.Y + 9);
        _down = Add(_ui.UpDownArrow(up: false, DetailsArea.End - new Vector2(9, 9)));
        _down.Pressed += () => ScrollDetails(DetailsArea.Size.Y - 9);

        // Down the right: the buttons, the list's pages, EXIT.
        _toggle = Button("TURN OFF", 31, "Turn the mod on or off (Space)", Toggle);
        _raise = Button("RAISE", 45, "Higher priority: its files win", () => MoveSelected(-1));
        _lower = Button("LOWER", 59, "Lower priority: the ones above win", () => MoveSelected(+1));
        Button("FOLDER", 73, "Open the mods folder", OpenFolder);
        Button("RELOAD", 87, "Look in the mods folders again", Reload);
        _previous = Add(_ui.Button(PreviousIcon, new Vector2(220, 105)));
        _previous.Pressed += () => ScrollList(-Slots);
        _next = Add(_ui.Button(NextIcon, new Vector2(271, 105)));
        _next.Pressed += () => ScrollList(Slots);
        _page = StoneUi.Text(_window, new Vector2(229, 104), 42, StoneUi.Dim, HorizontalAlignment.Center);
        TextureButton exit = _window.Button(ExitButton)!;
        exit.Position = new Vector2(ButtonX + (ButtonWidth - exit.Size.X) / 2, 124);
        exit.Pressed += Back;
        Explain(exit, "Back to the main menu (Esc)");

        // The name bar: the selected mod's state. Under the window: what the button under the pointer does, or what just happened.
        Rect2 bar2 = _window.RectOf("EBOX", NameBar);
        if (_window.TextBox(NameBar) is { } original)
        {
            original.Visible = false;
        }
        _state = StoneUi.Text(_window, bar2.Position + new Vector2(2, 0), bar2.Size.X - 4, StoneUi.Face);
        _status = StoneUi.Text(Canvas, new Vector2(4, WindowTop + 180), Width - 8, StoneUi.Dim, HorizontalAlignment.Center);
        Say(Hint);
    }

    private TextureButton Add(TextureButton button)
    {
        _window.AddChild(button);
        return button;
    }

    private TextureButton Button(string text, int y, string help, Action pressed)
    {
        TextureButton button = Add(_ui.TextButton(text, new Vector2(ButtonX, y), ButtonWidth));
        button.Pressed += pressed;
        Explain(button, help);
        return button;
    }

    private void Explain(Control control, string help)
    {
        control.MouseEntered += () => _status.Text = help;
        control.MouseExited += () => _status.Text = _said;
    }

    /// <summary>Say <paramref name="text"/> in the name bar until the next.</summary>
    private void Say(string text)
    {
        _said = text.Length > 0 ? text : Hint;
        _status.Text = _said;
    }

    // -- the list and the details --------------------------------------------------------

    private void Select(string name)
    {
        _selected = name;
        int at = Session.Mods.All.ToList().FindIndex(m => m.Name == name);
        if (at >= 0 && at < _top)
        {
            _top = at;
        }
        else if (at >= _top + Slots)
        {
            _top = at - Slots + 1;
        }
        Refresh();
    }

    private void ScrollList(int by)
    {
        _top = Math.Clamp(_top + by, 0, Math.Max(0, Session.Mods.All.Count - Slots));
        Refresh();
    }

    private void ScrollDetails(float by)
    {
        float most = Math.Max(0, _details.GetCombinedMinimumSize().Y - DetailsArea.Size.Y);
        float at = Math.Clamp(-_details.Position.Y + by, 0, most);
        _details.Position = new Vector2(0, -at);
        _up.Visible = at > 0;
        _down.Visible = at < most;
    }

    private void Refresh()
    {
        ModSet mods = Session.Mods;
        var problems = mods.Problems;
        _top = Math.Clamp(_top, 0, Math.Max(0, mods.All.Count - Slots));
        for (int i = 0; i < Slots; i++)
        {
            Mod? mod = _top + i < mods.All.Count ? mods.All[_top + i] : null;
            bool selected = mod is not null && mod.Name == _selected;
            _slots[i].TextureNormal = selected ? _slotSelected : _slotNormal;
            _slots[i].Disabled = mod is null;
            _names[i].Text = mod is null ? "" : mod.DisplayName + (problems.Any(p => p.Mod.Name == mod.Name) ? " !" : "");
            _names[i].LabelSettings = GameFonts.Settings(selected ? StoneUi.Yellow : mod is { Enabled: true } ? StoneUi.Face : StoneUi.Dim);
            _states[i].Text = mod is null ? "" : mod.Enabled ? "ON" : "OFF";
            _states[i].LabelSettings = GameFonts.Settings(mod is { Enabled: true } ? StoneUi.Face : StoneUi.Dim);
        }
        int pages = Math.Max(1, (mods.All.Count + Slots - 1) / Slots);
        _page.Text = pages > 1 ? $"{_top / Slots + 1}/{pages}" : "";
        _previous.Visible = _next.Visible = pages > 1;
        _previous.Disabled = _top == 0;
        _next.Disabled = _top + Slots >= mods.All.Count;

        Mod? chosen = mods.All.FirstOrDefault(m => m.Name == _selected);
        StoneUi.SetText(_toggle, chosen is { Enabled: true } ? "TURN OFF" : "TURN ON", chosen is null);
        var sameFolder = chosen is null ? [] : mods.All.Where(m => m.Root == chosen.Root).ToList();
        int at = chosen is null ? -1 : sameFolder.IndexOf(chosen);
        StoneUi.SetText(_raise, "RAISE", chosen is null || chosen.Bundled || at <= 0);
        StoneUi.SetText(_lower, "LOWER", chosen is null || chosen.Bundled || at < 0 || at == sameFolder.Count - 1);
        var counts = mods.CountsByMod();
        int broken = chosen is null ? 0 : problems.Count(p => p.Mod.Name == chosen.Name);
        _state.Text = chosen is null ? ""
            : broken > 0 ? $"{broken} file(s) can't be used"
            : chosen.Enabled ? (counts.GetValueOrDefault(chosen.Name) is int n and > 0 ? $"On: changes {n} things" : "On")
            : "Off";
        _state.LabelSettings = GameFonts.Settings(broken > 0 ? StoneUi.Red : chosen is { Enabled: true } ? StoneUi.Face : StoneUi.Dim);
        ShowDetails(chosen, problems);
        // Laid out on the next frame: then the arrows know whether there is more.
        Callable.From(() => ScrollDetails(0)).CallDeferred();
    }

    private void ShowDetails(Mod? mod, IReadOnlyList<ModProblem> problems)
    {
        foreach (Node child in _details.GetChildren())
        {
            _details.RemoveChild(child);
            child.QueueFree();
        }
        _details.Position = Vector2.Zero;
        if (mod is null)
        {
            Line("No mods yet.", StoneUi.Yellow);
            Line("A mod is a folder in one of these:", StoneUi.Face);
            foreach (string folder in Session.ModFolders(Session.Install!).OfType<string>())
            {
                Line(folder, StoneUi.Dim, path: true);
            }
            Line("Inside it, files named after what they replace: ICON/2048.png, TEXT/12.txt, RDFF/87.json. "
                + "The engine README (Mods) says more.", StoneUi.Face);
            return;
        }
        Line(mod.DisplayName + (mod.Version.Length > 0 ? $" {mod.Version}" : "") + (mod.Author.Length > 0 ? $", by {mod.Author}" : ""), StoneUi.Yellow);
        foreach (string paragraph in mod.Description.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Line(paragraph, StoneUi.Face);
        }
        Line(mod.Bundled ? "Comes with the game." : $"Folder: {Shortened(mod.Directory)}", StoneUi.Dim, path: !mod.Bundled);
        string rules = Path.Combine(mod.Directory, "rules");
        if (Directory.Exists(rules) && Directory.GetFiles(rules, "*.json") is { Length: > 0 } files)
        {
            Line($"Rules: {string.Join(", ", files.Select(Path.GetFileNameWithoutExtension).Order())} (from a new game).", StoneUi.Dim);
        }
        if (mod.InfoError is { } infoError)
        {
            Line(infoError, StoneUi.Red);
        }
        var mine = problems.Where(p => p.Mod.Name == mod.Name).ToList();
        if (mine.Count > 0)
        {
            Line("", StoneUi.Face);
            Line($"{mine.Count} file(s) can't be used; the game keeps the originals:", StoneUi.Red);
            foreach (ModProblem p in mine)
            {
                Line($"{Path.GetRelativePath(mod.Directory, p.Path)}: {p.Message}", StoneUi.Red, path: true);
            }
        }
    }

    /// <summary><paramref name="path"/> with the home folder as ~.</summary>
    private static string Shortened(string path)
    {
        string home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        return home.Length > 0 && path.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? "~" + path[home.Length..]
            : path;
    }

    /// <summary>A line of the details, wrapped to their width (a path anywhere, other text between words).</summary>
    private void Line(string text, Color colour, bool path = false) => _details.AddChild(new Label
    {
        Text = text.Length > 0 ? text : " ",
        CustomMinimumSize = new Vector2(DetailsArea.Size.X - 14, 0), // the arrows' column on the right
        AutowrapMode = path ? TextServer.AutowrapMode.Arbitrary : TextServer.AutowrapMode.WordSmart,
        MouseFilter = MouseFilterEnum.Ignore,
        LabelSettings = GameFonts.Settings(colour),
    });

    // -- the buttons --------------------------------------------------------------------

    private void Toggle()
    {
        if (Session.Mods.All.FirstOrDefault(m => m.Name == _selected) is not { } mod)
        {
            return;
        }
        bool enabled = !mod.Enabled;
        bool changed = Change(() =>
        {
            if (mod.Bundled)
            {
                Settings.SetBundledMod(mod.Name, enabled);
                Settings.Save();
            }
            else
            {
                ModManager.SetEnabled(mod, enabled);
            }
        }, mod.Name);
        if (changed)
        {
            Say($"{mod.DisplayName} is {(enabled ? "on" : "off")}.");
        }
    }

    private void MoveSelected(int by)
    {
        if (Session.Mods.All.FirstOrDefault(m => m.Name == _selected) is not { Bundled: false } mod)
        {
            return;
        }
        var sameFolder = Session.Mods.All.Where(m => m.Root == mod.Root).ToList();
        int at = sameFolder.IndexOf(mod);
        if (at + by >= 0 && at + by < sameFolder.Count && Move(sameFolder, at, by))
        {
            Say($"{mod.DisplayName} moved {(by < 0 ? "up" : "down")}.");
        }
    }

    private void Reload()
    {
        Session.ReloadMods();
        int n = Verify().Count;
        Refresh();
        Say(n == 0 ? "Reloaded; every mod file is usable." : $"Reloaded; {n} file(s) can't be used.");
    }

    private bool Move(List<Mod> sameFolder, int at, int by)
    {
        var names = sameFolder.Select(m => m.Name).ToList();
        (names[at], names[at + by]) = (names[at + by], names[at]);
        return Change(() => ModManager.SaveLoadOrder(sameFolder[0].Root, names), names[at + by]);
    }

    /// <summary>Make a change on disk, then load the mods again; false (saying why) if it couldn't be made.</summary>
    private bool Change(Action change, string? select)
    {
        bool changed = true;
        try
        {
            change();
            Say("");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say(e.Message);
            changed = false;
        }
        Session.ReloadMods();
        Verify();
        _selected = select ?? _selected;
        Callable.From(Refresh).CallDeferred();
        return changed;
    }

    private void OpenFolder()
    {
        string folder = ProjectSettings.GlobalizePath("user://mods");
        Directory.CreateDirectory(folder);
        OS.ShellOpen(folder);
        Say($"Opened {folder}");
    }
}
