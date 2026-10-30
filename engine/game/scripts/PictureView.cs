using DarkSun.Data.Gff;
using DarkSun.Data.Images;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// <c>REQUEST(6, 200 or 300)</c>'s picture (<c>60B9:060D</c>): <c>CMAT</c> n of
/// <c>RESOURCE.GFF</c>, an ordinary <c>BMP</c>-format image, in its own
/// palette <c>CPAL</c> n, drawn at the top left (<c>1A0A:3180</c>) and held for
/// 1 000 vertical retraces (<c>70E7:00B8</c>; 70 a second in mode 13h), about
/// 14 seconds, before the map comes back. 200 is a ruined temple in the sands
/// (GPL 177), 300 the Drajian commander Kraxis before the last battle (GPL 62).
/// The original waits it out; here a key or click puts it away sooner.
/// </summary>
public partial class PictureView : RetroScreen
{
    /// <summary>How long it stays: 1 000 retraces at 70 Hz.</summary>
    public const double Seconds = 1000 / 70.0;

    public required int Number { get; init; }

    public Action? Done { get; init; }

    private double _left = Seconds;
    private bool _finished;

    /// <summary>Picture <paramref name="number"/> and its palette are in <c>RESOURCE.GFF</c>.</summary>
    public static bool Exists(int number) =>
        Session.Install?.FindFile("RESOURCE.GFF") is not null && Session.Open("RESOURCE.GFF") is { } resources
        && resources.Contains("CMAT", number) && resources.Contains("CPAL", number);

    /// <summary>Picture <paramref name="number"/> in its palette, as RGBA, with its size.</summary>
    public static (int Width, int Height, byte[] Rgba) Decode(GffFile resources, int number)
    {
        ImageFrame frame = ImageDecoder.ReadFrames(resources.Read("CMAT", number).Span)[0];
        return (frame.Width, frame.Height, frame.ToRgba(Palette.FromBytes(resources.Read("CPAL", number).Span)));
    }

    public override void _Ready()
    {
        base._Ready();
        var (width, height, rgba) = Decode(Session.Open("RESOURCE.GFF"), Number);
        // Its own palette, not the map's: colour cycling doesn't reach it.
        Image image = Image.CreateFromData(width, height, false, Image.Format.Rgba8, rgba);
        Canvas.AddChild(new TextureRect { Texture = ImageTexture.CreateFromImage(image), MouseFilter = MouseFilterEnum.Ignore });
    }

    public override void _Process(double delta)
    {
        _left -= delta;
        if (_left <= 0)
        {
            Finish();
        }
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
