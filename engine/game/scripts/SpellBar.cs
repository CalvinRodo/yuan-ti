using DarkSun.Data.Images;
using DarkSun.Game.World;
using Godot;
using static DarkSun.Engine.StoneDraw;

namespace DarkSun.Engine;

/// <summary>A spell, power or item on the <see cref="SpellBar"/>: its icon, name, a note (casts left, points, charges) and whether it can be used now.</summary>
public sealed record SpellBarEntry(int Spell, Texture2D? Icon, string Name, string Note, bool Usable, Item? Item = null, WorldObject? Holder = null);

/// <summary>A row of the <see cref="SpellBar"/>: its group's heading (on the group's first row), its level (or none) and what it holds.</summary>
public sealed record SpellBarRow(string Heading, string Level, IReadOnlyList<SpellBarEntry> Entries);

/// <summary>
/// The quick cast and CAST's choice of spell (issue #159), in the game's stone
/// (<see cref="StoneDraw"/>) and font at the screen's top left: the member's
/// name, then RECENT (the quick-cast list, <see cref="DarkSun.Game.Characters.Character.QuickCasts"/>,
/// ordered by kind and level), then every spell the member knows by its kind
/// (wizard, priest) and level, its psionic powers, and the items it carries that
/// cast a spell. Each is its icon (<c>ICON</c>) on the original's slot (<c>BMP</c>
/// 13007 frame 2), the orange X (frame 6) over one that can't be used now. The line
/// at the foot names the one under the pointer and what it has left. A click on one
/// chooses it; SPELLS opens the Cast Spells page; X, Esc, a right-click or a click
/// outside closes it.
/// </summary>
public partial class SpellBar : RetroScreen
{
    private const int Left = 4, Top = 4, HeaderHeight = 15, InfoHeight = 12, MostPerRow = 16, SlotArt = 13007, SlotFrame = 2, CantFrame = 6;

    /// <summary>The slot's size in its pixels (an icon is 16 inside it).</summary>
    private const int SlotPixels = 18;

    private readonly Panel _panel = new();
    private readonly List<SpellBarRow> _rows = [];
    private string _title = "";
    private int _perRow = 8, _labelWidth, _levelWidth;
    private (int Row, int Index)? _hover;

    /// <summary>
    /// A slot's pixel in the screen's (smaller than the rest of the screen: a whole number of
    /// the window's pixels one fewer than the screen's scale, so the icons stay sharp).
    /// </summary>
    private float _texel = 1;

    private float Cell => SlotPixels * _texel;

    private float RowHeight => Math.Max(Cell, GameFonts.Size) + 1;
    private string? _said;
    private Texture2D? _slot, _cross;

    /// <summary>An entry chosen (the bar has closed).</summary>
    public Action<SpellBarEntry>? Choose { get; set; }

    /// <summary>SPELLS: the Cast Spells page.</summary>
    public Action? OpenSpells { get; set; }

