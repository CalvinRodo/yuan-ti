using DarkSun.Game.Animation;
using DarkSun.Game.Combat;
using DarkSun.Game.Lighting;
using DarkSun.Game.World;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// The map's light (<c>[display] lighting</c>; none in DSUN.EXE, off by default): the sky by the
/// game clock outdoors, a fixed light indoors (<see cref="LightRules"/>), the lights the region
/// gives of itself (<see cref="GlowScanner"/>), those its figures carry (an item, a condition), a
/// least light on every figure so night never hides who is where, and the spells' flashes at the
/// points their particles show (<see cref="SpellLights"/>). Drawn by <see cref="LightingLayer"/>;
/// looks only: no rule, sight or fog sees it. What isn't lit (the damage splashes, the spells'
/// particles, the overhead map's marks, the debug overlay) is drawn on <see cref="_unlit"/>, over it.
/// </summary>
public partial class RegionView
{
    private LightingLayer _lighting = null!;

    /// <summary>Drawn over the light, following the map: what lights itself or must read the same by night.</summary>
    private CanvasLayer _unlit = null!;

    /// <summary>The region's own lights, and the region they were found for (−1: none yet).</summary>
    private List<MapLight> _mapLights = [];
    private int _mapLightsRegion = -1;

    /// <summary>The palette the lighting's <c>pixel</c> lookup was made for.</summary>
    private DarkSun.Data.Images.Palette? _lightPalette;

    /// <summary>The hour for this run (<c>--hour=H</c>, the checks), instead of the game clock's.</summary>
    private double? _hourForRun;

    /// <summary>Development (<c>--day-speed=N</c>, with <c>--hour</c>): the run's hour moves on N hours a second.</summary>
    private double _daySpeed;

    /// <summary>Spells' lights shown now (<see cref="LightUp"/>).</summary>
    private readonly List<SpellFlash> _flashes = [];

    private readonly Dictionary<AreaField, SpellFlash> _fieldLights = [];

    /// <summary>A spell's light: from where it starts to where it flies (in the flight's time), fading over its seconds, or lasting until ended.</summary>
    private sealed class SpellFlash(Light light, Vector2 from, Vector2 to, double flight, int seed)
    {
        public Light Light { get; } = light;
        public int Seed { get; } = seed;
        public double Age { get; set; }

        /// <summary>When a lasting one (a field's) was ended, its age then; it fades out after.</summary>
        public double? EndedAt { get; set; }

        public Vector2 At => flight > 0 ? from.Lerp(to, (float)Math.Min(1, Age / flight)) : to;

        /// <summary>How much of its strength shows now (0: gone).</summary>
        public float Left =>
            EndedAt is double ended ? (float)Math.Max(0, 1 - (Age - ended) / FadeOut)
            : Light.Seconds > 0 ? (float)Math.Max(0, 1 - Age / Light.Seconds)
            : flight > 0 && Age >= flight ? 0
            : 1;
    }

    private const double FadeOut = 0.3;

    /// <summary>How far a figure's least light reaches, map pixels; the selected character's farther.</summary>
    private const float FigureLight = 26, SelectedLight = 36;

    private bool LightingOn => Settings.LightingMode != Settings.LightingOff;

    private void BuildLighting()
    {
        _lighting = new LightingLayer();
        AddChild(_lighting);
        _unlit = new CanvasLayer { Layer = 2, FollowViewportEnabled = true };
        AddChild(_unlit);
        foreach (Node2D node in new Node2D[] { _particles, _floaters, _overlay, _overheadMarks })
        {
            node.Reparent(_unlit, keepGlobalTransform: false);
        }
        if (OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--hour=")) is { } hour
            && double.TryParse(hour["--hour=".Length..], System.Globalization.CultureInfo.InvariantCulture, out double h))
        {
            _hourForRun = h;
        }
        if (OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--day-speed=")) is { } speed
            && double.TryParse(speed["--day-speed=".Length..], System.Globalization.CultureInfo.InvariantCulture, out double n))
        {
            (_daySpeed, _hourForRun) = (n, _hourForRun ?? 0);
        }
    }

    /// <summary>The hour on the map: the run's, else the game clock's.</summary>
    private double Hour => _hourForRun ?? Session.LightRules.HourOf(_world?.Clock ?? 0);

