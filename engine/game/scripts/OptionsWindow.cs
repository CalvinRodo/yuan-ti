using Godot;

namespace DarkSun.Engine;

/// <summary>
/// The options (O, on the main menu and the map; the original has no such
/// screen), in the stone of DSUN.EXE's LOAD and SAVE window (<c>WIND</c> 3009,
/// as the mods screen): an OPTIONS banner and the ten slot bars, each an
/// option and its value, ten to a page. GAME holds music and sound volume, full
/// screen, the opening, autosave, the map's information lines, pathfinding,
/// the script repairs, shadows, dust, enemy rings, box selection and the
/// enemy keys; KEYS every key that can be rebound: click one, then press the
/// new key (Esc keeps the old one), DEFAULTS puts them all back. A click on a
/// bar changes its value (a right-click goes back a step); Up and Down choose a
/// bar, Left and Right change it, Enter clicks it, Tab turns to the other
/// page. The name bar says how the bars work; the line under the window, what
/// the bar under the pointer does. Changes apply at once and
/// are saved (<see cref="Settings"/>, <c>user://settings.cfg</c>) when the
/// window closes (EXIT, O or Esc).
/// </summary>
public partial class OptionsWindow : RetroScreen
{
    private const int WindowNumber = 3009, WindowTop = 10, BannerButton = 2056, ExitButton = 2058, FirstSlot = 2059, Slots = 10;
    private const int NameBar = 4001, SlotIcon = 18100, SelectedFrame = 3, PreviousIcon = 16105, NextIcon = 16104;
    private const int ButtonX = 216, ButtonWidth = 62;

    /// <summary>An option: its name on the bar, its value now, a step up (+1) or down (−1), and what it does.</summary>
    private sealed record Row(string Name, Func<string> Value, Action<int> Change, string Help);

    private StoneUi _ui = null!;
    private UiWindowView _window = null!;
    private readonly TextureButton[] _slots = new TextureButton[Slots];
    private readonly Label[] _names = new Label[Slots], _values = new Label[Slots];
    private TextureButton _game = null!, _keysButton = null!, _defaults = null!, _previous = null!, _next = null!;
    private Label _page = null!, _about = null!, _status = null!;
    private Texture2D? _slotNormal, _slotSelected;
    private List<Row> _rows = [];
    private bool _keysPage;
    private int _top, _chosen = -1, _hovered = -1;
    private string? _waitingFor;
    private string _said = "";

    /// <summary>The window closed (the options are saved): the screen updates what depends on them.</summary>
    public Action? Closed { get; set; }

    /// <summary>What the bottom line says now (for the checks).</summary>
    public string Status => _status.Text;

    public override void _Ready()
    {
        base._Ready();
        Visible = false;
        var backdrop = (ColorRect)GetChild(0);
        backdrop.Color = new Color(0, 0, 0, 0.55f); // the screen behind shows through, and takes no clicks
        backdrop.MouseFilter = MouseFilterEnum.Stop;
        _ui = StoneUi.Load();
        Build();
    }

    public void Open()
    {
        Visible = true;
        _waitingFor = null;
        ShowPage(keys: false);
        Say("");
    }

    public void Close()
    {
        if (!Visible)
        {
            return;
        }
        _waitingFor = null;
        Visible = false;
        if (!Settings.Save())
        {
            GD.PushWarning("The options couldn't be saved.");
        }
        Closed?.Invoke();
    }

    // -- building -----------------------------------------------------------------------

