using DarkSun.Data.Gff;
using DarkSun.Data.Images;
using DarkSun.Game.Cinematics;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// <c>REQUEST(6, 200 or 300)</c>'s picture (<c>60B9:060D</c>): <c>CMAT</c> n of
/// <c>RESOURCE.GFF</c>, an ordinary <c>BMP</c>-format image, in its own
/// palette <c>CPAL</c> n. 200 is a ruined temple in the sands (GPL 177), 300
/// the Drajian commander Kraxis before the last battle (GPL 62). As DSUN.EXE
/// shows it, in retraces (70 a second, <see cref="PaletteFades"/>): the map
/// fades out to its colour 0 (<c>0980:4FEB(0, 0)</c>, after reading the DAC);
/// the screen is cleared and the picture's palette set, so it shows its colour
/// 0; the picture, drawn at the top left off screen, fades in from that
/// (<c>0980:4C09(1, 0)</c>); it is held for 1 000 retraces (<c>70E7:00B8</c>,
/// about 14 s); it fades out to colour 0 the same way; and the map is drawn
/// again at once. The map's used colours are taken as its whole palette. The
/// original waits it all out; here a key or click puts it away sooner.
/// </summary>
public partial class PictureView : RetroScreen
{
    /// <summary>How long the picture is held: 1 000 retraces.</summary>
    public const int HoldRetraces = 1000;

    public required int Number { get; init; }

    /// <summary>The map's palette, which fades out first (null: none to fade).</summary>
    public Palette? MapPalette { get; init; }

    public Action? Done { get; init; }

    private ColorRect _back = null!;
    private TextureRect _picture = null!;
    private Color _mapColour0, _colour0;
    private double _at;
    private bool _finished;

    // The phases' ends, in retraces from the start.
    private int _mapRead, _mapFaded, _pictureRead, _pictureIn, _held, _outRead, _end;
    private int _mapSteps, _pictureSteps;

    /// <summary>Picture <paramref name="number"/> and its palette are in <c>RESOURCE.GFF</c>.</summary>
    public static bool Exists(int number) =>
        Session.Install?.FindFile("RESOURCE.GFF") is not null && Session.Open("RESOURCE.GFF") is { } resources
        && resources.Contains("CMAT", number) && resources.Contains("CPAL", number);

    /// <summary>Picture <paramref name="number"/>, its palette, and its RGBA.</summary>
    public static (ImageFrame Frame, Palette Palette, byte[] Rgba) Decode(GffFile resources, int number)
    {
        ImageFrame frame = ImageDecoder.ReadFrames(resources.Read("CMAT", number).Span)[0];
        Palette palette = Palette.FromBytes(resources.Read("CPAL", number).Span);
        return (frame, palette, frame.ToRgba(palette));
    }

    /// <summary>
    /// The colours on the screen while the picture shows: those it uses, and colour 0
    /// where it doesn't cover the screen (cleared to 0 before it is drawn).
    /// </summary>
    public static IReadOnlyCollection<int> ColoursShown(ImageFrame frame)
    {
        var used = new HashSet<int>();
        for (int i = 0; i < frame.Pixels.Length; i++)
        {
            used.Add(frame.Opaque[i] ? frame.Pixels[i] : 0);
        }
        if (frame.Width < Width || frame.Height < Height)
        {
            used.Add(0);
        }
        return used;
    }

    /// <summary>The retraces of each phase, as <see cref="PictureView"/> plays them (for the check).</summary>
    public static (int MapRead, int MapSteps, int PictureRead, int PictureSteps) Phases(ImageFrame frame, Palette palette, Palette? map)
    {
        IReadOnlyCollection<int> shown = ColoursShown(frame);
        int[] mapColours = map is null ? [] : [.. Enumerable.Range(0, map.Count)];
        return (map is null ? 0 : PaletteFades.ReadRetraces(mapColours.Length),
            map is null ? 0 : PaletteFades.Steps(map, mapColours, map[0]),
            PaletteFades.ReadRetraces(shown.Count), PaletteFades.Steps(palette, shown, palette[0]));
    }

    public override void _Ready()
    {
        base._Ready();
        _back = (ColorRect)GetChild(0);
        var (frame, palette, rgba) = Decode(Session.Open("RESOURCE.GFF"), Number);
        // Its own palette, not the map's: colour cycling doesn't reach it.
        Image image = Image.CreateFromData(frame.Width, frame.Height, false, Image.Format.Rgba8, rgba);
        _picture = new TextureRect { Texture = ImageTexture.CreateFromImage(image), MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        Canvas.AddChild(_picture);

        static Color Of(Rgb c) => Color.Color8(c.R, c.G, c.B);
        _colour0 = Of(palette[0]);
        _mapColour0 = MapPalette is { } map ? Of(map[0]) : _colour0;
        (int mapRead, _mapSteps, int pictureRead, _pictureSteps) = Phases(frame, palette, MapPalette);
        _mapRead = mapRead;
        _mapFaded = _mapRead + _mapSteps;
        _pictureRead = _mapFaded + pictureRead;
        _pictureIn = _pictureRead + _pictureSteps;
        _held = _pictureIn + HoldRetraces;
        _outRead = _held + pictureRead;
        _end = _outRead + _pictureSteps;
        Show(0);
    }

    public override void _Process(double delta)
    {
        _at += delta * PaletteFades.RetracesPerSecond;
        if (_at >= _end)
        {
            Finish();
            return;
        }
        Show((int)_at);
    }

    /// <summary>The screen <paramref name="retrace"/> retraces in. A fade's step k (from 1) comes at its k-th retrace.</summary>
    private void Show(int retrace)
    {
        static float Part(int done, int steps) => steps == 0 ? 1 : Math.Clamp(done / (float)steps, 0, 1);
        if (retrace < _mapFaded)
        {
            // The map shows through, then fades to its colour 0.
            _back.Color = _mapColour0 with { A = retrace < _mapRead ? 0 : Part(retrace - _mapRead + 1, _mapSteps) };
            _picture.Visible = false;
            return;
        }
        _back.Color = _colour0;
        _picture.Visible = retrace >= _pictureRead;
        float shown = retrace < _pictureRead ? 0
            : retrace < _pictureIn ? Part(retrace - _pictureRead + 1, _pictureSteps)
            : retrace < _outRead ? 1
            : 1 - Part(retrace - _outRead + 1, _pictureSteps);
        _picture.Modulate = Colors.White with { A = shown };
    }

    public override void _Input(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false, Unicode: not '?' } or InputEventMouseButton { Pressed: true })
        {
            GetViewport().SetInputAsHandled();
            Finish();
        }
    }

    private void Finish()
    {
        if (_finished)
        {
            return;
        }
        _finished = true;
        QueueFree();
        Done?.Invoke();
    }
}
