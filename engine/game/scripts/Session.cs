using DarkSun.Data;
using DarkSun.Data.Assets;
using DarkSun.Data.Mods;
using DarkSun.Data.Gff;
using DarkSun.Game.Characters;
using DarkSun.Game.Rules;
using DarkSun.Game.Scripting;
using DarkSun.Game.World;

namespace DarkSun.Engine;

/// <summary>
/// State shared between scenes: the game install, the GFF files every scene
/// needs (opened once, with the mods applied), and the asset cache.
/// </summary>
public static class Session
{
    private static readonly Dictionary<string, GffFile> Files = new(StringComparer.OrdinalIgnoreCase);

    public static GameInstall? Install { get; private set; }

    /// <summary>The mods, found in <c>DARKSUN_MODS</c>, the user data folder's <c>mods</c> and the game folder's <c>mods</c>.</summary>
    public static ModSet Mods { get; private set; } = ModSet.Empty;

    /// <summary>Decoded images and palettes, shared by every scene.</summary>
    public static AssetCache Assets { get; } = new();

    private static GplMachine? _scripts;

    /// <summary>Script host: carries out dialogue and the world commands the creature model supports, and records the rest.</summary>
    public static WorldGplHost ScriptHost { get; private set; } = new();

    /// <summary>The GPL interpreter for this session; the game-start script has run when <see cref="GameStarted"/>.</summary>
    public static GplMachine Scripts
    {
        get
        {
            if (_scripts is null)
            {
                GffFile gpl = Open("GPLDATA.GFF");
                byte[] fieldTypes = gpl.TryRead("GPLX", 1, out var gplx) ? GplDecoder.FieldTypes(gplx.Span) : [];
                _scripts = new GplMachine(new GffScripts(gpl), ScriptHost, fieldTypes, BorlandRandom.FromTime(DateTimeOffset.Now))
                {
                    Breakpoints = Breakpoints,
                };
            }
            return _scripts;
        }
    }

    public static bool GameStarted { get; set; }

    /// <summary>A headless check is running (<c>--check-…</c>).</summary>
    public static bool Checking { get; } = Godot.OS.GetCmdlineUserArgs().Any(a => a.StartsWith("--check", StringComparison.Ordinal));

    private const string BreakpointsFile = "user://breakpoints.txt";

    private static GplBreakpoints? _breakpoints;

    /// <summary>
    /// The script debugger's breakpoints (<c>ScriptDebugger</c>): every
    /// machine this session makes gets them, and they are kept in the user
    /// data folder's <c>breakpoints.txt</c>, one per line (<c>GPL 12 @0040</c>).
    /// </summary>
    public static GplBreakpoints Breakpoints
    {
        get
        {
            if (_breakpoints is null && Checking)
            {
                // The headless checks run the scripts through: the player's breakpoints would stop them.
                _breakpoints = new GplBreakpoints();
            }
            if (_breakpoints is null)
            {
                _breakpoints = new GplBreakpoints();
                string path = Godot.ProjectSettings.GlobalizePath(BreakpointsFile);
                if (File.Exists(path))
                {
                    _breakpoints.Read(File.ReadAllText(path));
                }
                _breakpoints.Changed += () =>
                {
                    try
                    {
                        File.WriteAllText(path, _breakpoints.Write());
                    }
                    catch (IOException e)
                    {
                        Godot.GD.PushWarning($"Couldn't keep the breakpoints: {e.Message}");
                    }
                };
            }
            return _breakpoints;
        }
    }

    private static List<CharacterSprite>? _sprites;

    /// <summary>
    /// Object definitions that can walk: an <c>SCMD</c> with a moving walk
    /// sequence in every direction, one per <c>BMP </c>, in <c>OJFF</c> order.
    /// Party members walk as one of these (DSUN.EXE's choice per race isn't traced).
    /// </summary>
    public static List<CharacterSprite> CharacterSprites => _sprites ??= LoadSprites();

