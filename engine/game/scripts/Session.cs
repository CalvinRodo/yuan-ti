using DarkSun.Data;
using DarkSun.Data.Assets;
using DarkSun.Data.Mods;
using DarkSun.Data.Gff;
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
                _scripts = new GplMachine(new GffScripts(gpl), ScriptHost, fieldTypes, BorlandRandom.FromTime(DateTimeOffset.Now));
            }
            return _scripts;
        }
    }

    public static bool GameStarted { get; set; }

    /// <summary>Forget the current game: fresh scripts, globals and triggers.</summary>
    public static void NewGame()
    {
        _scripts = null;
        ScriptHost = new WorldGplHost();
        GameStarted = false;
    }

    public static void Use(GameInstall install)
    {
        Install = install;
        Files.Clear();
        Assets.Clear();
        Mods = ModSet.Load([
            System.Environment.GetEnvironmentVariable(ModSet.FolderVariable),
            Godot.ProjectSettings.GlobalizePath("user://mods"),
            Path.Combine(install.Directory, "mods"),
        ]);
        if (Mods.Mods.Count > 0)
        {
            Godot.GD.Print($"Mods: {string.Join(", ", Mods.Mods.Select(m => m.Name))} ({Mods.Count} resources)");
        }
        _scripts = null;
        ScriptHost = new WorldGplHost();
        GameStarted = false;
    }

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