    private void Build()
    {
        _window = UiWindowView.Build(WindowNumber, _ui.Palette)!;
        _window.Position = new Vector2(0, WindowTop);
        Canvas.AddChild(_window);
        Rect2 bannerAt = _window.RectOf("BUTN", BannerButton);
        _window.Button(BannerButton)!.Visible = false;
        _window.Button(2057)!.Visible = false;
        _window.AddChild(_ui.Banner("OPTIONS", bannerAt.Position));

        _slotNormal = _ui.Icon(SlotIcon);
        _slotSelected = _ui.Icon(SlotIcon, SelectedFrame);
        for (int i = 0; i < Slots; i++)
        {
            TextureButton bar = _window.Button(FirstSlot + i)!;
            int slot = i;
            _slots[i] = bar;
            _names[i] = StoneUi.Text(bar, new Vector2(3, -1), bar.Size.X - 6, StoneUi.Face);
            _values[i] = StoneUi.Text(bar, new Vector2(3, -1), bar.Size.X - 6, StoneUi.Face, HorizontalAlignment.Right);
            bar.Pressed += () => Use(_top + slot, +1);
            bar.GuiInput += e =>
            {
                switch (e)
                {
                    case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }:
                        Use(_top + slot, -1);
                        bar.AcceptEvent();
                        break;
                    case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel:
                        Turn(wheel.ButtonIndex == MouseButton.WheelUp ? -1 : 1);
                        bar.AcceptEvent();
                        break;
                }
            };
            bar.MouseEntered += () => Hover(_top + slot);
            bar.MouseExited += () => Hover(-1);
        }

        // Down the right: the pages of options, the default keys, the list's pages, EXIT.
        _game = Button("GAME", 31, "Sound, display and play", () => ShowPage(keys: false));
        _keysButton = Button("KEYS", 45, "Change the keys", () => ShowPage(keys: true));
        _defaults = Button("DEFAULTS", 59, "Every key back to its default", () =>
        {
            Controls.ResetKeys();
            _waitingFor = null;
            Refresh();
            Say("Every key is back to its default.");
        });
        _previous = Add(_ui.Button(PreviousIcon, new Vector2(220, 105)));
        _previous.Pressed += () => Turn(-1);
        _next = Add(_ui.Button(NextIcon, new Vector2(271, 105)));
        _next.Pressed += () => Turn(+1);
        _page = StoneUi.Text(_window, new Vector2(229, 104), 42, StoneUi.Dim, HorizontalAlignment.Center);
        TextureButton exit = _window.Button(ExitButton)!;
        exit.Position = new Vector2(ButtonX + (ButtonWidth - exit.Size.X) / 2, 124);
        exit.Pressed += Close;
        Explain(exit, "Save the options and close (O or Esc)");

        // The name bar: how the bars work. Under the window: what the bar under the pointer does, or what just happened.
        Rect2 nameBar = _window.RectOf("EBOX", NameBar);
        if (_window.TextBox(NameBar) is { } original)
        {
            original.Visible = false;
        }
        _about = StoneUi.Text(_window, nameBar.Position + new Vector2(2, 0), nameBar.Size.X - 4, StoneUi.Face);
        _status = StoneUi.Text(Canvas, new Vector2(4, WindowTop + 180), Width - 8, StoneUi.Dim, HorizontalAlignment.Center);
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

    // -- the options ------------------------------------------------------------------------

    private static string OnOff(bool on) => on ? "ON" : "OFF";

    private static Row Toggle(string name, Func<bool> get, Action<bool> set, string help) =>
        new(name, () => OnOff(get()), _ => { set(!get()); Settings.Apply(); }, help);

    private static Row Volume(string name, Func<float> get, Action<float> set, string help) =>
        new(name, () => $"{(int)MathF.Round(get() * 100)}%", step => { set(Math.Clamp(get() + step * 0.1f, 0, 1)); Settings.Apply(); }, help);

