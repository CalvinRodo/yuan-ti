using DarkSun.Data.Images;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// Pieces of the original's stone windows for screens it hadn't (the mods)
/// or that need more than its windows hold (saved games past ten): its
/// <c>ICON</c> buttons, its plain bar (<c>ICON</c> 11110) with a word on it,
/// its banner (<c>ICON</c> 2056) with another word, and labels in the game's
/// font, all in palette 1001.
/// </summary>
public sealed class StoneUi(Palette palette)
{
    public const int PaletteNumber = 1001;

    /// <summary>The text colours: grey-white (52), yellow (47), the status grey (27), red (77).</summary>
    public static readonly Color Face = Color.Color8(212, 212, 224), Yellow = Color.Color8(228, 232, 24),
        Dim = QuestionWindow.TitleGrey, Red = Color.Color8(212, 64, 40);

    private const int PlainBar = 11110, BannerIcon = 2056;

    /// <summary>The 320 × 200 screens' palette.</summary>
    public static StoneUi Load() => new(Session.Assets.Palette(Session.Open("RESOURCE.GFF").Read("PAL ", PaletteNumber)));

    public Palette Palette { get; } = palette;

    /// <summary>Frame <paramref name="frame"/> of <c>ICON</c> <paramref name="number"/>; null if it has none (or an empty one).</summary>
    public Texture2D? Icon(int number, int frame = 0) =>
        Session.Open("RESOURCE.GFF").TryRead("ICON", number, out var data) && Session.Assets.Frames(data) is var frames
            && frame < frames.Count && frames[frame].Width > 1
            ? Textures.ToTexture(frames[frame], Palette)
            : null;

    /// <summary>A button of <c>ICON</c> <paramref name="icon"/>'s frames: normal, under the pointer, disabled, pressed.</summary>
    public TextureButton Button(int icon, Vector2 at)
    {
        var button = new TextureButton { Position = at, FocusMode = Control.FocusModeEnum.None };
        SetIcon(button, icon);
        return button;
    }

    public void SetIcon(TextureButton button, int icon)
    {
        button.TextureNormal = Icon(icon, 0);
        button.TextureHover = Icon(icon, 1);
        button.TextureDisabled = Icon(icon, 2) ?? button.TextureNormal;
        button.TexturePressed = Icon(icon, 3) ?? button.TextureHover;
        button.Size = button.TextureNormal?.GetSize() ?? button.Size;
    }

    /// <summary>
    /// A small arrow button pointing up or down: the preferences' left and
    /// right arrows (<c>ICON</c> 16105, 16104) turned a quarter.
    /// </summary>
    public TextureButton UpDownArrow(bool up, Vector2 at)
    {
        TextureButton button = Button(up ? 16105 : 16104, at);
        button.PivotOffset = button.Size / 2;
        button.Rotation = MathF.PI / 2;
        return button;
    }

    /// <summary>The plain bar (<c>ICON</c> 11110, stretched to <paramref name="width"/>) with <paramref name="text"/> on it.</summary>
    public TextureButton TextButton(string text, Vector2 at, float width)
    {
        TextureButton button = Button(PlainBar, at);
        button.TextureDisabled = button.TextureNormal; // its disabled frame is blank: the word greys out instead
        button.IgnoreTextureSize = true;
        button.StretchMode = TextureButton.StretchModeEnum.Scale;
        button.Size = new Vector2(width, button.Size.Y);
        Label label = Text(button, new Vector2(0, (button.Size.Y - GameFonts.Size) / 2 - 1), width, Face, HorizontalAlignment.Center);
        label.Name = "Text";
        label.Text = text;
        button.MouseEntered += () => label.LabelSettings = GameFonts.Settings(button.Disabled ? Dim : Yellow);
        button.MouseExited += () => label.LabelSettings = GameFonts.Settings(button.Disabled ? Dim : Face);
        return button;
    }

    /// <summary>Change a <see cref="TextButton"/>'s word, or grey it out with the button.</summary>
    public static void SetText(TextureButton button, string text, bool disabled = false)
    {
        button.Disabled = disabled;
        if (button.GetNodeOrNull<Label>("Text") is { } label)
        {
            label.Text = text;
            label.LabelSettings = GameFonts.Settings(disabled ? Dim : Face);
        }
    }

    /// <summary>The banner (<c>ICON</c> 2056, SAVE) with its letters scraped off and <paramref name="text"/> in yellow instead.</summary>
    public Control Banner(string text, Vector2 at)
    {
        var banner = new Control { Position = at, MouseFilter = Control.MouseFilterEnum.Ignore };
        if (Icon(BannerIcon) is { } art && art.GetImage() is { } image)
        {
            // The plaque's face, inside its rim, in its middle grey (colour 37).
            image.FillRect(new Rect2I(7, 5, image.GetWidth() - 14, 14), Color.Color8(76, 76, 104));
            banner.AddChild(new TextureRect { Texture = ImageTexture.CreateFromImage(image), MouseFilter = Control.MouseFilterEnum.Ignore });
            banner.Size = art.GetSize();
        }
        Text(banner, new Vector2(0, (banner.Size.Y - GameFonts.Size) / 2 - 1), banner.Size.X, Yellow, HorizontalAlignment.Center).Text = text;
        return banner;
    }

    /// <summary>A label in the game's font; its top 2 pixels above the letters, as every label's.</summary>
    public static Label Text(Control parent, Vector2 at, float width, Color colour, HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var label = new Label
        {
            Position = at,
            Size = new Vector2(width, GameFonts.Size),
            HorizontalAlignment = align,
            ClipText = true,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            LabelSettings = GameFonts.Settings(colour),
        };
        parent.AddChild(label);
        return label;
    }
}
