using DarkSun.Data;
using DarkSun.Data.Gff;
using DarkSun.Game.Rules;
using DarkSun.Game.Scripting;
using DarkSun.Game.World;

namespace DarkSun.Engine;

/// <summary>
/// State shared between scenes: the game install and the GFF files every
/// scene needs, opened once.
/// </summary>
public static class Session
{
    private static readonly Dictionary<string, GffFile> Files = new(StringComparer.OrdinalIgnoreCase);

    public static GameInstall? Install { get; private set; }

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
            file = GffFile.Open(path);
            Files[name] = file;
        }
        return file;
    }
}
