// Usage:
//   DarkSun.ModTool export <game-dir> <file.gff> <tag> <number> <mod-dir> [--palette <file.gff>:<number>] [--this-file] [--bin]
//   DarkSun.ModTool list <game-dir> <mods-dir>
//   DarkSun.ModTool check <game-dir> <mods-dir>
//   DarkSun.ModTool gpl-check <game-dir>
//   DarkSun.ModTool rules <mod-dir>
//
// export writes a resource into a mod folder in the form the engine reads
// back: images as PNG (one file, or a folder with one PNG per frame), text
// (TEXT, MERR, SPIN) as UTF-8 .txt, items, monsters, regions and names
// (IT1R, RDFF, RDAT, NAME) as .json, scripts (GPL, MAS) as .gpl source,
// sound effects (BVOC) as .wav, the font (FONT) as a .png sheet (FontSheet),
// anything else (or anything, with --bin) as .bin. Edit the file and the
// engine uses it; a .json may be cut down to the fields it changes. The path
// is <mod-dir>/<TAG>/<number>.<ext>, which replaces the resource in whichever
// file has it; with --this-file it is <mod-dir>/<FILE>.GFF/<TAG>/..., for that
// file only. Images are drawn in PAL --palette (default: the file's first
// PAL, else RESOURCE.GFF PAL 1001).
//
// list prints every resource the mods in <mods-dir> replace or add; check
// builds them all and prints the files that can't be used (exit code 1 if
// any). gpl-check exports every script as source and assembles it again,
// and fails unless every one comes back byte for byte. rules writes the
// original's rule tables into <mod-dir>/rules/ (characters.json, creatures.json),
// keeping any that are there; check also reads the mods' rule files.

using System.Globalization;
using System.Text;
using DarkSun.Data.Gff;
using DarkSun.Data.Images;
using DarkSun.Data.Mods;
using DarkSun.Data.Text;
using DarkSun.Game.Characters;
using DarkSun.Game.Rules;
using DarkSun.Game.Scripting;

string[] imageTags = ["BMP ", "CBMP", "TILE", "ICON", "PORT", "WALL"];
string[] textTags = ["TEXT", "MERR", "SPIN"];
// The rule files a mod may have in rules/, and what reads each (throwing InvalidDataException when it can't be used).
(string File, Action<string> Read)[] ruleFiles = [("characters.json", p => CharacterRules.Read(p)), ("creatures.json", p => CreatureRules.Read(p))];

