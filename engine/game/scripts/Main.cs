using DarkSun.Data;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// Start screen: finds the player's game files and shows what was found.
/// Run with <c>-- --check-install</c> to print the result and quit (exit code
/// 1 if no install was found), e.g. from a headless test.
/// </summary>
public partial class Main : Control
{
    private const string SettingsPath = "user://settings.cfg";

    private Label _status = null!;
    private FileDialog _picker = null!;

    public override void _Ready()
    {
        BuildUi();
        GameInstall? install = GameInstall.Locate([LoadConfiguredDirectory(), DevelopmentDirectory()]);

        if (OS.GetCmdlineUserArgs().Contains("--check-install"))
        {
            GD.Print(install is null ? "No game install found." : Describe(install));
            GetTree().Quit(install is null ? 1 : 0);
            return;
        }
        ShowStatus(install, null);
    }

    private void BuildUi()
    {
        var box = new VBoxContainer();
        box.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
        box.GrowHorizontal = GrowDirection.Both;
        box.GrowVertical = GrowDirection.Both;
        AddChild(box);

        box.AddChild(new Label { Text = "Dark Sun: Shattered Lands", HorizontalAlignment = HorizontalAlignment.Center });
        _status = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        box.AddChild(_status);

        var choose = new Button { Text = "Choose game folder…" };
        choose.Pressed += () => _picker.PopupCentered(new Vector2I(600, 360));
        box.AddChild(choose);

        _picker = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenDir,
            Access = FileDialog.AccessEnum.Filesystem,
            Title = "Folder containing GPLDATA.GFF",
        };
        _picker.DirSelected += OnDirectoryChosen;
        AddChild(_picker);
    }

    private void OnDirectoryChosen(string directory)
    {
        GameInstall? install = GameInstall.TryOpen(directory, out string? error);
        if (install is not null)
        {
            SaveConfiguredDirectory(install.Directory);
        }
        ShowStatus(install, error);
    }

    private void ShowStatus(GameInstall? install, string? error) =>
        _status.Text = install is not null ? Describe(install)
            : error ?? "Game files not found. Choose the folder where Dark Sun is installed.";

    private static string Describe(GameInstall install) =>
        $"Game files: {install.Directory}\n{install.RegionFiles.Count} regions";

    private static string? LoadConfiguredDirectory()
    {
        var config = new ConfigFile();
        return config.Load(SettingsPath) == Error.Ok
            ? config.GetValue("game", "directory", "").AsString()
            : null;
    }

    private static void SaveConfiguredDirectory(string directory)
    {
        var config = new ConfigFile();
        config.Load(SettingsPath);
        config.SetValue("game", "directory", directory);
        config.Save(SettingsPath);
    }

    /// <summary>The repository's <c>dark_sun/</c> folder, for runs from a checkout.</summary>
    private static string DevelopmentDirectory() =>
        ProjectSettings.GlobalizePath("res://").PathJoin("../../dark_sun").SimplifyPath();
}
