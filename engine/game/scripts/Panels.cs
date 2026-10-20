using Godot;

namespace DarkSun.Engine;

/// <summary>The look the engine's own windows share (journal, options, shop, key help): a dark panel with small text and buttons.</summary>
public static class Panels
{
    private const int FontSize = 10;

    public static StyleBoxFlat Panel() => new()
    {
        BgColor = new Color(0.12f, 0.08f, 0.05f, 0.96f),
        BorderColor = new Color(0.6f, 0.42f, 0.25f),
        BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
        ContentMarginLeft = 6, ContentMarginTop = 4, ContentMarginRight = 6, ContentMarginBottom = 5,
    };

    public static Label Text(string text, Color? color = null)
    {
        var label = Small(new Label { Text = text });
        if (color is { } c)
        {
            label.AddThemeColorOverride("font_color", c);
        }
        return label;
    }

    public static Button AddButton(Container parent, string text, Action pressed)
    {
        var b = Small(new Button { Text = text, FocusMode = Control.FocusModeEnum.None });
        b.Pressed += pressed;
        parent.AddChild(b);
        return b;
    }

    public static T Small<T>(T control) where T : Control
    {
        control.AddThemeFontSizeOverride("font_size", FontSize);
        return control;
    }
}
