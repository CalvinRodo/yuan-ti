using DarkSun.Data.Gff;
using DarkSun.Data.Images;
using DarkSun.Data.Mods;
using DarkSun.Data.Ui;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// A <c>WIND</c> from <c>RESOURCE.GFF</c> (mods applied) built as Godot
/// controls, in the 320 × 200 screen's pixels, so a screen's layout is the
/// window's data and the screen's code only fills it in:
/// <list type="bullet">
/// <item>its backdrop: the <c>BMP</c> its data names (<see cref="UiWindow.Backdrop"/>),
/// else that of the window's number when there is one (or the one given), cut
/// to the window's size;</item>
/// <item>each <c>BUTN</c> a <see cref="TextureButton"/> named <c>BUTN n</c> at its
/// place and size, drawn with its <c>ICON</c>'s frames (normal, highlighted,
/// disabled, pressed; none: an invisible button), with its text (if it has
/// one) in a <see cref="Label"/> named <c>Text</c>, placed by the control's
/// alignment;</item>
/// <item>each <c>APFM</c> an empty <see cref="Control"/> named <c>APFM n</c>, a
/// frame or slot for the screen to draw into;</item>
/// <item>each <c>EBOX</c> a <see cref="Label"/> named <c>EBOX n</c>, its text box,
/// in the game's font and its colour;</item>
/// <item>the <c>ACCL</c>'s keys in <see cref="Keys"/>.</item>
/// </list>
/// The layout is easy to change: a mod's <c>WIND/n.json</c> moves controls
/// (<see cref="RecordJson"/>); without one, the engine's own adjustments to
/// the original, <c>res://ui/WIND/n.json</c> in the same form, apply.
/// </summary>
public partial class UiWindowView : Control
{
    /// <summary>The engine's adjustments to the original layouts (same JSON as a mod's).</summary>
    public const string AdjustmentsFolder = "res://ui";

    private readonly Dictionary<(string Tag, int Number), Control> _items = [];
    private readonly Func<int, Color> _colour;

    private UiWindowView(UiWindow window, Func<int, Color> colour)
    {
        Window = window;
        _colour = colour;
        Name = $"WIND {window.Number}";
        Size = new Vector2(window.Width, window.Height);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public UiWindow Window { get; }

    /// <summary>The window's accelerators: key → command.</summary>
    public IReadOnlyList<Accelerator> Keys { get; private set; } = [];

    /// <summary>
    /// Window <paramref name="number"/> drawn in <paramref name="palette"/>,
    /// with <c>BMP</c> <paramref name="backdrop"/> behind it (default: the one
    /// its data names, <see cref="UiWindow.Backdrop"/>, else the window's own
    /// number, if there is one; 0: none). Null if there is no such window.
    /// </summary>
    public static UiWindowView? Build(int number, Palette palette, int? backdrop = null)
    {
        GffFile resources = Session.Open("RESOURCE.GFF");
        if (!resources.TryRead("WIND", number, out var data))
        {
            return null;
        }
        UiWindow window = UiWindow.Parse(Adjusted(number, data).Span);
        var view = new UiWindowView(window, i => palette[i] is var c ? Color.Color8(c.R, c.G, c.B) : Colors.White);
        Texture2D? Art(string tag, int n, int frame) =>
            resources.TryRead(tag, n, out var d) && Session.Assets.Frames(d) is var frames && frame < frames.Count && frames[frame].Width > 1
                ? Textures.ToTexture(frames[frame], palette)
                : null;

        int bmp = backdrop ?? (window.Backdrop > 0 ? window.Backdrop : number);
        if (bmp > 0 && Art("BMP ", bmp, 0) is { } back)
        {
            var rect = new Rect2(0, 0, Math.Min(window.Width, back.GetWidth()), Math.Min(window.Height, back.GetHeight()));
            view.AddChild(new TextureRect { Name = "Backdrop", Texture = new AtlasTexture { Atlas = back, Region = rect }, MouseFilter = MouseFilterEnum.Ignore });
        }
        foreach (WindowItem item in window.Items)
        {
            string tag = item.Tag.Trim();
            if (tag == "ACCL")
            {
                view.Keys = resources.TryRead("ACCL", item.Number, out var accl) ? AcceleratorTable.Parse(accl.Span) : [];
                continue;
            }
            if (!resources.TryRead(item.Tag, item.Number, out var res))
            {
                continue;
            }
            UiControl c;
            try
            {
                c = UiControl.Parse(res.Span);
            }
            catch (InvalidDataException)
            {
                continue;
            }
            Control made = tag switch
            {
                "BUTN" => view.Button(c, Art),
                "EBOX" => view.TextBox(c),
                _ => new Control { MouseFilter = MouseFilterEnum.Ignore },
            };
            made.Name = $"{tag} {item.Number}";
            made.Position = new Vector2(item.X, item.Y);
            made.Size = new Vector2(c.Width, c.Height);
            view._items.TryAdd((tag, item.Number), made);
            view.AddChild(made);
        }
        return view;
    }

    /// <summary>The window's bytes with a mod's changes, or else the engine's adjustments.</summary>
    private static ReadOnlyMemory<byte> Adjusted(int number, ReadOnlyMemory<byte> data)
    {
        string path = $"{AdjustmentsFolder}/WIND/{number}.json";
        if (Session.Mods.Replacing("RESOURCE.GFF", "WIND", number) is not null || !Godot.FileAccess.FileExists(path))
        {
            return data;
        }
        try
        {
            return RecordJson.Apply("WIND", data.Span, Godot.FileAccess.GetFileAsString(path));
        }
        catch (InvalidDataException e)
        {
            GD.PushWarning($"{path}: {e.Message}");
            return data;
        }
    }

    private TextureButton Button(UiControl c, Func<string, int, int, Texture2D?> art)
    {
        var button = new TextureButton
        {
            IgnoreTextureSize = true,
            StretchMode = c.Align.IconX == UiAlignment.Centre ? TextureButton.StretchModeEnum.KeepCentered : TextureButton.StretchModeEnum.Keep,
            FocusMode = FocusModeEnum.None,
        };
        if (c.Icon > 0)
        {
            button.TextureNormal = art("ICON", c.Icon, UiButton.NormalFrame);
            button.TextureHover = art("ICON", c.Icon, UiButton.HighlightFrame);
            button.TextureFocused = button.TextureHover;
            button.TextureDisabled = art("ICON", c.Icon, UiButton.DisabledFrame);
            button.TexturePressed = art("ICON", c.Icon, UiButton.PressedFrame) ?? button.TextureHover;
        }
        if (c.Text.Length > 0)
        {
            var text = new Label
            {
                Name = "Text",
                Text = c.Text,
                Size = new Vector2(c.Width, c.Height),
                HorizontalAlignment = c.Align.TextX switch
                {
                    UiAlignment.Centre => HorizontalAlignment.Center,
                    UiAlignment.End => HorizontalAlignment.Right,
                    _ => HorizontalAlignment.Left,
                },
                VerticalAlignment = c.Align.TextY switch
                {
                    UiAlignment.Centre => VerticalAlignment.Center,
                    UiAlignment.End => VerticalAlignment.Bottom,
                    _ => VerticalAlignment.Top,
                },
                LabelSettings = GameFonts.Settings(GameFonts.Tan),
                MouseFilter = MouseFilterEnum.Ignore,
            };
            button.AddChild(text);
            text.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); // the button's size, whatever it becomes
        }
        return button;
    }

