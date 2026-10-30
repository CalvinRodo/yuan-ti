using DarkSun.Data.Images;
using DarkSun.Game.Magic;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// The Dark Sun icon in the map's top left corner (<c>WIND</c> 3001's
/// <c>APFM</c> 1001; <c>BMP</c> 5012 at (10, 10), <c>191F:037E</c>): frame 0
/// the grey sun, frame 1 the burning one, shown while the pointer is on it
/// (<c>1587:0059</c>'s events 2 and 4). A left click opens the character
/// screens at the page last shown (event 0x40, <c>DS:4971</c>), a right click
/// the quick-cast window (event 0x100, <see cref="QuickCastWindow"/>).
/// </summary>
public partial class DarkSunIcon : Control
{
    public const int Bmp = 5012, X = 10, Y = 10;

    private readonly TextureRect _picture = new() { MouseFilter = MouseFilterEnum.Ignore };
    private Texture2D? _grey, _burning;

    public Action? LeftClicked { get; set; }

    public Action? RightClicked { get; set; }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        AddChild(_picture);
        Palette palette = Session.Assets.Palette(Session.Open("RESOURCE.GFF").Read("PAL ", StoneUi.PaletteNumber));
        if (Session.Open("RESOURCE.GFF").TryRead("BMP ", Bmp, out var data) && Session.Assets.Frames(data) is { Count: >= 2 } frames)
        {
            (_grey, _burning) = (Textures.ToTexture(frames[0], palette), Textures.ToTexture(frames[1], palette));
            Size = _grey.GetSize();
        }
        _picture.Texture = _grey;
        MouseEntered += () => _picture.Texture = _burning;
        MouseExited += () => _picture.Texture = _grey;
        GetViewport().SizeChanged += Fit;
        Fit();
    }

    /// <summary>At (10, 10) of the 320 × 200 screen, scaled as it is and centred across.</summary>
    private void Fit()
    {
        Vector2 screen = GetViewportRect().Size;
        float scale = Math.Max(1f, MathF.Floor(Math.Min(screen.X / RetroScreen.Width, screen.Y / RetroScreen.Height)));
        Scale = new Vector2(scale, scale);
        Position = new Vector2(MathF.Floor((screen.X - RetroScreen.Width * scale) / 2), 0) + new Vector2(X, Y) * scale;
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is not InputEventMouseButton { Pressed: true } click)
        {
            return;
        }
        if (click.ButtonIndex == MouseButton.Left)
        {
            LeftClicked?.Invoke();
        }
        else if (click.ButtonIndex == MouseButton.Right)
        {
            RightClicked?.Invoke();
        }
        AcceptEvent();
    }
}

/// <summary>
/// DSUN.EXE's quick cast (<c>5F6D:08EC</c>, the manual's "Quick Cast"): a
/// right-click on the Dark Sun icon opens <c>WIND</c> 14002 at the screen's top
/// left (<c>6BB8:0152(14002, 0, 0)</c>) with the member's last spells and
/// powers (<see cref="DarkSun.Game.Characters.Character.QuickCasts"/>) in its
/// slots, <c>APFM</c> 11213–11218 (the window has no backdrop): each slot frame 2
/// of <c>BMP</c> 13007, a spell's
/// <c>ICON</c> a pixel in, and frame 6 (the orange X) over one that can't be
/// used now (<see cref="Spellcasting.CanUse"/>). A click on one chooses it to
/// cast (<c>5F6D:0043</c>); the spells icon (<c>BUTN</c> 11305) opens the Cast
/// Spells page; the return icon (10308), Esc or a click outside closes it.
/// With nothing in the list the Cast Spells page opens instead (<c>5F6D:0CB4</c>).
/// </summary>
public partial class QuickCastWindow : Control
{
    private const int Window = 14002, FirstSlot = 11213, SlotArt = 13007, ListedFrame = 2, CantFrame = 6,
        SpellsButton = 11305, ReturnButton = 10308;

    private readonly List<(Control Control, TextureRect Back, TextureRect Icon, TextureRect Mark)> _slots = [];
    private IReadOnlyList<int> _spells = [];

    /// <summary>A spell or power chosen to cast.</summary>
    public Action<int>? Choose { get; set; }

    /// <summary>The spells icon: the Cast Spells page.</summary>
    public Action? OpenSpells { get; set; }

    public override void _Ready()
    {
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
    }

