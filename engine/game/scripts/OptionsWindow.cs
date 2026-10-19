using Godot;

namespace DarkSun.Engine;

/// <summary>
/// The options (O, on the main menu and the map; new): music and sound
/// volume, full screen, the opening, autosave, the map's debug lines,
/// pathfinding, and the keys, each a button: click it, then press the new
/// key (Esc keeps the old one). Changes apply at once and are saved
/// (<see cref="Settings"/>, <c>user://settings.cfg</c>) when the window closes.
/// </summary>
public partial class OptionsWindow : PanelContainer
{
    private static readonly Color Heading = new(0.95f, 0.8f, 0.55f);
    private static readonly Color Dim = new(0.7f, 0.65f, 0.6f);

    private GridContainer _keys = null!;
    private Label _note = null!;
    private string? _waitingFor;
    private Button? _waitingButton;

    /// <summary>The window closed (the options are saved): the screen updates what depends on them.</summary>
    public Action? Closed { get; set; }

    public override void _Ready()
    {
        Visible = false;
        SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
        GrowHorizontal = GrowDirection.Both;
        GrowVertical = GrowDirection.Both;
        CustomMinimumSize = new Vector2(440, 330);
        AddThemeStyleboxOverride("panel", InventoryWindow.Panel());
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 3);
        AddChild(column);
        var top = new HBoxContainer();
        column.AddChild(top);
        top.AddChild(InventoryWindow.Text("Options", Heading));
        top.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        top.AddChild(InventoryWindow.Text("O or Esc closes", Dim));

        var general = new GridContainer { Columns = 2 };
        general.AddThemeConstantOverride("h_separation", 10);
        column.AddChild(general);
        Slider(general, "Music volume", Settings.MusicVolume, v => Settings.MusicVolume = v);
        Slider(general, "Sound volume", Settings.SoundVolume, v => Settings.SoundVolume = v);
        Check(general, "Full screen", Settings.Fullscreen, v => Settings.Fullscreen = v);
        Check(general, "Skip the opening", Settings.SkipIntro, v => Settings.SkipIntro = v);
        Check(general, "Autosave on entering a region", Settings.Autosave, v => Settings.Autosave = v);
        Check(general, "Show the map's information lines", Settings.DebugHud, v => Settings.DebugHud = v);
        Check(general, "Walk as the original plans paths", Settings.OriginalPathfinding, v => Settings.OriginalPathfinding = v);

        column.AddChild(InventoryWindow.Text("Keys (click one, then press the new key)", Heading));
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 120), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        column.AddChild(scroll);
        _keys = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _keys.AddThemeConstantOverride("h_separation", 10);
        _keys.AddThemeConstantOverride("v_separation", 1);
        scroll.AddChild(_keys);
        _note = InventoryWindow.Text("", Dim);
        column.AddChild(_note);
        var buttons = new HBoxContainer();
        column.AddChild(buttons);
        InventoryWindow.AddButton(buttons, "Default keys", () =>
        {
            Controls.ResetKeys();
            _note.Text = "Every key is back to its default.";
            ShowKeys();
        });
        buttons.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        InventoryWindow.AddButton(buttons, "Close", Close);
        ShowKeys();
    }

    public void Open()
    {
        Visible = true;
        _note.Text = "";
        ShowKeys();
    }

    public void Close()
    {
        if (!Visible)
        {
            return;
        }
        StopWaiting();
        Visible = false;
        _note.Text = Settings.Save() ? "" : "The options couldn't be saved.";
        Closed?.Invoke();
    }

    private static void Slider(GridContainer grid, string label, float value, Action<float> set)
    {
        grid.AddChild(InventoryWindow.Text(label));
        var slider = new HSlider { MinValue = 0, MaxValue = 100, Step = 5, Value = value * 100, CustomMinimumSize = new Vector2(160, 0), FocusMode = FocusModeEnum.None };
        slider.ValueChanged += v =>
        {
            set((float)v / 100);
            Settings.Apply();
        };
        grid.AddChild(slider);
    }

    private static void Check(GridContainer grid, string label, bool value, Action<bool> set)
    {
        grid.AddChild(InventoryWindow.Text(label));
        // An on/off button: plainer to see on the dark panel than a check box.
        var box = InventoryWindow.Small(new Button { ToggleMode = true, ButtonPressed = value, Text = value ? "On" : "Off", FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(50, 0) });
        box.Toggled += on =>
        {
            box.Text = on ? "On" : "Off";
            set(on);
            Settings.Apply();
        };
        grid.AddChild(box);
    }

    /// <summary>Each action the player can rebind, named as the key help names it, with its keys now.</summary>
    private void ShowKeys()
    {
        foreach (Node child in _keys.GetChildren())
        {
            child.QueueFree();
        }
        var what = KeyHelp.AllSections.SelectMany(s => s.Lines).Where(l => l.Action is not null)
            .GroupBy(l => l.Action!).ToDictionary(g => g.Key, g => g.First().What);
        foreach (string action in Controls.Rebindable)
        {
            _keys.AddChild(InventoryWindow.Text(what.GetValueOrDefault(action, action)));
            string keys = KeyHelp.Keys(action);
            var button = InventoryWindow.AddButton(_keys, keys.Length > 0 ? keys : "(none)", () => { });
            button.CustomMinimumSize = new Vector2(110, 0);
            string a = action;
            button.Pressed += () => Wait(a, button);
        }
    }

    private void Wait(string action, Button button)
    {
        StopWaiting();
        _waitingFor = action;
        _waitingButton = button;
        button.Text = "press a key…";
        _note.Text = "Press the new key (Esc keeps the old one).";
    }

    private void StopWaiting()
    {
        (_waitingFor, _waitingButton) = (null, null);
    }

    public override void _Input(InputEvent e)
    {
        if (!Visible || e is not InputEventKey { Pressed: true, Echo: false } key)
        {
            return;
        }
        if (_waitingFor is { } action)
        {
            GetViewport().SetInputAsHandled();
            if (key.Keycode is Key.Shift or Key.Ctrl or Key.Alt or Key.Meta)
            {
                return; // a modifier alone: wait for the key it goes with
            }
            StopWaiting();
            if (key.Keycode == Key.Escape)
            {
                _note.Text = "Kept the old key.";
            }
            else
            {
                Controls.Rebind(action, key);
                string name = Controls.Name(key);
                var clash = Controls.Rebindable.Where(a => a != action && KeyHelp.Keys(a).Split(" / ").Contains(KeyHelp.Keys(action)));
                _note.Text = clash.Any() ? $"{name} is also bound to {string.Join(", ", clash)}: both will happen." : $"Bound to {name}.";
            }
            ShowKeys();
            return;
        }
        if (e.IsActionPressed(Controls.Back) || e.IsActionPressed(Controls.ToggleOptions))
        {
            GetViewport().SetInputAsHandled();
            Close();
        }
    }
}
