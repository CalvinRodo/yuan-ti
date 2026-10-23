using DarkSun.Game.Combat;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// The combat log on the map, as Obsidian Edition's
/// (github.com/daaki85/darksun-obsidian-edition, its "combat log in the
/// game"): a panel in the game's stone docked at the screen's lower right
/// (180 × 78 of the 320 × 200 screen, its foot at row 198), a header
/// ("Round 7: K'ratchek, then Slig, Gerakis") and the fight's lines in a dark
/// well in the game's font, each coloured by its kind, the names by whose
/// they are (each party member's colour, monsters red), a filled pip on a
/// party member's line and a hollow one on a monster's. The header's buttons:
/// L hides it (as the L key, which shows it again), the level chip (Least,
/// Short, Detail: <see cref="LogLevel"/>), − and + its size (collapsed to the
/// newest line, docked, expanded with the filters: All, Party, Damage,
/// Saves). The scrollbar, the wheel or a drag of its handle read back, and the
/// LATEST chip comes back to the newest. Whether it is shown, its level and
/// its size are kept in the settings.
/// </summary>
public partial class CombatLogView : RetroScreen
{
    public enum PanelSize { Collapsed, Docked, Expanded }

    private const int PanelWidth = 180, Right = 316, Foot = 198, LineHeight = GameFonts.Size, Indent = 8, WheelRows = 3, HandleMin = 8;

    private static readonly Dictionary<PanelSize, (int Height, bool Filters)> Sizes = new()
    {
        [PanelSize.Collapsed] = (15, false),
        [PanelSize.Docked] = (78, false),
        [PanelSize.Expanded] = (130, true),
    };

    // Obsidian Edition's colours (its palette.py): the stone, the well, the line kinds and the names.
    private static readonly Color Outline = Color.FromHtml("#202038"), Lit = Color.FromHtml("#A6A6BE"), Stone = Color.FromHtml("#71718A"),
        Dark = Color.FromHtml("#595971"), Well = Color.FromHtml("#30304D"), TextShadow = Color.FromHtml("#101018"), Pale = Color.FromHtml("#D7D7E3"),
        Monster = Color.FromHtml("#FF7070");

    private static readonly Color[] WellShades = [Color.FromHtml("#262640"), Color.FromHtml("#2C2C48"), Well, Color.FromHtml("#3A3A5A")];

    private static readonly Color[] PartyColours = [Color.FromHtml("#6FD8E8"), Color.FromHtml("#F48CF4"), Color.FromHtml("#FFB86B"), Color.FromHtml("#FFFFFF")];

    private static readonly Dictionary<LogKind, Color> KindColours = new()
    {
        [LogKind.Hit] = Color.FromHtml("#8FD16A"),
        [LogKind.Miss] = Color.FromHtml("#B4B4C8"),
        [LogKind.Damage] = Color.FromHtml("#FFA600"),
        [LogKind.Save] = Color.FromHtml("#86ABFF"),
        [LogKind.Detail] = Color.FromHtml("#C8C8D8"),
        [LogKind.Round] = Pale,
        [LogKind.Turn] = Color.FromHtml("#D3A28A"),
        [LogKind.Fell] = Color.FromHtml("#E7EB18"),
        [LogKind.Other] = Color.FromHtml("#E7EB18"),
    };

    private static readonly string[] LevelNames = ["Least", "Short", "Detail"];

    /// <summary>The fight's log (<see cref="RegionView"/> writes it).</summary>
    public CombatLog Log { get; set; } = new(n => $"effect {n}", _ => "an item");

    /// <summary>The fight, for the header (null out of one).</summary>
    public Func<Battle?> Battle { get; set; } = () => null;

    /// <summary>The party slot of a party member (its names' colour), else null.</summary>
    public Func<Combatant, int?> PartySlot { get; set; } = _ => null;

    /// <summary>The map is covered by a screen or window: the panel waits under it.</summary>
    public Func<bool> Covered { get; set; } = () => false;

    private readonly Panel _panel = new();
    private int _version = -1;
    private LogLevel _level;
    private PanelSize _size;
    private LogFilter _filter;
    private string _header = "";

    /// <summary>The top row shown when read back; null follows the newest.</summary>
    private int? _first;
    private List<Row> _rows = [];
    private float? _dragFrom;
    private int _dragFirst;

