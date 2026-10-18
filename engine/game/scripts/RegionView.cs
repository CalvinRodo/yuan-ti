using DarkSun.Data.Animation;
using DarkSun.Data.Gff;
using DarkSun.Data.Images;
using DarkSun.Data.Regions;
using DarkSun.Game.Animation;
using DarkSun.Game.Movement;
using DarkSun.Game.Scripting;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// Shows a whole region with a party to walk around. The ground is one
/// texture; objects, walls and characters are sprites depth-sorted by their
/// bottom edge, as DSUN.EXE draws them row by row. Objects with an
/// <c>SCMD</c> script animate on a 96 Hz tick. Characters hold their
/// footprint's cells, so they never overlap. Entering a region runs its
/// <c>MAS</c> script (after the game-start script, once); characters stepping
/// into cells fire move triggers; right-clicking an object fires its talk-to,
/// use, look or pick-up trigger. World commands are not carried out yet: they
/// are listed in the script log (F2).
/// Click: the selected character walks there; Shift+click: the whole party;
/// Right-click: interact; Tab: select the next character; N: add one; C:
/// change its sprite; arrows, WASD or middle-drag scroll; wheel or +/- zoom;
/// PgUp/PgDn change region; F1 shows the <c>GMAP</c> flags, occupied cells and
/// triggers. Run with <c>-- --check-regions</c> to load every region, run its
/// scripts, animate it and walk the party in it, then quit.
/// </summary>
public partial class RegionView : Node2D, IGplWorld
{
    private const float PanSpeed = 400f;
    private static readonly float[] ZoomLevels = [0.25f, 0.5f, 1f, 2f, 3f, 4f];

    /// <summary>The original game's party size.</summary>
    private const int PartySize = 4;

    /// <summary>Region shown when the view opens; kept across visits. −1: not yet chosen, so a new game starts.</summary>
    private static int _regionIndex = -1;

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
    private readonly List<(RegionObject Object, Sprite2D Sprite)> _objectSprites = [];
    private readonly Dictionary<Walker, Cell> _lastCells = [];
    private readonly List<string> _scriptLines = [];
    private int _scriptLogSeen;
    private Label _scriptLog = null!;
    private PanelContainer _scriptPanel = null!;
    private DialogueWindow _dialogueWindow = null!;
    private readonly Dictionary<int, ImageTexture?> _portraits = [];
    private readonly HashSet<RegionObject> _hidden = [];
    private readonly Dictionary<RegionObject, Vector2> _moved = [];
    private (int Region, int X, int Y)? _pendingRegion;
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
        Session.ScriptHost.World = this;

        if (OS.GetCmdlineUserArgs().Contains("--check-regions"))
        {
            _checking = true;
            CheckAllRegions();
            return;
        }
        if (OS.GetCmdlineUserArgs().Contains("--check-exits"))
        {
            CheckExits();
            return;
        }
        if (_regionIndex < 0)
        {
            // A new game begins where DSUN.EXE's does: region 42, the party at pixel (488, 152).
            _regionIndex = 0;
            EnterRegion(GplMachine.StartRegion,
                new Cell(GplMachine.StartX / Region.TileSize, GplMachine.StartY / Region.TileSize));
            if (OS.GetCmdlineUserArgs().Contains("--check-start"))
            {
                GD.Print($"new game: {Session.Install!.RegionFiles[_regionIndex]} (region {_region.Number}); party at "
                    + string.Join(", ", _party.Select(m => $"({m.Walker.Cell.X},{m.Walker.Cell.Y})")));
                GD.Print(string.Join('\n', _scriptLines));
                GetTree().Quit();
            }
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

        _scriptPanel = new PanelContainer { Visible = false };
        _scriptPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomWide);
        _scriptPanel.GrowVertical = Control.GrowDirection.Begin;
        _scriptLog = new Label { LabelSettings = new LabelSettings { FontSize = 9 }, AutowrapMode = TextServer.AutowrapMode.Off };
        _scriptPanel.AddChild(_scriptLog);
        layer.AddChild(_scriptPanel);