    private Label TextBox(UiControl c) => new()
    {
        AutowrapMode = TextServer.AutowrapMode.WordSmart,
        ClipText = true,
        LabelSettings = GameFonts.Settings(c.TextColour > 0 ? _colour(c.TextColour) : GameFonts.Tan),
        MouseFilter = MouseFilterEnum.Ignore,
    };

    /// <summary>
    /// Put <paramref name="text"/> on button <paramref name="number"/>, centred in
    /// the game's font (a screen filling in a window's labels, as DSUN.EXE's code does).
    /// </summary>
    public void SetButtonText(int number, string text)
    {
        if (Button(number) is not { } button)
        {
            return;
        }
        if (button.GetNodeOrNull<Label>("Text") is not { } label)
        {
            label = new Label
            {
                Name = "Text",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                LabelSettings = GameFonts.Settings(GameFonts.Tan),
                MouseFilter = MouseFilterEnum.Ignore,
            };
            button.AddChild(label);
            label.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        }
        label.Text = text;
    }

    // -- finding controls ------------------------------------------------------------

    /// <summary>The control <c>tag number</c>, or null if the window hasn't it.</summary>
    public Control? Item(string tag, int number) => _items.GetValueOrDefault((tag, number));

    public TextureButton? Button(int number) => Item("BUTN", number) as TextureButton;

    /// <summary>An <c>APFM</c>: a frame or slot to draw into.</summary>
    public Control? Slot(int number) => Item("APFM", number);

    public Label? TextBox(int number) => Item("EBOX", number) as Label;

    /// <summary>A button's text label (its placeholder replaced by the screen).</summary>
    public Label? ButtonText(int number) => Button(number)?.GetNodeOrNull<Label>("Text");

    /// <summary>The place and size of a control, in the window.</summary>
    public Rect2 RectOf(string tag, int number) => Item(tag, number) is { } c ? new Rect2(c.Position, c.Size) : default;

    /// <summary>All the controls, in window order.</summary>
    public IEnumerable<(string Tag, int Number, Control Control)> Items => _items.Select(kv => (kv.Key.Tag, kv.Key.Number, kv.Value));
}
