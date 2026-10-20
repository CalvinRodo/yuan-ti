using DarkSun.Data.Gff;
using DarkSun.Data.Images;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// DSUN.EXE's GAME MENU on the map (Esc, or the character screens' wide
/// button): <c>WIND</c> 10500 over <c>BMP</c> 10000, its buttons named as the
/// original's status line names them (<c>DS:0C4A</c>):
/// <list type="bullet">
/// <item>the character screens: VIEW CHARACTER, VIEW INVENTORY, spells and psionics (10300, 11304–11306);</item>
/// <item>EXIT TO DOS (10301: "EXIT: SAVE GAME?" SAVE / QUIT / CANCEL; here, back to the main menu),
/// LOAD/SAVE GAME (10302), SET PREFERENCES (10303);</item>
/// <item>OVERHEAD MAP (10305: the whole region), CENTER ON LEADER (10306);</item>
/// <item>MOVE, LOOK and ATTACK CURSOR (10310–10312: what a left click on the map does), COLLAPSE PARTY (10313: everyone to the leader);</item>
/// <item>RETURN TO GAME (10308).</item>
/// </list>
/// SET PREFERENCES is <c>WIND</c> 16500 over the same frame (<c>870B</c>): music
/// and sound on or off and their volumes, GAME DIFFICULTY (EASY, BALANCED,
/// HARD, HIDEOUS), ANIMATIONS ON/OFF, ABOUT, back to the GAME MENU. The
/// questions are <c>WIND</c> 14000 (a title and three choices) over <c>BMP</c> 14000.
/// </summary>
public partial class GameMenu : RetroScreen
{
    public enum Cursor { Move, Look, Attack }

    private const int PaletteNumber = 1001, MenuWindow = 10500, PreferencesWindow = 16500, Frame = 10000, AskWindow = 14000;
    private const int ReturnButton = 10308, MenuButton = 11308;
    private const int MusicToggle = 16300, SoundToggle = 16301, AboutButton = 16302, AnimationsToggle = 16303;
    private const int MusicUp = 16304, MusicDown = 16305, SoundUp = 16306, SoundDown = 16307, HarderButton = 16308, EasierButton = 16309;

    private static readonly Color Ink = new(0.95f, 0.9f, 0.75f);

    private static readonly string[] Difficulties = ["EASY", "BALANCED", "HARD", "HIDEOUS"];

    private UiWindowView _menu = null!, _preferences = null!, _ask = null!;
    private Label _menuStatus = null!, _preferencesStatus = null!, _difficulty = null!, _askTitle = null!;
    private readonly Label[] _askChoices = new Label[3];
    private readonly Action?[] _askActions = new Action?[3];
    private ColorRect _musicBar = null!, _soundBar = null!;
    private Cursor _cursor;

    /// <summary>Open a character screen page.</summary>
    public Action<CharacterScreen.Page>? ShowCharacter { get; set; }

    /// <summary>Save the game (to a new file); false if it couldn't be.</summary>
    public Func<bool>? SaveGame { get; set; }

    /// <summary>The load screen.</summary>
    public Action? LoadGame { get; set; }

    /// <summary>Leave for the main menu.</summary>
    public Action? ExitGame { get; set; }

    public Action? OverheadMap { get; set; }

    public Action? CenterOnLeader { get; set; }

    public Action? CollapseParty { get; set; }

    /// <summary>What a left click on the map does now.</summary>
    public Action<Cursor>? CursorChanged { get; set; }

    /// <summary>The preferences changed (difficulty, animations): the map applies them.</summary>
    public Action? PreferencesChanged { get; set; }

    public bool IsOpen => Visible;

    public Cursor Mode => _cursor;

