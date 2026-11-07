using System.Numerics;

namespace DarkSun.Game.Animation;

/// <summary>A particle: where it is, where it goes, and how far through its life.</summary>
public struct Particle
{
    public Vector2 Position;
    public Vector2 Velocity;
    public float Age;
    public float Life;

    /// <summary>Where in its palette or colours it is, 0–1, if they are picked (<see cref="ParticleDefinition.RandomColor"/>).</summary>
    public float Pick;

    /// <summary>Its size's share of the definition's (<see cref="ParticleDefinition.Scale"/>).</summary>
    public float Scale;

    /// <summary>How far it is turned, in radians, and how fast it turns (<see cref="ParticleDefinition.Spin"/>).</summary>
    public float Rotation, Spin;

    /// <summary>0 at its birth, 1 at its death.</summary>
    public readonly float T => Age / Life;
}

/// <summary>
/// A <see cref="ParticleDefinition"/> playing: from <paramref name="from"/> (the caster)
/// to <paramref name="to"/> (where the spell lands, or the one it reaches), over the
/// area's <paramref name="cells"/> (pixel centres), flying for <paramref name="flight"/>
/// seconds if it is a flight, or lasting until <see cref="Stop"/> if it is a field. It
/// moves on in fixed steps from a seeded roll, so the same effect plays the same way at
/// any frame rate; nothing in it reaches the battle. Its definition's layers play with it
/// (<see cref="Layers"/>), each from its own roll, their heads flying its heads' ways.
/// </summary>
public sealed class ParticleEffect
{
    public const int StepsPerSecond = 60;

    /// <summary>Seconds a step.</summary>
    public const float Step = 1f / StepsPerSecond;

    /// <summary>Half a map cell: how far from a cell's centre a particle may start in it.</summary>
    private const float HalfCell = 8;

    private readonly IReadOnlyList<Vector2> cells;
    private readonly float flight;
    private readonly bool lasts;
    private readonly Random _random;
    private readonly List<Particle> _particles = [];
    private readonly (Vector2 Out, Vector2 In, float Ends)[] _paths;
    private readonly bool[] _arrived;
    private float _owed;
    private double _clock;
    private int _steps;
    private bool _stopped, _begun;

    public ParticleEffect(ParticleDefinition definition, Vector2 from, Vector2 to, IReadOnlyList<Vector2> cells, float flight, bool lasts, int seed)
        : this(definition, from, to, cells, flight, lasts, seed, null)
    {
    }

    private ParticleEffect(ParticleDefinition definition, Vector2 from, Vector2 to, IReadOnlyList<Vector2> cells, float flight, bool lasts, int seed,
        (Vector2, Vector2, float)[]? paths)
    {
        (Definition, From, To, this.cells, this.flight, this.lasts) = (definition, from, to, cells, flight, lasts);
        _random = new Random(seed);
        _paths = paths ?? Paths(definition, from, to, flight, new Random(seed ^ 0x5F3759DF));
        _arrived = new bool[_paths.Length];
        Layers = [.. definition.Layers.Select((layer, i) => new ParticleEffect(layer, from, to, cells, flight, lasts, seed + (i + 1) * 0x3C6EF372, _paths))];
    }

    public ParticleDefinition Definition { get; }

    public Vector2 From { get; }

    public Vector2 To { get; }

    public IReadOnlyList<Particle> Particles => _particles;

    /// <summary>Its definition's layers, playing with it (drawn over it, in order).</summary>
    public IReadOnlyList<ParticleEffect> Layers { get; }

    /// <summary>It and its layers, in the order drawn.</summary>
    public IEnumerable<ParticleEffect> WithLayers => Layers.Prepend(this);

    /// <summary>How far the view shakes now, in map pixels (<see cref="ParticleDefinition.Shake"/>: the most of it and its layers').</summary>
    public float Shaking => WithLayers.Max(e => e.Definition.Shake is { Seconds: > 0 } s && e.Time - e.Definition.Delay is var t && t >= 0 && t < s.Seconds
        ? s.Strength * (1 - t / s.Seconds) : 0);

    /// <summary>Seconds since it started.</summary>
    public float Time { get; private set; }

    /// <summary>Seconds it gives off particles (a field: until stopped).</summary>
    public float Emits => flight > 0 ? flight : Definition.Seconds;

    /// <summary>It gives off particles now or will (a layer waiting out its delay).</summary>
    public bool Emitting => !_stopped && (lasts || Time < Definition.Delay + Emits);

    /// <summary>Nothing more to show, of it or its layers.</summary>
    public bool Done => !Emitting && _particles.Count == 0 && Layers.All(l => l.Done);

    /// <summary>The playback waits for it: a flight until it lands, else one that blocks until it is done.</summary>
    public bool Blocks => (flight > 0 ? Time < flight : Definition.Blocks && !Done) || Layers.Any(l => l.Blocks);

