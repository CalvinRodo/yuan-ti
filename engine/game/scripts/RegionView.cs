using DarkSun.Data.Animation;
using DarkSun.Data.Gff;
using DarkSun.Data.Images;
using DarkSun.Data.Regions;
using DarkSun.Game.Animation;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// Shows a whole region: the ground as one texture, then objects and walls
/// as sprites in the original draw order. Objects with an <c>SCMD</c> script
/// animate on a 96 Hz tick, as in DSUN.EXE. Scroll with the arrow keys, WASD
/// or by dragging; zoom with the mouse wheel or +/-; PgUp/PgDn change region;
/// F1 shows the <c>GMAP</c> flags. Run with <c>-- --check-regions</c> to load
/// every region and quit.
/// </summary>
public partial class RegionView : Node2D
{
    private const float PanSpeed = 400f;
    private static readonly float[] ZoomLevels = [0.25f, 0.5f, 1f, 2f, 3f, 4f];

    /// <summary>Region shown when the view opens; kept across visits.</summary>
    private static int _regionIndex;

    private readonly Dictionary<ImageFrame, Texture2D> _textures = [];
    private readonly List<(Sprite2D Sprite, RegionObject Object, ScmdAnimator Animator)> _animated = [];
    private Palette _palette = null!;
    private double _clock;
    private Node2D _map = null!;
    private MapFlagsOverlay _overlay = null!;
    private Camera2D _camera = null!;
    private Label _hud = null!;
    private int _zoomLevel = Array.IndexOf(ZoomLevels, 1f);
    private Vector2 _mapSize;

    public override void _Ready()
    {
        Controls.Register();
        _map = new Node2D();
        AddChild(_map);
        _overlay = new MapFlagsOverlay { Visible = false };
        AddChild(_overlay);
        _camera = new Camera2D();
        AddChild(_camera);
        AddChild(BuildHud());

        if (OS.GetCmdlineUserArgs().Contains("--check-regions"))
        {
            CheckAllRegions();
            return;
        }
        ShowRegion(_regionIndex);
    }

    private CanvasLayer BuildHud()
    {
        var layer = new CanvasLayer();
        var panel = new PanelContainer { Position = new Vector2(4, 4) };
        _hud = new Label { LabelSettings = new LabelSettings { FontSize = 10 } };
        panel.AddChild(_hud);
        layer.AddChild(panel);
        return layer;
    }

    private void ShowRegion(int index)
    {
        IReadOnlyList<string> files = Session.Install!.RegionFiles;
        _regionIndex = (index % files.Count + files.Count) % files.Count;
        string file = files[_regionIndex];
        Region region = LoadRegion(file);

        _hud.Text = $"{file} · region {region.Number} · {region.Objects.Count} objects ({_animated.Count} animated), {region.Walls.Count} walls\n"
            + "Arrows/WASD or drag: scroll · Wheel or +/-: zoom · PgUp/PgDn: region · F1: blocked cells · Esc: back";
        _camera.Position = _mapSize / 2;
        ClampCamera();
    }

    /// <summary>Replace the map's nodes with <paramref name="file"/>'s region.</summary>
    private Region LoadRegion(string file)
    {
        foreach (Node child in _map.GetChildren())
        {
            child.QueueFree();
        }
        _textures.Clear();
        _animated.Clear();
        _clock = 0;

        Region region = Region.Load(Session.Open(file), [Session.Open("SEGOBJEX.GFF"), Session.Open("GPLDATA.GFF")]);
        Palette palette = region.LoadPalette(Session.Open("GPLDATA.GFF"), Session.Open("RESOURCE.GFF"));
        _palette = palette;
        _mapSize = new Vector2(region.Width, region.Height) * Region.TileSize;

        byte[] ground = RegionRenderer.RenderRgb(region, palette, RegionLayers.Ground);
        Image groundImage = Image.CreateFromData((int)_mapSize.X, (int)_mapSize.Y, false, Image.Format.Rgb8, ground);
        _map.AddChild(new Sprite2D { Texture = ImageTexture.CreateFromImage(groundImage), Centered = false, Name = "Ground" });

        // Same order as the software renderer: objects in ETAB order, then walls.
        var scripts = new GffSet([Session.Open("SEGOBJEX.GFF"), Session.Open("GPLDATA.GFF")]);
        foreach (RegionObject obj in region.Objects)
        {
            Sprite2D? sprite = AddSprite(new PlacedSprite(obj.X, obj.Y, obj.Frame, obj.Mirrored));
            if (sprite is not null && obj.Scmd > 0 && scripts.TryRead("SCMD", obj.Scmd, out var data))
            {
                _animated.Add((sprite, obj, new ScmdAnimator(Scmd.Parse(data.Span), obj.Frames.Count, mirrored: obj.Mirrored)));
            }
        }
        foreach (PlacedSprite wall in region.Walls)
        {
            AddSprite(wall);
        }
        _overlay.Show(region);
        Animate();
        return region;
    }

