// Usage:
//   DarkSun.ModTool export <game-dir> <file.gff> <tag> <number> <mod-dir> [--palette <file.gff>:<number>] [--this-file]
//   DarkSun.ModTool list <game-dir> <mods-dir>
//
// export writes a resource into a mod folder in the form the engine reads
// back: images as PNG (one file, or a folder with one PNG per frame), text
// (TEXT, MERR, SPIN, NAME) as UTF-8 .txt, anything else as .bin. Edit the file
// and the engine uses it. The path is <mod-dir>/<TAG>/<number>.<ext>, which
// replaces the resource in whichever file has it; with --this-file it is
// <mod-dir>/<FILE>.GFF/<TAG>/..., for that file only. Images are drawn in
// PAL --palette (default: the file's first PAL, else RESOURCE.GFF PAL 1001).
//
// list prints every resource the mods in <mods-dir> replace or add.

using System.Globalization;
using System.Text;
using DarkSun.Data.Gff;
using DarkSun.Data.Images;
using DarkSun.Data.Mods;

string[] imageTags = ["BMP ", "CBMP", "TILE", "ICON", "PORT", "WALL", "FONT"];
string[] textTags = ["TEXT", "MERR", "SPIN", "NAME"];

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

    if (imageTags.Contains(tag))
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
if (args.Length == 3 && args[0] == "list")
{
    ModSet mods = ModSet.Load([args[2]]);
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
    return 0;
}
Console.Error.WriteLine("usage: DarkSun.ModTool export <game-dir> <file.gff> <tag> <number> <mod-dir> [--palette <file.gff>:<number>] [--this-file]");
Console.Error.WriteLine("       DarkSun.ModTool list <game-dir> <mods-dir>");
return 2;

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
