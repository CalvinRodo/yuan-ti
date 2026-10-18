using DarkSun.Data.Animation;
using DarkSun.Data.Gff;
using DarkSun.Data.Images;
using DarkSun.Data.Regions;
using DarkSun.Game.Animation;
using DarkSun.Game.Movement;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// Shows a whole region with a party to walk around. The ground is one
/// texture; objects, walls and characters are sprites depth-sorted by their
/// bottom edge, as DSUN.EXE draws them row by row. Objects with an
/// <c>SCMD</c> script animate on a 96 Hz tick. Characters hold their
/// footprint's cells, so they never overlap.
/// Click: the selected character walks there; Shift+click: the whole party;
/// Tab: select the next character; N: add one; C: change its sprite; arrows,
/// WASD or right-drag scroll; wheel or +/- zoom; PgUp/PgDn change region; F1
/// shows the <c>GMAP</c> flags and occupied cells. Run with
/// <c>-- --check-regions</c> to load every region, animate it and walk the
/// party in it, then quit.
/// </summary>
public partial class RegionView : Node2D
{
    private const float PanSpeed = 400f;
    private static readonly float[] ZoomLevels = [0.25f, 0.5f, 1f, 2f, 3f, 4f];

    /// <summary>The original game's party size.</summary>
    private const int PartySize = 4;

    /// <summary>Region shown when the view opens; kept across visits.</summary>
    private static int _regionIndex;

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
    private readonly List<PartyMember> _party = [];
    private Occupancy _occupancy = new();
    private SelectionMarker _marker = null!;
    private int _selected;
    private int _nextId = 1;
    private int _nextSprite;
    private bool _follow = true;
    private int _zoomLevel = Array.IndexOf(ZoomLevels, 1f);
    private Vector2 _mapSize;

    private long Now => (long)(_clock * ScmdAnimator.TicksPerSecond);

    public override void _Ready()
    {
        Controls.Register();
        _ground = new Node2D();
        AddChild(_ground);
        _marker = new SelectionMarker();
        AddChild(_marker);
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
        PlaceParty();
        UpdateHud();
        _follow = true;
        FollowCharacter();
    }