    public override void _Ready()
    {
        base._Ready();
        Visible = false;
        ((ColorRect)GetChild(0)).Color = new Color(0, 0, 0, 0.35f); // the map shows through
        GffFile resources = Session.Open("RESOURCE.GFF");
        Palette palette = Session.Assets.Palette(resources.Read("PAL ", PaletteNumber));
        _menu = Place(UiWindowView.Build(MenuWindow, palette, Frame)!);
        _preferences = Place(UiWindowView.Build(PreferencesWindow, palette, Frame)!);
        _ask = Place(UiWindowView.Build(AskWindow, palette)!);
        _menuStatus = StatusLine(_menu);
        _preferencesStatus = StatusLine(_preferences);
        BuildMenu();
        BuildPreferences();
        BuildAsk();
    }

    private UiWindowView Place(UiWindowView window)
    {
        window.Position = ((new Vector2(Width, Height) - window.Size) / 2).Floor();
        window.Visible = false;
        Canvas.AddChild(window);
        return window;
    }

    /// <summary>The frame's bar along the bottom (<c>APFM</c> 11270 marks where the text starts).</summary>
    private static Label StatusLine(UiWindowView window)
    {
        Rect2 at = window.RectOf("APFM", 11270);
        var label = Text(window, new Vector2(at.Position.X, at.Position.Y - 2), window.Size.X - 2 * at.Position.X, "");
        label.HorizontalAlignment = HorizontalAlignment.Center;
        return label;
    }

    private static Label Text(Control parent, Vector2 at, float width, string text)
    {
        var label = new Label
        {
            Position = at,
            Size = new Vector2(width, 10),
            Text = text,
            ClipText = true,
            MouseFilter = MouseFilterEnum.Ignore,
            LabelSettings = GameFonts.Settings(Ink),
        };
        parent.AddChild(label);
        return label;
    }

    /// <summary>Wire a button: what it does, and its name in the status line while the pointer is on it.</summary>
    private void On(UiWindowView window, Label status, int number, string name, Action pressed)
    {
        if (window.Button(number) is not { } b)
        {
            return;
        }
        b.TooltipText = name;
        b.MouseEntered += () => status.Text = name;
        b.MouseExited += () => status.Text = "";
        b.Pressed += pressed;
    }

    private void BuildMenu()
    {
        Label s = _menuStatus;
        On(_menu, s, ReturnButton, "RETURN TO GAME", Close);
        On(_menu, s, 10300, "VIEW CHARACTER", () => Character(CharacterScreen.Page.Character));
        On(_menu, s, 11304, "VIEW INVENTORY", () => Character(CharacterScreen.Page.Items));
        On(_menu, s, 11305, "CAST SPELL", () => Character(CharacterScreen.Page.Spells));
        On(_menu, s, 11306, "USE PSIONIC", () => Character(CharacterScreen.Page.Psionics));
        On(_menu, s, 10301, "EXIT TO DOS", () => Ask("EXIT: SAVE GAME?", ("SAVE", () =>
        {
            if (SaveGame?.Invoke() != false)
            {
                Close();
                ExitGame?.Invoke();
            }
        }), ("QUIT", () =>
        {
            Close();
            ExitGame?.Invoke();
        }), ("CANCEL", ShowMenu)));
        On(_menu, s, 10302, "LOAD/SAVE GAME", () => Ask("LOAD/SAVE GAME", ("SAVE", () =>
        {
            bool saved = SaveGame?.Invoke() != false;
            ShowMenu();
            _menuStatus.Text = saved ? "GAME SAVED" : "CAN'T SAVE NOW";
        }), ("LOAD", () =>
        {
            Close();
            LoadGame?.Invoke();
        }), ("CANCEL", ShowMenu)));
        On(_menu, s, 10303, "SET PREFERENCES", ShowPreferences);
        On(_menu, s, 10305, "OVERHEAD MAP", () => Then(OverheadMap));
        On(_menu, s, 10306, "CENTER ON LEADER", () => Then(CenterOnLeader));
        On(_menu, s, 10310, "MOVE CURSOR", () => SetCursor(Cursor.Move));
        On(_menu, s, 10311, "LOOK CURSOR", () => SetCursor(Cursor.Look));
        On(_menu, s, 10312, "ATTACK CURSOR", () => SetCursor(Cursor.Attack));
        On(_menu, s, 10313, "COLLAPSE PARTY", () => Then(CollapseParty));
        foreach (int n in new[] { 10310, 10311, 10312 })
        {
            _menu.Button(n)!.ToggleMode = true;
        }
    }

