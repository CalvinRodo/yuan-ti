using Godot;
using static DarkSun.Engine.StoneDraw;

namespace DarkSun.Engine;

/// <summary>
/// The fight's bar at the screen's lower left (issue #159), in the game's stone and
/// font as the combat log beside it (<see cref="StoneDraw"/>): whose turn it is and
/// what they have left, what a click on the map would do, and the buttons CAST,
/// GUARD, WAIT, END TURN and AUTO as small stone buttons, their keys in the tooltip.
/// It keeps to the 130 pixels left of the combat log, its lines wrapping to fit.
/// </summary>
public partial class CombatBar : RetroScreen
{
    private const int Left = 4, Foot = 198, BarWidth = 130, LineHeight = GameFonts.Size + 1, ChipHeight = 11, Pad = 4;

    private static readonly Color TipInk = Color.FromHtml("#F0D890");

    private readonly Panel _panel = new();
    private readonly List<Button> _buttons = [];
    private string _turn = "", _tip = "";
    private List<string> _turnLines = [], _tipLines = [];
    private int _hover = -1;

    private sealed class Button(string text, string action, Action pressed)
    {
        public string Text { get; } = text;
        public string Action { get; } = action;
        public Action Pressed { get; } = pressed;
        public bool Disabled { get; set; }
        public Rect2I Rect { get; set; }
    }

    public CombatBar()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        Canvas.MouseFilter = MouseFilterEnum.Ignore;
        ((ColorRect)GetChild(0)).Visible = false;
        _panel.View = this;
        _panel.MouseFilter = MouseFilterEnum.Stop;
        Canvas.AddChild(_panel);
        Visible = false;
    }

    /// <summary>A button: its word, the action whose keys its tooltip gives, what it does.</summary>
    public void AddButton(string text, string action, Action pressed)
    {
        _buttons.Add(new Button(text, action, pressed));
        Layout();
    }

    /// <summary>Grey the button showing <paramref name="text"/> out (or not).</summary>
    public void SetDisabled(string text, bool disabled)
    {
        if (_buttons.FirstOrDefault(b => b.Text == text) is { } b && b.Disabled != disabled)
        {
            b.Disabled = disabled;
            _panel.QueueRedraw();
        }
    }

    public bool IsDisabled(string text) => _buttons.FirstOrDefault(b => b.Text == text)?.Disabled ?? true;

    /// <summary>Whose turn and what they have left: its parts split at " · ", as many to a line as fit (joined with commas).</summary>
    public string Turn
    {
        get => _turn;
        set
        {
            if (_turn != value)
            {
                _turn = value;
                Layout();
            }
        }
    }

    /// <summary>What a click on the map would do now (wrapped at its words).</summary>
    public string Tip
    {
        get => _tip;
        set
        {
            if (_tip != value)
            {
                _tip = value;
                Layout();
            }
        }
    }

    /// <summary>Press the button showing <paramref name="text"/>, as a click would (for the checks); false if it is greyed out or not there.</summary>
    public bool Press(string text)
    {
        if (_buttons.FirstOrDefault(b => b.Text == text) is not { Disabled: false } b)
        {
            return false;
        }
        b.Pressed();
        return true;
    }

    // -- the layout -------------------------------------------------------------------------------------

    private static List<string> Pack(IEnumerable<string> pieces, string joint, int room)
    {
        var lines = new List<string>();
        foreach (string piece in pieces)
        {
            if (lines.Count > 0 && Measure(lines[^1] + joint + piece) <= room)
            {
                lines[^1] += joint + piece;
            }
            else
            {
                lines.Add(Fit(piece, room));
            }
        }
        return lines;
    }

    private void Layout()
    {
        int room = BarWidth - 2 * Pad - 2;
        _turnLines = Pack(_turn.Split(" · ", StringSplitOptions.RemoveEmptyEntries), ", ", room); // the game's font has no "·"
        _tipLines = _tip.Length == 0 ? [] : Pack(_tip.Split(' '), " ", room);
        int y = Pad + (_turnLines.Count + _tipLines.Count) * LineHeight + 2, x = Pad;
        foreach (Button b in _buttons)
        {
            int w = Measure(b.Text) + 8;
            if (x > Pad && x + w > BarWidth - Pad)
            {
                (x, y) = (Pad, y + ChipHeight + 2);
            }
            b.Rect = new Rect2I(x, y, w, ChipHeight);
            x += w + 2;
        }
        int height = (_buttons.Count > 0 ? _buttons[^1].Rect.End.Y : y) + Pad;
        _panel.Position = new Vector2(Left, Foot - height);
        _panel.Size = new Vector2(BarWidth, height);
        _panel.QueueRedraw();
    }

    // -- drawing ----------------------------------------------------------------------------------------

    private void Draw(Panel p)
    {
        Frame(p, new Rect2I(0, 0, BarWidth, (int)_panel.Size.Y));
        int y = Pad;
        foreach (string line in _turnLines)
        {
            DrawText(p, line, Pad + 1, y, Pale);
            y += LineHeight;
        }
        foreach (string line in _tipLines)
        {
            DrawText(p, line, Pad + 1, y, TipInk);
            y += LineHeight;
        }
        for (int i = 0; i < _buttons.Count; i++)
        {
            Button b = _buttons[i];
            Chip(p, b.Rect, b.Text, b.Disabled ? Dim : i == _hover ? Yellow : Pale, false);
        }
    }

    // -- the pointer ------------------------------------------------------------------------------------

    private int ButtonAt(Vector2 at) => _buttons.FindIndex(b => b.Rect.HasPoint((Vector2I)at.Floor()));

    private void Input(InputEvent e)
    {
        if (e is InputEventMouseMotion motion && ButtonAt(motion.Position) is var hover && hover != _hover)
        {
            _hover = hover;
            _panel.TooltipText = hover >= 0 ? KeyHelp.Keys(_buttons[hover].Action) : "";
            _panel.QueueRedraw();
        }
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click && ButtonAt(click.Position) is >= 0 and var i
            && !_buttons[i].Disabled)
        {
            _buttons[i].Pressed();
        }
        if (e is InputEventMouseButton)
        {
            _panel.AcceptEvent();
        }
    }

    /// <summary>The panel's control: draws it and takes the pointer over it.</summary>
    private sealed partial class Panel : Control
    {
        public CombatBar View = null!;

        public override void _Draw() => View.Draw(this);

        public override void _GuiInput(InputEvent e) => View.Input(e);

        public override void _Notification(int what)
        {
            if (what == NotificationMouseExit && View._hover >= 0)
            {
                View._hover = -1;
                QueueRedraw();
            }
        }
    }
}
