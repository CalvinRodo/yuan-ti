using Godot;

namespace DarkSun.Engine;

/// <summary>A party member as View Character shows them (null: an empty box).</summary>
public sealed record PartyMemberView(string Name, int Hp, int MaxHp, int Psp, int MaxPsp, int Status, bool ComputerControl, bool Locked = false);

/// <summary>
/// What DSUN.EXE draws in View Character's frames (<c>WIND</c> 11500; the
/// frames' hover texts are <c>78A9:0400</c>), shared by Create Characters'
/// screen and the map's:
/// <list type="bullet">
/// <item>under each row of boxes three bars (<c>APFM</c> 11261–11266: row r's
/// hit points at 11261 + r, psionic points at 11263 + r, status at 11265 + r),
/// each split between the row's two members; the bars name themselves
/// HIT POINTS: CURRENT/MAX, PSIONIC POINTS: CURRENT/MAX, CURRENT STATUS
/// (<c>DS:328E</c>);</item>
/// <item>the status line (at <c>APFM</c> 11270) names what the pointer is on: a
/// box its member or PRESS RIGHT MOUSE BUTTON when empty; the leader button
/// "%s IS LEADER" / "MAKE %s LEADER"; computer control "COMPUTER CONTROL IS
/// ON / OFF / LOCKED"; INACTIVE CHARACTER for an empty box's buttons.</item>
/// </list>
/// The leader and computer-control buttons show their state pressed.
/// Without <c>bars</c> (Create Characters' View Character, whose
/// <c>8F23:1E30</c> writes the points and status under the boxes instead,
/// <see cref="ViewCharacterText"/>) the bar frames stay empty.
/// </summary>
public sealed class PartyFrames
{
    public const int FirstBox = 11300, FirstLeader = 11309, FirstComputer = 11313, FirstBar = 11261;

    private static readonly string[] BarNames = ["HIT POINTS: CURRENT/MAX", "PSIONIC POINTS: CURRENT/MAX", "CURRENT STATUS"];

    /// <summary>DSUN.EXE's status words (<c>DS:11DC</c>), by status.</summary>
    public static readonly string[] StatusNames = ["New", "Okay", "Stunned", "Out Cold", "Dying", "Dead", "Animated", "Petrified", "Gone"];

    private readonly UiWindowView _window;
    private readonly Label _status;
    private readonly Func<int, PartyMemberView?> _member;
    private readonly Func<int> _leader;
    private readonly ColorRect[,] _fills = new ColorRect[3, 4];
    private string? _saved;

    public PartyFrames(UiWindowView window, Label status, Func<int, PartyMemberView?> member, Func<int> leader, bool bars = true)
    {
        (_window, _status, _member, _leader) = (window, status, member, leader);
        for (int bar = 0; bar < (bars ? 3 : 0); bar++)
        {
            for (int row = 0; row < 2; row++)
            {
                if (window.Slot(FirstBar + bar * 2 + row) is not { } frame)
                {
                    continue;
                }
                for (int side = 0; side < 2; side++)
                {
                    int slot = row * 2 + side, b = bar;
                    float half = frame.Size.X / 2;
                    var track = new ColorRect
                    {
                        Position = new Vector2(side * half + 1, 0),
                        Size = new Vector2(half - 2, frame.Size.Y),
                        Color = new Color(0.08f, 0.08f, 0.12f),
                        MouseFilter = Control.MouseFilterEnum.Stop,
                    };
                    var fill = new ColorRect { Size = track.Size, MouseFilter = Control.MouseFilterEnum.Ignore };
                    track.AddChild(fill);
                    frame.AddChild(track);
                    track.MouseEntered += () => Hover(BarText(b, slot));
                    track.MouseExited += Unhover;
                    _fills[b, slot] = fill;
                }
            }
        }
        for (int i = 0; i < 4; i++)
        {
            int slot = i;
            Hoverable(FirstBox + i, () => _member(slot) is { } m ? m.Name.ToUpperInvariant() : "PRESS RIGHT MOUSE BUTTON");
            Hoverable(FirstLeader + i, () => _member(slot) is not { } m ? "INACTIVE CHARACTER"
                : _leader() == slot ? $"{m.Name.ToUpperInvariant()} IS LEADER" : $"MAKE {m.Name.ToUpperInvariant()} LEADER");
            Hoverable(FirstComputer + i, () => _member(slot) is { } m
                ? $"COMPUTER CONTROL IS {(m.Locked ? "LOCKED" : m.ComputerControl ? "ON" : "OFF")}"
                : "INACTIVE CHARACTER");
        }
    }