    /// <summary>A field is gone: no more particles, and those alive live out their lives.</summary>
    public void Stop()
    {
        _stopped = true;
        foreach (ParticleEffect layer in Layers)
        {
            layer.Stop();
        }
    }

    /// <summary>The flight's heads in the air now (none once landed, or if it isn't a flight).</summary>
    public IEnumerable<Vector2> Heads()
    {
        for (int i = 0; i < _paths.Length; i++)
        {
            if (HeadAt(i) is { } at)
            {
                yield return at;
            }
        }
    }

    /// <summary>Head <paramref name="i"/> now, or null once it has landed (or this isn't a flight).</summary>
    private Vector2? HeadAt(int i)
    {
        if (flight <= 0 || Time >= _paths[i].Ends)
        {
            return null;
        }
        float u = Time / _paths[i].Ends, v = 1 - u;
        var (o, n) = (_paths[i].Out, _paths[i].In);
        return v * v * v * From + 3 * v * v * u * o + 3 * v * u * u * n + u * u * u * To;
    }

    /// <summary>
    /// Each head's way: a curve pulled toward two points a third and two thirds along, each swung up to
    /// <see cref="ParticleDefinition.Wander"/> off the straight line (unswung, the straight line at an even
    /// speed), and when it lands (staggered).
    /// </summary>
    private static (Vector2, Vector2, float)[] Paths(ParticleDefinition d, Vector2 from, Vector2 to, float flight, Random random)
    {
        var paths = new (Vector2, Vector2, float)[d.Heads];
        for (int i = 0; i < d.Heads; i++)
        {
            float share = d.Heads == 1 ? 1 : 1 - d.Stagger + d.Stagger * i / (d.Heads - 1);
            Vector2 Swing(Vector2 around) =>
                around + Angle((float)(random.NextDouble() * MathF.Tau)) * d.Wander * (0.5f + 0.5f * (float)random.NextDouble());
            paths[i] = (Swing(Vector2.Lerp(from, to, 1 / 3f)), Swing(Vector2.Lerp(from, to, 2 / 3f)), flight * share);
        }
        return paths;
    }

    /// <summary>Move on <paramref name="seconds"/>, a fixed step at a time (counted from the start, so frames' rounding doesn't add up).</summary>
    public void Update(double seconds)
    {
        _clock += seconds;
        for (int due = (int)Math.Floor(_clock * StepsPerSecond + 1e-6); _steps < due; _steps++)
        {
            StepOnce();
        }
        foreach (ParticleEffect layer in Layers)
        {
            layer.Update(seconds);
        }
    }

