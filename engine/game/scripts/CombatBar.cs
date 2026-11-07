using DarkSun.Data.Images;
using Godot;
using static DarkSun.Engine.StoneDraw;

namespace DarkSun.Engine;

/// <summary>
/// The fight's quick-use bar at the screen's lower left (issue #159), in the game's
/// stone (<see cref="StoneDraw"/>): a row of icon buttons, CAST, GUARD, WAIT, END TURN
/// and AUTO, the original's pictures where it has one (<see cref="AddButton"/>), drawn
/// a step smaller than the screen so they stay sharp, as the spell bar's. A stone plaque
/// over it says what a click on the map would do (the chance to hit, a spell's targets),
/// else what the member whose turn it is has left; under the pointer a button's name and
/// keys take its place, as a tooltip.
/// </summary>
public partial class CombatBar : RetroScreen
{
    private const int Left = 4, Foot = 198, Pad = 2, Gap = 1, SlotPixels = 18, LineHeight = GameFonts.Size + 1;

    /// <summary>How long the pointer rests on a button before its tooltip shows (seconds).</summary>
    private const double TooltipDelay = 0.3;

    private static readonly Color TipInk = Color.FromHtml("#F0D890");

    private readonly Panel _panel = new();
    private readonly List<Button> _buttons = [];
    private string _turn = "", _tip = "";
    private int _hover = -1;
    private double _rested;
    private float _texel = 1;

    /// <summary>
    /// A button: its name (the tooltip's), the action whose keys the tooltip gives, what it does,
    /// and its picture: an <c>ICON</c> of four frames (normal, under the pointer, greyed, pressed),
    /// one of one frame set on a stone slot, or none, a placeholder (its letter on the slot).
    /// </summary>
    private sealed class Button(string name, string action, string placeholder, Action pressed)
    {
        public string Name { get; } = name;
        public string Action { get; } = action;
        public string Placeholder { get; } = placeholder;
        public Action Pressed { get; } = pressed;
        public Texture2D[] Frames { get; set; } = [];
        public bool Disabled { get; set; }
        public Rect2 Rect { get; set; }
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
        Resized += Layout;
    }

    public override void _Ready()
    {
        base._Ready();
        Layout();
    }

    /// <summary>
    /// A button named <paramref name="name"/>: <paramref name="icon"/> an <c>ICON</c> of
    /// <c>RESOURCE.GFF</c> for its picture, or null for a placeholder showing
    /// <paramref name="placeholder"/> until the original's art is found for it.
    /// </summary>
    public void AddButton(string name, string action, int? icon, string placeholder, Action pressed)
    {
        var button = new Button(name, action, placeholder, pressed);
        if (icon is int number && Session.Open("RESOURCE.GFF").TryRead("ICON", number, out var data))
        {
            Palette palette = Session.Assets.Palette(Session.Open("RESOURCE.GFF").Read("PAL ", StoneUi.PaletteNumber));
            button.Frames = [.. Session.Assets.Frames(data).Where(f => f.Width > 1).Select(f => Textures.ToTexture(f, palette))];
        }
        _buttons.Add(button);
        Layout();
    }

    /// <summary>Grey the button named <paramref name="name"/> out (or not).</summary>
    public void SetDisabled(string name, bool disabled)
    {
        if (_buttons.FirstOrDefault(b => b.Name == name) is { } b && b.Disabled != disabled)
        {
            b.Disabled = disabled;
            _panel.QueueRedraw();
        }
    }

    public bool IsDisabled(string name) => _buttons.FirstOrDefault(b => b.Name == name)?.Disabled ?? true;

    /// <summary>Whose turn and what they have left (parts split at " · ").</summary>
    public string Turn
    {
        get => _turn;
        set
        {
            if (_turn != value)
            {
                _turn = value;
                _panel.QueueRedraw();
            }
        }
    }

    /// <summary>What a click on the map would do now: the plaque shows it in place of the turn while there is one.</summary>
    public string Tip
    {
        get => _tip;
        set
        {
            if (_tip != value)
            {
                _tip = value;
                _panel.QueueRedraw();
            }
        }
    }

    /// <summary>The bar's top left in the screen's 320 × 200 pixels (the spell bar opens over it).</summary>
    public Vector2 TopLeft => _panel.Position;

    /// <summary>Press the button named <paramref name="name"/>, as a click would (for the checks); false if it is greyed out or not there.</summary>
    public bool Press(string name)
    {
        if (_buttons.FirstOrDefault(b => b.Name == name) is not { Disabled: false } b)
        {
            return false;
        }
        b.Pressed();
        return true;
    }

    /// <summary>The tooltip the button named <paramref name="name"/> shows (for the checks).</summary>
    public string TooltipOf(string name) => _buttons.FirstOrDefault(b => b.Name == name) is { } b ? Tooltip(b) : "";

    /// <summary>Show the button named <paramref name="name"/> as under the pointer, its tooltip up (for screenshots).</summary>
    public void Hover(string name)
    {
        (_hover, _rested) = (_buttons.FindIndex(b => b.Name == name), TooltipDelay);
        _panel.QueueRedraw();
    }