    private static List<Row> GameRows()
    {
        string[] rings = ["NONE", "CHOSEN", "ALL"];
        return
        [
            Volume("Music volume", () => Settings.MusicVolume, v => Settings.MusicVolume = v, "Click: louder; right-click: softer"),
            Volume("Sound volume", () => Settings.SoundVolume, v => Settings.SoundVolume = v, "Click: louder; right-click: softer"),
            Toggle("Full screen", () => Settings.Fullscreen, v => Settings.Fullscreen = v, "The whole screen or a window"),
            Toggle("Skip the opening", () => Settings.SkipIntro, v => Settings.SkipIntro = v, "Start at the main menu"),
            Toggle("Autosave", () => Settings.Autosave, v => Settings.Autosave = v, "Save on entering a region"),
            Toggle("Map information", () => Settings.DebugHud, v => Settings.DebugHud = v, "The map's information lines"),
            Toggle("Original paths", () => Settings.OriginalPathfinding, v => Settings.OriginalPathfinding = v, "Walk as the original plans paths"),
            Toggle("Repair scripts", () => Settings.ScriptRepairs, v => Settings.ScriptRepairs = v, "Mend the game's script bugs (next launch)"),
            Toggle("Shadows", () => Settings.Shadows, v => Settings.Shadows = v, "Shadows under the figures"),
            Toggle("Dust", () => Settings.Dust, v => Settings.Dust = v, "Dust when walking on sand"),
            new("Enemy rings", () => rings[Settings.EnemyRings], step => Settings.EnemyRings = (Settings.EnemyRings + step + 3) % 3,
                "Rings under enemies in a fight"),
            Toggle("Box selection", () => Settings.BoxSelect, v => Settings.BoxSelect = v, "Drag a box to pick who walks"),
            Toggle("Tab picks enemies", () => Settings.ChooseEnemyKeys, v => Settings.ChooseEnemyKeys = v, "In a fight Tab chooses, Enter attacks"),
        ];
    }

    private List<Row> KeyRows()
    {
        var what = KeyHelp.AllSections.SelectMany(s => s.Lines).Where(l => l.Action is not null)
            .GroupBy(l => l.Action!).ToDictionary(g => g.Key, g => g.First().What);
        // The bar takes what it does up to any bracket ("Zoom in (or mouse wheel)"); the line under the window, all of it.
        return [.. Controls.Rebindable.Select(action => new Row(what.GetValueOrDefault(action, action).Split(" (")[0],
            () => _waitingFor == action ? "PRESS A KEY" : KeyHelp.Keys(action) is { Length: > 0 } keys ? keys : "NONE",
            _ => Wait(action), what.GetValueOrDefault(action, action)))];
    }

    // -- showing and choosing ---------------------------------------------------------------------

    private void ShowPage(bool keys)
    {
        _keysPage = keys;
        _waitingFor = null;
        _rows = keys ? KeyRows() : GameRows();
        (_top, _chosen) = (0, -1);
        Refresh();
    }

    private void Turn(int pages)
    {
        _top = Math.Clamp(_top + pages * Slots, 0, Math.Max(0, (_rows.Count - 1) / Slots * Slots));
        Refresh();
    }

    private void Hover(int row)
    {
        _hovered = row < _rows.Count ? row : -1;
        ShowAbout();
    }

    private void ShowAbout()
    {
        _about.Text = _waitingFor is not null ? "Esc keeps the old key" : _keysPage ? "Click, then press the new key" : "Click: change. Right: back";
        int row = _hovered >= 0 ? _hovered : _chosen;
        _status.Text = _hovered >= 0 && row < _rows.Count ? _rows[row].Help : _said;
    }

    /// <summary>Change row <paramref name="row"/> a step (or, on KEYS, wait for its new key).</summary>
    private void Use(int row, int step)
    {
        if (row < 0 || row >= _rows.Count)
        {
            return;
        }
        _chosen = row;
        _rows[row].Change(step);
        Refresh();
    }

    private void Wait(string action)
    {
        _waitingFor = action;
        Say($"Press the new key for {KeyHelp.AllSections.SelectMany(s => s.Lines).FirstOrDefault(l => l.Action == action)?.What ?? action}.");
    }

    private void Say(string text)
    {
        _said = text.Length > 0 ? text : "Changes apply at once. O or Esc closes.";
        _status.Text = _said;
    }

