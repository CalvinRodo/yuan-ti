using DarkSun.Data.Animation;
using DarkSun.Data.Gff;
using DarkSun.Data.Images;
using DarkSun.Data.Regions;
using DarkSun.Game.Animation;
using DarkSun.Game.Movement;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// Shows a whole region with a character to walk around. The ground is one
/// texture; objects, walls and the character are sprites depth-sorted by their
/// bottom edge, as DSUN.EXE draws them row by row. Objects with an
/// <c>SCMD</c> script animate on a 96 Hz tick.
/// Click to walk; Tab changes the character; arrows, WASD or right-drag
/// scroll; wheel or +/- zoom; PgUp/PgDn change region; F1 shows the
/// <c>GMAP</c> flags. Run with <c>-- --check-regions</c> to load every region,
/// animate it and walk a character in it, then quit.
/// </summary>
public partial class RegionView : Node2D
{
    private const float PanSpeed = 400f;
    private static readonly float[] ZoomLevels = [0.25f, 0.5f, 1f, 2f, 3f, 4f];

    /// <summary>Region and character shown when the view opens; kept across visits.</summary>
    private static int _regionIndex;
    private static int _characterIndex;

    private readonly Dictionary<ImageFrame, Texture2D> _textures = [];
    private readonly List<(Sprite2D Sprite, RegionObject Object, ScmdAnimator Animator)> _animated = [];
    private List<CharacterSprite>? _characters;
    private Region _region = null!;
    private Palette _palette = null!;
    private double _clock;
    private Node2D _ground = null!;
    private Node2D _sorted = null!;
    private MapFlagsOverlay _overlay = null!;
    private Camera2D _camera = null!;
    private Label _hud = null!;
    private Walker? _walker;
    private CharacterSprite? _character;
    private Sprite2D? _characterSprite;
    private bool _follow = true;
    private int _zoomLevel = Array.IndexOf(ZoomLevels, 1f);
    private Vector2 _mapSize;

    private long Now => (long)(_clock * ScmdAnimator.TicksPerSecond);

    public override void _Ready()
    {
        Controls.Register();
        _ground = new Node2D();
        AddChild(_ground);
        _sorted = new Node2D { YSortEnabled = true };
        AddChild(_sorted);
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
        LoadRegion(files[_regionIndex]);
        PlaceCharacter(NearestFreeCell(new Cell(_region.Width / 2, _region.Height / 2)));
        UpdateHud();
        _follow = true;
        FollowCharacter();
    }

    private void UpdateHud()
    {
        string character = _character is null ? "none" : $"OJFF {_character.Ojff}";
        _hud.Text = $"{Session.Install!.RegionFiles[_regionIndex]} · region {_region.Number} · "
            + $"{_region.Objects.Count} objects ({_animated.Count} animated) · character {character}\n"
            + "Click: walk · Tab: character · Arrows/WASD or right-drag: scroll · Wheel or +/-: zoom · "
            + "PgUp/PgDn: region · F1: blocked cells · Esc: back";
    }

    /// <summary>Replace the map's nodes with <paramref name="file"/>'s region.</summary>
    private void LoadRegion(string file)
    {
        foreach (Node child in _ground.GetChildren().Concat(_sorted.GetChildren()))
        {
            child.QueueFree();
        }
        _textures.Clear();
        _animated.Clear();
        _characterSprite = null;
        _walker = null;
        _clock = 0;

        _region = Region.Load(Session.Open(file), [Session.Open("SEGOBJEX.GFF"), Session.Open("GPLDATA.GFF")]);
        _palette = _region.LoadPalette(Session.Open("GPLDATA.GFF"), Session.Open("RESOURCE.GFF"));
        _mapSize = new Vector2(_region.Width, _region.Height) * Region.TileSize;

        byte[] ground = RegionRenderer.RenderRgb(_region, _palette, RegionLayers.Ground);
        Image groundImage = Image.CreateFromData((int)_mapSize.X, (int)_mapSize.Y, false, Image.Format.Rgb8, ground);
        _ground.AddChild(new Sprite2D { Texture = ImageTexture.CreateFromImage(groundImage), Centered = false, Name = "Ground" });

        // Each sprite's node sits at the point it is sorted by: an object's ETAB
        // position, a wall's cell bottom. The image hangs off it via Offset.
        var scripts = new GffSet([Session.Open("SEGOBJEX.GFF"), Session.Open("GPLDATA.GFF")]);
        foreach (RegionObject obj in _region.Objects)
        {
            Sprite2D? sprite = AddSprite(obj.Frame, new Vector2(obj.EtabX, obj.EtabY), new Vector2(obj.X, obj.Y), obj.Mirrored);
            if (sprite is not null && obj.Scmd > 0 && scripts.TryRead("SCMD", obj.Scmd, out var data))
            {
                _animated.Add((sprite, obj, new ScmdAnimator(Scmd.Parse(data.Span), obj.Frames.Count, mirrored: obj.Mirrored)));
            }
        }
        foreach (PlacedSprite wall in _region.Walls)
        {
            var bottom = new Vector2(wall.X + wall.Frame.Width / 2, wall.Y + wall.Frame.Height);
            AddSprite(wall.Frame, bottom, new Vector2(wall.X, wall.Y), wall.Mirrored);
        }
        _overlay.Show(_region);
        Animate();
    }

