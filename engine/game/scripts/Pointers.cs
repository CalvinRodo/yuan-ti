using DarkSun.Data.Gff;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// The original's mouse pointers as the system cursor: <c>RESOURCE.GFF</c>
/// <c>ICON</c> 6001–6010, which <c>191F:0A99</c> and the map's hover code
/// (<c>1587:29FA</c>) pick by pointer mode, and a spell's own icon while it is
/// aimed (<c>577D:0889</c>). Drawn at the screen's scale.
/// </summary>
public static class Pointers
{
    public const int Move = 6001, CantMove = 6002, Attack = 6003, CantAttack = 6004, Ranged = 6005, CantRanged = 6006,
        Look = 6007, CantLook = 6008, CantCast = 6009, Wait = 6010;

    /// <summary>The original draws on a 320 × 200 screen; the viewport is 640 × 400.</summary>
    private const int ViewportScale = 2;

    private static readonly Dictionary<(int Icon, int Scale), (Image Image, Vector2 Hotspot)?> Cache = [];
    private static (int Icon, int Scale)? _shown;

    /// <summary>Not an <c>ICON</c>: the item held to use with something (mode 3, <see cref="ShowHeld"/>).</summary>
    public const int Held = -1;

    /// <summary>Show <c>ICON</c> <paramref name="icon"/> as the pointer (the system's arrow if there is no such icon).</summary>
    public static void Show(Window window, int icon) => Show(window, icon, scale => Load(icon, scale));

    /// <summary>
    /// The pointer of pointer mode 3 (<c>191F:0A99</c>: the held item's picture,
    /// <c>4281:003E</c>): <paramref name="picture"/> of object
    /// <paramref name="index"/>, aimed by its middle.
    /// </summary>
    public static void ShowHeld(Window window, int index, Image picture) =>
        Show(window, -2 - index, scale => Scaled(picture.Duplicate() as Image ?? picture, scale, centred: true));

    private static void Show(Window window, int key, Func<int, (Image, Vector2)?> load)
    {
        if (DisplayServer.GetName() == "headless")
        {
            return;
        }
        int scale = ViewportScale * Math.Max(1, (int)Math.Min(window.Size.X / 640f, window.Size.Y / 400f));
        if (_shown == (key, scale))
        {
            return;
        }
        _shown = (key, scale);
        if (!Cache.TryGetValue((key, scale), out var pointer))
        {
            Cache[(key, scale)] = pointer = load(scale);
        }
        if (pointer is { } p)
        {
            Input.SetCustomMouseCursor(p.Image, Input.CursorShape.Arrow, p.Hotspot);
        }
        else
        {
            Input.SetCustomMouseCursor(null);
        }
    }

    /// <summary>The system's own pointer again (menus and other screens).</summary>
    public static void Reset()
    {
        if (_shown is not null && DisplayServer.GetName() != "headless")
        {
            Input.SetCustomMouseCursor(null);
        }
        _shown = null;
    }

    private static (Image, Vector2)? Load(int icon, int scale)
    {
        GffFile resources = Session.Open("RESOURCE.GFF");
        if (!resources.TryRead("ICON", icon, out var data) || Session.Assets.Frames(data) is not [var frame, ..] || frame.Width == 0)
        {
            return null;
        }
        // The arrows, the sword and the missile point to the top left; the rest are aimed by their middle.
        return Scaled(Textures.ToImage(frame, Session.Assets.Palette(resources.Read("PAL ", 1001))), scale, centred: icon is not (>= Move and <= CantRanged));
    }

    private static (Image, Vector2) Scaled(Image image, int scale, bool centred)
    {
        scale = Math.Max(1, Math.Min(scale, 256 / Math.Max(1, Math.Max(image.GetWidth(), image.GetHeight()))));
        image.Resize(image.GetWidth() * scale, image.GetHeight() * scale, Image.Interpolation.Nearest);
        return (image, centred ? image.GetSize() / 2 : Vector2.Zero);
    }
}
