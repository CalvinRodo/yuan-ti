using DarkSun.Game.Scripting;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// A script's <see cref="GplDialogue"/>, in DSUN.EXE's text windows, 318 × 58
/// at the bottom of the 320 × 200 screen over the stone panel <c>BMP</c> 3007,
/// scaled as the screen is:
/// <list type="bullet">
/// <item>The text window (<c>WIND</c> 3007): the portrait (<c>PORT</c>, SHOW_PIC)
/// in its frame (<c>ICON</c> 2090; the sun emblem when there is none), the
/// text box (<c>EBOX</c> 4002, 236 × 46 at (58, 6); text in colour 199,
/// its +0x80, on 229, the transparent key at +0x7E) and, on the
/// right, the up arrow (<c>ICON</c> 2093) and MORE (<c>ICON</c> 12100). Text
/// that doesn't fit is paged: MORE shows the next page, the arrow the one
/// before. Once all is read MORE (or a click on the text) answers CONTINUE,
/// opens the choices, or puts the window away.</item>
/// <item>The choice window (<c>WIND</c> 3008): the menu's title and its
/// options on five stone bars (<c>ICON</c> 12102–12106), with the arrow and
/// MORE scrolling them. ASK_YES_NO is offered here as Yes and No under
/// "Answer Yes or No", as DSUN.EXE words it.</item>
/// </list>
/// Keys: Space or Enter for MORE, 1–9 for a choice, Y and N for Yes and No,
/// the mouse wheel to scroll. The game's font (<c>FONT</c> 2081) isn't
/// decoded: text is drawn in Godot's. DSUN.EXE draws PRINT_STRING's style
/// byte the same whatever it is (<c>84C9:0253</c> ignores it for window 2);
/// here narration (98, by the scripts' use) is set in italics.
/// </summary>
public partial class DialogueWindow : Control
{
    public const int Width = 320, Height = 58;

    private const int NarrationStyle = 98;
    private const int FontSize = 7;
    private const int OptionsShown = 5;

    /// <summary>The text box's colour (<c>EBOX</c> 4002 +0x80).</summary>
    private const int TextColour = 199;

    private Control _panel = null!;
    private TextureRect _backdrop = null!;
    private Control _textMode = null!, _menuMode = null!;
    private TextureRect _frame = null!, _portrait = null!;
    private RichTextLabel _text = null!;
    private TextureButton _up = null!, _more = null!;
    private Label _title = null!;
    private readonly List<(TextureRect Bar, Label Text, Button Hit)> _options = [];

    private GplDialogue? _dialogue;
    private int _shownVersion = -1;
    private Func<int, ImageTexture?>? _portraits;
    private Func<string, int, int, Texture2D?>? _art;
    private Func<int, Color>? _colour;

    private int _top;
    private int _lines;
    private bool _menuShown;
    private int _menuTop;
    private List<(string Text, int Answer)> _choices = [];
    private int _textCount;
    private int _clears;

    /// <summary>Resume the paused script with the player's answer.</summary>
    public Action<int>? Answer { get; set; }