    private static List<CharacterSprite> LoadSprites()
    {
        GffFile objects = Open("SEGOBJEX.GFF");
        var scripts = new Dictionary<int, DarkSun.Data.Animation.Scmd?>();
        var seenBmps = new HashSet<int>();
        var list = new List<CharacterSprite>();
        foreach (ResourceDescriptor r in objects.DescribeResources().Where(r => r.Tag == "OJFF").OrderBy(r => r.Number))
        {
            var def = DarkSun.Data.Regions.ObjectDefinition.Parse(objects.Read(r.Tag, r.Number).Span);
            if (def.Scmd <= 0 || seenBmps.Contains(def.Bmp))
            {
                continue;
            }
            if (!scripts.TryGetValue(def.Scmd, out var script))
            {
                script = objects.TryRead("SCMD", def.Scmd, out var data) ? DarkSun.Data.Animation.Scmd.Parse(data.Span) : null;
                scripts[def.Scmd] = script;
            }
            if (script is { CanWalk: true } && objects.TryRead("BMP ", def.Bmp, out var image))
            {
                seenBmps.Add(def.Bmp);
                var record = objects.TryRead("RDFF", r.Number, out var rdff) ? DarkSun.Data.Regions.CharacterRecord.FromRdff(rdff) : null;
                list.Add(new CharacterSprite(r.Number, script, Assets.Frames(image), DarkSun.Game.Movement.Footprint.Of(record)));
            }
        }
        return list;
    }

    /// <summary>The party for the next new game, as made on the Create Characters screen (empty: four placeholder characters).</summary>
    public static List<Character> Party { get; } = [];

    /// <summary>Characters kept between games (the manual's ADD and DROP): <c>characters</c> in the Godot user data folder.</summary>
    public static CharacterRoster Roster => new(Godot.ProjectSettings.GlobalizePath("user://characters"));

    private static string PartyPath => Godot.ProjectSettings.GlobalizePath("user://party.json");

    /// <summary>Keep <see cref="Party"/> for the next run.</summary>
    public static void SaveParty()
    {
        try
        {
            File.WriteAllText(PartyPath, System.Text.Json.JsonSerializer.Serialize(Party.Select(CharacterRoster.ToJson).ToList()));
        }
        catch (IOException e)
        {
            Godot.GD.PushWarning($"Couldn't keep the party: {e.Message}");
        }
    }

    private static void LoadParty()
    {
        Party.Clear();
        try
        {
            if (File.Exists(PartyPath) && System.Text.Json.JsonSerializer.Deserialize<List<string>>(File.ReadAllText(PartyPath)) is { } list)
            {
                Party.AddRange(list.Select(CharacterRoster.FromJson).Take(GameWorld.PartySize));
            }
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            Godot.GD.PushWarning($"Couldn't read the party: {e.Message}");
        }
    }

    private static CharacterRules? _rules;

    /// <summary>The character creation rules: a mod's <c>rules/characters.json</c> if one has it, else the original's (<see cref="CharacterRules.Default"/>).</summary>
    public static CharacterRules Rules
    {
        get
        {
            if (_rules is null)
            {
                string? path = Mods.Mods.Select(m => Path.Combine(m.Directory, "rules", "characters.json")).FirstOrDefault(File.Exists);
                try
                {
                    _rules = path is null ? CharacterRules.Default : CharacterRules.Read(path);
                }
                catch (InvalidDataException e)
                {
                    Godot.GD.PushWarning($"Mod file not used: {path}: {e.Message}");
                    _rules = CharacterRules.Default;
                }
            }
            return _rules;
        }
    }

    /// <summary>The names new characters get (<c>TEXT</c> in <c>RESOURCE.GFF</c>).</summary>
    public static CharacterNames Names => _names ??= CharacterNames.Load(Open("RESOURCE.GFF"));

    private static CharacterNames? _names;

    /// <summary>Where saved games go: <c>saves</c> in the Godot user data folder.</summary>
    public static string SavesFolder => Godot.ProjectSettings.GlobalizePath("user://saves");

    public static string SavePath(string name) => Path.Combine(SavesFolder, name + ".json");