    /// <summary>A row of the well: its pieces (text, colour), where it starts, its pip (null: none; true: filled), and a round's rule.</summary>
    private sealed record Row(List<(string Text, Color Colour)> Pieces, int X, bool? Pip, Color PipColour, bool Rule);

    public CombatLogView()
    {
        // Only the panel takes the pointer: the map around it gets its clicks.
        MouseFilter = MouseFilterEnum.Ignore;
        Canvas.MouseFilter = MouseFilterEnum.Ignore;
        ((ColorRect)GetChild(0)).Visible = false;
        _panel.View = this;
        _panel.MouseFilter = MouseFilterEnum.Stop;
        Canvas.AddChild(_panel);
    }

    public override void _Ready()
    {
        base._Ready();
        _level = (LogLevel)Math.Clamp(Settings.CombatLogLevel, 0, 2);
        _size = (PanelSize)Math.Clamp(Settings.CombatLogSize, 0, 2);
        Layout();
    }

    /// <summary>Shown (L): kept in the settings.</summary>
    public static bool Open
    {
        get => Settings.CombatLogOpen;
        set
        {
            Settings.CombatLogOpen = value;
            Settings.Save();
        }
    }

    public LogLevel Level => _level;

    /// <summary>Its size now (− and + change it).</summary>
    public PanelSize CurrentSize => _size;

    /// <summary>L: show it, or hide it.</summary>
    public void Toggle()
    {
        Open = !Open;
        _version = -1;
    }

    /// <summary>The newest lines again (a new fight).</summary>
    public void FollowNewest()
    {
        _first = null;
        _version = -1;
    }

    public override void _Process(double delta)
    {
        bool show = Open && !Log.IsEmpty && !Covered();
        if (Visible != show)
        {
            Visible = show;
        }
        string header = Log.Header(Battle());
        if (!show || (_version == Log.Version && header == _header))
        {
            return;
        }
        Rebuild(header);
    }

    /// <summary>The well's rows from the log as it is (the checks call it with the panel hidden).</summary>
    public void Rebuild(string? header = null)
    {
        header ??= Log.Header(Battle());
        _header = header;
        _version = Log.Version;
        _rows = RowsOf(Log.At(_level, Sizes[_size].Filters ? _filter : LogFilter.All));
        _panel.QueueRedraw();
    }

    // -- the layout -------------------------------------------------------------------------------------

    private int Height => Sizes[_size].Height;

    private void Layout()
    {
        _panel.Position = new Vector2(Right - PanelWidth, Foot - Height);
        _panel.Size = new Vector2(PanelWidth, Height);
        _version = -1;
        _panel.QueueRedraw();
    }

    /// <summary>The well: inside the frame, under the header (none collapsed).</summary>
    private Rect2I WellRect => _size == PanelSize.Collapsed ? new Rect2I(4, 13, PanelWidth - 8, 0) : new Rect2I(4, 15, PanelWidth - 8, Height - 19);

    private int TextTop => WellRect.Position.Y + 2 + (Sizes[_size].Filters ? LineHeight + 2 : 0);

    private int VisibleRows => _size == PanelSize.Collapsed ? 0 : Math.Max(1, (WellRect.End.Y - 1 - TextTop) / LineHeight);

    private int TextX => WellRect.Position.X + 7;

    private int TextRight => WellRect.End.X - 9;

    private Rect2I Track => new(WellRect.End.X - 6, TextTop - 1, 5, VisibleRows * LineHeight);

    private int TopRow => _first is { } f ? Math.Clamp(f, 0, Math.Max(0, _rows.Count - VisibleRows)) : Math.Max(0, _rows.Count - VisibleRows);

    /// <summary>The header's buttons, right to left: + and −, the level chip, L; scrolled back, LATEST before them.</summary>
    private List<(string Text, Rect2I Rect, Action Pressed)> Buttons()
    {
        var buttons = new List<(string, Rect2I, Action)>();
        int x = PanelWidth - 3;
        void Add(string text, int width, Action pressed)
        {
            x -= width;
            buttons.Add((text, new Rect2I(x, 2, width, 11), pressed));
            x -= 1;
        }
        if (_size != PanelSize.Expanded)
        {
            Add("+", 9, () => Resize(+1));
        }
        if (_size != PanelSize.Collapsed)
        {
            Add("-", 9, () => Resize(-1));
        }
        Add(LevelNames[(int)_level], Measure(LevelNames[(int)_level]) + 6, NextLevel);
        Add("L", 9, Toggle);
        if (_first is not null && _size != PanelSize.Collapsed)
        {
            Add("LATEST", Measure("LATEST") + 6, FollowNewest);
        }
        return buttons;
    }

