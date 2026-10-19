using System.Text;
using DarkSun.Data.Gff;
using DarkSun.Data.Items;
using DarkSun.Data.Mods;
using DarkSun.Data.Regions;
using DarkSun.Data.Text;

namespace DarkSun.Data.Tests;

public class RecordJsonTests
{
    private static byte[] Creature(string name, short hp)
    {
        var b = new Bytes();
        b.Byte(1, 0).Short(2).Short(249).Short(0).Short(CombatRecord.Size);
        byte[] combat = new byte[CombatRecord.Size];
        (combat[0], combat[1], combat[26]) = ((byte)hp, (byte)(hp >> 8), 5);
        Encoding.Latin1.GetBytes(name).CopyTo(combat, 40);
        b.Byte(combat);
        b.Byte(3, 0).Short(4).Short(5).Short(CharacterRecord.Length).Short(CharacterRecord.Length);
        b.Byte(new byte[CharacterRecord.Length]).Byte(0xFF);
        return b.ToArray();
    }

    [Fact]
    public void EditsOnlyTheFieldsNamed()
    {
        byte[] original = Creature("Gith", 12);

        byte[] edited = RecordJson.Apply("RDFF", original,
            """{ "combat": { "hp": 300, "name": "Gith Chief", "bytes": { "0x08": 7 } }, "character": { "size": 9 }, "_note": "ignored" }""");

        CombatRecord c = CombatRecord.Parse(edited)!;
        Assert.Equal((300, "Gith Chief", 5), (c.Hp, c.Name, c.Ac));
        Assert.Equal(7, edited[RdffEntry.HeaderSize + 8]);
        Assert.Equal(9, CharacterRecord.FromRdff(edited)!.Size);
        Assert.Equal(original.Length, edited.Length);
    }

    [Fact]
    public void ExportsEveryFieldAndReadsItBack()
    {
        byte[] original = ItemTests.ItemRdff(1, 3, 1, 2);

        string json = RecordJson.Export("RDFF", original, i => i == 1 ? "Long Sword" : null);

        Assert.Contains("\"_name\": \"Long Sword\"", json);
        Assert.Contains("\"bonus\": 2", json);
        Assert.Equal(original, RecordJson.Apply("RDFF", original, json));
        byte[] cheaper = RecordJson.Apply("RDFF", original, """{ "item": { "value": 999 } }""");
        Assert.Equal(999, ItemRecord.FromRdff(cheaper)!.Value);
    }

    [Fact]
    public void ItemTypesTakeEnumNamesAndCanBeAdded()
    {
        byte[] table = new byte[2 * ItemType.Size];

        byte[] edited = RecordJson.Apply("IT1R", table,
            """{ "1": { "weapon": "Melee, Thrown", "placement": "Hand", "damageBonus": -2 }, "3": { "range": 9 } }""");

        var types = ItemType.ParseAll(edited);
        Assert.Equal(4, types.Count);
        Assert.Equal((WeaponKind.Melee | WeaponKind.Thrown, ItemPlacement.Hand, (sbyte)-2), (types[1].Weapon, types[1].Placement, types[1].DamageBonus));
        Assert.Equal(9, types[3].Range);
        Assert.Contains("\"weapon\": \"Melee, Thrown\"", RecordJson.Export("IT1R", edited));
    }

    [Fact]
    public void NamesAndRegions()
    {
        var names = NameTable.Parse(RecordJson.Apply("NAME", new byte[NameTable.RecordSize], """{ "0": "Bone Club", "2": "Obsidian Axe" }"""));
        Assert.Equal(["Bone Club", "", "Obsidian Axe"], names.Names);

        RegionInfo region = RegionInfo.Parse(RecordJson.Apply("RDAT", new byte[RegionInfo.Size], """{ "music": 7, "cycle1Count": 4 }"""));
        Assert.Equal((7, 4), (region.Music, region.Cycle1.Count));
    }

    [Theory]
    [InlineData("RDFF", """{ "combat": { "hitpoints": 5 } }""", "combat.hitpoints: no such field")]
    [InlineData("RDFF", """{ "item": {} }""", "no item record (it has: combat, character)")]
    [InlineData("RDFF", """{ "combat": { "ac": 200 } }""", "combat.ac: 200 is out of range (-128 to 127)")]
    [InlineData("RDFF", """{ "combat": { "name": "A name far too long for it" } }""", "too long (at most 17 characters)")]
    [InlineData("IT1R", """{ "0": { "placement": "Tail" } }""", "or one of None, Chest")]
    [InlineData("RDFF", """{ "combat": { "hp": 5, } """, "")]
    public void SaysWhatIsWrong(string tag, string json, string message)
    {
        byte[] original = tag == "RDFF" ? Creature("Gith", 1) : new byte[ItemType.Size];

        var e = Assert.Throws<InvalidDataException>(() => RecordJson.Apply(tag, original, json));

        Assert.Contains(message, e.Message);
    }
}