    private Sprite2D? AddSprite(PlacedSprite placed)
    {
        if (placed.Frame.Width == 0 || placed.Frame.Height == 0)
        {
            return null;
        }
        var sprite = new Sprite2D
        {
            Texture = TextureFor(placed.Frame),
            Centered = false,
            FlipH = placed.Mirrored,
            Position = new Vector2(placed.X, placed.Y),
        };
        _map.AddChild(sprite);
        return sprite;
    }

    private Texture2D TextureFor(ImageFrame frame)
    {
        if (!_textures.TryGetValue(frame, out Texture2D? texture))
        {
            texture = Textures.ToTexture(frame, _palette);
            _textures[frame] = texture;
        }
        return texture;
    }

    /// <summary>Step every animated object to the current tick.</summary>
    private void Animate()
    {
        long now = (long)(_clock * ScmdAnimator.TicksPerSecond);
        foreach (var (sprite, obj, animator) in _animated)
        {
            if (!animator.Update(now))
            {
                continue;
            }
            ImageFrame frame = obj.Frames[animator.Frame];
            sprite.Texture = TextureFor(frame);
            sprite.Visible = frame.Width > 0 && frame.Height > 0;
            sprite.FlipH = animator.Mirrored;
            sprite.Position = new Vector2(obj.X + animator.OffsetX, obj.Y + animator.OffsetY);
            _ = animator.TakeSounds().Count(); // sound effects are not played yet
        }
    }

    private void CheckAllRegions()
    {
        foreach (string file in Session.Install!.RegionFiles)
        {
            Region region = LoadRegion(file);
            for (int tick = 0; tick < 10 * ScmdAnimator.TicksPerSecond; tick++)
            {
                _clock = (double)tick / ScmdAnimator.TicksPerSecond;
                Animate();
            }
            GD.Print($"{file}: region {region.Number}, {_mapSize.X}x{_mapSize.Y}, {_textures.Count} sprite textures, "
                + $"{region.Objects.Count} objects ({_animated.Count} animated), {region.Walls.Count} walls");
        }
        GetTree().Quit();
    }

    public override void _Process(double delta)
    {
        _clock += delta;
        Animate();

        Vector2 direction = Input.GetVector(Controls.PanLeft, Controls.PanRight, Controls.PanUp, Controls.PanDown);
        if (direction != Vector2.Zero)
        {
            _camera.Position += direction * PanSpeed * (float)delta / _camera.Zoom.X;
            ClampCamera();
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e.IsActionPressed(Controls.Back))
        {
            GetTree().ChangeSceneToFile("res://scenes/main.tscn");
        }
        else if (e.IsActionPressed(Controls.ToggleMapFlags))
        {
            _overlay.Visible = !_overlay.Visible;
        }
        else if (e.IsActionPressed(Controls.NextRegion))
        {
            ShowRegion(_regionIndex + 1);
        }
        else if (e.IsActionPressed(Controls.PreviousRegion))
        {
            ShowRegion(_regionIndex - 1);
        }
        else if (e.IsActionPressed(Controls.ZoomIn) || e is InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true })
        {
            Zoom(+1);
        }
        else if (e.IsActionPressed(Controls.ZoomOut) || e is InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true })
        {
            Zoom(-1);
        }
        else if (e is InputEventMouseMotion motion && (motion.ButtonMask & (MouseButtonMask.Left | MouseButtonMask.Middle)) != 0)
        {
            _camera.Position -= motion.Relative / _camera.Zoom.X;
            ClampCamera();
        }
    }

    private void Zoom(int step)
    {
        _zoomLevel = Math.Clamp(_zoomLevel + step, 0, ZoomLevels.Length - 1);
        _camera.Zoom = Vector2.One * ZoomLevels[_zoomLevel];
        ClampCamera();
    }

    /// <summary>Keep the view on the map; centre the map when it is smaller than the view.</summary>
    private void ClampCamera()
    {
        Vector2 half = GetViewportRect().Size / _camera.Zoom / 2;
        _camera.Position = new Vector2(
            half.X * 2 >= _mapSize.X ? _mapSize.X / 2 : Math.Clamp(_camera.Position.X, half.X, _mapSize.X - half.X),
            half.Y * 2 >= _mapSize.Y ? _mapSize.Y / 2 : Math.Clamp(_camera.Position.Y, half.Y, _mapSize.Y - half.Y));
    }
}