    public SpellBar()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        Canvas.MouseFilter = MouseFilterEnum.Ignore;
        ((ColorRect)GetChild(0)).Visible = false;
        _panel.View = this;
        _panel.MouseFilter = MouseFilterEnum.Stop;
        Canvas.AddChild(_panel);
        Visible = false;
    }

    public override void _Ready()
    {
        base._Ready();
        Palette palette = Session.Assets.Palette(Session.Open("RESOURCE.GFF").Read("PAL ", StoneUi.PaletteNumber));
        if (Session.Open("RESOURCE.GFF").TryRead("BMP ", SlotArt, out var data) && Session.Assets.Frames(data) is { Count: > CantFrame } frames)
        {
            (_slot, _cross) = (Textures.ToTexture(frames[SlotFrame], palette), Textures.ToTexture(frames[CantFrame], palette));
        }
    }

    /// <summary>Show <paramref name="rows"/> under <paramref name="title"/> (rows without entries are left out).</summary>
    public void Open(string title, IEnumerable<SpellBarRow> rows)
    {
        _title = title;
        float scale = Canvas.Scale.X;
        _texel = scale >= 2 ? (scale - 1) / scale : 1;
        GetParent()?.MoveChild(this, -1); // over the combat bar and log
        _rows.Clear();
        _rows.AddRange(rows.Where(r => r.Entries.Count > 0));
        (_hover, _said) = (null, null);
        _labelWidth = _rows.Select(r => Measure(r.Heading)).DefaultIfEmpty(0).Max() + 4;
        _levelWidth = _rows.Any(r => r.Level.Length > 0) ? _rows.Select(r => Measure(r.Level)).Max() + 4 : 0;
        // Up to twelve to a row, more if the screen's height needs it.
        int most = _rows.Select(r => r.Entries.Count).DefaultIfEmpty(1).Max();
        _perRow = Math.Min(12, most);
        while (PanelHeight > RetroScreen.Height - 2 * Top && _perRow < Math.Min(MostPerRow, most))
        {
            _perRow++;
        }
        _panel.Position = new Vector2(Left, Top);
        _panel.Size = new Vector2(PanelWidth, PanelHeight);
        _panel.QueueRedraw();
        Visible = true;
    }

    public void Close() => Visible = false;

    // -- the layout -------------------------------------------------------------------------------------

    /// <summary>The rows as drawn: each of <see cref="_rows"/> broken into lines of at most <see cref="_perRow"/>.</summary>
    private IEnumerable<(int Row, int First, int Line)> Lines()
    {
        int line = 0;
        for (int r = 0; r < _rows.Count; r++)
        {
            for (int first = 0; first < _rows[r].Entries.Count; first += _perRow)
            {
                yield return (r, first, line++);
            }
        }
    }

    private int CellsLeft => 6 + _labelWidth + _levelWidth;

    private int PanelWidth => Math.Max(CellsLeft + (int)MathF.Ceiling(_perRow * Cell) + 6, Measure(_title) + ButtonsWidth + 14);

    private int PanelHeight => HeaderHeight + (int)MathF.Ceiling(Math.Max(1, Lines().Count()) * RowHeight) + 3 + InfoHeight + 3;

    private Rect2I WellRect => new(4, HeaderHeight, PanelWidth - 8, PanelHeight - HeaderHeight - 4);

    private Rect2 CellRect(int line, int column) => new(CellsLeft + column * Cell, HeaderHeight + 2 + line * RowHeight, Cell, Cell);

    private int ButtonsWidth => Measure("SPELLS") + 6 + 1 + 9;

    /// <summary>The header's buttons: SPELLS and X.</summary>
    private List<(string Text, Rect2I Rect, Action Pressed)> Buttons()
    {
        int w = PanelWidth;
        int spells = Measure("SPELLS") + 6;
        return
        [
            ("SPELLS", new Rect2I(w - 3 - 9 - 1 - spells, 2, spells, 11), () =>
            {
                Close();
                OpenSpells?.Invoke();
            }),
            ("X", new Rect2I(w - 3 - 9, 2, 9, 11), Close),
        ];
    }

    private (int Row, int Index)? EntryAt(Vector2 at)
    {
        foreach (var (row, first, line) in Lines())
        {
            for (int i = first; i < Math.Min(first + _perRow, _rows[row].Entries.Count); i++)
            {
                if (CellRect(line, i - first).HasPoint(at + new Vector2(0.5f, 0.5f)))
                {
                    return (row, i);
                }
            }
        }
        return null;
    }

    // -- drawing ----------------------------------------------------------------------------------------

    private void Draw(Panel p)
    {
        int w = PanelWidth, h = PanelHeight;
        Frame(p, new Rect2I(0, 0, w, h));
        DrawText(p, Fit(_title, Buttons()[0].Rect.Position.X - 7), 5, 3, Yellow);
        foreach (var (text, rect, _) in Buttons())
        {
            Chip(p, rect, text, Pale, false);
        }
        DrawWell(p, WellRect);
        foreach (var (row, first, line) in Lines())
        {
            SpellBarRow r = _rows[row];
            int y = (int)(HeaderHeight + 2 + line * RowHeight + (Cell - GameFonts.Size) / 2);
            if (first == 0)
            {
                if (row == 0 || _rows[row - 1].Heading != r.Heading)
                {
                    DrawText(p, r.Heading, 7, y, Pale);
                }
                DrawText(p, r.Level, 6 + _labelWidth, y, Dim);
            }
            for (int i = first; i < Math.Min(first + _perRow, r.Entries.Count); i++)
            {
                SpellBarEntry e = r.Entries[i];
                Rect2 cell = CellRect(line, i - first);
                if (_slot is { } slot)
                {
                    p.DrawTextureRect(slot, cell, false);
                }
                else
                {
                    p.DrawRect(cell, Outline);
                }
                if (e.Icon is { } icon)
                {
                    // An item's picture may be bigger than a spell's: shrunk into the slot (whole pixels), centred.
                    Vector2 size = icon.GetSize();
                    float fit = Math.Min(1f, 16f / Math.Max(size.X, size.Y));
                    Vector2 drawn = (size * fit).Floor();
                    Vector2 inset = ((new Vector2(SlotPixels, SlotPixels) - drawn) / 2).Floor();
                    p.DrawTextureRect(icon, new Rect2(cell.Position + inset * _texel, drawn * _texel), false,
                        e.Usable ? Colors.White : new Color(0.55f, 0.55f, 0.6f));
                }
                if (!e.Usable && _cross is { } cross)
                {
                    p.DrawTextureRect(cross, cell, false);
                }
                if (_hover == (row, i))
                {
                    p.DrawRect(cell, Yellow, false, _texel);
                }
            }
        }
        // The foot: what is under the pointer, or what was said of it.
        int footY = h - 3 - InfoHeight;
        p.DrawRect(new Rect2(6, footY - 1, w - 12, 1), Dark.Lightened(0.2f));
        string info = _said ?? (_hover is { } at ? Describe(_rows[at.Row].Entries[at.Index]) : "Choose a spell to cast");
        DrawText(p, Fit(info, w - 14), 7, footY + 1, _said is not null ? Yellow : _hover is null ? Dim : Pale);
    }

    private static string Describe(SpellBarEntry e) => e.Note.Length > 0 ? $"{e.Name}: {e.Note}" : e.Name;

    // -- the pointer ------------------------------------------------------------------------------------

    private void Input(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseMotion motion:
                var hover = EntryAt((Vector2I)motion.Position.Floor());
                if (hover != _hover)
                {
                    (_hover, _said) = (hover, null);
                    _panel.QueueRedraw();
                }
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click:
                Press((Vector2I)click.Position.Floor());
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }:
                Close();
                break;
        }
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
        if (EntryAt(at) is { } hit)
        {
            Pick(_rows[hit.Row].Entries[hit.Index]);
        }
    }

    private void Pick(SpellBarEntry e)
    {
        if (!e.Usable)
        {
            _said = $"{e.Name}: {(e.Note.Length > 0 ? e.Note : "can't be used now")}";
            _panel.QueueRedraw();
            return;
        }
        Close();
        Choose?.Invoke(e);
    }

    public override void _Input(InputEvent e)
    {
        if (!Visible)
        {
            return;
        }
        if (e is InputEventMouseButton { Pressed: true } click
            && !new Rect2(_panel.GlobalPosition, _panel.Size * Canvas.Scale).HasPoint(click.Position))
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
        else if (e.IsActionPressed(Controls.Back))
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }

    // -- for the checks ---------------------------------------------------------------------------------

    /// <summary>The row headed <paramref name="heading"/> (its first if it has several levels), or none.</summary>
    public IReadOnlyList<SpellBarEntry> Row(string heading, string level = "") =>
        Visible ? _rows.FirstOrDefault(r => r.Heading == heading && (level.Length == 0 || r.Level == level))?.Entries ?? [] : [];

    /// <summary>The spells and powers of the RECENT row.</summary>
    public IReadOnlyList<int> Shown => [.. Row(Recent).Select(e => e.Spell)];

    /// <summary>Every row as shown: heading, level, entries.</summary>
    public IReadOnlyList<SpellBarRow> Rows => Visible ? _rows : [];

    /// <summary>Click entry <paramref name="index"/> of the RECENT row as the mouse would.</summary>
    public void Click(int index) => Click(Recent, index);

    /// <summary>Click entry <paramref name="index"/> of the row headed <paramref name="heading"/> as the mouse would.</summary>
    public void Click(string heading, int index)
    {
        if (Row(heading) is { } row && index >= 0 && index < row.Count)
        {
            Pick(row[index]);
        }
    }

    /// <summary>Is the RECENT row's entry <paramref name="index"/> crossed out?</summary>
    public bool CrossedOut(int index) => Row(Recent) is { } row && index < row.Count && !row[index].Usable;

    /// <summary>What the foot says after a click on one that can't be used.</summary>
    public string? Said => _said;

    public const string Recent = "RECENT";

    /// <summary>The panel's control: draws it and takes the pointer over it.</summary>
    private sealed partial class Panel : Control
    {
        public SpellBar View = null!;

        public override void _Draw() => View.Draw(this);

        public override void _GuiInput(InputEvent e) => View.Input(e);
    }
}