    private void Refresh()
    {
        float valueWidth = _keysPage ? 70 : 44;
        for (int i = 0; i < Slots; i++)
        {
            int row = _top + i;
            Row? r = row < _rows.Count ? _rows[row] : null;
            bool chosen = row == _chosen;
            _slots[i].TextureNormal = chosen ? _slotSelected : _slotNormal;
            _slots[i].Disabled = r is null;
            _names[i].Text = r?.Name ?? "";
            _names[i].Size = new Vector2(_slots[i].Size.X - valueWidth - 10, _names[i].Size.Y);
            _names[i].LabelSettings = GameFonts.Settings(chosen ? StoneUi.Yellow : StoneUi.Face);
            string value = r?.Value() ?? "";
            _values[i].Text = value;
            _values[i].LabelSettings = GameFonts.Settings(value is "OFF" or "NONE" ? StoneUi.Dim : chosen ? StoneUi.Yellow : StoneUi.Face);
        }
        int pages = Math.Max(1, (_rows.Count + Slots - 1) / Slots);
        _page.Text = pages > 1 ? $"{_top / Slots + 1}/{pages}" : "";
        _previous.Visible = _next.Visible = pages > 1;
        _previous.Disabled = _top == 0;
        _next.Disabled = _top + Slots >= _rows.Count;
        StoneUi.SetText(_game, "GAME", !_keysPage);
        StoneUi.SetText(_keysButton, "KEYS", _keysPage);
        _defaults.Visible = _keysPage;
        ShowAbout();
    }

    // -- keys ------------------------------------------------------------------------------------

    public override void _Input(InputEvent e)
    {
        if (!Visible || e is not InputEventKey { Pressed: true } key)
        {
            return;
        }
        GetViewport().SetInputAsHandled(); // the screen behind takes no keys while this is open
        if (_waitingFor is { } action)
        {
            if (key.Echo || key.Keycode is Key.Shift or Key.Ctrl or Key.Alt or Key.Meta)
            {
                return; // a modifier alone: wait for the key it goes with
            }
            _waitingFor = null;
            if (key.Keycode == Key.Escape)
            {
                Say("Kept the old key.");
            }
            else
            {
                Controls.Rebind(action, key);
                string name = Controls.Name(key);
                var clash = Controls.Rebindable.Where(a => a != action && KeyHelp.Keys(a).Split(" / ").Contains(KeyHelp.Keys(action)));
                Say(clash.Any() ? $"{name} is also bound to {string.Join(", ", clash)}: both happen." : $"Bound to {name}.");
            }
            Refresh();
            return;
        }
        if (e.IsActionPressed(Controls.Back) || e.IsActionPressed(Controls.ToggleOptions))
        {
            Close();
        }
        else if (e.IsActionPressed("ui_up", true) || e.IsActionPressed("ui_down", true))
        {
            _chosen = Math.Clamp(_chosen + (e.IsActionPressed("ui_up", true) ? -1 : 1), 0, _rows.Count - 1);
            _top = _chosen / Slots * Slots;
            Refresh();
        }
        else if (e.IsActionPressed("ui_left", true) || e.IsActionPressed("ui_right", true))
        {
            if (_keysPage)
            {
                Turn(e.IsActionPressed("ui_left", true) ? -1 : 1);
            }
            else
            {
                Use(_chosen, e.IsActionPressed("ui_left", true) ? -1 : 1);
            }
        }
        else if (e.IsActionPressed("ui_accept") && !key.Echo)
        {
            Use(_chosen, +1);
        }
        else if (key.Keycode is Key.Pageup or Key.Pagedown)
        {
            Turn(key.Keycode == Key.Pageup ? -1 : 1);
        }
        else if (key.Keycode == Key.Tab && !key.Echo)
        {
            ShowPage(!_keysPage);
        }
    }

    /// <summary>For the checks: the window and the line under it are inside the 320 × 200 screen.</summary>
    public bool WindowFits => new Rect2(0, 0, Width, Height).Encloses(new Rect2(_window.Position, _window.Size))
        && _status.Position.Y + GameFonts.Size <= Height;

    /// <summary>For the checks: change the option named <paramref name="name"/> on GAME a step, as a click on its bar.</summary>
    public string? Press(string name)
    {
        ShowPage(keys: false);
        int row = _rows.FindIndex(r => r.Name == name);
        Use(row, +1);
        return row < 0 ? null : _rows[row].Value();
    }
}