    private void BuildPreferences()
    {
        Label s = _preferencesStatus;
        // The volume bars between the arrows, and the difficulty between its arrows.
        _musicBar = Bar(_preferences.RectOf("BUTN", MusicDown), _preferences.RectOf("BUTN", MusicUp));
        _soundBar = Bar(_preferences.RectOf("BUTN", SoundDown), _preferences.RectOf("BUTN", SoundUp));
        Rect2 easier = _preferences.RectOf("BUTN", EasierButton), harder = _preferences.RectOf("BUTN", HarderButton);
        _difficulty = Text(_preferences, new Vector2(easier.End.X + 2, easier.Position.Y - 1), harder.Position.X - easier.End.X - 4, "");
        _difficulty.HorizontalAlignment = HorizontalAlignment.Center;

        On(_preferences, s, MusicToggle, "MUSIC TOGGLE ON/OFF", () => Change(() => Settings.MusicOn = !Settings.MusicOn));
        On(_preferences, s, MusicUp, "MUSIC VOLUME", () => Change(() => Settings.MusicVolume += 0.1f));
        On(_preferences, s, MusicDown, "MUSIC VOLUME", () => Change(() => Settings.MusicVolume -= 0.1f));
        On(_preferences, s, SoundToggle, "SOUND EFFECTS ON/OFF", () => Change(() => Settings.SoundOn = !Settings.SoundOn));
        On(_preferences, s, SoundUp, "SOUND EFFECT VOLUME", () => Change(() => Settings.SoundVolume += 0.1f));
        On(_preferences, s, SoundDown, "SOUND EFFECT VOLUME", () => Change(() => Settings.SoundVolume -= 0.1f));
        On(_preferences, s, HarderButton, "GAME DIFFICULTY", () => Change(() => Settings.Difficulty++));
        On(_preferences, s, EasierButton, "GAME DIFFICULTY", () => Change(() => Settings.Difficulty--));
        On(_preferences, s, AnimationsToggle, "ANIMATIONS ON/OFF", () => Change(() => Settings.Animations = !Settings.Animations));
        On(_preferences, s, AboutButton, "ABOUT", () => _preferencesStatus.Text = "SHATTERED LANDS (c) SSI 1993");
        On(_preferences, s, MenuButton, "GAME MENU", ShowMenu);
        On(_preferences, s, ReturnButton, "RETURN TO GAME", Close);
        foreach (int n in new[] { MusicToggle, SoundToggle, AnimationsToggle })
        {
            _preferences.Button(n)!.ToggleMode = true;
        }
    }