    /// <summary>The sky's light now: indoors the fixed one; outdoors the hour's, or noon's with day and night off.</summary>
    private System.Numerics.Vector3 Sky => Session.LightRules.Ambient(Settings.DayNight ? Hour : 13, _indoors);

    /// <summary>
    /// A spell's <paramref name="part"/> throws its light (if lighting and spell light are on and its look has one):
    /// at <paramref name="at"/>, or flying from it to <paramref name="to"/> in <paramref name="flight"/> seconds.
    /// </summary>
    private SpellFlash? LightUp(int spell, LookPart part, Vector2 at, Vector2? to = null, double flight = 0)
    {
        if (!LightingOn || !Settings.SpellLights || LooksHidden && !_checkingLights || Session.SpellLights.For(spell, part) is not { } light)
        {
            return null;
        }
        var flash = new SpellFlash(light, at, to ?? at, to is null ? 0 : flight, spell * 7 + (int)part);
        _flashes.Add(flash);
        return flash;
    }

    /// <summary>A field's glow while it lasts (<see cref="FieldLightGone"/> ends it).</summary>
    private void LightField(AreaField f, Vector2 centre)
    {
        if (LightUp(f.Effect.Number, LookPart.Field, centre) is { } flash)
        {
            _fieldLights[f] = flash;
        }
    }

    private void FieldLightGone(AreaField f)
    {
        if (_fieldLights.Remove(f, out SpellFlash? flash))
        {
            flash.EndedAt = flash.Age;
        }
    }

    /// <summary>The check lights the view itself (<see cref="CheckLighting"/>): the frame's light is left as it is.</summary>
    private bool _lightingHeld;

    /// <summary>The checks light the spells too (<c>--check-lighting</c>), though they hide the other looks.</summary>
    private bool _checkingLights;

    /// <summary>Each frame: light the view, or take the light away.</summary>
    private void UpdateLighting(double delta)
    {
        foreach (SpellFlash f in _flashes)
        {
            f.Age += delta;
        }
        if (_daySpeed != 0 && _hourForRun is double hour)
        {
            _hourForRun = (hour + delta * _daySpeed) % 24;
        }
        _flashes.RemoveAll(f => f.Left <= 0);
        if (_lightingHeld)
        {
            return; // the check draws its own light
        }
        if (!LightingOn || _region is null || OverheadShown)
        {
            _lighting.Off();
            _floor.Sun = (FloorEffects.FixedLean, 1);
            return;
        }
        LightRules rules = Session.LightRules;
        if (_mapLightsRegion != _region.Number)
        {
            _mapLights = GlowScanner.Scan(_region, _palette, rules);
            _mapLightsRegion = _region.Number;
        }
        if (_lightPalette != _palette)
        {
            _lighting.SetPalette(_palette);
            _lightPalette = _palette;
        }
        System.Numerics.Vector3 sky = Sky;
        var lights = new List<LightingLayer.Lit>(_mapLights.Count + _flashes.Count + 16);
        static Vector3 V(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);
        foreach (MapLight l in _mapLights)
        {
            lights.Add(new(new Vector2(l.At.X, l.At.Y), V(l.Light.Rgb * l.Light.Strength * l.Light.FlickerAt(_running, l.Seed)), l.Light.Radius));
        }
        foreach (SpellFlash f in _flashes)
        {
            lights.Add(new(f.At, V(f.Light.Rgb * f.Light.Strength * f.Left * f.Light.FlickerAt(_running, f.Seed)), f.Light.Radius));
        }
        AddFigureLights(lights, rules, sky);

        Vector2 view = GetViewportRect().Size / _camera.Zoom;
        Vector2 centre = _camera.GetScreenCenterPosition();
        const int margin = 24; // the camera moves after this, before the frame is drawn
        var topLeft = (Vector2I)(centre - view / 2).Floor() - new Vector2I(margin, margin);
        var size = (Vector2I)view.Ceil() + new Vector2I(margin * 2, margin * 2);
        _lighting.Frame(topLeft, size, V(sky), lights, Settings.LightingMode == Settings.PixelLighting);
        _floor.Sun = !_indoors && Settings.DayNight ? Sun(LightRules.Sun(Hour)) : (FloorEffects.FixedLean, 1);
    }

    private static (Vector2, float) Sun((System.Numerics.Vector2 Lean, float Strength) sun) => (new Vector2(sun.Lean.X, sun.Lean.Y), sun.Strength);