    public override void _Ready()
    {
        Visible = false;
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _panel = new Control { Size = new Vector2(Width, Height), MouseFilter = MouseFilterEnum.Stop, ClipContents = true };
        AddChild(_panel);
        _backdrop = new TextureRect { MouseFilter = MouseFilterEnum.Ignore };
        _panel.AddChild(_backdrop);

        _textMode = Layer();
        _frame = Picture(_textMode, new Vector2(6, 4));
        _portrait = Picture(_textMode, new Vector2(14, 12));
        _text = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollActive = false,
            Position = new Vector2(58, 6),
            Size = new Vector2(236, 46),
            MouseFilter = MouseFilterEnum.Pass,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        foreach (string font in new[] { "normal_font_size", "italics_font_size", "bold_font_size" })
        {
            _text.AddThemeFontSizeOverride(font, FontSize);
        }
        _text.AddThemeConstantOverride("line_separation", -2);
        _text.GuiInput += TextInput;
        _textMode.AddChild(_text);

        _menuMode = Layer();
        _title = new Label { Position = new Vector2(6, 1), Size = new Vector2(296, 11), ClipText = true, MouseFilter = MouseFilterEnum.Ignore };
        _title.AddThemeFontSizeOverride("font_size", FontSize);
        _menuMode.AddChild(_title);
        for (int i = 0; i < OptionsShown; i++)
        {
            int slot = i;
            var bar = new TextureRect { Position = new Vector2(3, 13 + 8 * i), Size = new Vector2(300, 10), MouseFilter = MouseFilterEnum.Ignore };
            _menuMode.AddChild(bar);
            var text = new Label { Position = new Vector2(8, 12 + 8 * i), Size = new Vector2(292, 10), ClipText = true, MouseFilter = MouseFilterEnum.Ignore };
            text.AddThemeFontSizeOverride("font_size", FontSize);
            _menuMode.AddChild(text);
            var hit = new Button { Flat = true, Position = new Vector2(3, 13 + 8 * i), Size = new Vector2(300, 8), FocusMode = FocusModeEnum.None };
            hit.AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
            hit.AddThemeStyleboxOverride("hover", new StyleBoxFlat { BgColor = new Color(1, 1, 1, 0.12f) });
            hit.AddThemeStyleboxOverride("pressed", new StyleBoxFlat { BgColor = new Color(1, 1, 1, 0.25f) });
            hit.Pressed += () => Choose(_menuTop + slot);
            _menuMode.AddChild(hit);
            _options.Add((bar, text, hit));
        }

        _up = new TextureButton { Position = new Vector2(305, 4), Size = new Vector2(14, 10), FocusMode = FocusModeEnum.None };
        _up.Pressed += Back;
        _panel.AddChild(_up);
        _more = new TextureButton { Position = new Vector2(305, 18), Size = new Vector2(14, 35), FocusMode = FocusModeEnum.None };
        _more.Pressed += More;
        _panel.AddChild(_more);
        Resized += Fit;
        Fit();
    }

    private Control Layer()
    {
        var layer = new Control { Size = new Vector2(Width, Height), MouseFilter = MouseFilterEnum.Ignore };
        _panel.AddChild(layer);
        return layer;
    }

    private static TextureRect Picture(Control parent, Vector2 at)
    {
        var picture = new TextureRect { Position = at, MouseFilter = MouseFilterEnum.Ignore };
        parent.AddChild(picture);
        return picture;
    }

    /// <summary>The 320 × 200 screen's scale, the window at its bottom, centred.</summary>
    private void Fit()
    {
        float scale = Math.Max(1f, MathF.Floor(Math.Min(Size.X / RetroScreen.Width, Size.Y / RetroScreen.Height)));
        _panel.Scale = new Vector2(scale, scale);
        _panel.Position = new Vector2(MathF.Floor((Size.X - Width * scale) / 2), Size.Y - Height * scale);
    }

    /// <param name="portraits">The texture of a <c>PORT</c> number, or null.</param>
    /// <param name="art">A frame of a <c>RESOURCE.GFF</c> image (<c>BMP</c>, <c>ICON</c>) in the screen's palette, or null.</param>
    /// <param name="colour">A colour of the screen's palette.</param>
    public void Attach(GplDialogue dialogue, Func<int, ImageTexture?> portraits, Func<string, int, int, Texture2D?> art, Func<int, Color> colour)
    {
        _dialogue = dialogue;
        _portraits = portraits;
        _art = art;
        _colour = colour;
        _shownVersion = -1;
        LoadArt();
        Refresh();
    }

    private void LoadArt()
    {
        if (_art is null)
        {
            return;
        }
        // The stone panel is the top 58 rows of BMP 3007.
        _backdrop.Texture = _art("BMP ", 3007, 0) is { } stone
            ? new AtlasTexture { Atlas = stone, Region = new Rect2(0, 0, Width, Height) }
            : null;
        _frame.Texture = _art("ICON", 2090, 0);
        Skin(_up, 2093);
        Skin(_more, 12100);
        for (int i = 0; i < OptionsShown; i++)
        {
            _options[i].Bar.Texture = _art("ICON", 12102 + i, 0);
        }
        Color text = _colour?.Invoke(TextColour) ?? Colors.White;
        _title.AddThemeColorOverride("font_color", text);
        foreach (var o in _options)
        {
            o.Text.AddThemeColorOverride("font_color", text);
        }
    }