    private ColorRect Bar(Rect2 left, Rect2 right)
    {
        var track = new ColorRect
        {
            Position = new Vector2(left.End.X + 3, left.Position.Y + 2),
            Size = new Vector2(right.Position.X - left.End.X - 6, left.Size.Y - 4),
            Color = new Color(0.1f, 0.1f, 0.15f),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        var fill = new ColorRect { Size = track.Size, Color = new Color(0.8f, 0.7f, 0.5f), MouseFilter = MouseFilterEnum.Ignore };
        track.AddChild(fill);
        _preferences.AddChild(track);
        return fill;
    }

    private void BuildAsk()
    {
        _askTitle = Text(_ask, _ask.RectOf("BUTN", 14004).Position, _ask.RectOf("BUTN", 14004).Size.X, "");
        _askTitle.HorizontalAlignment = HorizontalAlignment.Center;
        for (int i = 0; i < 3; i++)
        {
            int choice = i;
            Rect2 at = _ask.RectOf("BUTN", 14001 + i);
            _askChoices[i] = Text(_ask, at.Position, at.Size.X, "");
            _askChoices[i].HorizontalAlignment = HorizontalAlignment.Center;
            _ask.Button(14001 + i)!.Pressed += () => _askActions[choice]?.Invoke();
        }
        _ask.Button(ReturnButton)!.Pressed += ShowMenu;
    }

    // -- showing --------------------------------------------------------------------------

    public void Open()
    {
        Visible = true;
        ShowMenu();
    }

    public void Close() => Visible = false;

    private void ShowMenu() => Show(_menu);

    private void ShowPreferences()
    {
        Show(_preferences);
        RefreshPreferences();
    }

    private void Show(UiWindowView window)
    {
        foreach (UiWindowView w in new[] { _menu, _preferences, _ask })
        {
            w.Visible = w == window;
        }
        _menuStatus.Text = _preferencesStatus.Text = "";
        foreach (var (n, mode) in new[] { (10310, Cursor.Move), (10311, Cursor.Look), (10312, Cursor.Attack) })
        {
            _menu.Button(n)!.SetPressedNoSignal(_cursor == mode);
        }
    }

    /// <summary>A question in WIND 14000: up to three choices.</summary>
    private void Ask(string title, params (string Text, Action Then)[] choices)
    {
        Show(_ask);
        _askTitle.Text = title;
        for (int i = 0; i < 3; i++)
        {
            bool there = i < choices.Length;
            _askChoices[i].Text = there ? choices[i].Text : "";
            _askActions[i] = there ? choices[i].Then : null;
            _ask.Button(14001 + i)!.Visible = there;
        }
    }

    private void Character(CharacterScreen.Page page)
    {
        Close();
        ShowCharacter?.Invoke(page);
    }

    private void Then(Action? action)
    {
        Close();
        action?.Invoke();
    }

    private void SetCursor(Cursor cursor)
    {
        _cursor = cursor;
        Close();
        CursorChanged?.Invoke(cursor);
    }

    /// <summary>Set the cursor without the menu (Esc on the map goes back to moving).</summary>
    public void ResetCursor()
    {
        _cursor = Cursor.Move;
        CursorChanged?.Invoke(_cursor);
    }

    private void Change(Action change)
    {
        change();
        Settings.Apply();
        Settings.Save();
        PreferencesChanged?.Invoke();
        RefreshPreferences();
    }

    private void RefreshPreferences()
    {
        _preferences.Button(MusicToggle)!.SetPressedNoSignal(Settings.MusicOn);
        _preferences.Button(SoundToggle)!.SetPressedNoSignal(Settings.SoundOn);
        _preferences.Button(AnimationsToggle)!.SetPressedNoSignal(Settings.Animations);
        _musicBar.Size = new Vector2(((Control)_musicBar.GetParent()).Size.X * Settings.MusicVolume, _musicBar.Size.Y);
        _soundBar.Size = new Vector2(((Control)_soundBar.GetParent()).Size.X * Settings.SoundVolume, _soundBar.Size.Y);
        _difficulty.Text = Difficulties[Settings.Difficulty];
    }

    public override void _Input(InputEvent e)
    {
        if (!Visible || e is not InputEventKey { Pressed: true } key || key.Unicode == '?')
        {
            return;
        }
        if (e.IsActionPressed(Controls.Back))
        {
            if (_menu.Visible)
            {
                Close();
            }
            else
            {
                ShowMenu();
            }
        }
        GetViewport().SetInputAsHandled();
    }

    // -- for the checks ---------------------------------------------------------------------

    /// <summary>Press <c>BUTN</c> <paramref name="number"/> of the window showing (14001–14003: the question's choices).</summary>
    public void Press(int number)
    {
        foreach (UiWindowView w in new[] { _menu, _preferences, _ask })
        {
            if (w.Visible && w.Button(number) is { } b)
            {
                b.EmitSignal(BaseButton.SignalName.Pressed);
                return;
            }
        }
    }

    /// <summary>Which window shows: "menu", "preferences", "ask" or "".</summary>
    public string Showing => !Visible ? "" : _menu.Visible ? "menu" : _preferences.Visible ? "preferences" : _ask.Visible ? $"ask: {_askTitle.Text}" : "";

    public string Difficulty => _difficulty.Text;
}
