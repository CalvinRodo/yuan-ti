using DarkSun.Data.Gff;
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
        Settings.Apply();
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
            // The arena's crowd (58) repeats for minutes; the next sound stops it, as on the original's one digital channel.
            bool stops = true;
            if (Sounds.Instance is { } player)
            {
                player.Volume = 1;
                player.Play(58);
                bool crowd = player.PlayingRepeating;
                player.Play(37);
                stops = crowd && !player.PlayingRepeating;
                GD.Print($"sounds: the crowd {(crowd ? "repeats" : "didn't start")}, then {(stops ? "stops when another sound starts: OK" : "goes on")}");
            }
            GetTree().Quit(ok > 0 && stops ? 0 : 1);
            return;
        }
        if (args.Contains("--check-pictures") && install is not null)
        {
            // REQUEST(6, 200 / 300): the CMAT pictures decode, fit the screen and aren't one colour.
            Session.Use(install);
            bool ok = true;
            foreach (int n in new[] { 200, 300 })
            {
                if (!PictureView.Exists(n))
                {
                    GD.Print($"pictures: PROBLEM no CMAT / CPAL {n}");
                    ok = false;
                    continue;
                }
                var (frame, palette, rgba) = PictureView.Decode(Session.Open("RESOURCE.GFF"), n);
                int colours = Enumerable.Range(0, rgba.Length / 4).Select(i => BitConverter.ToInt32(rgba, i * 4)).Distinct().Count();
                bool fits = frame.Width <= RetroScreen.Width && frame.Height <= RetroScreen.Height && colours > 16;
                ok &= fits;
                // The fades as DSUN.EXE times them, with region 42's palette for the map.
                var map = Session.Open("GPLDATA.GFF").TryRead("PAL ", 42, out var pal) ? DarkSun.Data.Images.Palette.FromBytes(pal.Span) : null;
                var (mapRead, mapSteps, read, steps) = PictureView.Phases(frame, palette, map);
                int total = mapRead + mapSteps + 2 * (read + steps) + PictureView.HoldRetraces;
                GD.Print($"pictures: {n} is {frame.Width} x {frame.Height}, {colours} colours, {PictureView.ColoursShown(frame).Count} indexes on screen; "
                    + $"retraces: map {mapRead} + fade {mapSteps}, picture {read} + fade in {steps}, hold {PictureView.HoldRetraces}, {read} + fade out {steps}: "
                    + $"{total / DarkSun.Game.Cinematics.PaletteFades.RetracesPerSecond:F1} s{(fits ? "" : " PROBLEM")}");
            }
            GetTree().Quit(ok ? 0 : 1);
            return;
        }
        if (args.Contains("--check-music") && install is not null)
        {
            // Every music level of every region track: the section it asks for plays, within a minute.
            Session.Use(install);
            int changes = 0;
            long longest = 0;
            var problems = new List<string>();
            for (int track = 1; track <= 23; track++)
            {
                if (!Session.Open("RESOURCE.GFF").TryRead("GSEQ", track, out var xmi))
                {
                    problems.Add($"GSEQ {track} missing");
                    continue;
                }
                for (int level = 1; level <= DarkSun.Game.Audio.MusicTracks.Levels(track); level++)
                {
                    // Level 1 from section 2, any other from section 1.
                    foreach (int from in level == 1 ? new[] { 2 } : [1])
                    {
                        if (from > DarkSun.Game.Audio.MusicTracks.Levels(track))
                        {
                            continue;
                        }
                        var sequence = new DarkSun.Data.Audio.XmidiSequencer(xmi.Span);
                        var sections = new DarkSun.Game.Audio.MusicSections();
                        sections.Follow(sequence);
                        const int Second = DarkSun.Data.Audio.XmidiSequencer.TicksPerSecond;
                        long tick = 0;
                        // Plays until the section comes (ticks taken; -1 if not in a minute) or, with none, for that long.
                        long Play(int section, long ticks = 60 * Second)
                        {
                            long start = tick;
                            for (; tick - start < ticks && sections.Section != section && !sequence.Finished; tick++)
                            {
                                sequence.AdvanceTo(tick, _ => { });
                            }
                            return sections.Section == section ? tick - start : -1;
                        }
                        sections.SetLevel(from);
                        if (from != 1 && Play(from) < 0)
                        {
                            problems.Add($"track {track}: level {from} never came");
                            continue;
                        }
                        Play(section: 0, ticks: 5 * Second); // a while into the section
                        sections.SetLevel(level);
                        long took = Play(level);
                        if (took < 0)
                        {
                            problems.Add($"track {track}: level {from} to {level} never came{(sequence.Finished ? " (the track ended)" : "")}");
                            continue;
                        }
                        changes++;
                        longest = Math.Max(longest, took);
                    }
                }
            }
            GD.Print($"music: {changes} level changes in 23 tracks, the longest after {longest / (double)DarkSun.Data.Audio.XmidiSequencer.TicksPerSecond:F1} s");

            // The FM music: every track's timbres are in STDPATCH.AD, and its first 20 s sound on both cards.
            byte[] bank = File.ReadAllBytes(install.FindFile(Music.FmTimbres) ?? throw new FileNotFoundException(Music.FmTimbres));
            var library = AIL2.Formats.TimbreBank.Load(bank);
            string? soundFont = DarkSun.Game.Audio.SoundFontLocator.Find();
            var loudness = new List<string>();
            foreach (var (file, track) in Enumerable.Range(1, 23).Select(n => ("RESOURCE.GFF", n)).Concat(Enumerable.Range(26, 4).Select(n => ("CINE.GFF", n))))
            {
                if (!Session.Open(file).TryRead("GSEQ", track, out var xmi))
                {
                    problems.Add($"{file} GSEQ {track} missing");
                    continue;
                }
                foreach (var (patch, timbreBank) in DarkSun.Data.Audio.XmidiSequencer.Timbres(xmi.Span))
                {
                    if (!library.TryGet((byte)timbreBank, (byte)patch, out _))
                    {
                        problems.Add($"{file} GSEQ {track}: timbre {timbreBank}:{patch} isn't in {Music.FmTimbres}");
                    }
                }
                static double Rms(DarkSun.Game.Audio.IMusicSynth synth, byte[] xmi)
                {
                    var player = new DarkSun.Game.Audio.MusicPlayer(synth);
                    player.Play(xmi);
                    float[] left = new float[44100], right = new float[44100];
                    double sum = 0;
                    for (int second = 0; second < 20; second++)
                    {
                        player.Render(left, right);
                        sum += left.Sum(v => (double)v * v) + right.Sum(v => (double)v * v);
                    }
                    return Math.Sqrt(sum / (20 * 2 * 44100));
                }
                double adlib = Rms(new DarkSun.Game.Audio.FmSynth(bank, DarkSun.Game.Audio.FmDevice.AdLib, 44100), xmi.ToArray());
                double opl3 = Rms(new DarkSun.Game.Audio.FmSynth(bank, DarkSun.Game.Audio.FmDevice.SoundBlasterPro2, 44100), xmi.ToArray());
                double general = soundFont is null ? 0 : Rms(new DarkSun.Game.Audio.SoundFontSynth(new MeltySynth.SoundFont(soundFont), 44100), xmi.ToArray());
                loudness.Add($"{track} {adlib:F3}/{opl3:F3}/{general:F3}");
                if (adlib < 0.001 || opl3 < 0.001)
                {
                    problems.Add($"{file} GSEQ {track}: silent on FM ({adlib:F4}, {opl3:F4})");
                }
            }
            GD.Print($"music: FM loudness (RMS, AdLib/OPL3/SoundFont, first 20 s): {string.Join(", ", loudness)}");
            foreach (string p in problems)
            {
                GD.Print($"music: PROBLEM {p}");
            }
            GetTree().Quit(problems.Count == 0 && changes > 0 ? 0 : 1);
            return;
        }
        bool regionCheck = args.Contains("--check-regions") || args.Contains("--check-exits") || args.Contains("--check-start") || args.Contains("--check-verbs") || args.Contains("--check-memory") || args.Contains("--check-save")
            || args.Contains("--check-debugger") || args.Contains("--check-items") || args.Contains("--check-floor") || args.Contains("--check-character") || args.Contains("--check-dialogue")
            || args.Contains("--check-journal") || args.Contains("--check-colours");
        bool screenCheck = args.Contains("--check-intro") || args.Contains("--check-menu") || args.Contains("--check-mods") || args.Contains("--check-saves")
            || args.Contains("--check-party") || args.Contains("--check-chargen") || args.Contains("--check-windows");
        if (args.Contains("--check-install") || (install is null && (regionCheck || screenCheck)))
        {
            GD.Print(install is null ? "No game install found." : Describe(install));
            GetTree().Quit(install is null ? 1 : 0);
            return;
        }
        if (args.Contains("--check-defiling"))
        {
            // For this run (not saved): the mods the check is about.
            Settings.SetBundledMod("Defiling", true);
            Settings.SetBundledMod("SweetWater", true);
        }
        ShowStatus(install, null);
        if (args.Contains("--check-windows"))
        {
            CheckWindows();
            return;
        }
        if (regionCheck)
        {
            ChooseStart(args);
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
            Session.StartNewGame();
            RegionView.StartNewGame();
            ChooseStart(args);
            ChangeScene(RegionView.ScenePath);
        }
        else if (install is not null)
        {
            // Development: --cinematic=N plays cinematic N (1 the arrival, 100 the ending) instead of the intro.
            if (args.FirstOrDefault(a => a.StartsWith("--cinematic=")) is { } cinematic && int.TryParse(cinematic["--cinematic=".Length..], out int n)
                && DarkSun.Game.Cinematics.CinematicPlayer.Cinematics.ContainsKey(n))
            {
                IntroView.Cinematic = n;
            }
            Start(skipIntro: args.Contains("--no-intro"));
        }
    }

    /// <summary>The intro (once per run), then the main menu.</summary>
    private void Start(bool skipIntro)
    {
        bool intro = !skipIntro && !Settings.SkipIntro && !_introPlayed;
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

    /// <summary>
    /// <c>--check-windows</c>: build every <c>WIND</c> from its data
    /// (<see cref="UiWindowView"/>) and say what each holds and any control
    /// whose art is missing.
    /// </summary>
    private void CheckWindows()
    {
        GffFile resources = Session.Open("RESOURCE.GFF");
        var palette = Session.Assets.Palette(resources.Read("PAL ", 1001));
        int windows = 0, controls = 0;
        var missing = new List<string>();
        foreach (var r in resources.DescribeResources().Where(r => r.Tag == "WIND"))
        {
            if (UiWindowView.Build(r.Number, palette) is not { } view)
            {
                missing.Add($"WIND {r.Number}: not built");
                continue;
            }
            windows++;
            var items = view.Items.ToList();
            controls += items.Count;
            foreach (var (tag, number, control) in items)
            {
                if (control is TextureButton { TextureNormal: null } b && resources.TryRead("BUTN", number, out var butn)
                    && DarkSun.Data.Ui.UiControl.Parse(butn.Span).Icon is int icon and > 0)
                {
                    missing.Add($"WIND {r.Number}: BUTN {number} has no ICON {icon}");
                }
            }
            GD.Print($"  WIND {r.Number} {view.Size.X}x{view.Size.Y}: "
                + string.Join(" ", items.GroupBy(i => i.Tag).Select(g => $"{g.Key}×{g.Count()}"))
                + (view.GetNodeOrNull("Backdrop") is not null ? " over its BMP" : ""));
            view.Free();
        }
        foreach (string m in missing)
        {
            GD.Print($"  missing: {m}");
        }
        GD.Print($"windows: {windows} built from their data, {controls} controls, {missing.Count} missing art");
        GetTree().Quit(windows > 0 ? 0 : 1);
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

    /// <summary>A new game's start from <c>--region=N</c> (at (64, 49)) and <c>--at=X,Y</c>, for development.</summary>
    private static void ChooseStart(IEnumerable<string> args)
    {
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
    }
}
