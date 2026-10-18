using DarkSun.Data;
using DarkSun.Data.Gff;

namespace DarkSun.Engine;

/// <summary>
/// State shared between scenes: the game install and the GFF files every
/// scene needs, opened once.
/// </summary>
public static class Session
{
    private static readonly Dictionary<string, GffFile> Files = new(StringComparer.OrdinalIgnoreCase);

    public static GameInstall? Install { get; private set; }

    public static void Use(GameInstall install)
    {
        Install = install;
        Files.Clear();
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
