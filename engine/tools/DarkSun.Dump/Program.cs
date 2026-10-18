// Usage: DarkSun.Dump <game-dir> <out.tsv>
//
// One line per GFF resource, per decoded image frame and per palette, with
// SHA-1 hashes instead of content; the format matches tools/compare/gff_dump.py:
//   R  file  tag  number  offset  size  sha1(data)
//   I  file  tag  number  frame   WxH   sha1(pixels + mask)    (or "-  error")
//   P  file  tag  number  colors  sha1(rgb)                     (or "error")

using System.Security.Cryptography;
using DarkSun.Data.Gff;
using DarkSun.Data.Images;

string[] imageTags = ["BMP ", "CBMP", "TILE", "ICON", "PORT", "WALL", "FONT"];
string[] paletteTags = ["PAL ", "CPAL"];

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: DarkSun.Dump <game-dir> <out.tsv>");
    return 2;
}

using var output = new StreamWriter(args[1]) { NewLine = "\n" };
var files = Directory.EnumerateFiles(args[0], "*.GFF").Select(Path.GetFileName).Order(StringComparer.Ordinal);
foreach (string name in files!)
{
    GffFile gff = GffFile.Open(Path.Combine(args[0], name!));
    var resources = gff.DescribeResources();
    foreach (var r in resources)
    {
        output.WriteLine($"R\t{name}\t{r.Tag}\t{r.Number}\t{r.Offset}\t{r.Size}\t{Sha1(gff.Read(r.Tag, r.Number).Span)}");
    }
    foreach (var r in resources.Where(r => imageTags.Contains(r.Tag)))
    {
        string prefix = $"I\t{name}\t{r.Tag}\t{r.Number}";
        try
        {
            var frames = ImageDecoder.ReadFrames(gff.Read(r.Tag, r.Number).Span);
            for (int i = 0; i < frames.Count; i++)
            {
                ImageFrame f = frames[i];
                byte[] mask = [.. f.Opaque.Select(o => o ? (byte)1 : (byte)0)];
                output.WriteLine($"{prefix}\t{i}\t{f.Width}x{f.Height}\t{Sha1([.. f.Pixels, .. mask])}");
            }
        }
        catch (InvalidDataException)
        {
            output.WriteLine($"{prefix}\t-\terror");
        }
    }
    foreach (var r in resources.Where(r => paletteTags.Contains(r.Tag)))
    {
        string prefix = $"P\t{name}\t{r.Tag}\t{r.Number}";
        try
        {
            Palette p = Palette.FromBytes(gff.Read(r.Tag, r.Number).Span);
            byte[] rgb = [.. Enumerable.Range(0, p.Count).SelectMany(i => new[] { p[i].R, p[i].G, p[i].B })];
            output.WriteLine($"{prefix}\t{p.Count}\t{Sha1(rgb)}");
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException)
        {
            output.WriteLine($"{prefix}\terror");
        }
    }
}
return 0;

static string Sha1(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA1.HashData(data));
