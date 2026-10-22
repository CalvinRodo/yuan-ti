using DarkSun.Data.Gff;

namespace DarkSun.Data;

/// <summary>
/// A directory holding the player's copy of Dark Sun: Shattered Lands.
/// File names are matched case-insensitively, as DOS did, so an install
/// copied to a case-sensitive file system still works.
/// </summary>
public sealed class GameInstall
{
    /// <summary>Files the engine cannot start without.</summary>
    public static readonly IReadOnlyList<string> RequiredFiles =
        ["GPLDATA.GFF", "RESOURCE.GFF", "SEGOBJEX.GFF"];

    /// <summary>Environment variable that overrides the configured directory.</summary>
    public const string DirectoryVariable = "DARKSUN_DIR";

    private readonly Dictionary<string, string> _files;

    private GameInstall(string directory, Dictionary<string, string> files)
    {
        Directory = directory;
        _files = files;
    }

    public string Directory { get; }

    /// <summary>Region files (<c>RGNxx.GFF</c>), sorted by name.</summary>
    public IReadOnlyList<string> RegionFiles =>
        _files.Keys.Where(IsRegionFile).Order(StringComparer.Ordinal).ToList();

    /// <summary>Every GFF file in the install, sorted by name.</summary>
    public IReadOnlyList<string> GffFiles =>
        _files.Keys.Where(n => n.EndsWith(".GFF", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Every GFF file that opens, by name (upper case, sorted). One that can't
    /// is left out and passed to <paramref name="skipped"/> with the reason: the
    /// original game writes <c>DARKRUN.GFF</c> as it runs, and an install can hold an
    /// empty one.
    /// </summary>
    public IEnumerable<(string Name, GffFile File)> OpenGffFiles(Action<string, string>? skipped = null)
    {
        foreach (string name in GffFiles)
        {
            GffFile? gff = null;
            try
            {
                gff = GffFile.Open(_files[name]);
            }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                skipped?.Invoke(name, e.Message);
            }
            if (gff is not null)
            {
                yield return (name.ToUpperInvariant(), gff);
            }
        }
    }

    /// <summary>
    /// Open <paramref name="directory"/> as a game install, or return null
    /// with the reason in <paramref name="error"/>.
    /// </summary>
    public static GameInstall? TryOpen(string directory, out string? error)
    {
        if (!System.IO.Directory.Exists(directory))
        {
            error = $"{directory} does not exist";
            return null;
        }

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in System.IO.Directory.EnumerateFiles(directory))
        {
            files.TryAdd(Path.GetFileName(path), path);
        }

        var missing = RequiredFiles.Where(f => !files.ContainsKey(f)).ToList();
        if (missing.Count > 0)
        {
            error = $"{directory} is missing {string.Join(", ", missing)}";
            return null;
        }

        error = null;
        return new GameInstall(Path.GetFullPath(directory), files);
    }

    /// <summary>
    /// The first valid install among <paramref name="candidates"/>, after
    /// <see cref="DirectoryVariable"/> if it is set.
    /// </summary>
    public static GameInstall? Locate(IEnumerable<string?> candidates)
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable(DirectoryVariable);
        foreach (string? dir in candidates.Prepend(fromEnvironment))
        {
            if (!string.IsNullOrWhiteSpace(dir) && TryOpen(dir, out _) is { } install)
            {
                return install;
            }
        }
        return null;
    }

    /// <summary>Full path of <paramref name="name"/> in the install, or null.</summary>
    public string? FindFile(string name) => _files.GetValueOrDefault(name);

    private static bool IsRegionFile(string name) =>
        name.Length == "RGNxx.GFF".Length
        && name.StartsWith("RGN", StringComparison.OrdinalIgnoreCase)
        && name.EndsWith(".GFF", StringComparison.OrdinalIgnoreCase)
        && Uri.IsHexDigit(name[3]) && Uri.IsHexDigit(name[4]);
}
