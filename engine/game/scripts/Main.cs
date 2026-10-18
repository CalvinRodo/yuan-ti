using DarkSun.Data;
using DarkSun.Data.Gff;
using DarkSun.Data.Images;
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
    private HBoxContainer _preview = null!;
    private FileDialog _picker = null!;

    public override void _Ready()
    {
        BuildUi();
        GameInstall? install = GameInstall.Locate([LoadConfiguredDirectory(), DevelopmentDirectory()]);

        if (OS.GetCmdlineUserArgs().Contains("--check-install"))
        {
            GD.Print(install is null ? "No game install found." : Describe(install));
            if (install is not null)
            {
                ShowTilePreview(install);
                GD.Print($"Decoded {_preview.GetChildCount()} tiles from {install.RegionFiles[0]}");
            }
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
        _preview = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        box.AddChild(_preview);

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

    private void ShowStatus(GameInstall? install, string? error)
    {
        _status.Text = install is not null ? Describe(install)
            : error ?? "Game files not found. Choose the folder where Dark Sun is installed.";
        foreach (Node child in _preview.GetChildren())
        {
            child.QueueFree();
        }
        if (install is not null)
        {
            ShowTilePreview(install);
        }
    }

    /// <summary>
    /// The first ground tiles of the first region, as a check that the game
    /// files decode. A region's palette is the GPLDATA.GFF <c>PAL </c> with
    /// the number of the region's <c>GMAP</c>.
    /// </summary>
    private void ShowTilePreview(GameInstall install)
    {
        try
        {
            GffFile region = GffFile.Open(install.FindFile(install.RegionFiles[0])!);
            GffFile gplData = GffFile.Open(install.FindFile("GPLDATA.GFF")!);
            var resources = region.DescribeResources();
            int regionNumber = resources.First(r => r.Tag == "GMAP").Number;
            Palette palette = Palette.FromBytes(gplData.Read("PAL", regionNumber).Span);

            foreach (var tile in resources.Where(r => r.Tag == "TILE").Take(16))
            {
                ImageFrame frame = ImageDecoder.ReadFrames(region.Read(tile.Tag, tile.Number).Span)[0];
                _preview.AddChild(new TextureRect
                {
                    Texture = Textures.ToTexture(frame, palette),
                    StretchMode = TextureRect.StretchModeEnum.Keep,
                });
            }
        }
        catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or InvalidOperationException or IOException)
        {
            _status.Text += $"\nCould not decode {install.RegionFiles.FirstOrDefault()}: {e.Message}";
        }
    }

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