public sealed class ModFormatTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "darksun-modformats-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void Write(string relative, string text)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static GffFile Gff() => GffFile.FromBytes(GffFileTests.BuildGff());

    private static string Text(GffFile gff, string tag, int n) => Encoding.Latin1.GetString(gff.Read(tag, n).Span);

    [Fact]
    public void BuildersHandleTheirExtensions()
    {
        Write("A/TILE/100.up", "loud");
        ModBuilder upper = (tag, path, original) =>
            Encoding.Latin1.GetBytes($"{tag}:{File.ReadAllText(path).ToUpperInvariant()}:{Encoding.Latin1.GetString(original!.Value.Span)}");

        GffFile gff = ModSet.Load([_root], new Dictionary<string, ModBuilder> { [".up"] = upper }).Apply(Gff(), "T.GFF");

        Assert.Equal("TILE:LOUD:t100", Text(gff, "TILE", 100));
        Assert.Equal(0, ModSet.Load([_root]).Count);
    }

    [Fact]
    public void FilesThatCantBeUsedAreReportedAndTheOriginalKept()
    {
        Write("A/TILE/100.fail", "x");
        Write("A/TEST.GFF/RDAT/3.json", "{ \"music\": 4 }");
        Write("B/mod.json", "{ not json");
        ModBuilder fail = (_, _, _) => throw new InvalidDataException("line 2: no label nowhere");
        ModSet mods = ModSet.Load([_root], new Dictionary<string, ModBuilder> { [".fail"] = fail });
        var reported = new List<ModProblem>();
        mods.ProblemFound += reported.Add;

        GffFile gff = mods.Apply(Gff(), "TEST.GFF");

        Assert.Equal("t100", Text(gff, "TILE", 100));
        Assert.Equal(4, RegionInfo.Parse(gff.Read("RDAT", 3).Span).Music);
        ModProblem tile = Assert.Single(reported);
        Assert.Equal("A: " + Path.Combine("TILE", "100.fail") + ": line 2: no label nowhere", tile.ToString());
        Assert.Equal(2, mods.Problems.Count);
        Assert.Contains(mods.Problems, p => p.Mod.Name == "B" && p.Message.StartsWith("mod.json"));
        gff.Read("TILE", 100);
        Assert.Equal(2, mods.Problems.Count);
    }

    [Fact]
    public void VerifyBuildsEverything()
    {
        Write("A/TEXT/9.json", "{}");

        var problems = ModSet.Load([_root]).Verify([("T.GFF", Gff())]);

        Assert.Contains("TEXT has no JSON form", Assert.Single(problems).Message);
    }

    [Fact]
    public void ReadsModInfoAndListsDisabledMods()
    {
        Write("Better Bows/mod.json", """{ "title": "Better Bows", "version": "1.2", "author": "Ana", "description": "Longer range." }""");
        Write("Better Bows/TILE/100.bin", "bows");
        Write("_Old/TILE/200.bin", "old!");

        ModSet mods = ModSet.Load([_root]);

        Mod bows = Assert.Single(mods.Mods);
        Assert.Equal(("Better Bows", "1.2", "Ana", "Longer range."), (bows.DisplayName, bows.Version, bows.Author, bows.Description));
        Assert.Equal(["Better Bows", "Old"], mods.All.Select(m => m.Name));
        Assert.False(mods.All[1].Enabled);
        Assert.Equal(1, mods.CountsByMod()["Better Bows"]);
    }

    [Fact]
    public void ManagerTurnsModsOnAndOffAndOrdersThem()
    {
        Write("Alpha/TILE/100.bin", "alfa");
        Write("Beta/TILE/100.bin", "beta");

        Mod alpha = ModSet.Load([_root]).Mods[0];
        Mod off = ModManager.SetEnabled(alpha, false);
        Assert.Equal(Path.Combine(_root, "_Alpha"), off.Directory);
        Assert.Equal("beta", Text(ModSet.Load([_root]).Apply(Gff(), "T.GFF"), "TILE", 100));

        ModManager.SetEnabled(off, true);
        ModManager.SaveLoadOrder(_root, ["Beta", "Alpha"]);
        ModSet reordered = ModSet.Load([_root]);
        Assert.Equal(["Beta", "Alpha"], reordered.Mods.Select(m => m.Name));
        Assert.Equal(["Beta", "Alpha"], ModSet.ReadLoadOrder(_root));

        Directory.CreateDirectory(Path.Combine(_root, "_Beta"));
        Assert.Throws<IOException>(() => ModManager.SetEnabled(reordered.Mods[0], false));
    }
}
