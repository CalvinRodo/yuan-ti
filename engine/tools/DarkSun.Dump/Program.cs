// Usage: DarkSun.Dump <game-dir> <out.tsv> [<gpl-listing.txt>]
//
// One line per GFF resource, per decoded image frame and per palette, with
// SHA-1 hashes instead of content; the format matches tools/compare/gff_dump.py:
//   R  file  tag  number  offset  size  sha1(data)
//   I  file  tag  number  frame   WxH   sha1(pixels + mask)    (or "-  error")
//   P  file  tag  number  colors  sha1(rgb)                     (or "error")
//   M  file  WxH  sha1(rgb)   whole-region render, for RGNxx.GFF  (or "error")
//   T  file  tag  number  sha1(decoded text)        TEXT, MERR, SPIN
//   S  file  number  spell name  sha1(description)  SPIN
//   N  file  number  index  name                    NAME table records
//   Y  file  type  weapon,weight,placement,range,attacks,dice,sides,bonus,AC,classes,material   IT1R
//   Q  file  rdff  id  type  name number  bonus  name  item records (RDFF type 1)

using System.Security.Cryptography;
using System.Text;
using DarkSun.Data.Gff;
using DarkSun.Data.Images;
using DarkSun.Data.Regions;
using DarkSun.Data.Items;
using DarkSun.Data.Text;
using DarkSun.Game.Scripting;

string[] imageTags = ["BMP ", "CBMP", "TILE", "ICON", "PORT", "WALL", "FONT"];
string[] paletteTags = ["PAL ", "CPAL"];

if (args.Length is < 2 or > 3)
{
    Console.Error.WriteLine("usage: DarkSun.Dump <game-dir> <out.tsv> [<gpl-listing.txt>]");
    return 2;
}

// The listing of every GPL and MAS script, as tools/compare/gpl_listing.py writes it.
if (args.Length == 3)
{
    GffFile scripts = GffFile.Open(Path.Combine(args[0], "GPLDATA.GFF"));
    byte[] fieldTypes = scripts.TryRead("GPLX", 1, out var gplx) ? GplDecoder.FieldTypes(gplx.Span) : [];
    using var listing = new StreamWriter(args[2]) { NewLine = "\n" };
    foreach (string tag in new[] { "GPL ", "MAS " })
    {
        foreach (int n in scripts.DescribeResources().Where(r => r.Tag == tag).Select(r => r.Number).Order())
        {
            listing.WriteLine($"; {tag.Trim()} {n}");
            foreach (string line in GplDecoder.Listing(scripts.Read(tag, n).ToArray(), fieldTypes))
            {
                listing.WriteLine(line);
            }
        }
    }
}

using var output = new StreamWriter(args[1]) { NewLine = "\n" };
GffFile segObjEx = GffFile.Open(Path.Combine(args[0], "SEGOBJEX.GFF"));
GffFile gplData = GffFile.Open(Path.Combine(args[0], "GPLDATA.GFF"));
GffFile resourceGff = GffFile.Open(Path.Combine(args[0], "RESOURCE.GFF"));
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
    foreach (var r in resources)
    {
        if (GameText.TextTags.Contains(r.Tag))
        {
            ReadOnlySpan<byte> raw = gff.Read(r.Tag, r.Number).Span;
            output.WriteLine($"T\t{name}\t{r.Tag}\t{r.Number}\t{Sha1(Encoding.UTF8.GetBytes(GameText.Decode(raw)))}");
            if (r.Tag == "SPIN")
            {
                SpellInfo spell = SpellInfo.Parse(r.Number, raw);
                output.WriteLine($"S\t{name}\t{r.Number}\t{spell.Name}\t{Sha1(Encoding.UTF8.GetBytes(spell.Description))}");
            }
        }
        else if (r.Tag == "NAME")
        {
            NameTable names = NameTable.Parse(gff.Read(r.Tag, r.Number).Span);
            for (int i = 0; i < names.Count; i++)
            {
                output.WriteLine($"N\t{name}\t{r.Number}\t{i}\t{names[i]}");
            }
        }
    }
    if (name == "GPLDATA.GFF" && gff.TryRead("IT1R", 1, out var it1r))
    {
        foreach (ItemType t in ItemType.ParseAll(it1r.Span))
        {
            output.WriteLine($"Y\t{name}\t{t.Number}\t{(int)t.Weapon},{t.Weight},{(int)t.Placement},{t.Range},{t.Attacks},"
                + $"{t.DamageDice},{t.DamageSides},{t.DamageBonus},{t.ArmorClass},{t.LegalClasses},{t.Material}");
        }
    }
    if (name == "SEGOBJEX.GFF")
    {
        var catalog = ItemCatalog.Load(gplData);
        foreach (var r in resources.Where(r => r.Tag == "RDFF"))
        {
            if (ItemRecord.FromRdff(gff.Read(r.Tag, r.Number)) is { } item)
            {
                output.WriteLine($"Q\t{name}\t{r.Number}\t{item.Id}\t{item.Type}\t{item.NameIndex}\t{item.Bonus}\t{catalog.NameOf(item)}");
            }
        }
    }
    if (name!.StartsWith("RGN", StringComparison.Ordinal))
    {
        try
        {
            Region region = Region.Load(gff, [segObjEx, gplData]);
            byte[] rgb = RegionRenderer.RenderRgb(region, region.LoadPalette(gplData, resourceGff));
            output.WriteLine($"M\t{name}\t{region.Width * Region.TileSize}x{region.Height * Region.TileSize}\t{Sha1(rgb)}");
        }
        catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or InvalidOperationException)
        {
            output.WriteLine($"M\t{name}\terror");
        }
    }
}
return 0;

static string Sha1(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA1.HashData(data));