    private List<(LogFilter Filter, Rect2I Rect)> FilterChips()
    {
        var chips = new List<(LogFilter, Rect2I)>();
        if (!Sizes[_size].Filters)
        {
            return chips;
        }
        int x = WellRect.Position.X + 2;
        foreach (LogFilter f in Enum.GetValues<LogFilter>())
        {
            int w = Measure(f.ToString()) + 6;
            chips.Add((f, new Rect2I(x, WellRect.Position.Y + 1, w, LineHeight + 1)));
            x += w + 2;
        }
        return chips;
    }

    private void Resize(int by)
    {
        _size = (PanelSize)Math.Clamp((int)_size + by, 0, 2);
        Settings.CombatLogSize = (int)_size;
        Settings.Save();
        Layout();
    }

    private void NextLevel()
    {
        _level = (LogLevel)(((int)_level + 1) % 3);
        Settings.CombatLogLevel = (int)_level;
        Settings.Save();
        FollowNewest();
    }

    // -- the lines --------------------------------------------------------------------------------------

    private static Font FontIn(Color colour) => GameFonts.Get(colour, TextShadow) ?? ThemeDB.FallbackFont;

    private static int Measure(string text) => (int)FontIn(Pale).GetStringSize(text, HorizontalAlignment.Left, -1, GameFonts.Size).X;

    private Color NameColour(Combatant c) => PartySlot(c) is int slot ? PartyColours[Math.Clamp(slot, 0, PartyColours.Length - 1)] : Monster;

    /// <summary>The lines as the well's rows: each wrapped to its width, the rows after a line's first indented.</summary>
    private List<Row> RowsOf(List<LogLine> lines)
    {
        var rows = new List<Row>();
        foreach (LogLine line in lines)
        {
            Color ink = KindColours[line.Kind];
            if (line.Kind == LogKind.Round)
            {
                rows.Add(new Row([(line.Text, ink)], TextX, null, ink, true));
                continue;
            }
            int x0 = TextX + Indent * line.Depth;
            var row = new List<(string, Color)>();
            int x = x0;
            bool first = true;
            void Break()
            {
                rows.Add(new Row(row, first ? x0 : x0 + Indent, first && line.Whose is not null && line.Depth == 0 ? line.Whose.PartyMember : null,
                    line.Whose is { } w ? NameColour(w) : ink, false));
                row = [];
                first = false;
                x = x0 + Indent;
            }
            foreach (LogRun run in line.Runs)
            {
                Color colour = run.Name is { } who ? NameColour(who) : ink;
                // Words with the spaces before them, so a row breaks between words.
                foreach (string word in System.Text.RegularExpressions.Regex.Split(run.Text, @"(?=\s)"))
                {
                    if (word.Length == 0)
                    {
                        continue;
                    }
                    int width = Measure(word);
                    if (x + width > TextRight && row.Count > 0)
                    {
                        Break();
                        string trimmed = word.TrimStart();
                        row.Add((trimmed, colour));
                        x += Measure(trimmed);
                        continue;
                    }
                    if (row.Count > 0 && row[^1].Item2 == colour)
                    {
                        row[^1] = (row[^1].Item1 + word, colour);
                    }
                    else
                    {
                        row.Add((word, colour));
                    }
                    x += width;
                }
            }
            Break();
        }
        return rows;
    }

    // -- drawing ----------------------------------------------------------------------------------------

