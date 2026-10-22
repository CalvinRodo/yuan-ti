namespace DarkSun.Data.Tests;

public sealed class GameInstallTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("darksun-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void Touch(params string[] names)
    {
        foreach (string name in names)
        {
            File.WriteAllBytes(Path.Combine(_dir, name), []);
        }
    }

    [Fact]
    public void OpensDirectoryWithRequiredFilesInAnyCase()
    {
        Touch("gpldata.gff", "Resource.GFF", "SEGOBJEX.GFF");

        var install = GameInstall.TryOpen(_dir, out string? error);

        Assert.NotNull(install);
        Assert.Null(error);
        Assert.Equal(Path.Combine(_dir, "gpldata.gff"), install.FindFile("GPLDATA.GFF"));
    }

    [Fact]
    public void ReportsMissingFiles()
    {
        Touch("GPLDATA.GFF");

        var install = GameInstall.TryOpen(_dir, out string? error);

        Assert.Null(install);
        Assert.Contains("RESOURCE.GFF, SEGOBJEX.GFF", error);
    }

    [Fact]
    public void OpensTheGffFilesItCanAndSkipsTheRest()
    {
        // The original game writes DARKRUN.GFF as it runs; an install can hold an empty one.
        foreach (string name in new[] { "GPLDATA.GFF", "resource.gff", "SEGOBJEX.GFF" })
        {
            File.WriteAllBytes(Path.Combine(_dir, name), GffFileTests.BuildGff());
        }
        Touch("DARKRUN.GFF");
        var install = GameInstall.TryOpen(_dir, out _)!;

        var skipped = new List<string>();
        var opened = install.OpenGffFiles((name, _) => skipped.Add(name)).ToList();

        Assert.Equal(["GPLDATA.GFF", "RESOURCE.GFF", "SEGOBJEX.GFF"], opened.Select(f => f.Name));
        Assert.Equal(["DARKRUN.GFF"], skipped);
    }

    [Fact]
    public void ListsRegionFiles()
    {
        Touch("GPLDATA.GFF", "RESOURCE.GFF", "SEGOBJEX.GFF", "RGN0A.GFF", "RGN02.GFF", "RGNFF.GFF",
            "RGNXY.GFF", "RGN02.BAK");

        var install = GameInstall.TryOpen(_dir, out _)!;

        Assert.Equal(["RGN02.GFF", "RGN0A.GFF", "RGNFF.GFF"], install.RegionFiles);
    }

    [Fact]
    public void LocateSkipsInvalidCandidates()
    {
        Touch("GPLDATA.GFF", "RESOURCE.GFF", "SEGOBJEX.GFF");

        var install = GameInstall.Locate([null, "", Path.Combine(_dir, "nope"), _dir]);

        Assert.Equal(Path.GetFullPath(_dir), install?.Directory);
    }
}
