using System.Text;
using System.Text.Json;
using DarkSun.Data.Gff;
using DarkSun.Data.Images;

namespace DarkSun.Data.Mods;

/// <summary>
/// A mod: a folder of loose files that replace game resources.
/// <paramref name="Name"/> is the folder's name without a disabling <c>_</c> or <c>.</c>.
/// </summary>
public sealed record Mod(string Name, string Directory)
{
    /// <summary>The optional <c>mod.json</c>: <c>{"title", "version", "author", "description"}</c>.</summary>
    public const string InfoFile = "mod.json";

    public bool Enabled { get; init; } = true;

    /// <summary>The mods folder it is in.</summary>
    public string Root => Path.GetDirectoryName(Directory)!;

    public string Title { get; init; } = "";

    public string Version { get; init; } = "";

    public string Author { get; init; } = "";

    public string Description { get; init; } = "";

    /// <summary>A problem reading <see cref="InfoFile"/>, if any.</summary>
    public string? InfoError { get; init; }

    public string DisplayName => Title.Length > 0 ? Title : Name;

    internal static Mod Read(string directory)
    {
        string folder = Path.GetFileName(directory);
        bool enabled = !folder.StartsWith('.') && !folder.StartsWith('_');
        var mod = new Mod(enabled ? folder : folder[1..], directory) { Enabled = enabled };
        string info = Path.Combine(directory, InfoFile);
        if (!File.Exists(info))
        {
            return mod;
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(info), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            string Get(string key) => doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            return mod with { Title = Get("title"), Version = Get("version"), Author = Get("author"), Description = Get("description") };
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidOperationException)
        {
            return mod with { InfoError = $"{InfoFile}: {e.Message}" };
        }
    }
}

/// <summary>A mod file that couldn't be used; the game keeps the original resource.</summary>
public sealed record ModProblem(Mod Mod, string Path, string Message)
{
    public override string ToString() => $"{Mod.Name}: {System.IO.Path.GetRelativePath(Mod.Directory, Path)}: {Message}";
}

/// <summary>
/// Builds a resource from a mod file in a format of its own (a <c>.gpl</c>
/// script, say), given the resource's tag and the original bytes, if any.
/// Throws to report a problem.
/// </summary>
public delegate byte[] ModBuilder(string tag, string path, ReadOnlyMemory<byte>? original);

/// <summary>
/// The installed mods, highest priority first, and the resources they
/// replace. A mods folder holds one folder per mod; <c>load_order.txt</c>
/// in it, if present, lists mod folder names highest priority first (others
/// follow alphabetically); folders whose names start with <c>.</c> or
/// <c>_</c> are disabled. Inside a mod:
/// <list type="bullet">
/// <item><c>&lt;TAG&gt;/&lt;number&gt;.&lt;ext&gt;</c> replaces that resource in whichever
/// GFF file has it (<c>BMP/20028.png</c>, <c>GPL/12.bin</c>);</item>
/// <item><c>&lt;FILE&gt;.GFF/&lt;TAG&gt;/&lt;number&gt;.&lt;ext&gt;</c> only in that file,
/// and may add resources the file doesn't have;</item>
/// <item><c>&lt;TAG&gt;/&lt;number&gt;/&lt;frame&gt;.png</c> replaces single frames of an
/// image, keeping the others.</item>
/// </list>
/// Extensions: <c>.bin</c> the resource's bytes, as stored (any tag: scripts,
/// palettes, maps, sequences…); <c>.png</c> an image, drawn in its own
/// colours; <c>.txt</c> text (UTF-8, saved as Latin-1 with CR LF line ends and a
/// terminating NUL, as the game's text resources are); <c>.json</c> field
/// edits of items, monsters, regions and names (<see cref="RecordJson"/>);
/// and whatever the builders passed to <see cref="Load"/> handle (the game
/// adds <c>.gpl</c>, script source). Tags are matched without their trailing
/// spaces and ignoring case. A file that can't be read or built is reported
/// in <see cref="Problems"/> and the original resource is used.
/// </summary>
public sealed class ModSet
{
    /// <summary>Environment variable naming an extra mods folder (searched first).</summary>
    public const string FolderVariable = "DARKSUN_MODS";

    public const string LoadOrderFile = "load_order.txt";

    private static readonly string[] BuiltInExtensions = [".bin", ".png", ".txt", ".json"];

    // (file or "" for any file, tag, number) → where its replacement is.
    private readonly Dictionary<(string File, string Tag, int Number), Replacement> _replacements = [];
    private readonly Dictionary<string, ModBuilder> _builders;
    private readonly List<ModProblem> _problems = [];

