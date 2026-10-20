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
/// the mouse wheel to scroll. Text is in the game's font (<see cref="GameFonts"/>),
/// all alike: DSUN.EXE ignores PRINT_STRING's style byte for window 2
/// (<c>84C9:0253</c>).
/// </summary>
public partial class DialogueWindow : Control
{
    public const int Width = 320, Height = 58;

    private const int FontSize = 7;
    private const int OptionsShown = 5;

    /// <summary>The text box's colour (<c>EBOX</c> 4002 +0x80).</summary>
    private const int TextColour = 199;

    // The windows' controls.
    private const int TextWindow = 3007, ChoiceWindow = 3008, ClickArea = 12300, PortraitBox = 2090, TextBox = 4002,
        TextUp = 2093, TextMore = 2094, FirstOption = 2076, ChoiceUp = 2095, ChoiceMore = 2096;

    private Control _panel = null!;
    private UiWindowView? _textMode, _menuMode;
    private TextureRect _portrait = null!;
    private RichTextLabel _text = null!;
    private TextureButton? _textUp, _textMore, _menuUp, _menuMore;
    private Label _title = null!;
    private readonly List<(TextureButton Bar, Label Text)> _options = [];
    private Texture2D? _noPortrait;

    private GplDialogue? _dialogue;
    private int _shownVersion = -1;
    private Func<int, ImageTexture?>? _portraits;

    private int _top;
    private int _lines;
    private bool _menuShown;
    private int _menuTop;
    private List<(string Text, int Answer)> _choices = [];
    private int _textCount;
    private int _clears;

    /// <summary>Resume the paused script with the player's answer.</summary>
    public Action<int>? Answer { get; set; }

    /// <summary>Resume a script waiting at INPUT_STRING with what the player typed.</summary>
    public Action<string>? AnswerText { get; set; }

    /// <summary>Where the player types for INPUT_STRING, over the text window's bottom line.</summary>
    private LineEdit _input = null!;

    public override void _Ready()
    {
        Visible = false;
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _panel = new Control { Size = new Vector2(Width, Height), MouseFilter = MouseFilterEnum.Stop, ClipContents = true };
        AddChild(_panel);
        _text = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollActive = false,
            MouseFilter = MouseFilterEnum.Pass,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _text.AddThemeConstantOverride("line_separation", 0);
        _text.GuiInput += TextInput;
        _portrait = new TextureRect { Size = new Vector2(32, 32), MouseFilter = MouseFilterEnum.Ignore };
        _title = new Label { Position = new Vector2(6, 1), Size = new Vector2(296, 11), ClipText = true, MouseFilter = MouseFilterEnum.Ignore };
        _input = new LineEdit { Visible = false, Size = new Vector2(200, 12), PlaceholderText = "Type, then Enter" };
        _input.AddThemeFontSizeOverride("font_size", FontSize);
        _input.TextSubmitted += text =>
        {
            _input.Visible = false;
            AnswerText?.Invoke(text);
        };
        AddChild(_input);
        Resized += Fit;
        Fit();
    }

    /// <summary>The 320 × 200 screen's scale, the window at its bottom, centred.</summary>
    private void Fit()
    {
        float scale = Math.Max(1f, MathF.Floor(Math.Min(Size.X / RetroScreen.Width, Size.Y / RetroScreen.Height)));
        _panel.Scale = new Vector2(scale, scale);
        _panel.Position = new Vector2(MathF.Floor((Size.X - Width * scale) / 2), Size.Y - Height * scale);
        if (_input is not null)
        {
            _input.Scale = _panel.Scale;
            _input.Position = _panel.Position + new Vector2(60, Height - 14) * scale;
        }
    }

    /// <param name="portraits">The texture of a <c>PORT</c> number, or null.</param>
    /// <param name="palette">The screen's palette, for the windows' art (the region's).</param>
    public void Attach(GplDialogue dialogue, Func<int, ImageTexture?> portraits, DarkSun.Data.Images.Palette palette)
    {
        _dialogue = dialogue;
        _portraits = portraits;
        _shownVersion = -1;
        BuildWindows(palette);
        Refresh();
    }

