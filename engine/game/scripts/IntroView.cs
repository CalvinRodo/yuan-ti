using DarkSun.Data.Gff;
using DarkSun.Game.Cinematics;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// Plays the opening cinematic (<c>CINE.GFF</c> scripts 1–8), then opens the
/// main menu. Any key or mouse button skips it, as in DSUN.EXE. The music
/// (<c>CINE.GFF</c> <c>GSEQ</c> 26–29) plays through <see cref="Music"/>; the
/// cinematic keeps the music's timing either way.
/// Run with <c>-- --check-intro</c> to play it through without a screen and
/// print when each script starts.
/// </summary>
public partial class IntroView : RetroScreen
{
    public const string ScenePath = "res://scenes/intro.tscn";

    /// <summary>Which cinematic to play next time the scene opens (see <see cref="CinematicPlayer.Cinematics"/>).</summary>
    public static int Cinematic { get; set; }

    /// <summary>Where to go when it ends.</summary>
    public static string NextScene { get; set; } = MainMenu.ScenePath;

    /// <summary>
    /// Played over another screen (a script's <c>REQUEST(6)</c>) instead of as its own scene:
    /// this cinematic, and <see cref="Done"/> when it ends instead of changing scene.
    /// </summary>
    public int? Overlay { get; init; }

    public Action? Done { get; init; }

    private CinematicPlayer? _player;
    private TextureRect _picture = null!;
    private Image _image = null!;
    private ImageTexture _texture = null!;
    private readonly byte[] _rgb = new byte[CinematicPlayer.Width * CinematicPlayer.Height * 3];
    private int _shownVersion = -1;

    public override void _Ready()
    {
        base._Ready();
        _image = Image.CreateEmpty(CinematicPlayer.Width, CinematicPlayer.Height, false, Image.Format.Rgb8);
        _texture = ImageTexture.CreateFromImage(_image);
        _picture = new TextureRect { Texture = _texture, MouseFilter = MouseFilterEnum.Ignore };
        Canvas.AddChild(_picture);

        GffFile? cine = Session.Install?.FindFile("CINE.GFF") is not null ? Session.Open("CINE.GFF") : null;
        if (cine is null)
        {
            Finish();
            return;
        }
        _player = new CinematicPlayer(cine);
        _player.MusicStarted += n => Music.Instance?.Play("CINE.GFF", n);
        _player.MusicFade += (seconds, fadeIn) =>
        {
            if (fadeIn)
            {
                Music.Instance?.FadeIn(seconds);
            }
            else
            {
                Music.Instance?.FadeOut(seconds);
            }
        };
        _player.PlayCinematic(Overlay ?? Cinematic);

        if (OS.GetCmdlineUserArgs().Contains("--check-intro"))
        {
            CheckIntro(_player);
        }
    }

    public override void _Process(double delta)
    {
        if (_player is null)
        {
            return;
        }
        _player.Advance(delta);
        if (_player.Version != _shownVersion)
        {
            _shownVersion = _player.Version;
            Present(_player);
        }
        if (_player.Finished)
        {
            _player = null;
            Finish();
        }
    }

    public override void _Input(InputEvent e)
    {
        // Any key skips, but ? (the key help).
        if (_player is not null && e is InputEventKey { Pressed: true, Echo: false, Unicode: not '?' } or InputEventMouseButton { Pressed: true })
        {
            _player.Skip();
            GetViewport().SetInputAsHandled();
        }
    }

    private void Present(CinematicPlayer player)
    {
        byte[] screen = player.Screen, palette = player.Palette;
        for (int i = 0; i < screen.Length; i++)
        {
            int c = screen[i] * 3;
            for (int k = 0; k < 3; k++)
            {
                int v = palette[c + k];
                _rgb[i * 3 + k] = (byte)(v << 2 | v >> 4); // 6-bit VGA value to 8 bits
            }
        }
        _image.SetData(CinematicPlayer.Width, CinematicPlayer.Height, false, Image.Format.Rgb8, _rgb);
        _texture.Update(_image);
    }

    private void Finish()
    {
        if (Overlay is not null)
        {
            QueueFree();
            Done?.Invoke();
            return;
        }
        string next = NextScene;
        Cinematic = 0;
        NextScene = MainMenu.ScenePath;
        GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, next);
    }

    /// <summary>Play the whole cinematic at once, printing each script's start time, then quit.</summary>
    private void CheckIntro(CinematicPlayer player)
    {
        int script = -1;
        int changes = 0, version = player.Version;
        while (!player.Finished && player.Seconds < 600)
        {
            player.Step();
            if (player.CurrentScript != script && player.CurrentScript != 0)
            {
                script = player.CurrentScript;
                GD.Print($"{player.Seconds,7:F2}s  ACF {script}");
            }
            if (player.Version != version)
            {
                version = player.Version;
                changes++;
            }
        }
        string unsupported = player.UnsupportedOps.Count == 0 ? "none"
            : string.Join(", ", player.UnsupportedOps.Select(o => $"0x{o:x2}"));
        GD.Print($"cinematic {Cinematic}: {player.Seconds:F1} s, {changes} picture changes; unsupported ops: {unsupported}");
        GetTree().Quit(player.Finished ? 0 : 1);
    }
}
