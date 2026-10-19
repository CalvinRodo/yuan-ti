using DarkSun.Game.Saves;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// Load Saved Game (L on the main menu): the saved games, newest first, each
/// with its name, region, time and the mods it was saved with; Load opens it
/// in the region view, Delete removes the file. A save that can't be read
/// says why. Esc goes back. In the region view, F5 saves (quick save), F9
/// loads the quick save, and entering a region saves an autosave.
/// </summary>
public partial class SavesView : Control
{
    public const string ScenePath = "res://scenes/saves.tscn";

    /// <summary>Shown when the screen opens (why the last load failed).</summary>
    public static string? Message { get; set; }

    private const int FontSize = 12;

    private VBoxContainer _rows = null!;
    private Button? _firstLoad;
    private Label _status = null!;

    public override void _Ready()
    {
        if (Session.Install is null)
        {
            GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, "res://scenes/main.tscn");
            return;
        }
        var background = new ColorRect { Color = new Color(0.08f, 0.06f, 0.04f) };
        background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(background);
        var margin = new MarginContainer();
        margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "left", "right", "top", "bottom" })
        {
            margin.AddThemeConstantOverride($"margin_{side}", 16);
        }
        AddChild(margin);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 8);
        margin.AddChild(column);
        column.AddChild(Text("Load Saved Game", 18));
        column.AddChild(Text($"In the game: F5 saves, F9 loads the quick save; entering a region autosaves. Saves are in {Session.SavesFolder}.", FontSize, dim: true));
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        column.AddChild(scroll);
        _rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(_rows);
        var buttons = new HBoxContainer();
        column.AddChild(buttons);
        buttons.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        Button back = SmallButton("Back (Esc)", Back);
        buttons.AddChild(back);
        _status = Text(Message ?? "", FontSize, dim: true);
        Message = null;
        column.AddChild(_status);
        Refresh();
        if (OS.GetCmdlineUserArgs().Contains("--check-saves") || (DisplayServer.GetName() == "headless" && _status.Text.Length > 0))
        {
            if (_status.Text.Length > 0)
            {
                GD.Print($"saves: {_status.Text}");
            }
            foreach (var (path, save, error) in Session.ListSaves())
            {
                GD.Print(save is not null ? $"save {Path.GetFileName(path)}: {Describe(save)}" : $"save {Path.GetFileName(path)}: can't be read: {error}");
            }
            GetTree().Quit();
            return;
        }
        // Start on the newest save's Load (never on a Delete).
        (_firstLoad ?? back).CallDeferred(Control.MethodName.GrabFocus);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e.IsActionPressed(Controls.Back))
        {
            GetViewport().SetInputAsHandled();
            Back();
        }
    }

    private void Back() => GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, MainMenu.ScenePath);

    private void Refresh()
    {
        foreach (Node child in _rows.GetChildren())
        {
            child.QueueFree();
        }
        var saves = Session.ListSaves();
        _firstLoad = null;
        if (saves.Count == 0)
        {
            _rows.AddChild(Text("No saved games yet.", FontSize));
        }
        foreach (var (path, save, error) in saves)
        {
            var row = new HBoxContainer();
            row.AddChild(Text(save is not null ? Describe(save) : $"{Path.GetFileName(path)}: can't be read ({error})", FontSize, dim: save is null));
            ((Label)row.GetChild(0)).SizeFlagsHorizontal = SizeFlags.ExpandFill;
            if (save is not null)
            {
                Button load = SmallButton("Load", () => Load(save));
                _firstLoad ??= load;
                row.AddChild(load);
            }
            // Delete asks first: the second press deletes.
            Button delete = null!;
            delete = SmallButton("Delete", () =>
            {
                if (delete.Text != "Delete?")
                {
                    delete.Text = "Delete?";
                    _status.Text = $"Press Delete? again to delete {Path.GetFileName(path)}.";
                    return;
                }
                File.Delete(path);
                _status.Text = $"Deleted {Path.GetFileName(path)}.";
                Refresh();
            });
            row.AddChild(delete);
            _rows.AddChild(row);
        }
    }

    private static string Describe(SaveGame save) =>
        $"{save.Name} · region {save.Region} · {save.Saved.LocalDateTime:g} · {save.Party.Count} characters"
        + (save.Mods.Count > 0 ? $" · mods: {string.Join(", ", save.Mods)}" : "");

    private void Load(SaveGame save)
    {
        RegionView.PendingLoad = save;
        GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, RegionView.ScenePath);
    }

    private static Label Text(string text, int size, bool dim = false)
    {
        var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", dim ? new Color(0.75f, 0.65f, 0.5f) : new Color(0.95f, 0.85f, 0.65f));
        return label;
    }

    private static Button SmallButton(string text, Action pressed)
    {
        var button = new Button { Text = text };
        button.AddThemeFontSizeOverride("font_size", FontSize);
        button.Pressed += pressed;
        return button;
    }
}