    private void StepOnce()
    {
        ParticleDefinition d = Definition;
        if (!_begun && !_stopped && Time >= d.Delay - 1e-4f)
        {
            _begun = true;
            Emit(d.Burst);
        }
        if (_begun && Emitting)
        {
            int sources = flight > 0 ? Heads().Count() : d.Shape == ParticleShape.Cell && lasts ? Math.Max(1, cells.Count) : 1;
            _owed += d.Rate * sources * Step;
            int now = (int)_owed;
            _owed -= now;
            Emit(now);
        }
        Time = (_steps + 1) * Step;
        for (int i = 0; i < _arrived.Length && flight > 0; i++)
        {
            if (!_arrived[i] && Time >= _paths[i].Ends)
            {
                _arrived[i] = true;
                for (int n = 0; n < d.ArriveBurst; n++)
                {
                    Add(To, Aimed(To));
                }
            }
        }
        var gravity = new Vector2(d.Gravity[0], d.Gravity[1]) * Step;
        float keep = Math.Max(0, 1 - d.Drag * Step);
        Vector2 centre = Centre;
        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            Particle p = _particles[i];
            p.Age += Step;
            if (p.Age >= p.Life)
            {
                _particles.RemoveAt(i);
                continue;
            }
            Vector2 pull = d.Pull != 0 && centre != p.Position ? Vector2.Normalize(centre - p.Position) * d.Pull * Step : Vector2.Zero;
            Vector2 stir = d.Turbulence > 0 ? Angle((float)(_random.NextDouble() * MathF.Tau)) * d.Turbulence * Step : Vector2.Zero;
            p.Velocity = (p.Velocity + gravity + pull + stir) * keep;
            p.Position += p.Velocity * Step;
            p.Rotation += p.Spin * Step;
            _particles[i] = p;
        }
    }

    /// <summary><paramref name="count"/> particles where the shape puts them (a flight's spread over its heads).</summary>
    private void Emit(int count)
    {
        List<Vector2> heads = flight > 0 ? [.. Heads()] : [];
        for (int n = 0; n < count; n++)
        {
            Vector2 at = flight > 0
                ? heads.Count == 0 ? To : heads[n % heads.Count]
                : Start();
            Add(at, Aimed(at));
        }
    }

    private void Add(Vector2 at, float direction)
    {
        ParticleDefinition d = Definition;
        if (_particles.Count >= d.Max)
        {
            return;
        }
        float speed = Roll(d.Speed[0], d.Speed[1]);
        _particles.Add(new Particle
        {
            Position = at,
            Velocity = Angle(direction) * speed,
            Life = Roll(d.Lifetime[0], d.Lifetime[1]),
            Pick = d.RandomColor ? (float)_random.NextDouble() : 0,
            Scale = d.Scale[0] == d.Scale[1] ? d.Scale[0] : Roll(d.Scale[0], d.Scale[1]),
            Rotation = d.Spin is [0, 0] ? 0 : (float)(_random.NextDouble() * MathF.Tau),
            Spin = d.Spin is [0, 0] ? 0 : Roll(d.Spin[0], d.Spin[1]) * MathF.PI / 180,
        });
    }

    /// <summary>The point a circle, ring or point is about, and a pull pulls toward: the point, moved by the offset.</summary>
    private Vector2 Centre => To + new Vector2(Definition.Offset[0], Definition.Offset[1]);

    /// <summary>Where a particle starts, by the shape (an area's cells and a line moved by the offset, too: hail falling onto them, say).</summary>
    private Vector2 Start()
    {
        ParticleDefinition d = Definition;
        var offset = new Vector2(d.Offset[0], d.Offset[1]);
        switch (d.Shape)
        {
            case ParticleShape.Circle or ParticleShape.Ring:
                float r = d.Radius * (d.Shape == ParticleShape.Ring ? 1 : MathF.Sqrt((float)_random.NextDouble()));
                Vector2 off = Angle((float)(_random.NextDouble() * MathF.Tau)) * r;
                return Centre + off with { Y = off.Y / 2 };
            case ParticleShape.Cone:
                return From;
            case ParticleShape.Line:
                var (a, b) = cells.Count == 0 ? (From, To) : (cells.MinBy(c => Vector2.Distance(c, From)), cells.MaxBy(c => Vector2.Distance(c, From)));
                return Vector2.Lerp(a, b, (float)_random.NextDouble()) + Jitter(2) + offset;
            case ParticleShape.Cell when cells.Count > 0:
                return cells[_random.Next(cells.Count)] + Jitter(HalfCell) + offset;
            case ParticleShape.Point:
                return Centre;
            default:
                return To;
        }
    }

    /// <summary>
    /// A direction to fly from <paramref name="at"/>, in radians: the definition's, from the way to the point for
    /// a cone or when aimed, from the way out from the centre when radial.
    /// </summary>
    private float Aimed(Vector2 at)
    {
        ParticleDefinition d = Definition;
        float spread = Roll(-d.Spread, d.Spread);
        static float Degrees(Vector2 v) => MathF.Atan2(v.Y, v.X) * 180 / MathF.PI;
        float aim = d.Radial && at != Centre ? Degrees(at - Centre)
            : (d.Aim || d.Shape == ParticleShape.Cone) && To != From ? Degrees(To - From)
            : 0;
        return (aim + d.Direction + spread) * MathF.PI / 180;
    }

    private Vector2 Jitter(float size) => new(Roll(-size, size), Roll(-size, size));

    private float Roll(float low, float high) => low + (high - low) * (float)_random.NextDouble();

    private static Vector2 Angle(float radians) => new(MathF.Cos(radians), MathF.Sin(radians));

    /// <summary>A list's value at <paramref name="t"/> (0–1) of a life, its keys spread evenly and blended between.</summary>
    public static float Over(IReadOnlyList<float> keys, float t)
    {
        if (keys.Count == 1)
        {
            return keys[0];
        }
        float at = Math.Clamp(t, 0, 1) * (keys.Count - 1);
        int i = Math.Min((int)at, keys.Count - 2);
        return keys[i] + (keys[i + 1] - keys[i]) * (at - i);
    }

    /// <summary>A list's entry at <paramref name="t"/> (0–1) of a life, stepping from one to the next (palette indexes).</summary>
    public static T Stepped<T>(IReadOnlyList<T> keys, float t) => keys[Math.Min((int)(Math.Clamp(t, 0, 1) * keys.Count), keys.Count - 1)];

    /// <summary>A seed for an effect from what it shows, the same in every run (not <c>HashCode</c>, which changes between runs).</summary>
    public static int SeedOf(int spell, LookPart part, Vector2 from, Vector2 to)
    {
        uint h = (uint)spell * 0x9E3779B1u ^ (uint)part * 0x85EBCA77u ^ (uint)(int)from.X * 0xC2B2AE3Du
            ^ (uint)(int)from.Y * 0x27D4EB2Fu ^ (uint)(int)to.X * 0x165667B1u ^ (uint)(int)to.Y * 0x2C1B3C6Du;
        h ^= h >> 15;
        h *= 0x297A2D39u;
        return (int)(h ^ h >> 12);
    }
}
