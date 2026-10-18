using DarkSun.Data.Images;
using DarkSun.Game.Scripting;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// Shows a script's <see cref="GplDialogue"/>: portrait, text and the prompt
/// buttons (Continue, Yes/No, menu options). Answering resumes the paused
/// script; when a script has finished and left text, a Close button clears it.
/// Text in style 115 is shown as speech and style 98 as narration (inferred
/// from the scripts' use of them).
/// </summary>
public partial class DialogueWindow : PanelContainer
{
    private const int SpeechStyle = 115;
    private const int NarrationStyle = 98;

    private TextureRect _portrait = null!;
    private RichTextLabel _text = null!;
    private VBoxContainer _buttons = null!;
    private GplDialogue? _dialogue;
    private int _shownVersion = -1;
    private Func<int, ImageTexture?>? _portraits;

    /// <summary>Resume the paused script with the player's answer.</summary>
    public Action<int>? Answer { get; set; }

    public override void _Ready()
    {
        Visible = false;
        SetAnchorsAndOffsetsPreset(LayoutPreset.CenterBottom);
        GrowHorizontal = GrowDirection.Both;
        GrowVertical = GrowDirection.Begin;
        CustomMinimumSize = new Vector2(560, 0);
        OffsetBottom = -6;

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        AddChild(row);
        _portrait = new TextureRect
        {
            CustomMinimumSize = new Vector2(64, 64),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsVertical = SizeFlags.ShrinkBegin,
        };
        row.AddChild(_portrait);
        _text = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollFollowing = true,
            CustomMinimumSize = new Vector2(360, 64),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _text.AddThemeFontSizeOverride("normal_font_size", 10);
        _text.AddThemeFontSizeOverride("italics_font_size", 10);
        row.AddChild(_text);
        _buttons = new VBoxContainer { CustomMinimumSize = new Vector2(110, 0) };
        row.AddChild(_buttons);
    }

    /// <param name="portraits">The texture of a <c>PORT</c> number, or null.</param>
    public void Attach(GplDialogue dialogue, Func<int, ImageTexture?> portraits)
    {
        _dialogue = dialogue;
        _portraits = portraits;
        _shownVersion = -1;
        Refresh();
    }

    /// <summary>Redraw if the dialogue changed; hide when there is nothing to show.</summary>
    public void Refresh()
    {
        if (_dialogue is null || _dialogue.Version == _shownVersion)
        {
            return;
        }
        _shownVersion = _dialogue.Version;
        Visible = !_dialogue.IsEmpty;
        if (!Visible)
        {
            return;
        }

        _portrait.Texture = _dialogue.Portrait is int n ? _portraits?.Invoke(n) : null;
        _portrait.Visible = _portrait.Texture is not null;
        _text.Text = string.Concat(_dialogue.Text.Select(Format));

        foreach (Node b in _buttons.GetChildren())
        {
            b.QueueFree();
        }
        switch (_dialogue.Prompt)
        {
            case GplContinuePrompt:
                AddButton("Continue", 0);
                break;
            case GplYesNoPrompt:
                AddButton("Yes", 1);
                AddButton("No", 0);
                break;
            case GplMenuPrompt menu:
                for (int i = 0; i < menu.Options.Count; i++)
                {
                    if (menu.Options[i].Enabled)
                    {
                        AddButton(menu.Options[i].Text.Trim(), i);
                    }
                }
                break;
            default:
                var close = new Button { Text = "Close" };
                close.Pressed += () => _dialogue.Clear();
                _buttons.AddChild(close);
                break;
        }
    }

    private void AddButton(string label, int answer)
    {
        var button = new Button { Text = label, ClipText = true, CustomMinimumSize = new Vector2(110, 0) };
        button.AddThemeFontSizeOverride("font_size", 10);
        button.Pressed += () => Answer?.Invoke(answer);
        _buttons.AddChild(button);
    }

    private static string Format(GplText t)
    {
        string escaped = t.Text.Replace("[", "[lb]");
        return t.Style switch
        {
            NarrationStyle => $"[i][color=#d8c8a0]{escaped}[/color][/i]",
            SpeechStyle => $"[color=#ffffff]{escaped}[/color]",
            _ => escaped,
        };
    }

    public override void _Process(double delta) => Refresh();
}