    /// <summary>
    /// The figures' lights: what each carries or has (<see cref="LightRules.Items"/>, <see cref="LightRules.Conditions"/>),
    /// and where the sky is dimmer than <see cref="LightRules.LeastOnFigures"/>, enough more on each to make it up.
    /// </summary>
    private void AddFigureLights(List<LightingLayer.Lit> lights, LightRules rules, System.Numerics.Vector3 sky)
    {
        float lack = Math.Max(0, rules.LeastOnFigures - Math.Max(sky.X, Math.Max(sky.Y, sky.Z)));
        Sprite2D? selected = Selected?.Node;
        var objects = _party.Where(m => _world[m.Walker.Id] is not null).Select(m => (_world[m.Walker.Id]!, (Sprite2D)m.Node))
            .Concat(_creatureNodes.Select(p => (p.Key, p.Value)));
        foreach (var (o, sprite) in objects)
        {
            if (!sprite.Visible || !Living(o))
            {
                continue;
            }
            Vector2 body = sprite.Position - new Vector2(0, 14);
            if (lack > 0)
            {
                float reach = sprite == selected ? SelectedLight : FigureLight;
                lights.Add(new(body, Vector3.One * (lack * 1.6f), reach));
            }
            foreach (Light light in CarriedLights(o, rules))
            {
                Vector2 at = body + new Vector2(light.At.X, light.At.Y);
                lights.Add(new(at, V3(light.Rgb * light.Strength * light.FlickerAt(_running, o.Index)), light.Radius));
            }
        }
    }

