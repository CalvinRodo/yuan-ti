using DarkSun.Data;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// Start screen: finds the player's game files, then plays the intro and
/// opens the main menu (<c>-- --no-intro</c>: straight to the menu;
/// <c>-- --new-game</c>: straight into a new game, with <c>--region=N</c> and
/// <c>--at=X,Y</c> to start elsewhere; <c>-- --mods</c>: the mod list;
/// <c>-- --saves</c>: the saved games; <c>-- --load=quick</c>: load a save;
/// <c>-- --party</c> / <c>-- --chargen</c>: the character screens;
/// <c>-- --break-on-start</c> with a new game: the script debugger pauses at
/// the game-start script's first command). Only
/// shown when the files can't be found, to choose their folder.
/// For headless checks: <c>-- --check-install</c> prints what was found and
/// quits (exit code 1 if nothing); <c>-- --check-regions</c>,
/// <c>-- --check-exits</c> and <c>-- --check-start</c> run the region viewer's
/// checks and quit.
/// </summary>
public partial class Main : Control
{
    private const string SettingsPath = "user://settings.cfg";

    private Label _status = null!;
    private Button _viewRegions = null!;
    private FileDialog _picker = null!;

    public override void _Ready()
    {
        BuildUi();
        GameInstall? install = Session.Install
            ?? GameInstall.Locate([LoadConfiguredDirectory(), DevelopmentDirectory()]);
        string[] args = OS.GetCmdlineUserArgs();

        if (args.Contains("--check-help"))
        {
            // Every screen's key help, and any action no screen lists.
            Node?[] screens = [new RegionView(), new MainMenu(), new PartyView(), new CharacterGenView(), new SavesView(), new ModsView(), new IntroView(), null];
            foreach (Node? screen in screens)
            {
                var (title, sections) = KeyHelp.For(screen);
                GD.Print($"{title}: " + string.Join("; ", sections.Select(h => $"{h.Title} ({h.Lines.Length})")));
                screen?.Free();
            }
            foreach (HelpLine line in KeyHelp.AllSections.SelectMany(h => h.Lines).Where(l => l.Action is not null))
            {
                GD.Print($"  {line.Keys,-16} {line.What}");
            }
            var unlisted = KeyHelp.Unlisted().ToList();
            GD.Print(unlisted.Count == 0 ? "key help: every action listed" : $"key help: not listed: {string.Join(", ", unlisted)}");
            GetTree().Quit(unlisted.Count == 0 ? 0 : 1);
            return;
        }

        if (args.Contains("--check-display"))
        {
            // How the window shows the 640 × 400 viewport: the scale must be a whole number.
            Window window = GetWindow();
            Transform2D t = window.GetFinalTransform();
            GD.Print($"window {window.Size.X}x{window.Size.Y}, viewport {GetViewportRect().Size.X}x{GetViewportRect().Size.Y}, "
                + $"scale {t.Scale.X}x{t.Scale.Y}, offset {t.Origin.X},{t.Origin.Y}");
            GetTree().Quit();
            return;
        }
        if (args.Contains("--check-sounds") && install is not null)
        {
            // Decode every sample a sound number can play.
            Session.Use(install);
            int ok = 0, missing = 0;
            double seconds = 0;
            var loops = new List<string>();
            for (int id = 1; id <= DarkSun.Data.Audio.SoundEffects.LastSample; id++)
            {
                int number = DarkSun.Data.Audio.SoundEffects.Bvoc(id)!.Value;
                if (!Session.Open("RESOURCE.GFF").TryRead("BVOC", number, out var data))
                {
                    missing++;
                    continue;
                }
                var sound = DarkSun.Data.Audio.Voc.Decode(data.Span);
                ok++;
                if (sound.Loops)
                {
                    loops.Add($"{id} ({sound.Samples.Length / (double)sound.SampleRate:F1} s, repeated {sound.LoopCount} times)");
                }
                else
                {
                    seconds += sound.Seconds;
                }
            }
            GD.Print($"sounds: {ok} decoded, {missing} numbers without a BVOC, {seconds:F1} s in all; repeating: {string.Join(", ", loops)}");
            GetTree().Quit(ok > 0 ? 0 : 1);
            return;
        }
        bool regionCheck = args.Contains("--check-regions") || args.Contains("--check-exits") || args.Contains("--check-start") || args.Contains("--check-verbs") || args.Contains("--check-memory") || args.Contains("--check-save")
            || args.Contains("--check-debugger");
        bool screenCheck = args.Contains("--check-intro") || args.Contains("--check-menu") || args.Contains("--check-mods") || args.Contains("--check-saves")
            || args.Contains("--check-party") || args.Contains("--check-chargen");
        if (args.Contains("--check-install") || (install is null && (regionCheck || screenCheck)))
        {
            GD.Print(install is null ? "No game install found." : Describe(install));
            GetTree().Quit(install is null ? 1 : 0);
            return;
        }
        ShowStatus(install, null);
        if (regionCheck)
        {
            OpenRegionView();
        }
        else if (args.Contains("--check-menu"))
        {
            ChangeScene(MainMenu.ScenePath);
        }
        else if (install is not null && args.FirstOrDefault(a => a.StartsWith("--load=")) is { } load)
        {
            // Straight into a saved game (a name in the saves folder, e.g. --load=quick).
            string path = Session.SavePath(load["--load=".Length..]);
            try
            {
                RegionView.PendingLoad = DarkSun.Game.Saves.SaveGame.Read(path);
                ChangeScene(RegionView.ScenePath);
            }
            catch (Exception e) when (e is IOException or InvalidDataException)
            {
                SavesView.Message = $"Can't read {path}: {e.Message}";
                ChangeScene(SavesView.ScenePath);
            }
        }
        else if (args.Contains("--check-party") || install is not null && args.Contains("--party"))
        {
            ChangeScene(PartyView.ScenePath);
        }
        else if (args.Contains("--check-chargen") || install is not null && args.Contains("--chargen"))
        {
            CharacterGenView.Open(null, 0);
        }
        else if (args.Contains("--check-saves") || install is not null && args.Contains("--saves"))
        {
            ChangeScene(SavesView.ScenePath);
        }
        else if (install is not null && args.Contains("--mods") || args.Contains("--check-mods"))
        {
            ChangeScene(ModsView.ScenePath);
        }
        else if (install is not null && args.Contains("--new-game"))
        {
            // Straight into a new game, for development; --region=N and --at=X,Y choose where.
            Session.NewGame();
            RegionView.StartNewGame();
            var start = RegionView.NewGameStart;
            foreach (string arg in args)
            {
                if (arg.StartsWith("--region=") && int.TryParse(arg[9..], out int region))
                {
                    start = (region, new DarkSun.Game.Movement.Cell(64, 49));
                }
                else if (arg.StartsWith("--at=") && arg[5..].Split(',') is [var x, var y] && int.TryParse(x, out int cx) && int.TryParse(y, out int cy))
                {
                    start = (start.Region, new DarkSun.Game.Movement.Cell(cx, cy));
                }
            }
            RegionView.NewGameStart = start;
            ChangeScene(RegionView.ScenePath);
        }
        else if (install is not null)
        {
            Start(skipIntro: args.Contains("--no-intro"));
        }
    }

    /// <summary>The intro (once per run), then the main menu.</summary>
    private void Start(bool skipIntro)
    {
        bool intro = !skipIntro && !_introPlayed;
        _introPlayed = true;
        ChangeScene(intro ? IntroView.ScenePath : MainMenu.ScenePath);
    }

    private static bool _introPlayed;

    private void ChangeScene(string path) =>
        GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, path);

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

        _viewRegions = new Button { Text = "Play" };
        _viewRegions.Pressed += () => Start(skipIntro: false);
        box.AddChild(_viewRegions);
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
        if (install is not null && install != Session.Install)
        {
            Session.Use(install);
        }
        _viewRegions.Disabled = install is null;
    }

    private void OpenRegionView() => ChangeScene(RegionView.ScenePath);

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