    private static string Tooltip(Button b) => KeyHelp.Keys(b.Action) is { Length: > 0 } keys ? $"{b.Name} ({keys})" : b.Name;

    // -- the layout -------------------------------------------------------------------------------------

    private float Slot => SlotPixels * _texel;

    private void Layout()
    {
        float scale = Canvas.Scale.X;
        _texel = scale >= 2 ? (scale - 1) / scale : 1;
        float y = Pad;
        for (int i = 0; i < _buttons.Count; i++)
        {
            _buttons[i].Rect = new Rect2(Pad + i * (Slot + Gap), y, Slot, Slot);
        }
        float width = MathF.Ceiling(2 * Pad + _buttons.Count * (Slot + Gap) - Gap);
        float height = MathF.Ceiling(y + Slot + Pad);
        _panel.Position = new Vector2(Left, Foot - height);
        _panel.Size = new Vector2(width, height);
        _panel.QueueRedraw();
    }

    public override void _Process(double delta)
    {
        if (_hover >= 0 && _rested < TooltipDelay && (_rested += delta) >= TooltipDelay)
        {
            _panel.QueueRedraw();
        }
    }

    // -- drawing ----------------------------------------------------------------------------------------

    /// <summary>What the plaque says now, and in which ink: a button's tooltip, the click's tip, or the turn (commas for "·", which the font hasn't).</summary>
    public (string Text, Color Ink) Plaque =>
        _hover >= 0 && _rested >= TooltipDelay ? (Tooltip(_buttons[_hover]), Yellow)
            : _tip.Length > 0 ? (_tip.Replace(" · ", ", "), TipInk)
            : (_turn.Replace(" · ", ", "), Pale);

    private void Draw(Panel p)
    {
        Frame(p, new Rect2I(Vector2I.Zero, (Vector2I)_panel.Size));
        for (int i = 0; i < _buttons.Count; i++)
        {
            DrawButton(p, _buttons[i], i == _hover);
        }
        var (text, ink) = Plaque;
        if (text.Length > 0)
        {
            DrawTooltip(p, text, ink, _hover >= 0 && _rested >= TooltipDelay ? _buttons[_hover].Rect.Position.X : 0);
        }
    }

    private void DrawButton(Panel p, Button b, bool hover)
    {
        Texture2D[] f = b.Frames;
        if (f.Length >= 3)
        {
            // The original's button: its own frames.
            Texture2D frame = b.Disabled ? f[2] : hover ? f[1] : f[0];
            p.DrawTextureRect(frame, new Rect2(b.Rect.Position, frame.GetSize() * _texel), false);
            return;
        }
        // A stone slot, the picture (or the placeholder's letter) on it.
        Rect2I slot = new((Vector2I)b.Rect.Position.Floor(), (Vector2I)b.Rect.Size.Ceil());
        Chip(p, slot, "", Pale, false);
        Color ink = b.Disabled ? new Color(0.55f, 0.55f, 0.6f) : Colors.White;
        if (f.Length > 0)
        {
            Vector2 size = f[0].GetSize();
            Vector2 inset = ((new Vector2(SlotPixels, SlotPixels) - size) / 2).Floor();
            p.DrawTextureRect(f[0], new Rect2(b.Rect.Position + inset * _texel, size * _texel), false, ink);
        }
        else
        {
            int x = (int)(b.Rect.Position.X + (b.Rect.Size.X - Measure(b.Placeholder)) / 2), y = (int)(b.Rect.Position.Y + (b.Rect.Size.Y - GameFonts.Size) / 2);
            DrawText(p, b.Placeholder, x, y, b.Disabled ? Dim : hover ? Yellow : Pale);
        }
        if (hover && !b.Disabled)
        {
            p.DrawRect(b.Rect, Yellow, false, _texel);
        }
    }

    /// <summary>A small stone plaque with <paramref name="text"/>, just above the bar from <paramref name="x"/> (kept left of the combat log).</summary>
    private void DrawTooltip(Panel p, string text, Color ink, float x)
    {
        const int Room = 128; // the combat log's left edge, 136, less the bar's left
        text = Fit(text, Room - 8);
        int w = Measure(text) + 8, h = LineHeight + 4;
        int left = (int)Math.Clamp(x, 0, Math.Max(0, Room - w));
        Frame(p, new Rect2I(left, -h - 1, w, h));
        DrawText(p, text, left + 4, -h + 1, ink);
    }

    // -- the pointer ------------------------------------------------------------------------------------

    private int ButtonAt(Vector2 at) => _buttons.FindIndex(b => b.Rect.HasPoint(at));

    private void Input(InputEvent e)
    {
        if (e is InputEventMouseMotion motion)
        {
            int hover = ButtonAt(motion.Position);
            if (hover != _hover)
            {
                (_hover, _rested) = (hover, 0);
                _panel.QueueRedraw();
            }
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

    /// <summary>The panel's control: draws it (and its tooltip, above it) and takes the pointer over it.</summary>
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
