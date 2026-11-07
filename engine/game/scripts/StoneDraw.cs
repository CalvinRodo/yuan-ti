using Godot;

namespace DarkSun.Engine;

/// <summary>
/// Drawing the engine's own panels over the map in the game's stone, as Obsidian
/// Edition's (github.com/daaki85/darksun-obsidian-edition, its palette.py): an
/// outlined stone frame with lit and dark edges, small bevelled buttons, a dark
/// mottled well, and text in the game's font. The combat log
/// (<see cref="CombatLogView"/>), the combat bar (<see cref="CombatBar"/>) and the
/// spell bar (<see cref="SpellBar"/>) share it, in the 320 × 200 screen's pixels.
/// </summary>
public static class StoneDraw
{
    public static readonly Color Outline = Color.FromHtml("#202038"), Lit = Color.FromHtml("#A6A6BE"), Stone = Color.FromHtml("#71718A"),
        Dark = Color.FromHtml("#595971"), Well = Color.FromHtml("#30304D"), TextShadow = Color.FromHtml("#101018"), Pale = Color.FromHtml("#D7D7E3"),
        Yellow = Color.FromHtml("#E7EB18"), Dim = Color.FromHtml("#9A9AB0");

    public static readonly Color[] WellShades = [Color.FromHtml("#262640"), Color.FromHtml("#2C2C48"), Well, Color.FromHtml("#3A3A5A")];

    public static Font FontIn(Color colour) => GameFonts.Get(colour, TextShadow) ?? ThemeDB.FallbackFont;

    /// <summary>The width of <paramref name="text"/> in the game's font.</summary>
    public static int Measure(string text) => (int)FontIn(Pale).GetStringSize(text, HorizontalAlignment.Left, -1, GameFonts.Size).X;

    /// <summary><paramref name="text"/> with its top at <paramref name="y"/> (the letters' top 2 pixels below it).</summary>
    public static void DrawText(CanvasItem p, string text, int x, int y, Color colour) =>
        p.DrawString(FontIn(colour), new Vector2(x, y + 8), text, HorizontalAlignment.Left, -1, GameFonts.Size, Colors.White);

    /// <summary><paramref name="text"/> cut to <paramref name="room"/> pixels, "..." at its end if it was.</summary>
    public static string Fit(string text, int room)
    {
        if (Measure(text) <= room)
        {
            return text;
        }
        while (text.Length > 0 && Measure(text + "...") > room)
        {
            text = text[..^1];
        }
        return text + "...";
    }

    /// <summary>The frame: an outline, the stone, its lit and dark edges.</summary>
    public static void Frame(CanvasItem p, Rect2I r)
    {
        p.DrawRect(new Rect2(r.Position, r.Size), Outline);
        p.DrawRect(new Rect2(r.Position + Vector2I.One, r.Size - 2 * Vector2I.One), Stone);
        p.DrawRect(new Rect2(r.Position.X + 1, r.Position.Y + 1, r.Size.X - 2, 1), Lit);
        p.DrawRect(new Rect2(r.Position.X + 1, r.Position.Y + 1, 1, r.Size.Y - 2), Lit);
        p.DrawRect(new Rect2(r.Position.X + 1, r.End.Y - 2, r.Size.X - 2, 1), Dark);
        p.DrawRect(new Rect2(r.End.X - 2, r.Position.Y + 1, 1, r.Size.Y - 2), Dark);
    }

    /// <summary>A small stone button: bevelled, its word in the middle (pressed in when chosen).</summary>
    public static void Chip(CanvasItem p, Rect2I r, string text, Color ink, bool down)
    {
        p.DrawRect(new Rect2(r.Position, r.Size), Outline);
        p.DrawRect(new Rect2(r.Position + Vector2I.One, r.Size - 2 * Vector2I.One), down ? Dark : Stone);
        p.DrawRect(new Rect2(r.Position.X + 1, r.Position.Y + 1, r.Size.X - 2, 1), down ? Outline : Lit);
        p.DrawRect(new Rect2(r.Position.X + 1, r.Position.Y + 1, 1, r.Size.Y - 2), down ? Outline : Lit);
        DrawText(p, text, r.Position.X + (r.Size.X - Measure(text)) / 2, r.Position.Y + (r.Size.Y - 11) / 2 + 1, ink);
    }

    private static readonly Dictionary<Vector2I, Texture2D> Wells = [];

    /// <summary>The well's mottle: its four shades, a pixel at a time, the same each time.</summary>
    public static Texture2D WellTexture(Vector2I size)
    {
        if (Wells.TryGetValue(size, out Texture2D? texture))
        {
            return texture;
        }
        Image image = Image.CreateEmpty(Math.Max(1, size.X), Math.Max(1, size.Y), false, Image.Format.Rgba8);
        for (int y = 0; y < size.Y; y++)
        {
            for (int x = 0; x < size.X; x++)
            {
                uint n = (uint)(x * 374761393 + y * 668265263);
                n = (n ^ (n >> 13)) * 1274126177;
                n ^= n >> 16;
                int shade = (n & 255) < 32 ? 1 : (n & 255) < 48 ? 3 : (n & 255) < 64 ? 0 : 2;
                image.SetPixel(x, y, WellShades[shade]);
            }
        }
        return Wells[size] = ImageTexture.CreateFromImage(image);
    }

    /// <summary>The well, mottled, at <paramref name="r"/>.</summary>
    public static void DrawWell(CanvasItem p, Rect2I r) => p.DrawTextureRect(WellTexture(r.Size), new Rect2(r.Position, r.Size), false);

    /// <summary>The 320 × 200 screen's scale in a window of <paramref name="screen"/> (the largest whole number that fits).</summary>
    public static float ScaleFor(Vector2 screen) => Math.Max(1f, MathF.Floor(Math.Min(screen.X / RetroScreen.Width, screen.Y / RetroScreen.Height)));
}