    private void UpdateHud()
    {
        string selected = Selected is { } m
            ? $"character {_selected + 1}/{_party.Count}: OJFF {m.Sprite.Ojff}, footprint {2 * m.Walker.Footprint.HalfWidth + 1}×{m.Walker.Footprint.Height + 1}"
            : "no characters";
        _hud.Text = $"{Session.Install!.RegionFiles[_regionIndex]} · region {_region.Number} · "
            + $"{_region.Objects.Count} objects ({_animated.Count} animated) · {selected}\n"
            + "Click: walk · Shift+click: party · Tab: select · N: add · C: sprite · "
            + "Arrows/WASD or right-drag: scroll · Wheel or +/-: zoom · PgUp/PgDn: region · F1: map flags · Esc: back";
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
        _party.Clear();
        _occupancy = new Occupancy();
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
        _overlay.Show(_region, _occupancy);
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

    private PartyMember? Selected => _party.Count > 0 ? _party[_selected] : null;

    /// <summary>Put a party of <see cref="PartySize"/> near the middle of the map.</summary>
    private void PlaceParty()
    {
        _selected = 0;
        _nextSprite = 0;
        Cell centre = new(_region.Width / 2, _region.Height / 2);
        for (int i = 0; i < PartySize; i++)
        {
            AddCharacter(_party.Count > 0 ? _party[^1].Walker.Cell : centre);
        }
    }

    /// <summary>Add a character with the next walking sprite, on the free cell nearest to <paramref name="near"/>.</summary>
    private PartyMember? AddCharacter(Cell near)
    {
        List<CharacterSprite> sprites = Characters();
        if (sprites.Count == 0)
        {
            return null;
        }
        CharacterSprite sprite = sprites[_nextSprite++ % sprites.Count];
        Footprint footprint = Footprint.EstimateFromWidth(sprite.Frames.Max(f => f.Width));
        Cell? cell = NearestCell(near, c => Fits(c, footprint));
        if (cell is null)
        {
            return null;
        }
        var member = new PartyMember(
            new Walker(_nextId++, sprite.Script, sprite.Sizes, cell.Value, footprint, _occupancy),
            sprite,
            new Sprite2D { Centered = false });
        _sorted.AddChild(member.Node);
        _party.Add(member);
        member.Walker.Update(Now, _region.IsBlocked);
        DrawMember(member);
        return member;
    }

    /// <summary>Give the selected character the next walking sprite, keeping its place if it fits.</summary>
    private void ChangeSprite()
    {
        if (Selected is not { } old)
        {
            return;
        }
        Cell cell = old.Walker.Cell;
        old.Walker.Remove();
        old.Node.QueueFree();
        _party.RemoveAt(_selected);
        if (AddCharacter(cell) is { } added)
        {
            _party.Remove(added);
            _party.Insert(_selected, added);
        }
        _selected = Math.Min(_selected, Math.Max(0, _party.Count - 1));
    }

    /// <summary>Whether a creature with <paramref name="footprint"/> fits at <paramref name="cell"/> right now.</summary>
    private bool Fits(Cell cell, Footprint footprint) =>
        footprint.Cells(cell).All(c => !_region.IsBlocked(c.X, c.Y) && _occupancy.OwnerOf(c) is null);

    private void DrawMember(PartyMember member)
    {
        Walker walker = member.Walker;
        ImageFrame frame = member.Sprite.Frames[walker.Animator.Frame];
        member.Node.Texture = TextureFor(frame);
        member.Node.FlipH = walker.Animator.Mirrored;
        member.Node.Position = new Vector2(walker.Feet.X, walker.Feet.Y);
        member.Node.Offset = new Vector2(-walker.Animator.HotX, -walker.Animator.HotY);
    }

    private void WalkTo(Cell goal, bool wholeParty)
    {
        foreach (PartyMember member in wholeParty ? _party : Selected is { } m ? [m] : [])
        {
            member.Walker.WalkTo(goal, _region.IsBlocked);
        }
        _follow = true;
    }

    /// <summary>The cell nearest to <paramref name="start"/> that satisfies <paramref name="ok"/>, searching outwards.</summary>
    private Cell? NearestCell(Cell start, Func<Cell, bool> ok)
    {
        for (int radius = 0; radius < Math.Max(_region.Width, _region.Height); radius++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    var cell = new Cell(start.X + dx, start.Y + dy);
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == radius && ok(cell))
                    {
                        return cell;
                    }
                }
            }
        }
        return null;
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
        foreach (PartyMember member in _party)
        {
            if (member.Walker.Update(now, _region.IsBlocked))
            {
                DrawMember(member);
            }
        }
        if (_overlay.Visible)
        {
            _overlay.QueueRedraw();
        }
        _marker.Follow(Selected?.Walker);
    }

    private void FollowCharacter()
    {
        if (_follow && Selected is { } member)
        {
            _camera.Position = new Vector2(member.Walker.Feet.X, member.Walker.Feet.Y);
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
        else if (e.IsActionPressed(Controls.NextCharacter) && _party.Count > 0)
        {
            _selected = (_selected + 1) % _party.Count;
            _follow = true;
            UpdateHud();
        }
        else if (e.IsActionPressed(Controls.AddCharacter))
        {
            Cell near = Selected?.Walker.Cell ?? new Cell(_region.Width / 2, _region.Height / 2);
            if (AddCharacter(near) is not null)
            {
                _selected = _party.Count - 1;
            }
            UpdateHud();
        }
        else if (e.IsActionPressed(Controls.ChangeSprite))
        {
            ChangeSprite();
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
        else if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } click)
        {
            Vector2 at = GetGlobalMousePosition();
            WalkTo(new Cell((int)Math.Floor(at.X / Region.TileSize), (int)Math.Floor(at.Y / Region.TileSize)), click.ShiftPressed);
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
    /// For every region: load it, place the party, send everyone to a cell 20
    /// columns away for 15 simulated seconds, and check after every tick that
    /// no two characters' footprints overlap.
    /// </summary>
    private void CheckAllRegions()
    {
        int failures = 0;
        foreach (string file in Session.Install!.RegionFiles)
        {
            LoadRegion(file);
            PlaceParty();
            Cell start = _party[0].Walker.Cell;
            Cell goal = new(start.X + 20, start.Y);
            WalkTo(goal, wholeParty: true);
            int overlaps = 0;
            for (int tick = 0; tick < 15 * ScmdAnimator.TicksPerSecond; tick++)
            {
                _clock = (double)tick / ScmdAnimator.TicksPerSecond;
                Animate();
                var cells = _party.SelectMany(m => m.Walker.Footprint.Cells(m.Walker.Cell)).ToList();
                overlaps += cells.Count - cells.Distinct().Count();
            }
            failures += overlaps;
            string members = string.Join(", ", _party.Select(m =>
                $"{m.Walker.Footprint.HalfWidth * 2 + 1}x{m.Walker.Footprint.Height + 1} at ({m.Walker.Cell.X},{m.Walker.Cell.Y})"));
            GD.Print($"{file}: region {_region.Number}, {_animated.Count} animated objects; party from ({start.X},{start.Y}) "
                + $"towards ({goal.X},{goal.Y}): {members}; {(_party.Any(m => m.Walker.IsWalking) ? "still walking" : "stopped")}; "
                + $"{overlaps} overlaps");
        }
        GD.Print($"{Characters().Count} walking character sprites; {failures} overlaps in total");
        GetTree().Quit(failures == 0 ? 0 : 1);
    }
}

/// <summary>A character on the map: its movement, its sprite set, and its node.</summary>
public sealed record PartyMember(Walker Walker, CharacterSprite Sprite, Sprite2D Node);

/// <summary>A ring under the selected character's feet, drawn below the sprites.</summary>
public partial class SelectionMarker : Node2D
{
    private Walker? _walker;

    public void Follow(Walker? walker)
    {
        _walker = walker;
        Visible = walker is not null;
        if (walker is not null)
        {
            Position = new Vector2(walker.Feet.X, walker.Feet.Y - 2);
            QueueRedraw();
        }
    }

    public override void _Draw()
    {
        if (_walker is null)
        {
            return;
        }
        float radius = 6 + _walker.Footprint.HalfWidth * 16;
        DrawSetTransform(Vector2.Zero, 0, new Vector2(1, 0.5f));
        DrawArc(Vector2.Zero, radius, 0, Mathf.Tau, 32, new Color(0.4f, 1f, 0.4f, 0.9f), 1.5f);
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