    private void Draw(Panel p)
    {
        int w = PanelWidth, h = Height;
        // The frame: an outline, the stone, its lit and dark edges.
        p.DrawRect(new Rect2(0, 0, w, h), Outline);
        p.DrawRect(new Rect2(1, 1, w - 2, h - 2), Stone);
        p.DrawRect(new Rect2(1, 1, w - 2, 1), Lit);
        p.DrawRect(new Rect2(1, 1, 1, h - 2), Lit);
        p.DrawRect(new Rect2(1, h - 2, w - 2, 1), Dark);
        p.DrawRect(new Rect2(w - 2, 1, 1, h - 2), Dark);

        // The header: the round and who acts (collapsed, the newest line), then its buttons.
        var buttons = Buttons();
        int headerRight = buttons.Count > 0 ? buttons[^1].Rect.Position.X - 2 : w - 4;
        string header = _size == PanelSize.Collapsed && _rows.Count > 0 ? string.Concat(_rows[^1].Pieces.Select(x => x.Text)).Trim()
            : _header.Length > 0 ? _header : "Combat log";
        DrawText(p, Fit(header, headerRight - 5), 5, 3, Pale);
        foreach (var (text, rect, _) in buttons)
        {
            Chip(p, rect, text, text == "LATEST" ? KindColours[LogKind.Other] : Pale, false);
        }
        if (_size == PanelSize.Collapsed)
        {
            return;
        }

        // The well, mottled in its four shades, and its lines.
        Rect2I well = WellRect;
        p.DrawTextureRect(WellTexture(well.Size), new Rect2(well.Position, well.Size), false);
        foreach (var (filter, rect) in FilterChips())
        {
            Chip(p, rect, filter.ToString(), filter == _filter ? KindColours[LogKind.Other] : Pale, filter == _filter);
        }
        int top = TopRow;
        for (int i = 0; i < VisibleRows && top + i < _rows.Count; i++)
        {
            Row row = _rows[top + i];
            int y = TextTop + i * LineHeight;
            if (row.Rule)
            {
                string text = row.Pieces[0].Text;
                int tw = Measure(text), cx = (TextX + TextRight) / 2;
                DrawText(p, text, cx - tw / 2, y, row.PipColour);
                p.DrawRect(new Rect2(TextX, y + 4, cx - tw / 2 - 3 - TextX, 1), Dark.Lightened(0.2f));
                p.DrawRect(new Rect2(cx + tw / 2 + 3, y + 4, TextRight - (cx + tw / 2 + 3), 1), Dark.Lightened(0.2f));
                continue;
            }
            if (row.Pip is { } filled)
            {
                var pip = new Rect2(well.Position.X + 2, y + 3, 3, 3);
                if (filled)
                {
                    p.DrawRect(pip, row.PipColour);
                }
                else
                {
                    p.DrawRect(pip, row.PipColour, false, 1);
                }
            }
            int x = row.X;
            foreach (var (text, colour) in row.Pieces)
            {
                DrawText(p, text, x, y, colour);
                x += Measure(text);
            }
        }

        // The scrollbar: its track and handle.
        Rect2I track = Track;
        p.DrawRect(new Rect2(track.Position, track.Size), WellShades[0]);
        var (hy, hh) = Handle();
        p.DrawRect(new Rect2(track.Position.X + 1, hy, track.Size.X - 2, hh), Lit);
    }

    /// <summary>The handle's top and height in the track.</summary>
    private (int Y, int H) Handle()
    {
        Rect2I track = Track;
        int total = Math.Max(_rows.Count, VisibleRows);
        int h = Math.Max(HandleMin, track.Size.Y * VisibleRows / Math.Max(1, total));
        int room = track.Size.Y - h;
        int most = Math.Max(1, total - VisibleRows);
        return (track.Position.Y + room * TopRow / most, h);
    }

    private static void DrawText(Panel p, string text, int x, int y, Color colour) =>
        p.DrawString(FontIn(colour), new Vector2(x, y + 8), text, HorizontalAlignment.Left, -1, GameFonts.Size, Colors.White);

    /// <summary><paramref name="text"/> cut to <paramref name="room"/> pixels, "..." at its end if it was.</summary>
    private static string Fit(string text, int room)
    {
        if (Measure(text) <= room)
        {
            return text;
        }
        while (text.Length > 0 && Measure(text + "...") > room)
        {
            text = text[..^1];
        }
        return text + "...";
    }

    /// <summary>A small stone button: bevelled, its word in the middle (pressed in when chosen).</summary>
    private static void Chip(Panel p, Rect2I r, string text, Color ink, bool down)
    {
        p.DrawRect(new Rect2(r.Position, r.Size), Outline);
        p.DrawRect(new Rect2(r.Position + Vector2I.One, r.Size - 2 * Vector2I.One), down ? Dark : Stone);
        p.DrawRect(new Rect2(r.Position.X + 1, r.Position.Y + 1, r.Size.X - 2, 1), down ? Outline : Lit);
        p.DrawRect(new Rect2(r.Position.X + 1, r.Position.Y + 1, 1, r.Size.Y - 2), down ? Outline : Lit);
        DrawText(p, text, r.Position.X + (r.Size.X - Measure(text)) / 2, r.Position.Y + 1, ink);
    }