    /// <summary>A button's looks from its icon's frames: 0 normal, 1 highlighted, 2 disabled, 3 pressed.</summary>
    private void Skin(TextureButton button, int icon)
    {
        button.TextureNormal = _art!("ICON", icon, 0);
        button.TextureHover = _art("ICON", icon, 1);
        button.TextureDisabled = _art("ICON", icon, 2);
        button.TexturePressed = _art("ICON", icon, 3);
    }

    /// <summary>Redraw if the dialogue changed; hide when there is nothing to show.</summary>
    public void Refresh()
    {
        if (_dialogue is null || _dialogue.Version == _shownVersion)
        {
            return;
        }
        _shownVersion = _dialogue.Version;
        // A shop has a window of its own (ShopWindow).
        bool show = !_dialogue.IsEmpty && _dialogue.Prompt is not GplShopPrompt;
        if (!show)
        {
            Visible = false;
            (_top, _lines, _menuShown, _menuTop, _textCount) = (0, 0, false, 0, 0);
            _text.Text = "";
            return;
        }
        // Cleared since last shown (CLEAR_PIC, then new text in the same frame): start again.
        bool cleared = _dialogue.Clears != _clears;
        _clears = _dialogue.Clears;
        if (cleared)
        {
            (_top, _lines) = (0, 0);
        }
        bool wasRead = !Visible || cleared || _top + Page >= _lines;
        int before = Visible && !cleared ? _lines : 0;
        Visible = true;

        Texture2D? face = _dialogue.Portrait is int n ? _portraits?.Invoke(n) : null;
        _portrait.Texture = face ?? _art?.Invoke("ICON", 2090, 1);
        _text.Text = string.Concat(_dialogue.Text.Select(Format));

        _choices = _dialogue.Prompt switch
        {
            GplYesNoPrompt => [("Yes", 1), ("No", 0)],
            GplMenuPrompt menu => [.. menu.Options.Select((o, i) => (o.Text.Trim(), i, o.Enabled)).Where(o => o.Enabled).Select(o => (o.Item1, o.i))],
            _ => [],
        };
        _title.Text = _dialogue.Prompt switch
        {
            GplYesNoPrompt => "Answer Yes or No",
            GplMenuPrompt menu => menu.Title.Trim(),
            _ => "",
        };
        if (_choices.Count == 0 || cleared || _dialogue.Text.Count != _textCount)
        {
            _menuShown = false; // new text is read before the choices
        }
        _textCount = _dialogue.Text.Count;
        _menuTop = 0;
        CallDeferred(MethodName.Paginate, before, wasRead);
    }

    /// <summary>Once the text is laid out: start at the new text if the old was all read, then show.</summary>
    private void Paginate(int before, bool wasRead)
    {
        _lines = _text.GetLineCount();
        if (wasRead && _lines - _top > Page)
        {
            _top = Math.Clamp(before, 0, Math.Max(0, _lines - 1));
        }
        _top = Math.Clamp(_top, 0, Math.Max(0, _lines - 1));
        // Nothing left to read before a choice: go straight to it.
        if (_choices.Count > 0 && _top + Page >= _lines && _dialogue?.Text.Count == 0)
        {
            _menuShown = true;
        }
        ShowPage();
    }

    /// <summary>Lines the text box holds.</summary>
    private int Page
    {
        get
        {
            int lines = Math.Max(1, _text.GetLineCount());
            float height = _text.GetContentHeight() / lines;
            return height <= 0 ? 4 : Math.Max(1, (int)(46 / height));
        }
    }

    private bool AllRead => _top + Page >= _lines;