    /// <summary>
    /// The two windows from their data (<see cref="UiWindowView"/>): the text
    /// window over its stone panel, the choice window over the same panel;
    /// the screen's text and portrait go into their controls' places.
    /// </summary>
    private void BuildWindows(DarkSun.Data.Images.Palette palette)
    {
        foreach (Node old in new Node?[] { _textMode, _menuMode }.OfType<Node>())
        {
            foreach (Node mine in new Node[] { _text, _portrait, _title })
            {
                if (mine.GetParent() == old)
                {
                    old.RemoveChild(mine);
                }
            }
            old.QueueFree();
        }
        _options.Clear();
        _textMode = UiWindowView.Build(TextWindow, palette);
        _menuMode = UiWindowView.Build(ChoiceWindow, palette, backdrop: TextWindow);
        if (_textMode is null || _menuMode is null)
        {
            return;
        }
        _panel.AddChild(_textMode);
        _panel.AddChild(_menuMode);
        Color colour = palette[TextColour] is var c ? Color.Color8(c.R, c.G, c.B) : GameFonts.Tan;

        // The text window: the whole window a button, the portrait in its frame, the text in its box.
        if (_textMode.Button(ClickArea) is { } area)
        {
            area.Pressed += More;
        }
        if (_textMode.Button(PortraitBox) is { } frame)
        {
            frame.StretchMode = TextureButton.StretchModeEnum.KeepCentered;
            frame.MouseFilter = MouseFilterEnum.Ignore;
            _portrait.Position = frame.Position + ((frame.Size - _portrait.Size) / 2).Floor();
            _noPortrait = UiArt(palette, "ICON", PortraitBox, 1); // frame 1: the sun, when there is no portrait
        }
        _textMode.AddChild(_portrait);
        Rect2 box = _textMode.RectOf("EBOX", TextBox);
        if (_textMode.TextBox(TextBox) is { } label)
        {
            label.Visible = false; // the paged text goes here instead
        }
        _text.Position = box.Position;
        _text.Size = box.Size;
        GameFonts.Use(_text, colour, "normal_font", "italics_font", "bold_font");
        _textMode.AddChild(_text);
        _textUp = Wire(_textMode.Button(TextUp), Back);
        _textMore = Wire(_textMode.Button(TextMore), More);

        // The choice window: the title above its five bars, an option on each.
        GameFonts.Use(_title, colour);
        _menuMode.AddChild(_title);
        for (int i = 0; i < OptionsShown; i++)
        {
            int slot = i;
            if (_menuMode.Button(FirstOption + i) is not { } bar)
            {
                continue;
            }
            bar.Pressed += () => Choose(_menuTop + slot);
            bar.MouseEntered += () => bar.Modulate = new Color(1.25f, 1.25f, 1.25f);
            bar.MouseExited += () => bar.Modulate = Colors.White;
            Label text = bar.GetNodeOrNull<Label>("Text") ?? new Label { Name = "Text", MouseFilter = MouseFilterEnum.Ignore };
            if (text.GetParent() is null)
            {
                bar.AddChild(text);
            }
            text.Position = new Vector2(5, -1);
            text.Size = bar.Size;
            text.ClipText = true;
            text.LabelSettings = GameFonts.Settings(colour);
            _options.Add((bar, text));
        }
        _menuUp = Wire(_menuMode.Button(ChoiceUp), Back);
        _menuMore = Wire(_menuMode.Button(ChoiceMore), More);
    }

    private static TextureButton? Wire(TextureButton? button, Action pressed)
    {
        if (button is not null)
        {
            button.Pressed += pressed;
        }
        return button;
    }

    private static Texture2D? UiArt(DarkSun.Data.Images.Palette palette, string tag, int number, int frame) =>
        Session.Open("RESOURCE.GFF").TryRead(tag, number, out var data) && Session.Assets.Frames(data) is var frames && frame < frames.Count
            ? Textures.ToTexture(frames[frame], palette)
            : null;

    /// <summary>Redraw if the dialogue changed; hide when there is nothing to show.</summary>
    public void Refresh()
    {
        if (_dialogue is null || _dialogue.Version == _shownVersion)
        {
            return;
        }
        _shownVersion = _dialogue.Version;
        // A shop has a screen of its own (the store, CharacterScreen).
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
        _portrait.Texture = face ?? _noPortrait;
        _text.Text = string.Concat(_dialogue.Text.Select(Format));

        _choices = _dialogue.Prompt switch
        {
            GplYesNoPrompt => [("Yes", 1), ("No", 0)],
            GplMenuPrompt menu => [.. menu.Options.Select((o, i) => (o.Text.Trim(), i, o.Enabled)).Where(o => o.Enabled).Select(o => (o.Item1, o.i))],
            _ => [],
        };
        bool typing = _dialogue.Prompt is GplTextPrompt;
        if (typing && !_input.Visible)
        {
            _input.MaxLength = ((GplTextPrompt)_dialogue.Prompt!).MaxLength;
            _input.Text = "";
            _input.Visible = true;
            _input.CallDeferred(Control.MethodName.GrabFocus);
        }
        else if (!typing)
        {
            _input.Visible = false;
        }
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
        if (_textMode is null || _menuMode is null)
        {
            return;
        }
        _textMode.Visible = !_menuShown;
        _menuMode.Visible = _menuShown;
        if (_menuShown)
        {
            for (int i = 0; i < _options.Count; i++)
            {
                int at = _menuTop + i;
                bool has = at < _choices.Count;
                _options[i].Bar.Visible = has;
                _options[i].Text.Text = has ? $"{at + 1}. {_choices[at].Text}" : "";
            }
            Enable(_menuUp, true); // back to the text, or the options before
            Enable(_menuMore, _menuTop + OptionsShown < _choices.Count);
        }
        else
        {
            _text.ScrollToLine(_top);
            Enable(_textUp, _top > 0);
            // MORE goes on: to the next page, the choices, the script, or away.
            Enable(_textMore, true);
        }
    }

    private static void Enable(TextureButton? button, bool on)
    {
        if (button is not null)
        {
            button.Disabled = !on;
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

    /// <summary>All text alike, as DSUN.EXE draws it whatever the style.</summary>
    private static string Format(GplText t) => t.Text.Replace("[", "[lb]");

    public override void _Process(double delta) => Refresh();
}