if (args.Length >= 6 && args[0] == "export")
{
    string gameDir = args[1], fileName = args[2].ToUpperInvariant();
    string tag = GffFile.NormalizeTag(args[3].ToUpperInvariant());
    int number = int.Parse(args[4], CultureInfo.InvariantCulture);
    string modDir = args[5];
    bool thisFile = args.Contains("--this-file");
    GffFile gff = GffFile.Open(Path.Combine(gameDir, fileName));
    if (!gff.TryRead(tag, number, out var data))
    {
        Console.Error.WriteLine($"no {tag.Trim()} {number} in {fileName}");
        return 1;
    }
    string dir = thisFile ? Path.Combine(modDir, fileName, tag.Trim()) : Path.Combine(modDir, tag.Trim());
    Directory.CreateDirectory(dir);

    if (args.Contains("--bin"))
    {
        Write(Path.Combine(dir, $"{number}.bin"), data.ToArray());
    }
    else if (RecordJson.Handles(tag))
    {
        var names = NameTable.Read(GffFile.Open(Path.Combine(gameDir, "GPLDATA.GFF")));
        Write(Path.Combine(dir, $"{number}.json"), Encoding.UTF8.GetBytes(RecordJson.Export(tag, data, i => names[i])));
    }
    else if (tag is "GPL " or "MAS ")
    {
        GffFile gpl = GffFile.Open(Path.Combine(gameDir, "GPLDATA.GFF"));
        byte[] fieldTypes = FieldTypes(gpl);
        var entries = tag == "GPL " ? GplAssembler.EntryPoints(Scripts(gpl), fieldTypes).GetValueOrDefault(number) : null;
        string source = GplAssembler.Disassemble(data.ToArray(), fieldTypes, entries, $"{tag.Trim()} {number} from {fileName}");
        Write(Path.Combine(dir, $"{number}{GplModFormat.Extension}"), Encoding.UTF8.GetBytes(source));
    }
    else if (imageTags.Contains(tag))
    {
        Palette palette = ChoosePalette(gameDir, gff, args);
        var frames = ImageDecoder.ReadFrames(data.Span);
        if (frames.Count == 1)
        {
            Write(Path.Combine(dir, $"{number}.png"), Png.Encode(frames[0].Width, frames[0].Height, frames[0].ToRgba(palette)));
        }
        else
        {
            string frameDir = Path.Combine(dir, number.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(frameDir);
            for (int i = 0; i < frames.Count; i++)
            {
                Write(Path.Combine(frameDir, $"{i}.png"), Png.Encode(frames[i].Width, frames[i].Height, frames[i].ToRgba(palette)));
            }
            Console.WriteLine($"  {frames.Count} frames; delete the ones you don't change, the rest keep the original's");
        }
    }
    else if (tag == "FONT")
    {
        Write(Path.Combine(dir, $"{number}.png"), FontSheet.Export(GameFont.Parse(data.Span)));
        Console.WriteLine("  16 × 16 cells of 8 × 10: white the letters, dark their shadow, the red row under each its width");
    }
    else if (tag == "BVOC")
    {
        Write(Path.Combine(dir, $"{number}.wav"), DarkSun.Data.Audio.Wav.Encode(DarkSun.Data.Audio.Voc.Decode(data.Span)));
    }
    else if (textTags.Contains(tag))
    {
        string text = Encoding.Latin1.GetString(data.Span).Split('\0')[0].Replace("\r\n", "\n");
        Write(Path.Combine(dir, $"{number}.txt"), Encoding.UTF8.GetBytes(text));
    }
    else
    {
        Write(Path.Combine(dir, $"{number}.bin"), data.ToArray());
    }
    return 0;
}
if (args.Length == 3 && args[0] == "check")
{
    ModSet mods = ModSet.Load([args[2]], GplModFormat.Builders);
    var problems = mods.Verify(Directory.EnumerateFiles(args[1], "*.GFF", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
        .Order().Select(p => (Path.GetFileName(p).ToUpperInvariant(), GffFile.Open(p))));
    var bad = new List<string>();
    foreach (var mod in mods.Mods)
    {
        foreach (var (file, read) in ruleFiles)
        {
            string path = Path.Combine(mod.Directory, "rules", file);
            try
            {
                if (File.Exists(path))
                {
                    read(path);
                }
            }
            catch (InvalidDataException e)
            {
                bad.Add($"{path}: {e.Message}");
            }
        }
    }
    Console.WriteLine($"{mods.Mods.Count} mods, {mods.Count} resources, {problems.Count + bad.Count} files can't be used");
    foreach (ModProblem p in problems)
    {
        Console.WriteLine($"  {p}");
    }
    foreach (string b in bad)
    {
        Console.WriteLine($"  {b}");
    }
    return problems.Count + bad.Count > 0 ? 1 : 0;
}
if (args.Length == 2 && args[0] == "rules")
{
    string dir = Path.Combine(args[1], "rules");
    Directory.CreateDirectory(dir);
    foreach (var (file, json) in new[] { ("characters.json", CharacterRules.Default.ToJson()), ("creatures.json", CreatureRules.Default.ToJson()) })
    {
        string path = Path.Combine(dir, file);
        if (File.Exists(path))
        {
            Console.WriteLine($"{path}: kept (already there)");
            continue;
        }
        File.WriteAllText(path, json);
        Console.WriteLine($"{path}: the original's rules");
    }
    return 0;
}
if (args.Length == 2 && args[0] == "gpl-check")
{
    GffFile gpl = GffFile.Open(Path.Combine(args[1], "GPLDATA.GFF"));
    byte[] fieldTypes = FieldTypes(gpl);
    var entries = GplAssembler.EntryPoints(Scripts(gpl), fieldTypes);
    int same = 0, total = 0, raw = 0, lines = 0;
    foreach (ResourceDescriptor r in gpl.DescribeResources().Where(r => r.Tag is "GPL " or "MAS "))
    {
        total++;
        byte[] data = gpl.Read(r.Tag, r.Number).ToArray();
        string source = GplAssembler.Disassemble(data, fieldTypes, r.Tag == "GPL " ? entries.GetValueOrDefault(r.Number) : null);
        string[] sourceLines = source.Split('\n');
        lines += sourceLines.Length;
        raw += sourceLines.Count(l => l.TrimStart().StartsWith(".bytes", StringComparison.Ordinal));
        try
        {
            if (GplAssembler.Assemble(source).AsSpan().SequenceEqual(data))
            {
                same++;
                continue;
            }
            Console.WriteLine($"{r.Tag.Trim()} {r.Number}: assembles to different bytes");
        }
        catch (GplAssemblyException e)
        {
            Console.WriteLine($"{r.Tag.Trim()} {r.Number}: {e.Message}");
        }
    }
    Console.WriteLine($"{same}/{total} scripts assemble back to the same bytes; {lines} lines of source, {raw} kept as .bytes");
    return same == total ? 0 : 1;
}
if (args.Length == 3 && args[0] == "list")
{
    ModSet mods = ModSet.Load([args[2]], GplModFormat.Builders);
    Console.WriteLine($"{mods.Mods.Count} mods, highest priority first: {string.Join(", ", mods.Mods.Select(m => m.Name))}");
    foreach (string path in Directory.EnumerateFiles(args[1], "*.GFF").Order())
    {
        string name = Path.GetFileName(path).ToUpperInvariant();
        GffFile original = GffFile.Open(path);
        GffFile modded = mods.Apply(original, name);
        foreach (ResourceDescriptor r in modded.DescribeResources())
        {
            if (mods.Replacing(name, r.Tag, r.Number) is { } mod && (original.Contains(r.Tag, r.Number) || r.Offset < 0))
            {
                bool added = !original.Contains(r.Tag, r.Number);
                Console.WriteLine($"{name}\t{r.Tag.Trim()} {r.Number}\t{(added ? "added" : "replaced")} by {mod.Name}");
            }
        }
    }
    foreach (ModProblem p in mods.Problems)
    {
        Console.WriteLine($"can't use {p}");
    }
    return 0;
}
Console.Error.WriteLine("usage: DarkSun.ModTool export <game-dir> <file.gff> <tag> <number> <mod-dir> [--palette <file.gff>:<number>] [--this-file] [--bin]");
Console.Error.WriteLine("       DarkSun.ModTool list <game-dir> <mods-dir>");
Console.Error.WriteLine("       DarkSun.ModTool check <game-dir> <mods-dir>");
Console.Error.WriteLine("       DarkSun.ModTool gpl-check <game-dir>");
Console.Error.WriteLine("       DarkSun.ModTool rules <mod-dir>");
return 2;

static byte[] FieldTypes(GffFile gpl) => gpl.TryRead("GPLX", 1, out var gplx) ? GplDecoder.FieldTypes(gplx.Span) : [];

static IEnumerable<byte[]> Scripts(GffFile gpl) =>
    gpl.DescribeResources().Where(r => r.Tag is "GPL " or "MAS ").Select(r => gpl.Read(r.Tag, r.Number).ToArray());

static void Write(string path, byte[] bytes)
{
    File.WriteAllBytes(path, bytes);
    Console.WriteLine($"wrote {path}");
}

static Palette ChoosePalette(string gameDir, GffFile gff, string[] args)
{
    int at = Array.IndexOf(args, "--palette");
    if (at >= 0 && at + 1 < args.Length && args[at + 1].Split(':') is [var file, var n])
    {
        GffFile source = GffFile.Open(Path.Combine(gameDir, file));
        return Palette.FromBytes(source.Read("PAL ", int.Parse(n, CultureInfo.InvariantCulture)).Span);
    }
    if (gff.DescribeResources().FirstOrDefault(r => r.Tag == "PAL ") is { Tag: not null } own)
    {
        return Palette.FromBytes(gff.Read(own.Tag, own.Number).Span);
    }
    return Palette.FromBytes(GffFile.Open(Path.Combine(gameDir, "RESOURCE.GFF")).Read("PAL ", 1001).Span);
}
