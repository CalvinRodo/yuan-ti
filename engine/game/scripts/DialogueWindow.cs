using DarkSun.Game.Scripting;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// A script's <see cref="GplDialogue"/>, in DSUN.EXE's text windows, 320 × 58
/// over the stone panel <c>BMP</c> 3007, scaled as the 320 × 200 screen is: the
/// text window at the top of the screen (<c>8BEB</c> opens <c>WIND</c> 3007 at
/// (0, 0)) and, while there are choices, the choice window at the bottom
/// under it, the text staying in view above them:
/// <list type="bullet">
/// <item>The text window (<c>WIND</c> 3007): the portrait (<c>PORT</c>, SHOW_PIC)
/// in its frame (<c>ICON</c> 2090; the sun emblem when there is none), the
/// text box (<c>EBOX</c> 4002, 236 × 46 at (58, 6); text in orange, colour
/// 45, as the original shows it, though the box's +0x80 says 199) and, on the
/// right, the up arrow (<c>ICON</c> 2093) and MORE (<c>ICON</c> 12100). Text
/// that doesn't fit is paged: MORE shows the next page, the arrow the one
/// before. Once all is read MORE (or a click on the text) answers CONTINUE,
/// opens the choices, or puts the window away.</item>
/// <item>The choice window (<c>WIND</c> 3008, <c>84C9:0BCE</c>): the menu's
/// title, upper-cased, at (6, 4) in yellow (colour 47, its shadow 17: the
/// format "%C%C%C%s"), and its options on five stone bars (<c>ICON</c>
/// 12102–12106) as the script wrote them, leading spaces and all (no
/// numbers), each at its bar's top left, orange (45) and red (77) under
/// the pointer. The arrow and MORE scroll them. ASK_YES_NO is offered here
/// as Yes and No under "ANSWER YES OR NO", as DSUN.EXE words it.</item>
/// <item>The typing window (<c>WIND</c> 3500, <c>84C9:0253</c> mode 4, for
/// INPUT_STRING and the other INPUT_ commands, <c>7372:1235</c>): opened at
/// (0, 140) under the text window, which stays; it is only an edit box,
/// <c>EBOX</c> 4500 (239 × 46 at (56, 6)), where the player types.</item>
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

    /// <summary>
    /// The colours DSUN.EXE's conversations show (from a capture of the original;
    /// the same in every palette): the text and the options orange (45, 252 164 0),
    /// the option under the pointer red (77, 212 64 40), and the choice window's
    /// title yellow (47, its shadow 17: <c>84C9:0BCE</c>'s "%C%C%C%s" sets 0x2F and
    /// 0x11). The text box's <c>EBOX</c> +0x80 (199, tan) and the button colours
    /// <c>6A94:0138</c> sets (254, 52) aren't what reaches the screen, by the capture;
    /// how the original gets its colours there isn't traced.
    /// </summary>
    private const int TextColour = 45, OptionColour = 45, OptionHoverColour = 77, TitleColour = 47, TitleShadow = 17;

    // The windows' controls.
    private const int TextWindow = 3007, ChoiceWindow = 3008, TypingWindow = 3500, TypingBox = 4500, ClickArea = 12300, PortraitBox = 2090, TextBox = 4002,
        TextUp = 2093, TextMore = 2094, FirstOption = 2076, ChoiceUp = 2095, ChoiceMore = 2096;

    /// <summary>Where DSUN.EXE opens the typing window (<c>6BB8:0152(3500, 0, 140)</c>).</summary>
    private const int TypingTop = 140;

    /// <summary>The text window's place (the screen's top), the choice window's (its bottom) and the typing window's.</summary>
    private Control _panel = null!, _bottom = null!, _typing = null!;
    private UiWindowView? _textMode, _menuMode, _typingMode;
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

    /// <summary>Where the player types for INPUT_STRING: the typing window's edit box.</summary>
    private LineEdit _input = null!;

    public override void _Ready()
    {
        Visible = false;
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _panel = new Control { Size = new Vector2(Width, Height), MouseFilter = MouseFilterEnum.Stop, ClipContents = true };
        _bottom = new Control { Size = new Vector2(Width, Height), MouseFilter = MouseFilterEnum.Stop, ClipContents = true, Visible = false };
        _typing = new Control { Size = new Vector2(Width, Height), MouseFilter = MouseFilterEnum.Stop, ClipContents = true, Visible = false };
        AddChild(_panel);
        AddChild(_bottom);
        AddChild(_typing);
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
        _title = new Label { Position = new Vector2(6, 4), Size = new Vector2(296, 11), ClipText = true, MouseFilter = MouseFilterEnum.Ignore };
        _input = new LineEdit { Flat = true, CaretBlink = true, ContextMenuEnabled = false, MouseFilter = MouseFilterEnum.Stop };
        _input.TextSubmitted += text =>
        {
            _typing.Visible = false;
            AnswerText?.Invoke(text);
        };
        Resized += Fit;
        Fit();
    }

    /// <summary>The 320 × 200 screen's scale, centred across: the text window at the top, the choices at the bottom.</summary>
    private void Fit()
    {
        float scale = Math.Max(1f, MathF.Floor(Math.Min(Size.X / RetroScreen.Width, Size.Y / RetroScreen.Height)));
        _panel.Scale = _bottom.Scale = _typing.Scale = new Vector2(scale, scale);
        float left = MathF.Floor((Size.X - Width * scale) / 2);
        _panel.Position = new Vector2(left, 0);
        _bottom.Position = new Vector2(left, Size.Y - Height * scale);
        _typing.Position = new Vector2(left, Size.Y - (RetroScreen.Height - TypingTop) * scale);
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
        foreach (Node old in new Node?[] { _textMode, _menuMode, _typingMode }.OfType<Node>())
        {
            foreach (Node mine in new Node[] { _text, _portrait, _title, _input })
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
        _typingMode = UiWindowView.Build(TypingWindow, palette, backdrop: TextWindow);
        if (_textMode is null || _menuMode is null || _typingMode is null)
        {
            return;
        }
        _panel.AddChild(_textMode);
        _bottom.AddChild(_menuMode);
        _typing.AddChild(_typingMode);
        Color Of(int index) => palette[index] is var c ? Color.Color8(c.R, c.G, c.B) : GameFonts.Tan;
        Color colour = Of(TextColour);

        // The text window: the whole window a button, the portrait in its frame, the text in its box.
        if (_textMode.Button(ClickArea) is { } area)
        {
            area.Pressed += NextPage;
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
        _textUp = Wire(_textMode.Button(TextUp), PageBack);
        _textMore = Wire(_textMode.Button(TextMore), NextPage);

        // The choice window: the title above its five bars, an option on each.
        _title.LabelSettings = GameFonts.Settings(Of(TitleColour), shadow: Of(TitleShadow));
        _menuMode.AddChild(_title);
        LabelSettings normal = GameFonts.Settings(Of(OptionColour)), lit = GameFonts.Settings(Of(OptionHoverColour));
        for (int i = 0; i < OptionsShown; i++)
        {
            int slot = i;
            if (_menuMode.Button(FirstOption + i) is not { } bar)
            {
                continue;
            }
            bar.Pressed += () => Choose(_menuTop + slot);
            Label text = bar.GetNodeOrNull<Label>("Text") ?? new Label { Name = "Text", MouseFilter = MouseFilterEnum.Ignore };
            if (text.GetParent() is null)
            {
                bar.AddChild(text);
            }
            text.Position = Vector2.Zero;
            text.Size = bar.Size;
            text.ClipText = true;
            text.LabelSettings = normal;
            bar.MouseEntered += () => text.LabelSettings = lit;
            bar.MouseExited += () => text.LabelSettings = normal;
            _options.Add((bar, text));
        }
        _menuUp = Wire(_menuMode.Button(ChoiceUp), Back);
        _menuMore = Wire(_menuMode.Button(ChoiceMore), More);

        // The typing window: what the player types, in the edit box, in the text's colour.
        Rect2 typed = _typingMode.RectOf("EBOX", TypingBox);
        if (_typingMode.TextBox(TypingBox) is { } edit)
        {
            edit.Visible = false;
        }
        _input.Position = typed.Position;
        _input.Size = new Vector2(typed.Size.X, 12);
        GameFonts.Use(_input, colour);
        _input.AddThemeColorOverride("caret_color", colour);
        _input.AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
        _input.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        _input.AddThemeConstantOverride("minimum_character_width", 0);
        _typingMode.AddChild(_input);
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
        bool cleared = _dialogue.Pages != _clears;
        _clears = _dialogue.Pages;
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
            GplMenuPrompt menu => [.. menu.Options.Select((o, i) => (o.Text.TrimEnd(), i, o.Enabled)).Where(o => o.Enabled).Select(o => (o.Item1, o.i))],
            _ => [],
        };
        bool typing = _dialogue.Prompt is GplTextPrompt;
        if (typing && !_typing.Visible)
        {
            _input.MaxLength = ((GplTextPrompt)_dialogue.Prompt!).MaxLength;
            _input.Text = "";
            _typing.Visible = true;
            _input.CallDeferred(Control.MethodName.GrabFocus);
        }
        else if (!typing)
        {
            _typing.Visible = false;
        }
        _title.Text = _dialogue.Prompt switch
        {
            GplYesNoPrompt => "ANSWER YES OR NO",
            GplMenuPrompt menu => menu.Title.Trim().ToUpperInvariant(),
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
        // The text stays at the top; the choices come up at the bottom once it is read.
        _bottom.Visible = _menuShown;
        _text.ScrollToLine(_top);
        Enable(_textUp, _top > 0);
        // MORE goes on: to the next page, the choices, the script, or away; nothing more once the choices are up.
        Enable(_textMore, !_menuShown || !AllRead);
        if (_menuShown)
        {
            for (int i = 0; i < _options.Count; i++)
            {
                int at = _menuTop + i;
                bool has = at < _choices.Count;
                _options[i].Bar.Visible = has;
                _options[i].Text.Text = has ? _choices[at].Text : "";
            }
            Enable(_menuUp, _menuTop > 0);
            Enable(_menuMore, _menuTop + OptionsShown < _choices.Count);
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

    /// <summary>
    /// A click on the map while the text shows goes on as MORE does, as in DSUN.EXE, where the
    /// text window waits for any click; choices and typed answers still wait for their own.
    /// </summary>
    public void ClickOutside()
    {
        if (Visible && !_menuShown && _dialogue?.Prompt is not GplTextPrompt)
        {
            More();
        }
    }

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
        if (_menuShown && _menuTop > 0)
        {
            _menuTop = Math.Max(0, _menuTop - OptionsShown);
            ShowPage();
        }
        else
        {
            PageBack();
        }
    }

    /// <summary>The text window's up arrow: the page before.</summary>
    private void PageBack()
    {
        _top = Math.Max(0, _top - Page);
        ShowPage();
    }

    /// <summary>The text window's MORE (or a click on the text): the next page; once all is read, as MORE.</summary>
    private void NextPage()
    {
        if (!AllRead)
        {
            _top = Math.Min(_top + Page, Math.Max(0, _lines - 1));
            ShowPage();
        }
        else if (!_menuShown)
        {
            More();
        }
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