    private static Vector3 V3(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>The lights on <paramref name="o"/>: its items worn or held, and in a fight its conditions.</summary>
    private IEnumerable<Light> CarriedLights(WorldObject o, LightRules rules)
    {
        if (rules.Items.Count > 0)
        {
            foreach (Item item in o.Items.Where(i => i.Equipped))
            {
                if (rules.Items.TryGetValue(item.Rdff.ToString(System.Globalization.CultureInfo.InvariantCulture), out Light? light))
                {
                    yield return light;
                }
            }
        }
        if (rules.Conditions.Count > 0 && _battle?.Combatants.FirstOrDefault(c => c.Source == o) is { } fighter)
        {
            foreach (Condition condition in fighter.Effects.Select(e => e.Condition).Distinct())
            {
                if (rules.Conditions.TryGetValue(condition.ToString(), out Light? light))
                {
                    yield return light;
                }
            }
        }
    }

    /// <summary>
    /// <c>--check-lighting</c>: the light's data reads; the sky is brighter at noon than at midnight
    /// and fixed indoors; lava, fires and torches are found in the regions that have them; a
    /// spell's flash fades, a field's glow lasts until the field goes; what is drawn unlit sits
    /// over the light and the windows over that. With a window (not headless; <c>--lighting-shots=DIR</c>
    /// saves what it looked at): under a white sky and no lights the map shows as it does unlit;
    /// the colour cycling still turns under the light and no code shows; noon is brighter than
    /// midnight; and <c>pixel</c> leaves every map pixel one of the region's palette's colours.
    /// </summary>
    private async void CheckLighting()
    {
        _checking = _checkingLights = true;
        var problems = new List<string>();
        void Expect(bool ok, string what)
        {
            GD.Print($"lighting: {(ok ? "" : "PROBLEM ")}{what}");
            if (!ok)
            {
                problems.Add(what);
            }
        }
        async Task Seconds(double s) => await ToSignal(GetTree().CreateTimer(s, ignoreTimeScale: true), SceneTreeTimer.SignalName.Timeout);
        static float Luma(System.Numerics.Vector3 c) => 0.3f * c.X + 0.59f * c.Y + 0.11f * c.Z;

        LightRules rules = Session.LightRules;
        Expect(rules.Day.Count > 0 && Luma(rules.Ambient(13, false)) > 1 && Luma(rules.Ambient(0, false)) < 0.5f
            && Luma(rules.Ambient(13, true)) == Luma(rules.Ambient(0, true)),
            $"the sky: noon {Luma(rules.Ambient(13, false)):F2}, midnight {Luma(rules.Ambient(0, false)):F2}, indoors {Luma(rules.Ambient(13, true)):F2} at any hour");
        Expect(LightRules.Sun(12).Strength == 1 && LightRules.Sun(23).Strength == 0 && LightRules.Sun(8).Lean.X < 0 && LightRules.Sun(17).Lean.X > 0,
            "shadows fall west in the morning, east in the evening, none at night");
        Expect(Math.Abs(rules.HourOf(0) - (rules.HourAtZero ?? 0)) < 1e-9 && Math.Abs(rules.HourOf(3600) - rules.HourOf(0) - 1) < 1e-9,
            $"a new game starts at {rules.HourOf(0):F0}:00, an hour of the clock an hour later");

        // The regions' own lights: lava (6), torches (41, indoors), the arena (42).
        IReadOnlyList<string> files = Session.Install!.RegionFiles;
        int Lights(string file)
        {
            var region = DarkSun.Data.Regions.Region.Load(Session.Open(file), [Session.Open("SEGOBJEX.GFF"), Session.Open("GPLDATA.GFF")], Session.Assets);
            return GlowScanner.Scan(region, region.LoadPalette(Session.Open("GPLDATA.GFF"), Session.Open("RESOURCE.GFF"), Session.Assets), rules).Count;
        }
        int lava = Lights("RGN06.GFF"), torches = Lights("RGN29.GFF"), arena = Lights("RGN2A.GFF");
        Expect(lava >= 40 && torches >= 3, $"lights found: {lava} over the Lava Rifts' lava (region 6), {torches} torches and fires in region 41, {arena} in the arena");

        // Spells: a flash fades, a flight's light flies and goes, a field's lasts until it goes.
        Expect(Session.SpellLights.Count >= 100 && Session.SpellLights.For(28, LookPart.Burst) is { Radius: > 64 } && Session.SpellLights.For(55, LookPart.Field) is not null,
            $"{Session.SpellLights.Count} spell parts throw light (Fireball's burst, Wall of Fire's field)");
        string was = Settings.LightingMode;
        Settings.LightingForRun = Settings.SmoothLighting;
        SpellFlash? burst = LightUp(28, LookPart.Burst, new Vector2(100, 100));
        SpellFlash? flight = LightUp(28, LookPart.Flight, new Vector2(0, 0), new Vector2(200, 0), 1.0);
        SpellFlash? field = LightUp(55, LookPart.Field, new Vector2(50, 50));
        UpdateLighting(0.5);
        Expect(burst is { Left: > 0 and < 1 } && flight is { At.X: > 90 and < 110 } && field is { Left: 1 },
            $"half a second on: the burst's flash fading ({burst?.Left:F2}), the flight's light half way ({flight?.At.X:F0} of 200), the field's full ({field?.Left:F2})");
        UpdateLighting(1.0);
        Expect(!_flashes.Contains(burst!) && !_flashes.Contains(flight!) && _flashes.Contains(field!), "a second later the flash and the flight are gone, the field's glow stays");
        field!.EndedAt = field.Age;
        UpdateLighting(FadeOut + 0.01);
        Expect(!_flashes.Contains(field), "and goes once its field does");
        Expect(_particles.GetParent() == _unlit && _floaters.GetParent() == _unlit && _overheadMarks.GetParent() == _unlit
            && _lighting.Layer < _unlit.Layer && _unlit.Layer < _hudPanel.GetParent<CanvasLayer>().Layer,
            "the map, then its light, then the particles, splashes and marks unlit, then the windows");

        if (DisplayServer.GetName() != "headless")
        {
            string? shots = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--lighting-shots="))?["--lighting-shots=".Length..];
            async Task<Image> Shot(string name)
            {
                UpdateLighting(0); // the check holds the view's own frames
                await Seconds(0.25);
                Image image = GetViewport().GetTexture().GetImage();
                if (shots is not null)
                {
                    DirAccess.MakeDirRecursiveAbsolute(shots);
                    image.SavePng($"{shots}/{name}.png");
                }
                return image;
            }
            Session.Scripts.Abandon();
            Session.ScriptHost.Dialogue.Clear();
            _dialogueWindow.Refresh();
            ColourCycling cycling = ColourCycling.Instance!;
            await Seconds(1);
            _follow = false;
            SnapCamera();
            // Time stands still (a window over the map would dim it, and the cycling under it): only what the light does changes.
            Godot.Engine.TimeScale = 0;
            Settings.LightingForRun = Settings.LightingOff;
            await Seconds(0.5);
            Image unlit = await Shot("unlit");
            _lightingHeld = true;
            Vector2 view = GetViewportRect().Size / _camera.Zoom;
            var topLeft = (Vector2I)(_camera.GetScreenCenterPosition() - view / 2).Floor() - Vector2I.One * 4;
            _lighting.Frame(topLeft, (Vector2I)view.Ceil() + Vector2I.One * 8, Vector3.One, [], banded: false);
            Image white = await Shot("white-sky");
            int differ = 0;
            for (int y = 0; y < unlit.GetHeight(); y++)
            {
                for (int x = 0; x < unlit.GetWidth(); x++)
                {
                    Color a = unlit.GetPixel(x, y), b = white.GetPixel(x, y);
                    differ += Math.Max(Math.Abs(a.R8 - b.R8), Math.Max(Math.Abs(a.G8 - b.G8), Math.Abs(a.B8 - b.B8))) > 3 ? 1 : 0;
                }
            }
            Expect(differ == 0, $"under a white sky and no lights the map shows as unlit ({differ} pixels differ)");
            Godot.Engine.TimeScale = 1;
            _lighting.Frame(topLeft, (Vector2I)view.Ceil() + Vector2I.One * 8, Vector3.One * 0.8f, [new(GetCameraCentre(), new Vector3(1, 0.6f, 0.3f), 80)], banded: false);
            Image before = await Shot("lit-a");
            await Seconds(0.4);
            Image after = await Shot("lit-b");
            int changed = 0, codes = 0;
            for (int y = 0; y < before.GetHeight(); y++)
            {
                for (int x = 0; x < before.GetWidth(); x++)
                {
                    changed += before.GetPixel(x, y) != after.GetPixel(x, y) ? 1 : 0;
                    codes += cycling.ShowsCode(after.GetPixel(x, y)) ? 1 : 0;
                }
            }
            Expect(changed > 0 && codes == 0, $"under the light the colours still turn ({changed} pixels changed in 0.4 s) and no code shows ({codes})");
            _lightingHeld = false;
            Settings.LightingForRun = Settings.SmoothLighting;
            static double Mean(Image i)
            {
                double sum = 0;
                for (int y = 0; y < i.GetHeight(); y += 2)
                {
                    for (int x = 0; x < i.GetWidth(); x += 2)
                    {
                        Color c = i.GetPixel(x, y);
                        sum += 0.3 * c.R + 0.59 * c.G + 0.11 * c.B;
                    }
                }
                return sum / (i.GetWidth() / 2 * (i.GetHeight() / 2));
            }
            _hourForRun = 13;
            double noon = Mean(await Shot("noon"));
            _hourForRun = 0;
            double midnight = Mean(await Shot("midnight"));
            Expect(_indoors ? Math.Abs(noon - midnight) < 0.01 : noon > midnight * 1.25,
                _indoors ? $"indoors: the same light at noon and midnight ({noon:F3}, {midnight:F3})" : $"noon is brighter than midnight ({noon:F3} against {midnight:F3})");
            Settings.LightingForRun = Settings.PixelLighting;
            Image pixel = await Shot("pixel-midnight");
            var colours = Enumerable.Range(0, _palette.Count).Select(i => _palette[i]).Select(c => (c.R, c.G, c.B)).ToHashSet();
            int inPalette = 0, all = 0;
            for (int y = 0; y < pixel.GetHeight(); y++)
            {
                for (int x = 0; x < pixel.GetWidth(); x++)
                {
                    Color c = pixel.GetPixel(x, y);
                    all++;
                    inPalette += colours.Contains(((byte)c.R8, (byte)c.G8, (byte)c.B8)) ? 1 : 0;
                }
            }
            Expect(inPalette >= all * 0.98, $"pixel: the map's colours are the region palette's ({inPalette * 100.0 / all:F1} % of the screen)");
            // How fast with every light the region has (the most drawn a frame: LightingLayer.MaxLights).
            ulong from = Time.GetTicksMsec();
            int frames = 0, most = 0;
            while (Time.GetTicksMsec() - from < 2000)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                (frames, most) = (frames + 1, Math.Max(most, _lighting.Drawn));
            }
            Expect(frames >= 60, $"{frames / 2} frames a second lit, {most} of the region's {_mapLights.Count} lights drawn at most");
        }
        Settings.LightingForRun = was == Settings.LightingOff ? null : was;
        GD.Print(problems.Count == 0 ? "lighting: OK" : $"lighting: {problems.Count} problems");
        GetTree().Quit(problems.Count == 0 ? 0 : 1);
    }

    private Vector2 GetCameraCentre() => _camera.GetScreenCenterPosition();
}