    private ModSet(IReadOnlyList<Mod> all, IReadOnlyDictionary<string, ModBuilder>? builders)
    {
        All = all;
        Mods = [.. all.Where(m => m.Enabled)];
        _builders = new(builders ?? new Dictionary<string, ModBuilder>(), StringComparer.OrdinalIgnoreCase);
        _problems.AddRange(Mods.Where(m => m.InfoError is not null).Select(m => new ModProblem(m, Path.Combine(m.Directory, Mod.InfoFile), m.InfoError!)));
        IReadOnlyList<Mod> mods = Mods;
        // Lowest priority first, so higher ones overwrite.
        foreach (Mod mod in mods.Reverse())
        {
            foreach (string dir in Directory.EnumerateDirectories(mod.Directory))
            {
                string name = Path.GetFileName(dir);
                if (name.EndsWith(".GFF", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string tagDir in Directory.EnumerateDirectories(dir))
                    {
                        Index(mod, name.ToUpperInvariant(), tagDir);
                    }
                }
                else if (name.Length <= 4)
                {
                    Index(mod, "", dir);
                }
            }
        }
    }

    public static ModSet Empty { get; } = new([], null);

    /// <summary>Enabled mods, highest priority first.</summary>
    public IReadOnlyList<Mod> Mods { get; }

    /// <summary>Every mod found, disabled ones too, in priority order.</summary>
    public IReadOnlyList<Mod> All { get; }

    /// <summary>The mods folders searched, in order.</summary>
    public IReadOnlyList<string> Roots { get; private init; } = [];

    /// <summary>How many resources the mods replace or add.</summary>
    public int Count => _replacements.Count;

    /// <summary>Mod files that couldn't be used so far (they are built when the game first reads them; <see cref="Verify"/> builds them all).</summary>
    public IReadOnlyList<ModProblem> Problems
    {
        get
        {
            lock (_problems)
            {
                return [.. _problems];
            }
        }
    }

    /// <summary>Called for each new problem (to log it).</summary>
    public event Action<ModProblem>? ProblemFound;

