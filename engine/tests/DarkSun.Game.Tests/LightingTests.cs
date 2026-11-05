using System.Numerics;
using DarkSun.Data.Images;
using DarkSun.Data.Regions;
using DarkSun.Game.Animation;
using DarkSun.Game.Lighting;

namespace DarkSun.Game.Tests;

/// <summary>The map's light (<c>[display] lighting</c>): its data, the sky over the day, and the lights a region gives of itself.</summary>
public class LightingTests
{
    /// <summary>The engine's game folder (<c>engine/game</c>).</summary>
    private static string GameFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, "engine", "game");
            if (File.Exists(Path.Combine(path, "data", "lights.json")))
            {
                return path;
            }
        }
        throw new DirectoryNotFoundException("engine/game");
    }

    private static float Luma(Vector3 c) => 0.3f * c.X + 0.59f * c.Y + 0.11f * c.Z;

    [Fact]
    public void TheShippedLightsReadAndEverySpellLightHasItsParticles()
    {
        string game = GameFolder();
        LightRules rules = LightRules.Read(Path.Combine(game, "data", "lights.json"));
        Assert.NotEmpty(rules.Day);
        Assert.True(Luma(rules.Ambient(13, indoors: false)) > 1, "a hot glare at noon");
        Assert.True(Luma(rules.Ambient(0, indoors: false)) < 0.6f, "dark at midnight");
        Assert.Equal(rules.Ambient(13, indoors: true), rules.Ambient(0, indoors: true));
        Assert.Contains(rules.Palette, p => p.From == 240 && !p.HotOnly);
        Assert.Contains(rules.Palette, p => p.HotOnly);
        Assert.True(rules.Conditions.ContainsKey("FlameBlade"));

        // The lights share the particles' points: each spell part that throws light has particles there.
        var looks = new ParticleLooks([File.ReadAllText(Path.Combine(game, "looks", "particle_looks.json"))], new Dictionary<string, ParticleDefinition>());
        var lights = new SpellLights(SpellLights.Parse(File.ReadAllText(Path.Combine(game, "looks", "spell_lights.json"))));
        Assert.True(lights.Count > 100);
        Assert.NotNull(lights.For(28, LookPart.Burst)); // Fireball
        Assert.NotNull(lights.For(55, LookPart.Field)); // Wall of Fire
        Assert.Null(lights.For(23, LookPart.Field)); // Stinking Cloud: smoke gives no light
        foreach (int spell in Enumerable.Range(1, 196))
        {
            foreach (LookPart part in Enum.GetValues<LookPart>().Where(p => lights.For(spell, p) is not null))
            {
                Assert.Contains(looks.Looks.Keys, k => k.Spell == spell && k.Part == part);
            }
        }
    }

    private static LightRules Day(params DayKey[] keys) => new() { Day = keys };

    [Fact]
    public void TheSkyBlendsBetweenItsHoursAndAcrossMidnight()
    {
        LightRules rules = Day(new(6, "#000000", 1), new(18, "#ffffff", 1));
        Assert.Equal(Vector3.Zero, rules.Ambient(6, false));
        Assert.Equal(Vector3.One, rules.Ambient(18, false));
        Assert.Equal(0.5f, rules.Ambient(12, false).X, 3);
        // From 18 round to 6: half way at midnight.
        Assert.Equal(0.5f, rules.Ambient(0, false).X, 3);
        Assert.Equal(0.75f, rules.Ambient(21, false).X, 3);
        Assert.Equal(Vector3.One, Day().Ambient(3, false)); // no sky given: the map as drawn
        Assert.Equal(new Vector3(0.5f, 0.5f, 0.5f) * 0.8f, (Day() with { Indoors = new(0, "#808080", 0.8f) }).Ambient(12, true), new V3(0.01f));
    }

    private sealed class V3(float within) : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) <= within;

        public int GetHashCode(Vector3 v) => 0;
    }

    [Fact]
    public void TheHourComesFromTheClockFromTheHourItStartsAt()
    {
        var rules = new LightRules { HourAtZero = 9 };
        Assert.Equal(9, rules.HourOf(0), 6);
        Assert.Equal(10.5, rules.HourOf(5400), 6);
        Assert.Equal(1, rules.HourOf(16 * 3600), 6);
        Assert.Equal(9, rules.HourOf(LightRules.SecondsADay * 3), 6);
        Assert.Equal(0, new LightRules().HourOf(0), 6);
    }

    [Fact]
    public void ShadowsFallWestInTheMorningEastInTheEveningShortAtNoonAndNotAtNight()
    {
        var (morning, early) = LightRules.Sun(8);
        var (noon, high) = LightRules.Sun(12.5);
        var (evening, late) = LightRules.Sun(17);
        Assert.True(morning.X < 0 && evening.X > 0, "west, then east");
        Assert.True(noon.Length() < morning.Length() && noon.Length() < evening.Length(), "short at noon");
        Assert.True(high == 1 && early > 0 && late > 0);
        Assert.Equal(0, LightRules.Sun(23).Strength);
        Assert.Equal(0, LightRules.Sun(4).Strength);
    }

    [Fact]
    public void AFlickerStaysWithinItsShareTheSameEachTime()
    {
        var torch = new Light(Flicker: 0.3f);
        var seen = Enumerable.Range(0, 400).Select(i => torch.FlickerAt(i * 0.05, 7)).ToList();
        Assert.All(seen, f => Assert.InRange(f, 0.7f, 1f));
        Assert.True(seen.Max() - seen.Min() > 0.1f, "it moves");
        Assert.Equal(seen[37], torch.FlickerAt(37 * 0.05, 7));
        Assert.NotEqual(torch.FlickerAt(1, 7), torch.FlickerAt(1, 8)); // each its own
        Assert.Equal(1, new Light().FlickerAt(3, 1));
    }

    /// <summary>A palette: index 240 orange (fire), 230 hot red (lava), 231 blue (water), the rest grey.</summary>
    private static Palette TestPalette()
    {
        var colours = Enumerable.Range(0, 256).Select(_ => new Rgb(96, 96, 96)).ToArray();
        colours[240] = new Rgb(252, 164, 0);
        colours[230] = new Rgb(220, 40, 0);
        colours[231] = new Rgb(40, 120, 220);
        return new Palette(colours);
    }

    /// <summary>A <paramref name="size"/>-pixel square object at (x, y), all of palette index <paramref name="index"/>.</summary>
    private static RegionObject Square(int etab, int ojff, int x, int y, int size, byte index) =>
        new(etab, ojff, x, y, x, y, false,
            [new ImageFrame(size, size, Enumerable.Repeat(index, size * size).ToArray(), Enumerable.Repeat(true, size * size).ToArray())], 0);

    private static readonly LightRules Glows = new()
    {
        Palette =
        [
            new(240, 248, new Light("#ff9040", 56, 1), MinPixels: 6),
            new(224, 239, new Light("#ff5a20", 64, 1), HotOnly: true, MinPixels: 24),
        ],
    };

    [Fact]
    public void GlowingPixelsGiveALightAtTheirMiddleBrighterTheMoreThereAre()
    {
        var region = Region.Create(1, 16, 16, new byte[256],
            [Square(0, 100, 10, 10, 4, 240), Square(1, 101, 100, 100, 20, 240), Square(2, 102, 200, 20, 2, 240)]);
        List<MapLight> lights = GlowScanner.Scan(region, TestPalette(), Glows);
        Assert.Equal(2, lights.Count); // the 2 × 2 torch is too small to count
        MapLight small = lights.Single(l => l.At.X < 50), big = lights.Single(l => l.At.X > 50);
        Assert.Equal(new Vector2(11.5f, 11.5f), small.At);
        Assert.Equal(new Vector2(109.5f, 109.5f), big.At);
        Assert.True(big.Light.Strength > small.Light.Strength, "more pixels, more light");
        Assert.Equal(1, big.Light.Strength, 3); // 400 pixels: full
        Assert.Equal("#ff9040", small.Light.Color);
    }

    [Fact]
    public void OnlyHotColoursOfARangeGlowWhereTheRuleSaysSo()
    {
        var region = Region.Create(1, 16, 16, new byte[256], [Square(0, 100, 10, 10, 8, 230), Square(1, 101, 120, 120, 8, 231)]);
        MapLight lava = Assert.Single(GlowScanner.Scan(region, TestPalette(), Glows));
        Assert.Equal("#ff5a20", lava.Light.Color); // the red lava; the blue water in the same range gives none
        Assert.True(lava.At.X < 50);
    }

    [Fact]
    public void ObjectsNamedAndLightsPutByHandAreLitAsGiven()
    {
        var rules = Glows with
        {
            Objects = new Dictionary<string, Light> { ["100"] = new("#ffd080", 40, 0.8f, Offset: [0, -6]) },
            Regions = new Dictionary<string, IReadOnlyList<PlacedLight>> { ["7"] = [new(300, 40, new Light("#80a0ff", 30))] },
        };
        // Object 100 is named: its light, at its picture's middle and offset, not its pixels'.
        var region = Region.Create(7, 32, 16, new byte[512], [Square(0, 100, 20, 20, 10, 240)]);
        List<MapLight> lights = GlowScanner.Scan(region, TestPalette(), rules);
        Assert.Equal(2, lights.Count);
        Assert.Contains(lights, l => l.Light.Color == "#ffd080" && l.At == new Vector2(25, 19));
        Assert.Contains(lights, l => l.Light.Color == "#80a0ff" && l.At == new Vector2(300, 40));
    }

    [Fact]
    public void AModsLightsAreAddedOverTheEngines()
    {
        LightRules engine = LightRules.Parse("""
            { "Day": [{ "Hour": 12, "Color": "#ffffff" }], "FigureMinimum": 0.5, "HourAtZero": 9,
              "Palette": [{ "From": 240, "To": 248, "Light": { "Color": "#ff9040" } }],
              "Objects": { "1": { "Color": "#ffffff" }, "2": { "Color": "#ffffff" } } }
            """);
        LightRules mod = LightRules.Parse("""
            { "Palette": [{ "From": 1, "To": 5, "Light": { "Color": "#40ff80" } }],
              "Objects": { "2": { "Color": "#ff0000" }, "3": { "Color": "#00ff00" } },
              "Items": { "1234": { "Color": "#8080ff", "Radius": 30 } } }
            """);
        LightRules both = engine.Over(mod);
        Assert.Single(both.Day); // the mod gives no sky: the engine's
        Assert.Equal(0.5f, both.LeastOnFigures);
        Assert.Equal(9f, both.HourAtZero);
        Assert.Equal([1, 240], both.Palette.Select(p => p.From)); // the mod's first
        Assert.Equal("#ffffff", both.Objects["1"].Color);
        Assert.Equal("#ff0000", both.Objects["2"].Color);
        Assert.Equal("#00ff00", both.Objects["3"].Color);
        Assert.Equal(30, both.Items["1234"].Radius);
        Assert.Equal(0.7f, engine.Over(LightRules.Parse("""{ "FigureMinimum": 0.7 }""")).LeastOnFigures);
    }

    [Theory]
    [InlineData("""{ "Day": [{ "Hour": 25, "Color": "#ffffff" }] }""")]
    [InlineData("""{ "Day": [{ "Hour": 3, "Color": "white" }] }""")]
    [InlineData("""{ "Indoors": { "Hour": 0, "Color": "#12345" } }""")]
    [InlineData("""{ "FigureMinimum": 2 }""")]
    [InlineData("""{ "HourAtZero": 24 }""")]
    [InlineData("""{ "Palette": [{ "From": 250, "To": 240, "Light": { } }] }""")]
    [InlineData("""{ "Objects": { "9": { "Radius": 0 } } }""")]
    [InlineData("""{ "Conditions": { "FlameBlade": { "Flicker": 1.5 } } }""")]
    [InlineData("""{ "Items": { "9": { "Offset": [1] } } }""")]
    [InlineData("""{ "Day": [ """)]
    public void ALightsFileThatCantBeUsedIsRefused(string json) =>
        Assert.Throws<InvalidDataException>(() => LightRules.Parse(json));

    [Theory]
    [InlineData("""{ "9": { "Glow": { "Color": "#ffffff" } } }""")]
    [InlineData("""{ "zero": { "Cast": { "Color": "#ffffff" } } }""")]
    [InlineData("""{ "9": { "Cast": { "Color": "#ffffff", "Seconds": -1 } } }""")]
    public void ASpellLightsFileThatCantBeUsedIsRefused(string json) =>
        Assert.Throws<InvalidDataException>(() => SpellLights.Parse(json).ToList());

    [Fact]
    public void TheFirstSpellLightGivenForAPartWins()
    {
        var lights = new SpellLights([.. SpellLights.Parse("""{ "_about": "a mod's", "28": { "Burst": { "Color": "#00ff00", "Radius": 120 } } }"""),
            .. SpellLights.Parse("""{ "28": { "Burst": { "Color": "#ff8030" }, "Hit": { "Color": "#ff8030" } } }""")]);
        Assert.Equal("#00ff00", lights.For(28, LookPart.Burst)!.Color);
        Assert.Equal("#ff8030", lights.For(28, LookPart.Hit)!.Color);
        Assert.Null(lights.For(28, LookPart.Cast));
    }
}
