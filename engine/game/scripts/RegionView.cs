using DarkSun.Data.Images;
using DarkSun.Data.Regions;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// Shows a whole region: the ground as one texture, then objects and walls
/// as sprites in the original draw order. Scroll with the arrow keys, WASD or
/// by dragging; zoom with the mouse wheel or +/-; PgUp/PgDn change region.
/// Run with <c>-- --check-regions</c> to load every region and quit.
/// </summary>
public partial class RegionView : Node2D
{
    private const float PanSpeed = 400f;
    private static readonly float[] ZoomLevels = [0.25f, 0.5f, 1f, 2f, 3f, 4f];

    /// <summary>Region shown when the view opens; kept across visits.</summary>
    private static int _regionIndex;

    private readonly Dictionary<ImageFrame, Texture2D> _textures = [];
    private Node2D _map = null!;
    private Camera2D _camera = null!;
    private Label _hud = null!;
    private int _zoomLevel = Array.IndexOf(ZoomLevels, 1f);
    private Vector2 _mapSize;

    public override void _Ready()
    {
        Controls.Register();
        _map = new Node2D();
        AddChild(_map);
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

        _hud.Text = $"{file} · region {region.Number} · {region.Objects.Count} objects, {region.Walls.Count} walls\n"
            + "Arrows/WASD or drag: scroll · Wheel or +/-: zoom · PgUp/PgDn: region · Esc: back";
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

        Region region = Region.Load(Session.Open(file), [Session.Open("SEGOBJEX.GFF"), Session.Open("GPLDATA.GFF")]);
        Palette palette = region.LoadPalette(Session.Open("GPLDATA.GFF"), Session.Open("RESOURCE.GFF"));
        _mapSize = new Vector2(region.Width, region.Height) * Region.TileSize;

        byte[] ground = RegionRenderer.RenderRgb(region, palette, RegionLayers.Ground);
        Image groundImage = Image.CreateFromData((int)_mapSize.X, (int)_mapSize.Y, false, Image.Format.Rgb8, ground);
        _map.AddChild(new Sprite2D { Texture = ImageTexture.CreateFromImage(groundImage), Centered = false, Name = "Ground" });

        // Same order as the software renderer: objects in ETAB order, then walls.
        foreach (PlacedSprite sprite in region.Objects.Concat(region.Walls))
        {
            if (sprite.Frame.Width == 0 || sprite.Frame.Height == 0)
            {
                continue;
            }
            if (!_textures.TryGetValue(sprite.Frame, out Texture2D? texture))
            {
                texture = Textures.ToTexture(sprite.Frame, palette);
                _textures[sprite.Frame] = texture;
            }
            _map.AddChild(new Sprite2D
            {
                Texture = texture,
                Centered = false,
                FlipH = sprite.Mirrored,
                Position = new Vector2(sprite.X, sprite.Y),
            });
        }
        return region;
    }

    private void CheckAllRegions()
    {
        foreach (string file in Session.Install!.RegionFiles)
        {
            Region region = LoadRegion(file);
            GD.Print($"{file}: region {region.Number}, {_mapSize.X}x{_mapSize.Y}, {_textures.Count} sprite textures, "
                + $"{region.Objects.Count} objects, {region.Walls.Count} walls");
        }
        GetTree().Quit();
    }

    public override void _Process(double delta)
    {
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