    private readonly Dictionary<Vector2I, Texture2D> _wells = [];

    /// <summary>The well's mottle: its four shades, a pixel at a time, the same each time.</summary>
    private Texture2D WellTexture(Vector2I size)
    {
        if (_wells.TryGetValue(size, out Texture2D? texture))
        {
            return texture;
        }
        Image image = Image.CreateEmpty(Math.Max(1, size.X), Math.Max(1, size.Y), false, Image.Format.Rgba8);
        for (int y = 0; y < size.Y; y++)
        {
            for (int x = 0; x < size.X; x++)
            {
                uint n = (uint)(x * 374761393 + y * 668265263);
                n = (n ^ (n >> 13)) * 1274126177;
                n ^= n >> 16;
                int shade = (n & 255) < 32 ? 1 : (n & 255) < 48 ? 3 : (n & 255) < 64 ? 0 : 2;
                image.SetPixel(x, y, WellShades[shade]);
            }
        }
        return _wells[size] = ImageTexture.CreateFromImage(image);
    }

    // -- the pointer ------------------------------------------------------------------------------------

    private void Input(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel when _size != PanelSize.Collapsed:
                ScrollBy(wheel.ButtonIndex == MouseButton.WheelUp ? -WheelRows : WheelRows);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click:
                Press((Vector2I)click.Position.Floor());
                break;
            case InputEventMouseButton { Pressed: false, ButtonIndex: MouseButton.Left }:
                _dragFrom = null;
                break;
            case InputEventMouseMotion motion when _dragFrom is { } from:
                int room = Math.Max(1, Track.Size.Y - Handle().H);
                int most = Math.Max(0, _rows.Count - VisibleRows);
                ScrollTo(_dragFirst + (int)MathF.Round((motion.Position.Y - from) * most / room));
                break;
        }
        // Clicks and the wheel over the panel are its own, not the map's.
        if (e is InputEventMouseButton)
        {
            _panel.AcceptEvent();
        }
    }

    private void Press(Vector2I at)
    {
        foreach (var (_, rect, pressed) in Buttons())
        {
            if (rect.HasPoint(at))
            {
                pressed();
                return;
            }
        }
        foreach (var (filter, rect) in FilterChips())
        {
            if (rect.HasPoint(at))
            {
                _filter = filter;
                FollowNewest();
                return;
            }
        }
        Rect2I track = Track;
        if (_size != PanelSize.Collapsed && at.X >= track.Position.X && at.X < track.End.X && at.Y >= track.Position.Y && at.Y < track.End.Y)
        {
            var (hy, hh) = Handle();
            if (at.Y < hy)
            {
                ScrollBy(-VisibleRows);
            }
            else if (at.Y >= hy + hh)
            {
                ScrollBy(VisibleRows);
            }
            else
            {
                (_dragFrom, _dragFirst) = (at.Y, TopRow);
            }
        }
    }

    private void ScrollBy(int rows) => ScrollTo(TopRow + rows);

    private void ScrollTo(int row)
    {
        int last = Math.Max(0, _rows.Count - VisibleRows);
        row = Math.Clamp(row, 0, last);
        if (row >= last)
        {
            FollowNewest();
        }
        else
        {
            _first = row;
        }
        _panel.QueueRedraw();
    }

    // -- for the checks ---------------------------------------------------------------------------------

    /// <summary>The rows the well shows now, as text.</summary>
    public IReadOnlyList<string> ShownRows()
    {
        var shown = new List<string>();
        for (int i = 0; i < VisibleRows && TopRow + i < _rows.Count; i++)
        {
            shown.Add(string.Concat(_rows[TopRow + i].Pieces.Select(p => p.Text)));
        }
        return shown;
    }

    /// <summary>Press the header button showing <paramref name="text"/> ("+", "-", "L", the level); false if there is none.</summary>
    public bool PressButton(string text)
    {
        if (Buttons().FirstOrDefault(b => b.Text == text) is { Pressed: { } pressed })
        {
            pressed();
            return true;
        }
        return false;
    }

    /// <summary>The panel's control: draws it and takes the pointer over it.</summary>
    private sealed partial class Panel : Control
    {
        public CombatLogView View = null!;

        public override void _Draw() => View.Draw(this);

        public override void _GuiInput(InputEvent e) => View.Input(e);
    }
}