    private Sprite2D? AddSprite(ImageFrame frame, Vector2 sortPoint, Vector2 topLeft, bool mirrored)
    {
        if (frame.Width == 0 || frame.Height == 0)
        {
            return null;
        }
        var sprite = new Sprite2D
        {
            Texture = TextureFor(frame),
            Centered = false,
            FlipH = mirrored,
            Position = sortPoint,
            Offset = topLeft - sortPoint,
        };
        _sorted.AddChild(sprite);
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

    // -- character ------------------------------------------------------------

    /// <summary>
    /// Object definitions that can walk: an <c>SCMD</c> with a moving walk
    /// sequence in every direction. One per <c>BMP </c>, in <c>OJFF</c> order.
    /// </summary>
    private List<CharacterSprite> Characters()
    {
        if (_characters is not null)
        {
            return _characters;
        }
        GffFile objects = Session.Open("SEGOBJEX.GFF");
        var scripts = new Dictionary<int, Scmd?>();
        var seenBmps = new HashSet<int>();
        _characters = [];
        foreach (ResourceDescriptor r in objects.DescribeResources().Where(r => r.Tag == "OJFF").OrderBy(r => r.Number))
        {
            ObjectDefinition def = ObjectDefinition.Parse(objects.Read(r.Tag, r.Number).Span);
            if (def.Scmd <= 0 || seenBmps.Contains(def.Bmp))
            {
                continue;
            }
            if (!scripts.TryGetValue(def.Scmd, out Scmd? script))
            {
                script = objects.TryRead("SCMD", def.Scmd, out var data) ? Scmd.Parse(data.Span) : null;
                scripts[def.Scmd] = script;
            }
            if (script is { CanWalk: true } && objects.TryRead("BMP ", def.Bmp, out var image))
            {
                seenBmps.Add(def.Bmp);
                _characters.Add(new CharacterSprite(r.Number, script, ImageDecoder.ReadFrames(image.Span)));
            }
        }
        return _characters;
    }

    private void PlaceCharacter(Cell cell)
    {
        List<CharacterSprite> characters = Characters();
        if (characters.Count == 0)
        {
            return;
        }
        _characterIndex = (_characterIndex % characters.Count + characters.Count) % characters.Count;
        _character = characters[_characterIndex];
        Direction facing = _walker?.Facing ?? Direction.South;
        _walker = new Walker(_character.Script, _character.Sizes, cell, facing);
        _characterSprite ??= new Sprite2D { Centered = false };
        if (_characterSprite.GetParent() is null)
        {
            _sorted.AddChild(_characterSprite);
        }
        _walker.Update(Now, _region.IsBlocked);
        DrawCharacter();
    }

    private void DrawCharacter()
    {
        if (_walker is null || _character is null || _characterSprite is null)
        {
            return;
        }
        ImageFrame frame = _character.Frames[_walker.Animator.Frame];
        _characterSprite.Texture = TextureFor(frame);
        _characterSprite.FlipH = _walker.Animator.Mirrored;
        _characterSprite.Position = new Vector2(_walker.Feet.X, _walker.Feet.Y);
        _characterSprite.Offset = new Vector2(-_walker.Animator.HotX, -_walker.Animator.HotY);
    }

    private void WalkTo(Cell goal)
    {
        if (_walker is null)
        {
            return;
        }
        // Plan from the cell the current step ends in.
        Cell from = _walker.Cell;
        if (_walker.IsWalking)
        {
            (int fx, int fy) = _walker.Feet;
            from = new Cell(fx / Region.TileSize, (fy - 1) / Region.TileSize);
        }
        _walker.Follow(Pathfinder.FindPath(_region.IsBlocked, from, goal));
        _follow = true;
    }

    /// <summary>The free cell nearest to <paramref name="start"/>, searching outwards.</summary>
    private Cell NearestFreeCell(Cell start)
    {
        for (int radius = 0; radius < Math.Max(_region.Width, _region.Height); radius++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == radius && !_region.IsBlocked(start.X + dx, start.Y + dy))
                    {
                        return new Cell(start.X + dx, start.Y + dy);
                    }
                }
            }
        }
        return start;
    }

    // -- per frame --------------------------------------------------------------

    /// <summary>Step every animated object and the character to the current tick.</summary>
    private void Animate()
    {
        long now = Now;
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
            sprite.Position = new Vector2(obj.EtabX + animator.OffsetX, obj.EtabY + animator.OffsetY);
            _ = animator.TakeSounds().Count(); // sound effects are not played yet
        }
        if (_walker is not null && _walker.Update(now, _region.IsBlocked))
        {
            DrawCharacter();
        }
    }

    private void FollowCharacter()
    {
        if (_follow && _walker is not null)
        {
            _camera.Position = new Vector2(_walker.Feet.X, _walker.Feet.Y);
        }
        ClampCamera();
    }

    public override void _Process(double delta)
    {
        _clock += delta;
        Animate();

        Vector2 direction = Input.GetVector(Controls.PanLeft, Controls.PanRight, Controls.PanUp, Controls.PanDown);
        if (direction != Vector2.Zero)
        {
            _follow = false;
            _camera.Position += direction * PanSpeed * (float)delta / _camera.Zoom.X;
        }
        FollowCharacter();
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
        else if (e.IsActionPressed(Controls.NextCharacter) && _walker is not null)
        {
            _characterIndex++;
            PlaceCharacter(_walker.Cell);
            UpdateHud();
        }
        else if (e.IsActionPressed(Controls.ZoomIn) || e is InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true })
        {
            Zoom(+1);
        }
        else if (e.IsActionPressed(Controls.ZoomOut) || e is InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true })
        {
            Zoom(-1);
        }
        else if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
        {
            Vector2 at = GetGlobalMousePosition();
            WalkTo(new Cell((int)Math.Floor(at.X / Region.TileSize), (int)Math.Floor(at.Y / Region.TileSize)));
        }
        else if (e is InputEventMouseMotion motion && (motion.ButtonMask & (MouseButtonMask.Right | MouseButtonMask.Middle)) != 0)
        {
            _follow = false;
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

    // -- headless check ----------------------------------------------------------

    /// <summary>
    /// For every region: load it, then walk the character towards a cell 20
    /// columns away for 10 simulated seconds, and report how far it got.
    /// </summary>
    private void CheckAllRegions()
    {
        foreach (string file in Session.Install!.RegionFiles)
        {
            LoadRegion(file);
            Cell start = NearestFreeCell(new Cell(_region.Width / 2, _region.Height / 2));
            PlaceCharacter(start);
            WalkTo(new Cell(start.X + 20, start.Y));
            int pathLength = _walker!.IsWalking ? Pathfinder.FindPath(_region.IsBlocked, start, new Cell(start.X + 20, start.Y)).Count : 0;
            for (int tick = 0; tick < 10 * ScmdAnimator.TicksPerSecond; tick++)
            {
                _clock = (double)tick / ScmdAnimator.TicksPerSecond;
                Animate();
            }
            GD.Print($"{file}: region {_region.Number}, {_region.Objects.Count} objects ({_animated.Count} animated), "
                + $"{_region.Walls.Count} walls; character OJFF {_character?.Ojff} walked {start} -> {_walker.Cell} "
                + $"({pathLength}-cell path, {(_walker.IsWalking ? "still walking" : "arrived")})");
        }
        GD.Print($"{Characters().Count} walking character sprites");
        GetTree().Quit();
    }
}

/// <summary>An object definition usable as a walking character.</summary>
public sealed class CharacterSprite(int ojff, Scmd script, IReadOnlyList<ImageFrame> frames)
{
    public int Ojff { get; } = ojff;
    public Scmd Script { get; } = script;
    public IReadOnlyList<ImageFrame> Frames { get; } = frames;
    public IReadOnlyList<(int Width, int Height)> Sizes { get; } = [.. frames.Select(f => (f.Width, f.Height))];
}
