using DarkSun.Data.Regions;
using DarkSun.Game.Movement;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// Box selection (<see cref="Settings.BoxSelect"/>, off by default; not in
/// DSUN.EXE, which moves the leader alone or the whole party): out of a fight,
/// with the MOVE pointer, dragging on the map picks the characters whose
/// figures the box touches, and a click walks only them while the rest stay.
/// A click without a drag walks as before. Choosing another leader, collapsing
/// the party, a fight or a new region drops the picks; so does a box round no one.
/// </summary>
public partial class RegionView
{
    /// <summary>How far, in screen pixels, the pointer moves before a press becomes a box.</summary>
    private const float BoxDragDistance = 6;

    private readonly List<PartyMember> _picked = [];
    private BoxSelection _box = null!;
    private Vector2? _boxFrom, _boxScreenFrom;
    private Vector2 _boxTo;
    private bool _boxShift;

    /// <summary>The picked characters still in the party, in party order.</summary>
    private List<PartyMember> Picked => [.. _party.Where(_picked.Contains)];

    private void ClearPicked()
    {
        _picked.Clear();
        _boxFrom = null;
        _box?.Show(null, []);
    }

    /// <summary>The map point under a mouse event (map pixels, through the camera).</summary>
    private Vector2 MapPoint(InputEventMouse e) => GetCanvasTransform().AffineInverse() * e.Position;

    /// <summary>A press, drag or release of the box; true when it was one (the map's other clicks then wait).</summary>
    private bool BoxInput(InputEvent e)
    {
        if (!Settings.BoxSelect || InCombat || _cursor != GameMenu.Cursor.Move)
        {
            _boxFrom = null;
            return false;
        }
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press:
                (_boxFrom, _boxScreenFrom, _boxTo, _boxShift) = (MapPoint(press), press.Position, MapPoint(press), press.ShiftPressed);
                return true;
            case InputEventMouseMotion motion when _boxFrom is not null:
                if ((motion.ButtonMask & MouseButtonMask.Left) == 0)
                {
                    _boxFrom = null; // let go somewhere the map didn't hear
                    return false;
                }
                _boxTo = MapPoint(motion);
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } release when _boxFrom is { } from:
                _boxFrom = null;
                if (release.Position.DistanceTo(_boxScreenFrom!.Value) < BoxDragDistance)
                {
                    WalkTo(new Cell((int)Math.Floor(from.X / Region.TileSize), (int)Math.Floor(from.Y / Region.TileSize)), _boxShift);
                }
                else
                {
                    Pick(new Rect2(from, Vector2.Zero).Expand(MapPoint(release)));
                }
                return true;
        }
        return false;
    }

    /// <summary>The characters whose figures <paramref name="box"/> touches are picked; the leader becomes one of them.</summary>
    private void Pick(Rect2 box)
    {
        List<PartyMember> inside = [.. _party.Where((p, i) => p.Node.Visible && _world[i] is { Collapsed: false }
            && (p.Node.GetGlobalTransform() * p.Node.GetRect()).Intersects(box, includeBorders: true))];
        if (Selected is { } leader && !inside.Contains(leader) && inside.Count > 0)
        {
            MakeLeader(_party.IndexOf(inside[0]));
        }
        ClearPicked();
        _picked.AddRange(inside);
        Notice(inside.Count switch
        {
            0 => "",
            1 => $"{MemberName(_party.IndexOf(inside[0]))} picked",
            _ => $"{inside.Count} characters picked: a click walks them",
        });
    }
}

/// <summary>The box being dragged and a ring under each picked character but the leader (who has <see cref="SelectionMarker"/>'s).</summary>
public partial class BoxSelection : Node2D
{
    private Rect2? _box;
    private List<Walker> _picked = [];

    public void Show(Rect2? box, List<Walker> picked)
    {
        if (_box == box && _picked.SequenceEqual(picked) && picked.Count == 0)
        {
            return;
        }
        (_box, _picked) = (box, picked);
        QueueRedraw();
    }

    public override void _Draw()
    {
        var green = new Color(0.4f, 1f, 0.4f, 0.9f);
        if (_box is { } box)
        {
            DrawRect(box, new Color(green, 0.12f));
            DrawRect(box, green, filled: false, width: 1);
        }
        foreach (Walker w in _picked)
        {
            float radius = 6 + w.Footprint.HalfWidth * 16;
            DrawSetTransform(new Vector2(w.Feet.X, w.Feet.Y - 2), 0, new Vector2(1, 0.5f));
            DrawArc(Vector2.Zero, radius, 0, Mathf.Tau, 32, new Color(green, 0.55f), 1);
        }
        DrawSetTransform(Vector2.Zero);
    }
}
