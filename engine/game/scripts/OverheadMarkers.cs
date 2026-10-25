using Godot;

namespace DarkSun.Engine;

/// <summary>
/// The overhead map's marks (OVERHEAD MAP, <c>BUTN</c> 10305): the whole region in
/// view, the party's members as steady dots and the enemies still on it as
/// flashing ones, as the original shows them (the guide: "Flashing dots on the
/// Overhead map mark remaining enemies"). Drawn in map pixels, the dots a fixed
/// size on the screen whatever the zoom.
/// </summary>
public partial class OverheadMarkers : Node2D
{
    /// <summary>Flashes a second: on half the time, off the other half.</summary>
    private const double Flashes = 2;

    private const float Radius = 2.5f;

    private static readonly Color Enemy = new(0.95f, 0.15f, 0.1f), Member = new(1f, 1f, 0.85f), Outline = new(0, 0, 0, 0.8f);

    private double _time;

    /// <summary>Where the marks go (map pixels) and whether each is an enemy.</summary>
    public Func<IEnumerable<(Vector2 At, bool Enemy)>> Marks { get; set; } = () => [];

    /// <summary>Whether the enemies' dots show now (for the checks).</summary>
    public bool EnemiesLit => (int)(_time * Flashes * 2) % 2 == 0;

    public override void _Process(double delta)
    {
        _time += delta;
        QueueRedraw();
    }

    public override void _Draw()
    {
        float scale = 1f / Math.Max(0.01f, GetViewport().GetCamera2D()?.Zoom.X ?? 1f);
        foreach (var (at, enemy) in Marks())
        {
            if (enemy && !EnemiesLit)
            {
                continue;
            }
            DrawCircle(at, (Radius + 1) * scale, Outline);
            DrawCircle(at, Radius * scale, enemy ? Enemy : Member);
        }
    }
}