    private void Hoverable(int button, Func<string> text)
    {
        if (_window.Button(button) is { } b)
        {
            Hoverable(b, text);
        }
    }

    /// <summary>Name <paramref name="control"/> on the status line while the pointer is on it.</summary>
    public void Hoverable(Control control, Func<string> text)
    {
        control.MouseEntered += () => Hover(text());
        control.MouseExited += Unhover;
    }

    /// <summary>Put the screen's own message on the status line (what shows again when the pointer leaves a frame).</summary>
    public string Resting
    {
        get => _saved ?? _status.Text;
        set
        {
            _saved = null;
            _status.Text = value;
        }
    }

    private void Hover(string text)
    {
        _saved ??= _status.Text;
        _status.Text = text;
    }

    private void Unhover()
    {
        if (_saved is { } saved)
        {
            _status.Text = saved;
            _saved = null;
        }
    }

    private string BarText(int bar, int slot) => _member(slot) is not { } m ? "INACTIVE CHARACTER" : bar switch
    {
        0 => $"{BarNames[0].Split(':')[0]}: {m.Hp}/{m.MaxHp}",
        1 => $"{BarNames[1].Split(':')[0]}: {m.Psp}/{m.MaxPsp}",
        _ => $"{BarNames[2]}: {StatusName(m.Status).ToUpperInvariant()}",
    };

    public static string StatusName(int status) => status >= 0 && status < StatusNames.Length ? StatusNames[status] : "Okay";

    /// <summary>Draw the bars and the buttons' states.</summary>
    public void Refresh()
    {
        int leader = _leader();
        for (int slot = 0; slot < 4; slot++)
        {
            PartyMemberView? m = _member(slot);
            Fill(0, slot, m is null ? 0 : Ratio(m.Hp, m.MaxHp), new Color(0.75f, 0.2f, 0.15f));
            Fill(1, slot, m is null ? 0 : Ratio(m.Psp, m.MaxPsp), new Color(0.3f, 0.45f, 0.85f));
            Fill(2, slot, m is null ? 0 : 1, m?.Status switch
            {
                null or 0 or 1 => new Color(0.3f, 0.65f, 0.3f),
                2 => new Color(0.85f, 0.75f, 0.25f),
                3 or 4 => new Color(0.85f, 0.45f, 0.15f),
                _ => new Color(0.35f, 0.3f, 0.3f),
            });
            if (_window.Button(FirstLeader + slot) is { } l)
            {
                l.ToggleMode = true;
                l.Disabled = m is null;
                l.SetPressedNoSignal(m is not null && slot == leader);
            }
            if (_window.Button(FirstComputer + slot) is { } c)
            {
                c.ToggleMode = true;
                c.Disabled = m is null;
                c.SetPressedNoSignal(m is { ComputerControl: true });
            }
        }
    }

    private static float Ratio(int value, int max) => max <= 0 ? 0 : Math.Clamp((float)value / max, 0, 1);

    private void Fill(int bar, int slot, float ratio, Color colour)
    {
        if (_fills[bar, slot] is { } fill)
        {
            var track = (Control)fill.GetParent();
            fill.Size = new Vector2(MathF.Round(track.Size.X * ratio), track.Size.Y);
            fill.Color = colour;
        }
    }
}