    /// <summary>The saved games, newest first, with the ones that can't be read (and why).</summary>
    public static IReadOnlyList<(string Path, DarkSun.Game.Saves.SaveGame? Save, string? Error)> ListSaves()
    {
        if (!Directory.Exists(SavesFolder))
        {
            return [];
        }
        var list = new List<(string, DarkSun.Game.Saves.SaveGame?, string?)>();
        foreach (string path in Directory.EnumerateFiles(SavesFolder, "*.json"))
        {
            try
            {
                list.Add((path, DarkSun.Game.Saves.SaveGame.Read(path), null));
            }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                list.Add((path, null, e.Message));
            }
        }
        // Readable saves first, newest first; the ones that can't be read after.
        return [.. list.OrderBy(s => s.Item2 is null).ThenByDescending(s => s.Item2?.Saved ?? File.GetLastWriteTime(s.Item1))];
    }

    /// <summary>The regions the party has left this game, as it left them.</summary>
    public static RegionMemory Regions { get; } = new();

    /// <summary>
    /// A script host for a new game: AWARD reaches the party's characters,
    /// and each member starts with their character's hit points (10 for a
    /// placeholder member).
    /// </summary>
    private static WorldGplHost NewHost()
    {
        var host = new WorldGplHost { PartyCharacter = slot => slot < Party.Count ? Party[slot] : null };
        for (int slot = 0; slot < host.PartyMembers.Count; slot++)
        {
            if (slot < Party.Count && Party[slot].Hp > 0)
            {
                Vitals v = host.PartyMembers[slot].Vitals;
                (v.Hp, v.MaxHp) = (Party[slot].Hp, Party[slot].Hp);
            }
        }
        return host;
    }

    /// <summary>Forget the current game: fresh scripts, globals and triggers.</summary>
    public static void NewGame()
    {
        _scripts = null;
        ScriptHost = NewHost();
        GameStarted = false;
        Regions.Forget();
    }

    public static void Use(GameInstall install)
    {
        Install = install;
        Files.Clear();
        Assets.Clear();
        (_sprites, _rules, _names) = (null, null, null);
        GameFonts.Reset();
        LoadParty();
        Mods = ModSet.Load(ModFolders(install), GplModFormat.Builders);
        Mods.ProblemFound += p => Godot.GD.PushWarning($"Mod file not used: {p}");
        if (Mods.Mods.Count > 0)
        {
            Godot.GD.Print($"Mods: {string.Join(", ", Mods.Mods.Select(m => m.Name))} ({Mods.Count} resources)");
        }
        _scripts = null;
        ScriptHost = NewHost();
        GameStarted = false;
        Regions.Forget();
    }

    /// <summary>Where mods are looked for, first first: <c>DARKSUN_MODS</c>, the user data folder's <c>mods</c>, the game folder's <c>mods</c>.</summary>
    public static IReadOnlyList<string?> ModFolders(GameInstall install) =>
    [
        System.Environment.GetEnvironmentVariable(ModSet.FolderVariable),
        Godot.ProjectSettings.GlobalizePath("user://mods"),
        Path.Combine(install.Directory, "mods"),
    ];

    /// <summary>Load the mods again (after the mod list changes them), with fresh files, caches and game.</summary>
    public static void ReloadMods()
    {
        if (Install is not null)
        {
            Use(Install);
        }
    }

    /// <summary>Build everything the mods change, so <see cref="ModSet.Problems"/> lists every file that can't be used.</summary>
    public static IReadOnlyList<ModProblem> VerifyMods() =>
        Install is null ? [] : Mods.Verify(Install.GffFiles.Select(n => (n, GffFile.Open(Install.FindFile(n)!))));

    /// <summary>A GFF file from the install, opened on first use.</summary>
    public static GffFile Open(string name)
    {
        if (Install is null)
        {
            throw new InvalidOperationException("no game install selected");
        }
        if (!Files.TryGetValue(name, out GffFile? file))
        {
            string path = Install.FindFile(name) ?? throw new FileNotFoundException($"{name} not found in {Install.Directory}");
            file = Mods.Apply(GffFile.Open(path), name);
            Files[name] = file;
        }
        return file;
    }
}
