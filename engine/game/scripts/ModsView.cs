using DarkSun.Data.Mods;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// The mod list, from the main menu (M): every mod found, in priority order
/// (highest first), with a box to turn each on or off (renames its folder
/// with a leading <c>_</c>) and arrows to change its priority (writes
/// <c>load_order.txt</c>; only among mods in the same folder). Changes apply
/// at once: the mods are loaded again. Selecting a mod shows its
/// <c>mod.json</c> details, what it replaces, and any file that couldn't be
/// used (every changed resource is built when the screen opens). Esc goes back.
/// Run with <c>-- --check-mods</c> to print the list and its problems and quit
/// (exit code 1 if there are problems).
/// </summary>
public partial class ModsView : Control
{
    public const string ScenePath = "res://scenes/mods.tscn";

    private const int FontSize = 12;

    private VBoxContainer _rows = null!;
    private RichTextLabel _details = null!;
    private Label _status = null!;
    private string? _selected;

    public override void _Ready()
    {
        if (Session.Install is null)
        {
            GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, "res://scenes/main.tscn");
            return;
        }
        BuildUi();
        IReadOnlyList<ModProblem> problems = Verify();
        if (OS.GetCmdlineUserArgs().Contains("--check-mods"))
        {
            ModSet mods = Session.Mods;
            GD.Print($"mods: {mods.All.Count} found in {string.Join(", ", mods.Roots)}; {mods.Mods.Count} on, {mods.Count} resources");
            var counts = mods.CountsByMod();
            foreach (Mod mod in mods.All)
            {
                GD.Print($"  {(mod.Enabled ? "on " : "off")} {mod.Name}: {counts.GetValueOrDefault(mod.Name)} resources");
            }
            foreach (ModProblem p in problems)
            {
                GD.Print($"  problem: {p}");
            }
            GetTree().Quit(problems.Count > 0 ? 1 : 0);
            return;
        }
        _selected = Session.Mods.All.FirstOrDefault()?.Name;
        Refresh();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e.IsActionPressed(Controls.Back))
        {
            GetViewport().SetInputAsHandled();
            Back();
        }
    }

    private static IReadOnlyList<ModProblem> Verify() =>
        Session.Mods.Count > 0 ? Session.VerifyMods() : Session.Mods.Problems;

    private void Back() => GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, MainMenu.ScenePath);

    private void BuildUi()
    {
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

        column.AddChild(Text("Mods", 18));
        column.AddChild(Text("Highest priority first. Changes apply at once.", FontSize, dim: true));

        var split = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        column.AddChild(split);
        var scroll = new ScrollContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        split.AddChild(scroll);
        _rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(_rows);
        _details = new RichTextLabel
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            BbcodeEnabled = true,
            ScrollActive = true,
            SelectionEnabled = true,
        };
        _details.AddThemeFontSizeOverride("normal_font_size", FontSize);
        _details.AddThemeFontSizeOverride("bold_font_size", FontSize);
        split.AddChild(_details);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 8);
        column.AddChild(buttons);
        buttons.AddChild(SmallButton("Open mods folder", OpenFolder));
        buttons.AddChild(SmallButton("Reload", () =>
        {
            Session.ReloadMods();
            int n = Verify().Count;
            Refresh();
            _status.Text = n == 0 ? "Reloaded; every mod file is usable." : $"Reloaded; {n} file(s) can't be used.";
        }));
        buttons.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        Button back = SmallButton("Back (Esc)", Back);
        buttons.AddChild(back);
        back.CallDeferred(Control.MethodName.GrabFocus);

        _status = Text("", FontSize, dim: true);
        column.AddChild(_status);
    }

    private void Refresh()
    {
        foreach (Node child in _rows.GetChildren())
        {
            child.QueueFree();
        }
        ModSet mods = Session.Mods;
        var counts = mods.CountsByMod();
        var problems = mods.Problems;
        if (mods.All.Count == 0)
        {
            _rows.AddChild(Text("No mods found.", FontSize));
        }
        foreach (Mod mod in mods.All)
        {
            var row = new HBoxContainer();
            var on = new CheckBox { ButtonPressed = mod.Enabled, TooltipText = "On or off" };
            on.AddThemeFontSizeOverride("font_size", FontSize);
            on.Toggled += enabled => Change(() => ModManager.SetEnabled(mod, enabled), mod.Name);
            row.AddChild(on);
            int problemCount = problems.Count(p => p.Mod.Name == mod.Name);
            string label = mod.DisplayName + (mod.Version.Length > 0 ? $" {mod.Version}" : "")
                + (mod.Enabled ? $"  ({counts.GetValueOrDefault(mod.Name)})" : "") + (problemCount > 0 ? $"  ⚠ {problemCount}" : "");
            Button name = SmallButton(label, () =>
            {
                _selected = mod.Name;
                Refresh();
            });
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            name.Alignment = HorizontalAlignment.Left;
            name.ToggleMode = true;
            name.ButtonPressed = mod.Name == _selected;
            name.ClipText = true;
            row.AddChild(name);
            var sameFolder = mods.All.Where(m => m.Root == mod.Root).ToList();
            int at = sameFolder.IndexOf(mod);
            Button up = SmallButton("▲", () => Move(sameFolder, at, -1));
            up.Disabled = at == 0;
            up.TooltipText = "Higher priority";
            Button down = SmallButton("▼", () => Move(sameFolder, at, +1));
            down.Disabled = at == sameFolder.Count - 1;
            down.TooltipText = "Lower priority";
            row.AddChild(up);
            row.AddChild(down);
            _rows.AddChild(row);
        }
        ShowDetails(mods.All.FirstOrDefault(m => m.Name == _selected), counts, problems);
    }

    private void ShowDetails(Mod? mod, IReadOnlyDictionary<string, int> counts, IReadOnlyList<ModProblem> problems)
    {
        if (mod is null)
        {
            _details.Text = "A mod is a folder in one of these:\n"
                + string.Join("\n", Session.ModFolders(Session.Install!).OfType<string>().Select(f => "  " + Escape(f)))
                + "\n\nInside it, files named after the resources they replace: ICON/2048.png, TEXT/12.txt, "
                + "RDFF/87.json, GPL/12.gpl… See the engine README (Mods) or run make engine-mod-export to start one.";
            return;
        }
        var lines = new List<string> { $"[b]{Escape(mod.DisplayName)}[/b]" };
        if (mod.Version.Length > 0 || mod.Author.Length > 0)
        {
            lines.Add(Escape(string.Join(" · ", new[] { mod.Version, mod.Author.Length > 0 ? "by " + mod.Author : "" }.Where(s => s.Length > 0))));
        }
        lines.Add(Escape(mod.Directory));
        lines.Add(mod.Enabled ? $"Replaces or adds {counts.GetValueOrDefault(mod.Name)} resources." : "Off.");
        if (mod.Description.Length > 0)
        {
            lines.Add("");
            lines.Add(Escape(mod.Description));
        }
        var mine = problems.Where(p => p.Mod.Name == mod.Name).ToList();
        if (mine.Count > 0)
        {
            lines.Add("");
            lines.Add($"[color=#e8a060][b]{mine.Count} file(s) can't be used; the game keeps the originals:[/b][/color]");
            lines.AddRange(mine.Select(p => $"[color=#e8a060]{Escape(Path.GetRelativePath(mod.Directory, p.Path))}[/color]: {Escape(p.Message)}"));
        }
        _details.Text = string.Join("\n", lines);
    }

    private void Move(List<Mod> sameFolder, int at, int by)
    {
        var names = sameFolder.Select(m => m.Name).ToList();
        (names[at], names[at + by]) = (names[at + by], names[at]);
        Change(() => ModManager.SaveLoadOrder(sameFolder[0].Root, names), names[at + by]);
    }

    /// <summary>Make a change on disk, then load the mods again.</summary>
    private void Change(Action change, string? select)
    {
        try
        {
            change();
            _status.Text = "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _status.Text = e.Message;
        }
        Session.ReloadMods();
        Verify();
        _selected = select ?? _selected;
        Callable.From(Refresh).CallDeferred();
    }

    private void OpenFolder()
    {
        string folder = ProjectSettings.GlobalizePath("user://mods");
        Directory.CreateDirectory(folder);
        OS.ShellOpen(folder);
        _status.Text = $"Opened {folder}";
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

    private static string Escape(string s) => s.Replace("[", "[lb]");
}
