using System.Text;
using DarkSun.Data.Text;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// The game's font (<see cref="GameFont"/>, <c>FONT</c> 100, mods applied) as
/// Godot bitmap fonts, one per text colour: letters in that colour, their
/// shadow in palette colour 20, as DSUN.EXE draws text (<c>2BF9:055D</c>). Use
/// it at <see cref="Size"/> (9, the font's height) in the 320 × 200
/// screens' coordinates, with the control's font colour white. Characters
/// are looked up as the DOS code page 437 glyphs they are (the font's
/// 0x80–0xFF are Ç ü é …).
/// </summary>
public static class GameFonts
{
    /// <summary>The font's height, the size to use it at.</summary>
    public const int Size = 9;

    /// <summary>The shadow: colour 20, the same in every palette (56, 56, 84).</summary>
    public static readonly Color Shadow = Color.Color8(56, 56, 84);

    /// <summary>The text window's colour, 199 (224, 192, 160) in every palette.</summary>
    public static readonly Color Tan = Color.Color8(224, 192, 160);

    private const int Ascent = 8;

    private static GameFont? _font;
    private static bool _loaded;
    private static readonly Dictionary<(Color Face, Color Shadow), FontFile> Fonts = [];
    private static char[]? _codePage;

    /// <summary>The decoded font, or null if the game has none (or a mod's can't be read).</summary>
    public static GameFont? Font
    {
        get
        {
            if (!_loaded)
            {
                _loaded = true;
                try
                {
                    _font = Session.Install is not null && Session.Open("RESOURCE.GFF").TryRead("FONT", 100, out var data)
                        ? GameFont.Parse(data.Span)
                        : null;
                }
                catch (InvalidDataException e)
                {
                    GD.PushWarning($"FONT 100: {e.Message}; using Godot's font");
                }
            }
            return _font;
        }
    }

    /// <summary>Forget the fonts (another game folder, mods changed).</summary>
    public static void Reset()
    {
        (_font, _loaded) = (null, false);
        Fonts.Clear();
    }

    /// <summary>
    /// The font with letters in <paramref name="face"/> and their shadow in
    /// <paramref name="shadow"/> (<see cref="Shadow"/> if not given; DSUN.EXE's
    /// <c>%C</c> text codes remap the shadow pixel too, as the menu's title
    /// does), or null if there is no game font.
    /// </summary>
    public static Font? Get(Color face, Color? shadow = null)
    {
        if (Font is not { } font)
        {
            return null;
        }
        Color under = shadow ?? Shadow;
        if (!Fonts.TryGetValue((face, under), out FontFile? made))
        {
            made = Make(font, face, under);
            Fonts[(face, under)] = made;
        }
        return made;
    }

    /// <summary>Label settings for text in <paramref name="face"/>; Godot's font at a near size without a game font.</summary>
    public static LabelSettings Settings(Color face, int lineSpacing = 0, Color? shadow = null) => Get(face, shadow) is { } f
        ? new LabelSettings { Font = f, FontSize = Size, FontColor = Colors.White, LineSpacing = lineSpacing }
        : new LabelSettings { FontSize = 7, FontColor = face, LineSpacing = lineSpacing - 2, OutlineSize = 2, OutlineColor = shadow ?? Shadow };

    /// <summary>Give <paramref name="control"/> the font (its <paramref name="fonts"/> theme items) in <paramref name="face"/>.</summary>
    public static void Use(Control control, Color face, params string[] fonts)
    {
        if (Get(face) is not { } f)
        {
            control.AddThemeColorOverride("font_color", face);
            return;
        }
        foreach (string name in fonts.Length == 0 ? ["font"] : fonts)
        {
            control.AddThemeFontOverride(name, f);
            control.AddThemeFontSizeOverride(name == "font" ? "font_size" : name + "_size", Size);
        }
        control.AddThemeColorOverride("font_color", Colors.White);
        control.AddThemeColorOverride("default_color", Colors.White);
    }

    private static FontFile Make(GameFont font, Color face, Color shadow)
    {
        const int CellWidth = 8;
        int h = font.Height;
        var image = Image.CreateEmpty(16 * CellWidth, 16 * h, false, Image.Format.Rgba8);
        var file = new FontFile
        {
            FixedSize = Size,
            Antialiasing = TextServer.FontAntialiasing.None,
            GenerateMipmaps = false,
            Hinting = TextServer.Hinting.None,
            SubpixelPositioning = TextServer.SubpixelPositioning.Disabled,
        };
        var size = new Vector2I(Size, 0);
        char[] codePage = CodePage();
        for (int c = 0; c < 256; c++)
        {
            Glyph g = font.Glyphs[c];
            int left = c % 16 * CellWidth, top = c / 16 * h;
            int w = Math.Min(g.Width, CellWidth);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    byte p = g[x, y];
                    if (p != 0)
                    {
                        image.SetPixel(left + x, top + y, p == GameFont.ShadowPixel ? shadow : face);
                    }
                }
            }
            int glyph = codePage[c];
            file.SetGlyphAdvance(0, Size, glyph, new Vector2(g.Width, 0));
            file.SetGlyphOffset(0, size, glyph, new Vector2(0, -Ascent));
            file.SetGlyphSize(0, size, glyph, new Vector2(w, h));
            file.SetGlyphUVRect(0, size, glyph, new Rect2(left, top, w, h));
            file.SetGlyphTextureIdx(0, size, glyph, 0);
        }
        file.SetTextureImage(0, size, 0, image);
        file.SetCacheAscent(0, Size, Ascent);
        file.SetCacheDescent(0, Size, h - Ascent);
        return file;
    }

    /// <summary>Byte n of the font → the Unicode character it is (code page 437; 0–127 as they are).</summary>
    private static char[] CodePage()
    {
        if (_codePage is null)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var cp437 = Encoding.GetEncoding(437);
            _codePage = new char[256];
            for (int b = 0; b < 256; b++)
            {
                _codePage[b] = b < 128 ? (char)b : cp437.GetString([(byte)b])[0];
            }
        }
        return _codePage;
    }
}