    /// <summary>The spells and powers shown, for the checks.</summary>
    public IReadOnlyList<int> Shown => Visible ? _spells : [];

    /// <summary>Show <paramref name="spells"/> (at most six), those that <paramref name="usable"/> refuses crossed out.</summary>
    public void Open(IReadOnlyList<int> spells, Func<int, bool> usable)
    {
        foreach (Node child in GetChildren())
        {
            RemoveChild(child);
            child.QueueFree();
        }
        _slots.Clear();
        _spells = [.. spells.Take(DarkSun.Game.Characters.Character.QuickCastSlots)];
        GffArt art = new(StoneUi.PaletteNumber);
        if (UiWindowView.Build(Window, art.Palette) is not { } window)
        {
            return;
        }
        AddChild(window);
        for (int n = 0; n < DarkSun.Game.Characters.Character.QuickCastSlots; n++)
        {
            if (window.Slot(FirstSlot + n) is not { } control)
            {
                continue;
            }
            TextureRect Layer(Vector2 at) => new() { Position = at, MouseFilter = MouseFilterEnum.Ignore };
            var slot = (control, Layer(Vector2.Zero), Layer(Vector2.One), Layer(Vector2.Zero));
            control.AddChild(slot.Item2);
            control.AddChild(slot.Item3);
            control.AddChild(slot.Item4);
            control.MouseFilter = MouseFilterEnum.Stop;
            int index = n;
            control.GuiInput += e => SlotInput(index, e);
            _slots.Add(slot);
            slot.Item2.Texture = art.Frame("BMP ", SlotArt, ListedFrame); // every slot, filled or not
            if (n < _spells.Count)
            {
                int number = _spells[n];
                slot.Item3.Texture = Spellcasting.Get(number) is { } spell ? art.Frame("ICON", spell.Icon, 0) : null;
                slot.Item4.Texture = usable(number) ? null : art.Frame("BMP ", SlotArt, CantFrame);
                control.TooltipText = Session.SpellName(number) ?? "";
            }
        }
        if (window.Button(SpellsButton) is { } spellsIcon)
        {
            spellsIcon.Pressed += () =>
            {
                Close();
                OpenSpells?.Invoke();
            };
        }
        if (window.Button(ReturnButton) is { } back)
        {
            back.Pressed += Close;
        }
        Size = window.Size;
        Vector2 screen = GetViewportRect().Size;
        float scale = Math.Max(1f, MathF.Floor(Math.Min(screen.X / RetroScreen.Width, screen.Y / RetroScreen.Height)));
        Scale = new Vector2(scale, scale);
        Position = new Vector2(MathF.Floor((screen.X - RetroScreen.Width * scale) / 2), 0);
        Visible = true;
    }

    public void Close() => Visible = false;

    /// <summary>Click slot <paramref name="slot"/> (0–5) as the mouse would (for the checks).</summary>
    public void Click(int slot) => SlotInput(slot, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });

    /// <summary>Is slot <paramref name="slot"/> crossed out (for the checks)?</summary>
    public bool CrossedOut(int slot) => slot < _slots.Count && _slots[slot].Mark.Texture is not null;

    private void SlotInput(int slot, InputEvent e)
    {
        if (e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } || slot >= _spells.Count)
        {
            return;
        }
        int number = _spells[slot];
        Close();
        Choose?.Invoke(number);
        GetViewport()?.SetInputAsHandled();
    }

    public override void _Input(InputEvent e)
    {
        if (!Visible)
        {
            return;
        }
        if (e is InputEventMouseButton { Pressed: true } click && !new Rect2(GlobalPosition, Size * Scale).HasPoint(click.Position))
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

    /// <summary>Frames of <c>RESOURCE.GFF</c>'s pictures in one palette, each decoded once.</summary>
    private sealed class GffArt(int paletteNumber)
    {
        private readonly Dictionary<(string, int, int), Texture2D?> _made = [];

        public Palette Palette { get; } = Session.Assets.Palette(Session.Open("RESOURCE.GFF").Read("PAL ", paletteNumber));

        public Texture2D? Frame(string tag, int number, int frame)
        {
            if (!_made.TryGetValue((tag, number, frame), out Texture2D? t))
            {
                _made[(tag, number, frame)] = t = Session.Open("RESOURCE.GFF").TryRead(tag, number, out var data)
                    && Session.Assets.Frames(data) is var frames && frame < frames.Count
                    ? Textures.ToTexture(frames[frame], Palette) : null;
            }
            return t;
        }
    }
}
