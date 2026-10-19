using System.Text;
using DarkSun.Data.Gff;
using DarkSun.Data.Images;

namespace DarkSun.Data.Mods;

/// <summary>A mod: a folder of loose files that replace game resources.</summary>
public sealed record Mod(string Name, string Directory);

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
/// terminating NUL, as the game's text resources are). Tags are matched
/// without their trailing spaces and ignoring case.
/// </summary>
public sealed class ModSet
{
    /// <summary>Environment variable naming an extra mods folder (searched first).</summary>
    public const string FolderVariable = "DARKSUN_MODS";

    public const string LoadOrderFile = "load_order.txt";

    private static readonly string[] Extensions = [".bin", ".png", ".txt"];

    // (file or "" for any file, tag, number) → where its replacement is.
    private readonly Dictionary<(string File, string Tag, int Number), Replacement> _replacements = [];

    private ModSet(IReadOnlyList<Mod> mods)
    {
        Mods = mods;
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

    public static ModSet Empty { get; } = new([]);

    /// <summary>Mods, highest priority first.</summary>
    public IReadOnlyList<Mod> Mods { get; }

    /// <summary>How many resources the mods replace or add.</summary>
    public int Count => _replacements.Count;

    /// <summary>The mods in <paramref name="roots"/> (mods folders; missing ones are skipped), earlier roots first.</summary>
    public static ModSet Load(IEnumerable<string?> roots)
    {
        var mods = new List<Mod>();
        foreach (string? root in roots.Distinct())
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                continue;
            }
            var folders = Directory.EnumerateDirectories(root)
                .Where(d => Path.GetFileName(d) is var n && !n.StartsWith('.') && !n.StartsWith('_'))
                .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
                .ToList();
            string orderFile = Path.Combine(root, LoadOrderFile);
            if (File.Exists(orderFile))
            {
                var order = File.ReadAllLines(orderFile).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
                folders = [.. folders.OrderBy(d =>
                {
                    int at = order.FindIndex(o => o.Equals(Path.GetFileName(d), StringComparison.OrdinalIgnoreCase));
                    return at < 0 ? int.MaxValue : at;
                })];
            }
            mods.AddRange(folders.Select(d => new Mod(Path.GetFileName(d), d)));
        }
        return new ModSet(mods);
    }

    /// <summary><paramref name="file"/> as the mods change it (the file itself if they don't).</summary>
    public GffFile Apply(GffFile file, string fileName)
    {
        string key = fileName.ToUpperInvariant();
        return _replacements.Keys.Any(k => k.File == "" || k.File == key) ? file.WithOverrides(new FileOverrides(this, key)) : file;
    }

    /// <summary>Which mod replaces a resource of <paramref name="fileName"/>, if any (for tools and diagnostics).</summary>
    public Mod? Replacing(string fileName, string tag, int number) =>
        Find(fileName.ToUpperInvariant(), tag, number)?.Mod;

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
            if (int.TryParse(Path.GetFileNameWithoutExtension(path), out int number)
                && Extensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
            {
                _replacements[(file, tag, number)] = new Replacement(mod, path, isFrames: false);
            }
        }
        foreach (string dir in Directory.EnumerateDirectories(tagDir))
        {
            if (int.TryParse(Path.GetFileName(dir), out int number))
            {
                _replacements[(file, tag, number)] = new Replacement(mod, dir, isFrames: true);
            }
        }
    }

    private sealed class Replacement(Mod mod, string path, bool isFrames)
    {
        private byte[]? _loaded;

        public Mod Mod { get; } = mod;

        public string Path { get; } = path;

        public bool IsFrames { get; } = isFrames;

        /// <summary>The replacement bytes, read once (so caches keyed by the bytes keep working).</summary>
        public byte[] Load(Func<ReadOnlyMemory<byte>?> original)
        {
            if (_loaded is not null)
            {
                return _loaded;
            }
            if (IsFrames)
            {
                var pngs = Directory.EnumerateFiles(Path, "*.png")
                    .Where(f => int.TryParse(System.IO.Path.GetFileNameWithoutExtension(f), out _))
                    .ToDictionary(f => int.Parse(System.IO.Path.GetFileNameWithoutExtension(f)), File.ReadAllBytes);
                _loaded = ModFrames.Build(original() is { } o ? o.Span : [], pngs);
            }
            else
            {
                _loaded = System.IO.Path.GetExtension(Path).ToLowerInvariant() == ".txt"
                    ? Text(File.ReadAllText(Path, Encoding.UTF8))
                    : File.ReadAllBytes(Path);
            }
            return _loaded;
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
            data = replacement.Load(original);
            return true;
        }

        public IEnumerable<(string Tag, int Number)> Added =>
            mods._replacements.Keys.Where(k => k.File == file).Select(k => (GffFile.NormalizeTag(k.Tag), k.Number));
    }
}
