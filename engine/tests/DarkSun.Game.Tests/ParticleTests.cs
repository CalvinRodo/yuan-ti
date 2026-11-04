using System.Numerics;
using DarkSun.Game.Animation;

namespace DarkSun.Game.Tests;

/// <summary>The spells' particle looks (<c>[display] spell_effects</c>): their files, and how they play.</summary>
public class ParticleTests
{
    private static ParticleDefinition Def(string json) => ParticleDefinition.Parse(json);

    private static readonly Vector2 From = new(10, 10), To = new(170, 90);

    /// <summary>The engine's own looks (<c>engine/game/looks</c>).</summary>
    private static string LooksFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, "engine", "game", "looks");
            if (Directory.Exists(path))
            {
                return path;
            }
        }
        throw new DirectoryNotFoundException("engine/game/looks");
    }

    [Fact]
    public void EveryShippedLookReadsAndNamesParticlesThatAreThere()
    {
        string folder = LooksFolder();
        var definitions = Directory.GetFiles(Path.Combine(folder, "particles"), "*.json")
            .ToDictionary(p => Path.GetFileNameWithoutExtension(p), p => ParticleDefinition.Parse(File.ReadAllText(p)));
        var looks = new ParticleLooks([File.ReadAllText(Path.Combine(folder, "particle_looks.json"))], definitions);

        Assert.Empty(looks.Missing);
        // Each of the original's 196 looks (which of its parts: --check-spell-effects).
        foreach (int spell in Enumerable.Range(1, 196))
        {
            foreach (ParticleStyle style in Enum.GetValues<ParticleStyle>())
            {
                Assert.Contains(looks.Looks.Keys, k => k.Spell == spell && k.Style == style);
            }
        }
        Assert.All(looks.Looks.Where(l => l.Key.Style == ParticleStyle.Pixel), l => Assert.NotEmpty(definitions[l.Value].Palette));
        Assert.NotNull(looks.For(9, LookPart.Flight, ParticleStyle.Modern));
        Assert.Null(looks.For(9, LookPart.Burst, ParticleStyle.Modern)); // not given: the original's OJFF
        Assert.Null(looks.For(197, LookPart.Cast, ParticleStyle.Pixel)); // a mod's own spell: its OJFFs
    }

    [Fact]
    public void AModsLooksWinAndAMissingNameIsSaid()
    {
        var definitions = new Dictionary<string, ParticleDefinition>
        {
            ["mine"] = Def("""{"Burst": 5, "Palette": [46]}"""),
            ["engine"] = Def("""{"Burst": 3, "Palette": [46]}"""),
        };
        var looks = new ParticleLooks(
            ["""{"9": {"Cast": {"Pixel": "mine"}, "Hit": {"Modern": "gone"}}}""", """{"9": {"Cast": {"Pixel": "engine", "Modern": "engine"}}}"""],
            definitions);

        Assert.Equal(5, looks.For(9, LookPart.Cast, ParticleStyle.Pixel)!.Burst);
        Assert.Equal(3, looks.For(9, LookPart.Cast, ParticleStyle.Modern)!.Burst);
        Assert.Null(looks.For(9, LookPart.Hit, ParticleStyle.Modern));
        Assert.Equal(["spell 9 Hit Modern: no particles \"gone\""], looks.Missing);
    }

    [Theory]
    [InlineData("""{"Palette": [46], "Lifetime": [0, 1]}""")]
    [InlineData("""{"Lifetime": [0.5, 1]}""")]
    [InlineData("""{"Colors": ["#12345"]}""")]
    [InlineData("""{"Palette": [300]}""")]
    [InlineData("""{"Palette": [46], "Shape": "Square"}""")]
    [InlineData("""{"Palette": [46], "Stagger": 1}""")]
    [InlineData("""{"Palette": [46], "Offset": [0]}""")]
    public void ADefinitionThatCantBeUsedIsRefused(string json) => Assert.Throws<InvalidDataException>(() => Def(json));

    [Theory]
    [InlineData("""{"9": {"Glow": {"Pixel": "x"}}}""")]
    [InlineData("""{"9": {"Cast": {"Shiny": "x"}}}""")]
    [InlineData("""{"Magic Missile": {"Cast": {"Pixel": "x"}}}""")]
    public void AMapThatCantBeUsedIsRefused(string json) => Assert.Throws<InvalidDataException>(() => ParticleLooks.ParseMap(json).ToList());

    [Fact]
    public void ColoursAndListsOverALife()
    {
        Assert.Equal((1f, 0.5f, 0f, 1f), ParticleDefinition.ParseColour("#ff8000") is var c ? (c.R, MathF.Round(c.G, 2), c.B, c.A) : default);
        Assert.Equal(0f, ParticleDefinition.ParseColour("#ffffff00").A);
        Assert.Equal(2f, ParticleEffect.Over([1, 3], 0.5f));
        Assert.Equal(3f, ParticleEffect.Over([1, 3], 2));
        Assert.Equal(46, ParticleEffect.Stepped([46, 220, 161], 0.2f));
        Assert.Equal(220, ParticleEffect.Stepped([46, 220, 161], 0.5f));
        Assert.Equal(161, ParticleEffect.Stepped([46, 220, 161], 1));
    }

    [Fact]
    public void AFlightLandsOnItsPointInItsTimeWhateverItsHeadsWander()
    {
        ParticleDefinition swarm = Def("""{"Heads": 5, "Wander": 120, "Stagger": 0.3, "Rate": 100, "ArriveBurst": 4, "Colors": ["#ffffff"], "Max": 2000}""");
        var effect = new ParticleEffect(swarm, From, To, [], 1, false, 7);
        Assert.True(effect.Blocks);
        effect.Update(0.1);
        List<Vector2> heads = [.. effect.Heads()];
        Assert.Equal(5, heads.Count);
        Assert.True(heads.Max(h => DistanceToLine(h, From, To)) > 20, "the heads swing off the straight way");

        effect.Update(0.65); // the first heads land by 0.7 of the flight, the last at its end
        Assert.InRange(effect.Heads().Count(), 1, 4);
        Assert.True(effect.Blocks);
        effect.Update(0.3);
        Assert.Empty(effect.Heads());
        Assert.False(effect.Blocks); // the playback goes on when the flight is done, as with the original's projectile
        Assert.Contains(effect.Particles, p => Vector2.Distance(p.Position, To) < 1); // each landing's burst, at the point
    }

    private static float DistanceToLine(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 d = Vector2.Normalize(b - a);
        Vector2 off = p - a;
        return MathF.Abs(off.X * d.Y - off.Y * d.X);
    }

    [Fact]
    public void AStraightFlightFollowsTheProjectilesWay()
    {
        var effect = new ParticleEffect(Def("""{"Rate": 0, "Palette": [46]}"""), From, To, [], 1, false, 1);
        effect.Update(0.5);
        Vector2 head = Assert.Single(effect.Heads());
        Assert.True(Vector2.Distance(head, Vector2.Lerp(From, To, 0.5f)) < 0.01f);
    }

    [Fact]
    public void TheSameEffectPlaysTheSameAtAnyFrameRate()
    {
        ParticleDefinition fire = Def("""{"Shape": "Cell", "Burst": 20, "Rate": 90, "Seconds": 0.5, "Lifetime": [0.2, 0.6], "Speed": [5, 30], "Gravity": [0, -20], "Drag": 1, "Palette": [244]}""");
        Vector2[] cells = [new(8, 8), new(24, 8), new(40, 8)];
        var slow = new ParticleEffect(fire, From, To, cells, 0, false, 42);
        var fast = new ParticleEffect(fire, From, To, cells, 0, false, 42);
        var other = new ParticleEffect(fire, From, To, cells, 0, false, 43);
        for (int i = 0; i < 12; i++)
        {
            slow.Update(1 / 20.0);
            fast.Update(1 / 60.0);
            fast.Update(1 / 60.0);
            fast.Update(1 / 60.0);
            other.Update(1 / 20.0);
        }
        Assert.NotEmpty(slow.Particles);
        Assert.Equal(slow.Particles.Select(p => p.Position), fast.Particles.Select(p => p.Position));
        Assert.NotEqual(slow.Particles.Select(p => p.Position), other.Particles.Select(p => p.Position));
        Assert.All(slow.Particles, p => Assert.Contains(cells, c => MathF.Abs(p.Position.X - c.X) < 40));
        Assert.Equal(ParticleEffect.SeedOf(9, LookPart.Hit, From, To), ParticleEffect.SeedOf(9, LookPart.Hit, From, To));
    }

    [Fact]
    public void AFieldLastsUntilItGoesAndKeepsToItsLimit()
    {
        ParticleDefinition smoke = Def("""{"Shape": "Cell", "Rate": 50, "Lifetime": [1, 1], "Palette": [57], "Max": 120}""");
        Vector2[] cells = [.. Enumerable.Range(0, 40).Select(i => new Vector2(i * 16 + 8, 8))];
        var field = new ParticleEffect(smoke, From, To, cells, 0, true, 3);
        field.Update(10);
        Assert.True(field.Emitting);
        Assert.Equal(120, field.Particles.Count); // 40 cells × 50 a second, held to the limit
        Assert.False(field.Blocks);

        field.Stop();
        Assert.False(field.Emitting);
        Assert.False(field.Done); // what's alive lives out its life
        field.Update(1.1);
        Assert.True(field.Done);
    }

    [Fact]
    public void ABurstThatBlocksHoldsThePlaybackUntilItIsDone()
    {
        var burst = new ParticleEffect(Def("""{"Burst": 10, "Seconds": 0.2, "Lifetime": [0.3, 0.3], "Blocks": true, "Palette": [46]}"""), From, To, [], 0, false, 5);
        burst.Update(0.25); // the burst's particles live 0.3 s
        Assert.True(burst.Blocks);
        burst.Update(0.1);
        Assert.True(burst.Done);
        Assert.False(burst.Blocks);
    }

    [Fact]
    public void ARingStartsOnItsRimAboutTheOffsetPointAndRadialGoesOutOrAround()
    {
        ParticleDefinition ward = Def("""{"Shape": "Ring", "Radius": 12, "Offset": [0, -10], "Radial": true, "Direction": 0, "Spread": 0, "Burst": 40, "Speed": [10, 10], "Lifetime": [1, 1], "Palette": [46]}""");
        var effect = new ParticleEffect(ward, From, To, [], 0, false, 9);
        effect.Update(ParticleEffect.Step);
        var centre = To + new Vector2(0, -10);
        Assert.Equal(40, effect.Particles.Count);
        foreach (Particle p in effect.Particles)
        {
            Vector2 off = p.Position - p.Velocity * ParticleEffect.Step - centre;
            Assert.Equal(1, MathF.Sqrt(off.X * off.X / 144 + off.Y * off.Y / 36), 2); // on the flattened rim
            Assert.True(Vector2.Dot(Vector2.Normalize(off), Vector2.Normalize(p.Velocity)) > 0.99f, "out from the centre");
        }

        var around = new ParticleEffect(ward with { Direction = 90 }, From, To, [], 0, false, 9);
        around.Update(ParticleEffect.Step);
        Assert.All(around.Particles, p => Assert.True(MathF.Abs(Vector2.Dot(Vector2.Normalize(p.Position - centre), Vector2.Normalize(p.Velocity))) < 0.2f));
    }

    [Fact]
    public void APullDrawsParticlesToThePoint()
    {
        ParticleDefinition swirl = Def("""{"Shape": "Ring", "Radius": 20, "Radial": true, "Direction": 90, "Spread": 0, "Burst": 30, "Speed": [20, 20], "Pull": 60, "Lifetime": [2, 2], "Palette": [46]}""");
        var pulled = new ParticleEffect(swirl, From, To, [], 0, false, 4);
        var free = new ParticleEffect(swirl with { Pull = 0 }, From, To, [], 0, false, 4);
        pulled.Update(1);
        free.Update(1);
        Assert.True(pulled.Particles.Average(p => Vector2.Distance(p.Position, To)) < free.Particles.Average(p => Vector2.Distance(p.Position, To)) - 10);
    }

    [Fact]
    public void AnAimedHitFliesBackToTheCaster()
    {
        ParticleDefinition drain = Def("""{"Aim": true, "Direction": 180, "Spread": 0, "Burst": 10, "Speed": [50, 50], "Lifetime": [1, 1], "Palette": [219]}""");
        var effect = new ParticleEffect(drain, From, To, [], 0, false, 2);
        effect.Update(0.5);
        Assert.All(effect.Particles, p => Assert.True(Vector2.Distance(p.Position, From) < Vector2.Distance(To, From) - 20));
    }

    [Fact]
    public void ARandomColourIsPickedOnceAParticle()
    {
        ParticleDefinition rainbow = Def("""{"Burst": 50, "Lifetime": [1, 1], "Palette": [223, 166, 178, 220], "RandomColor": true}""");
        var effect = new ParticleEffect(rainbow, From, To, [], 0, false, 6);
        effect.Update(0.1);
        Assert.Equal(4, effect.Particles.Select(p => ParticleEffect.Stepped(rainbow.Palette, p.Pick)).Distinct().Count());
        float[] picks = [.. effect.Particles.Select(p => p.Pick)];
        effect.Update(0.5);
        Assert.Equal(picks, effect.Particles.Select(p => p.Pick));
    }

    [Fact]
    public void AnOffsetMovesAnAreasParticlesToo()
    {
        ParticleDefinition hail = Def("""{"Shape": "Cell", "Offset": [0, -48], "Burst": 30, "Lifetime": [1, 1], "Palette": [46]}""");
        var effect = new ParticleEffect(hail, From, To, [new(100, 100)], 0, false, 8);
        effect.Update(ParticleEffect.Step);
        Assert.All(effect.Particles, p => Assert.InRange(p.Position.Y, 100 - 48 - 8, 100 - 48 + 8));
    }

    [Fact]
    public void EveryShippedConeSpraysAtItsPoint()
    {
        var cones = Directory.GetFiles(Path.Combine(LooksFolder(), "particles"), "*.json")
            .Select(p => (Name: Path.GetFileName(p), Definition: ParticleDefinition.Parse(File.ReadAllText(p))))
            .Where(c => c.Definition.Shape == ParticleShape.Cone).ToList();
        Assert.NotEmpty(cones);
        Vector2 way = Vector2.Normalize(To - From);
        foreach (var (name, cone) in cones)
        {
            var effect = new ParticleEffect(cone, From, To, [], 0, false, 3);
            effect.Update(0.1);
            Assert.True(effect.Particles.All(p => Vector2.Dot(Vector2.Normalize(p.Velocity), way) > 0.8f), $"{name} sprays off its way");
        }
    }
}