    /// <summary>
    /// The mods in <paramref name="roots"/> (mods folders; missing ones are
    /// skipped), earlier roots first, with <paramref name="builders"/> for
    /// extra file formats by extension (".gpl").
    /// </summary>
    public static ModSet Load(IEnumerable<string?> roots, IReadOnlyDictionary<string, ModBuilder>? builders = null)
    {
        var mods = new List<Mod>();
        var used = new List<string>();
        foreach (string? root in roots.Distinct())
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                continue;
            }
            used.Add(root);
            var found = Directory.EnumerateDirectories(root).Select(Mod.Read)
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var order = ReadLoadOrder(root);
            mods.AddRange(found.OrderBy(m =>
            {
                int at = order.FindIndex(o => o.Equals(m.Name, StringComparison.OrdinalIgnoreCase));
                return at < 0 ? int.MaxValue : at;
            }));
        }
        return new ModSet(mods, builders) { Roots = used };
    }

    /// <summary>The names in a mods folder's <see cref="LoadOrderFile"/>, highest priority first.</summary>
    public static List<string> ReadLoadOrder(string root)
    {
        string file = Path.Combine(root, LoadOrderFile);
        return File.Exists(file)
            ? [.. File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))]
            : [];
    }

    /// <summary>
    /// Build every replacement the mods make to <paramref name="files"/> (the
    /// original GFF files, by name), so that <see cref="Problems"/> lists
    /// every file that can't be used.
    /// </summary>
    public IReadOnlyList<ModProblem> Verify(IEnumerable<(string Name, GffFile Original)> files)
    {
        foreach (var (name, original) in files)
        {
            GffFile modded = Apply(original, name);
            foreach (ResourceDescriptor r in modded.DescribeResources())
            {
                if (Replacing(name, r.Tag, r.Number) is not null)
                {
                    modded.TryRead(r.Tag, r.Number, out _);
                }
            }
        }
        return Problems;
    }

    /// <summary>What each enabled mod replaces or adds, by mod name.</summary>
    public IReadOnlyDictionary<string, int> CountsByMod() =>
        _replacements.Values.GroupBy(r => r.Mod.Name).ToDictionary(g => g.Key, g => g.Count());

    /// <summary><paramref name="file"/> as the mods change it (the file itself if they don't).</summary>
    public GffFile Apply(GffFile file, string fileName)
    {
        string key = fileName.ToUpperInvariant();
        return _replacements.Keys.Any(k => k.File == "" || k.File == key) ? file.WithOverrides(new FileOverrides(this, key)) : file;
    }

    /// <summary>Which mod replaces a resource of <paramref name="fileName"/>, if any (for tools and diagnostics).</summary>
    public Mod? Replacing(string fileName, string tag, int number) =>
        Find(fileName.ToUpperInvariant(), tag, number)?.Mod;

    /// <summary>The mod file or frames folder that replaces a resource, if any (the debugger shows a modded script's own source).</summary>
    public string? ReplacementPath(string fileName, string tag, int number) =>
        Find(fileName.ToUpperInvariant(), tag, number)?.Path;

    private Replacement? Find(string file, string tag, int number)
    {
        string t = tag.Trim().ToUpperInvariant();
        return _replacements.GetValueOrDefault((file, t, number)) ?? _replacements.GetValueOrDefault(("", t, number));
    }

    private void Index(Mod mod, string file, string tagDir)
    {
        string tag = Path.GetFileName(tagDir).ToUpperInvariant();
        foreach (string path in Directory.EnumerateFiles(tagDir))
        {
            string ext = Path.GetExtension(path);
            if (int.TryParse(Path.GetFileNameWithoutExtension(path), out int number)
                && (BuiltInExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase) || _builders.ContainsKey(ext)))
            {
                _replacements[(file, tag, number)] = new Replacement(this, mod, tag, path, isFrames: false);
            }
        }
        foreach (string dir in Directory.EnumerateDirectories(tagDir))
        {
            if (int.TryParse(Path.GetFileName(dir), out int number))
            {
                _replacements[(file, tag, number)] = new Replacement(this, mod, tag, dir, isFrames: true);
            }
        }
    }

    private void Report(ModProblem problem)
    {
        lock (_problems)
        {
            _problems.Add(problem);
        }
        ProblemFound?.Invoke(problem);
    }

    private sealed class Replacement(ModSet set, Mod mod, string tag, string path, bool isFrames)
    {
        private byte[]? _loaded;
        private bool _failed;

        public Mod Mod { get; } = mod;

        public string Path { get; } = path;

        public bool IsFrames { get; } = isFrames;

        /// <summary>
        /// The replacement bytes, built once (so caches keyed by the bytes keep
        /// working); null, reported once, if the file can't be used.
        /// </summary>
        public byte[]? Load(Func<ReadOnlyMemory<byte>?> original)
        {
            if (_loaded is not null || _failed)
            {
                return _loaded;
            }
            try
            {
                _loaded = Build(original);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _failed = true;
                set.Report(new ModProblem(Mod, Path, e.Message));
            }
            return _loaded;
        }

        private byte[] Build(Func<ReadOnlyMemory<byte>?> original)
        {
            if (IsFrames)
            {
                var pngs = Directory.EnumerateFiles(Path, "*.png")
                    .Where(f => int.TryParse(System.IO.Path.GetFileNameWithoutExtension(f), out _))
                    .ToDictionary(f => int.Parse(System.IO.Path.GetFileNameWithoutExtension(f)), File.ReadAllBytes);
                return ModFrames.Build(original() is { } o ? o.Span : [], pngs);
            }
            string ext = System.IO.Path.GetExtension(Path).ToLowerInvariant();
            if (set._builders.TryGetValue(ext, out ModBuilder? builder))
            {
                return builder(tag, Path, original());
            }
            return ext switch
            {
                ".txt" => Text(File.ReadAllText(Path, Encoding.UTF8)),
                ".json" => RecordJson.Apply(tag, original() is { } o ? o.Span : [], File.ReadAllText(Path, Encoding.UTF8)),
                _ => File.ReadAllBytes(Path),
            };
        }

        private static byte[] Text(string text)
        {
            string dos = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
            return [.. Encoding.Latin1.GetBytes(dos), 0];
        }
    }

    /// <summary>The replacements that apply to one GFF file.</summary>
    internal sealed class FileOverrides(ModSet mods, string file) : IGffOverrides
    {
        public bool TryRead(string tag, int number, Func<ReadOnlyMemory<byte>?> original, out ReadOnlyMemory<byte> data)
        {
            data = default;
            if (mods.Find(file, tag, number) is not { } replacement)
            {
                return false;
            }
            // A replacement for any file only applies where the resource exists.
            bool fileSpecific = mods._replacements.ContainsKey((file, tag.Trim().ToUpperInvariant(), number));
            if (!fileSpecific && original() is null)
            {
                return false;
            }
            if (replacement.Load(original) is not { } built)
            {
                return false;
            }
            data = built;
            return true;
        }

        public IEnumerable<(string Tag, int Number)> Added =>
            mods._replacements.Keys.Where(k => k.File == file).Select(k => (GffFile.NormalizeTag(k.Tag), k.Number));
    }
}