    private void ShowPage()
    {
        _textMode.Visible = !_menuShown;
        _menuMode.Visible = _menuShown;
        if (_menuShown)
        {
            for (int i = 0; i < OptionsShown; i++)
            {
                int at = _menuTop + i;
                bool has = at < _choices.Count;
                _options[i].Bar.Visible = has;
                _options[i].Text.Text = has ? $"{at + 1}. {_choices[at].Text}" : "";
                _options[i].Hit.Visible = has;
            }
            _up.Disabled = false; // back to the text, or the options before
            _more.Disabled = _menuTop + OptionsShown >= _choices.Count;
        }
        else
        {
            _text.ScrollToLine(_top);
            _up.Disabled = _top == 0;
            // MORE goes on: to the next page, the choices, the script, or away.
            _more.Disabled = false;
        }
    }

    // -- for the headless check ---------------------------------------------------------

    /// <summary>The choices are on show (else the text).</summary>
    public bool ShowingChoices => Visible && _menuShown;

    public IReadOnlyList<string> Choices => [.. _choices.Select(c => c.Text)];

    /// <summary>The first line shown, the lines in all, and how many fit.</summary>
    public (int Top, int Lines, int Page) Paging => (_top, _lines, Page);

    /// <summary>MORE, as the player presses it.</summary>
    public void PressMore() => More();

    public void PressUp() => Back();

    /// <summary>The <paramref name="n"/>th choice offered (0-based).</summary>
    public void Pick(int n) => Choose(n);

    // -- the player ----------------------------------------------------------------

    private void More()
    {
        if (_dialogue is null)
        {
            return;
        }
        if (_menuShown)
        {
            if (_menuTop + OptionsShown < _choices.Count)
            {
                _menuTop += OptionsShown;
            }
            ShowPage();
            return;
        }
        if (!AllRead)
        {
            _top = Math.Min(_top + Page, Math.Max(0, _lines - 1));
            ShowPage();
        }
        else if (_choices.Count > 0)
        {
            _menuShown = true;
            ShowPage();
        }
        else if (_dialogue.Prompt is GplContinuePrompt)
        {
            Answer?.Invoke(0);
        }
        else if (_dialogue.Prompt is null)
        {
            _dialogue.Clear(); // read: put the window away
        }
    }

    private void Back()
    {
        if (_menuShown)
        {
            if (_menuTop > 0)
            {
                _menuTop = Math.Max(0, _menuTop - OptionsShown);
            }
            else if (_lines > 0)
            {
                _menuShown = false; // back to the text
            }
        }
        else
        {
            _top = Math.Max(0, _top - Page);
        }
        ShowPage();
    }

    private void Choose(int index)
    {
        if (index >= 0 && index < _choices.Count)
        {
            Answer?.Invoke(_choices[index].Answer);
        }
    }

    private void TextInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown }:
                _top = Math.Min(_top + 1, Math.Max(0, _lines - Page));
                ShowPage();
                _text.AcceptEvent();
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp }:
                _top = Math.Max(0, _top - 1);
                ShowPage();
                _text.AcceptEvent();
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }:
                // The whole text area is a button (BUTN 12300): it goes on as MORE does.
                More();
                _text.AcceptEvent();
                break;
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!Visible || e is not InputEventKey { Pressed: true, Echo: false } key)
        {
            return;
        }
        if (key.Keycode is Key.Space or Key.Enter or Key.KpEnter)
        {
            More();
        }
        else if (_menuShown && key.Keycode is >= Key.Key1 and <= Key.Key9)
        {
            Choose((int)(key.Keycode - Key.Key1));
        }
        else if (_dialogue?.Prompt is GplYesNoPrompt && key.Keycode is Key.Y or Key.N)
        {
            Answer?.Invoke(key.Keycode == Key.Y ? 1 : 0);
        }
        else if (e.IsActionPressed(Controls.Back) && _dialogue?.Prompt is null)
        {
            _dialogue?.Clear();
        }
        else
        {
            return;
        }
        GetViewport().SetInputAsHandled();
    }

    private string Format(GplText t)
    {
        string escaped = t.Text.Replace("[", "[lb]");
        string colour = (_colour?.Invoke(TextColour) ?? Colors.White).ToHtml(false);
        return t.Style switch
        {
            NarrationStyle => $"[i][color=#{colour}]{escaped}[/color][/i]",
            _ => $"[color=#{colour}]{escaped}[/color]",
        };
    }

    public override void _Process(double delta) => Refresh();
}