        _dialogueWindow = new DialogueWindow
        {
            Answer = answer => ReportScript("answer", Session.Scripts.Resume(answer)),
        };
        layer.AddChild(_dialogueWindow);
        return layer;
    }

    private void ShowRegion(int index, Cell? start = null)
    {
        IReadOnlyList<string> files = Session.Install!.RegionFiles;
        _regionIndex = (index % files.Count + files.Count) % files.Count;
        LoadRegion(files[_regionIndex]);
        PlaceParty(start);
        RunRegionScripts();
        UpdateHud();
        _follow = true;
        FollowCharacter();
    }

    private void UpdateHud()
    {
        string selected = Selected is { } m
            ? $"character {_selected + 1}/{_party.Count}: OJFF {m.Sprite.Ojff}, footprint {2 * m.Walker.Footprint.HalfWidth + 1}×{m.Walker.Footprint.Height + 1}"
            : "no characters";
        GplTriggers t = Session.Scripts.Triggers;
        int objectTriggers = new[] { GplTriggerList.TalkTo, GplTriggerList.Use, GplTriggerList.Look, GplTriggerList.Pickup, GplTriggerList.Attack, GplTriggerList.UseWith }
            .Sum(t.Count);
        _hud.Text = $"{Session.Install!.RegionFiles[_regionIndex]} · region {_region.Number} · "
            + $"{_region.Objects.Count} objects ({_animated.Count} animated) · {selected}\n"
            + $"Triggers: {t.Count(GplTriggerList.MoveTile)} tile, {t.Count(GplTriggerList.MoveBox)} box, {objectTriggers} object, "
            + $"{Session.Scripts.Creatures.NoOrders.Count + Session.Scripts.Creatures.Sight.Count} creature\n"
            + "Click: walk · Shift+click: party · Right-click: interact · Tab: select · N: add · C: sprite · "
            + "Arrows/WASD or middle-drag: scroll · Wheel or +/-: zoom · PgUp/PgDn: region · F1: map · F2: script log · Esc: back";
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
        _objectSprites.Clear();
        _hidden.Clear();
        _moved.Clear();
        _lastCells.Clear();
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
            if (sprite is not null)
            {
                _objectSprites.Add((obj, sprite));
            }
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
        _overlay.Show(_region, _occupancy, Session.Scripts.Triggers);
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

    /// <summary>Put a party of <see cref="PartySize"/> near <paramref name="start"/>, or the middle of the map.</summary>
    private void PlaceParty(Cell? start = null)
    {
        _selected = 0;
        _nextSprite = 0;
        Cell centre = start ?? new(_region.Width / 2, _region.Height / 2);
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
            sprite.Visible = frame.Width > 0 && frame.Height > 0 && !_hidden.Contains(obj);
            sprite.FlipH = animator.Mirrored;
            Vector2 at = _moved.TryGetValue(obj, out Vector2 m) ? m : new Vector2(obj.EtabX, obj.EtabY);
            sprite.Position = at + new Vector2(animator.OffsetX, animator.OffsetY);
            _ = animator.TakeSounds().Count(); // sound effects are not played yet
        }
        for (int i = 0; i < _party.Count; i++)
        {
            PartyMember member = _party[i];
            if (member.Walker.Update(now, _region.IsBlocked))
            {
                DrawMember(member);
            }
            // Move triggers fire when a character reaches a new cell (trigger_on_move).
            Cell cell = member.Walker.Cell;
            if (_lastCells.TryGetValue(member.Walker, out Cell last) && last != cell
                && Session.Scripts.OnMove(i, cell.X, cell.Y, i == _selected) is { } run)
            {
                ReportScript($"character {i + 1} reached ({cell.X},{cell.Y})", run);
            }
            _lastCells[member.Walker] = cell;
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
        else if (_dialogueWindow.Visible && e is InputEventMouseButton { ButtonIndex: MouseButton.Left or MouseButton.Right })
        {
            // The dialogue is modal: the map takes no clicks until it closes.
        }
        else if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } click)
        {
            Vector2 at = GetGlobalMousePosition();
            WalkTo(new Cell((int)Math.Floor(at.X / Region.TileSize), (int)Math.Floor(at.Y / Region.TileSize)), click.ShiftPressed);
        }
        else if (e is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true })
        {
            Interact(GetGlobalMousePosition());
        }
        else if (e.IsActionPressed(Controls.ToggleScriptLog))
        {
            _scriptPanel.Visible = !_scriptPanel.Visible;
        }
        else if (e is InputEventMouseMotion motion && (motion.ButtonMask & MouseButtonMask.Middle) != 0)
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

    // -- scripts ---------------------------------------------------------------------

    /// <summary>A <c>PORT</c> portrait in the region's palette (they use colours every palette shares).</summary>
    private ImageTexture? Portrait(int number)
    {
        if (!_portraits.TryGetValue(number, out ImageTexture? texture))
        {
            texture = Session.Open("GPLDATA.GFF").TryRead("PORT", number, out var data)
                && ImageDecoder.ReadFrames(data.Span) is [var frame, ..]
                ? Textures.ToTexture(frame, _palette)
                : null;
            _portraits[number] = texture;
        }
        return texture;
    }

    /// <summary>The game-start script once per session, then the region's <c>MAS</c> script.</summary>
    private void RunRegionScripts()
    {
        GplMachine scripts = Session.Scripts;
        _portraits.Clear();
        _dialogueWindow.Attach(Session.ScriptHost.Dialogue, Portrait);
        if (!Session.GameStarted)
        {
            Session.GameStarted = true;
            ReportScript("new game", scripts.NewGame());
        }
        ReportScript($"entered region {_region.Number}", scripts.EnterRegion(_region.Number));
        _overlay.Show(_region, _occupancy, scripts.Triggers);
    }

    /// <summary>
    /// Right-click: the front-most object under the pointer gets its talk-to,
    /// use, look or pick-up trigger, the first one it has. Scripts name
    /// objects by negated <c>OJFF</c> number.
    /// </summary>
    private void Interact(Vector2 at)
    {
        var hit = _objectSprites
            .Where(o => o.Sprite.Visible && o.Sprite.Texture is { } tex
                && new Rect2(o.Sprite.Position + o.Sprite.Offset, tex.GetSize()).HasPoint(at))
            .OrderByDescending(o => o.Sprite.Position.Y)
            .Select(o => o.Object)
            .FirstOrDefault();
        if (hit is null)
        {
            return;
        }
        int actor = _selected;
        foreach (var (list, verb) in new[] { (GplTriggerList.TalkTo, "talk to"), (GplTriggerList.Use, "use"), (GplTriggerList.Look, "look at"), (GplTriggerList.Pickup, "pick up") })
        {
            if (Session.Scripts.OnObject(list, actor, -hit.Ojff) is { } run)
            {
                ReportScript($"{verb} OJFF {hit.Ojff}", run);
                return;
            }
        }
        AddScriptLine($"OJFF {hit.Ojff}: no talk, use, look or pick-up trigger");
    }

    /// <summary>Log a script run and the world commands it issued (PRINT_STRING as its text).</summary>
    private void ReportScript(string what, GplRunResult run)
    {
        string status = run.Suspended ? ", waiting for the player" : run.Error is null ? "" : $", stopped: {run.Error}";
        AddScriptLine($"> {what}: {run.Entry.Kind} {run.Entry.File} @{run.Entry.Address:x4}, {run.Instructions} instructions{status}");
        var log = Session.ScriptHost.Log;
        for (; _scriptLogSeen < log.Count; _scriptLogSeen++)
        {
            GplCommand c = log[_scriptLogSeen].Command;
            AddScriptLine(c.Mnemonic == "PRINT_STRING" && c.Texts.Count > 1 && c.Texts[1] is { } text
                ? $"    \"{text.Trim()}\""
                : $"    {c}");
        }
        UpdateHud();
        if (_checking)
        {
            _pendingRegion = null;
        }
        if (!run.Suspended && _pendingRegion is { } go)
        {
            _pendingRegion = null;
            EnterRegion(go.Region, new Cell(go.X, go.Y));
        }
    }

    // -- world commands (IGplWorld) ---------------------------------------------------

    /// <summary>
    /// TELEPORT the party. In this region each member goes to the free cell
    /// nearest (x, y) (as <c>7372:06DF</c> does through <c>find_free_cell</c>);
    /// another region is entered once the script has finished.
    /// </summary>
    public void TeleportParty(int region, int x, int y)
    {
        if (region != _region.Number)
        {
            _pendingRegion = (region, x, y);
            AddScriptLine($"    party leaves for region {region} ({x},{y})");
            return;
        }
        foreach (PartyMember member in _party)
        {
            Walker w = member.Walker;
            if (NearestCell(new Cell(x, y), c => w.CanStand(c, _region.IsBlocked)) is { } cell && w.Teleport(cell, _region.IsBlocked))
            {
                DrawMember(member);
            }
        }
    }

    /// <summary>
    /// TELEPORT the objects with <c>OJFF</c> <paramref name="number"/>: to
    /// another region (255: off the map) they disappear from this one; within
    /// this region they move to the cell (drawn standing at its bottom centre,
    /// an approximation).
    /// </summary>
    public void TeleportObjects(int number, int region, int x, int y)
    {
        foreach (var (obj, sprite) in _objectSprites.Where(o => o.Object.Ojff == number))
        {
            if (region != _region.Number)
            {
                _hidden.Add(obj);
                sprite.Visible = false;
            }
            else
            {
                _hidden.Remove(obj);
                _moved[obj] = new Vector2(x * Region.TileSize + Region.TileSize / 2, (y + 1) * Region.TileSize - 1);
                sprite.Position = _moved[obj];
                sprite.Visible = true;
            }
        }
    }

    /// <summary>
    /// Object fields the region view knows: field 95 of a special object is
    /// the facing (0–7 clockwise from north) of the selected character, which
    /// stands in for the party's active member (the region-exit script, GPL
    /// 80, checks it). Special objects are not otherwise told apart yet.
    /// </summary>
    public int? ReadField(GplObjectRef field) =>
        field.IsSpecial && field.Path is [95] && Selected is { } member ? (int)member.Walker.Facing : null;

    /// <summary>Change to region <paramref name="number"/> with the party at <paramref name="start"/>.</summary>
    private void EnterRegion(int number, Cell start)
    {
        int index = Session.Install!.RegionFiles.ToList().FindIndex(f => f.Equals($"RGN{number:X2}.GFF", StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            AddScriptLine($"    no region file for region {number}");
            return;
        }
        ShowRegion(index, start);
    }

    private void AddScriptLine(string line)
    {
        _scriptLines.Add(line);
        if (_scriptLines.Count > 300)
        {
            _scriptLines.RemoveRange(0, _scriptLines.Count - 300);
        }
        _scriptLog.Text = string.Join('\n', _scriptLines.TakeLast(16));
    }

    // -- headless check ----------------------------------------------------------

    private int _answered;
    private bool _checking;

    /// <summary>
    /// For every region: put the active member on each move trigger (a box's
    /// top-left cell) and report the triggers whose scripts take the party to
    /// another region.
    /// </summary>
    private void CheckExits()
    {
        IReadOnlyList<string> files = Session.Install!.RegionFiles;
        int exits = 0;
        for (int index = 0; index < files.Count; index++)
        {
            ShowRegion(index);
            AnswerForThePlayer();
            int number = _region.Number;
            var spots = Session.Scripts.Triggers.Records(GplTriggerList.MoveTile).Select(t => (t.A, t.B, "tile"))
                .Concat(Session.Scripts.Triggers.Records(GplTriggerList.MoveBox).Select(t => (t.A, t.B, "box"))).ToList();
            var found = new List<string>();
            foreach (var (x, y, kind) in spots)
            {
                if (_region.Number != number)
                {
                    ShowRegion(index);
                    AnswerForThePlayer();
                }
                Walker w = _party[0].Walker;
                if (!w.Teleport(new Cell(x, y), _region.IsBlocked))
                {
                    continue;
                }
                for (int tick = 0; tick < 4 && _region.Number == number; tick++)
                {
                    _clock += 1.0 / ScmdAnimator.TicksPerSecond;
                    Animate();
                    AnswerForThePlayer(yes: true);
                }
                if (_region.Number != number)
                {
                    found.Add($"{kind} ({x},{y}) -> region {_region.Number} at ({_party[0].Walker.Cell.X},{_party[0].Walker.Cell.Y})");
                }
            }
            exits += found.Count;
            GD.Print($"{files[index]}: {spots.Count} move triggers, {found.Count} exits{(found.Count > 0 ? ": " + string.Join("; ", found) : "")}");
        }
        GD.Print($"{exits} region exits found");
        GetTree().Quit();
    }

    /// <summary>With no player, answer every prompt: Continue, No (Yes if <paramref name="yes"/>), or each menu's first option.</summary>
    private void AnswerForThePlayer(bool yes = false)
    {
        for (int i = 0; i < 50 && Session.Scripts.IsSuspended; i++)
        {
            int answer = Session.ScriptHost.Dialogue.Prompt switch
            {
                GplMenuPrompt menu => menu.Options.ToList().FindIndex(o => o.Enabled),
                GplYesNoPrompt => yes ? 1 : 0,
                _ => 0,
            };
            _answered++;
            ReportScript("auto answer", Session.Scripts.Resume(answer));
        }
        Session.ScriptHost.Dialogue.Clear();
    }

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
            int linesBefore = _scriptLines.Count;
            RunRegionScripts();
            AnswerForThePlayer();
            string scriptLine = _scriptLines.Count > linesBefore ? _scriptLines[linesBefore] : "";
            PlaceParty();
            Cell start = _party[0].Walker.Cell;
            Cell goal = new(start.X + 20, start.Y);
            WalkTo(goal, wholeParty: true);
            int overlaps = 0;
            for (int tick = 0; tick < 15 * ScmdAnimator.TicksPerSecond; tick++)
            {
                _clock = (double)tick / ScmdAnimator.TicksPerSecond;
                Animate();
                AnswerForThePlayer();
                var cells = _party.SelectMany(m => m.Walker.Footprint.Cells(m.Walker.Cell)).ToList();
                overlaps += cells.Count - cells.Distinct().Count();
            }
            failures += overlaps;
            string members = string.Join(", ", _party.Select(m =>
                $"{m.Walker.Footprint.HalfWidth * 2 + 1}x{m.Walker.Footprint.Height + 1} at ({m.Walker.Cell.X},{m.Walker.Cell.Y})"));
            GD.Print($"{file}: region {_region.Number}, {_animated.Count} animated objects; party from ({start.X},{start.Y}) "
                + $"towards ({goal.X},{goal.Y}): {members}; {(_party.Any(m => m.Walker.IsWalking) ? "still walking" : "stopped")}; "
                + $"{overlaps} overlaps\n    scripts {scriptLine.TrimStart('>', ' ')}; "
                + $"{Session.Scripts.Triggers.Count(GplTriggerList.MoveBox)} box triggers; "
                + $"{_scriptLines.Skip(linesBefore).Count(l => l.Contains("reached"))} move triggers fired on the walk");
        }
        GD.Print($"{Characters().Count} walking character sprites; {failures} overlaps in total; "
            + $"{_answered} dialogue prompts answered, {_scriptLines.Count(l => l.Contains("stopped:"))} scripts stopped with an error");
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
