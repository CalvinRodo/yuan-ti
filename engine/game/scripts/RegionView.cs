using DarkSun.Data.Animation;
using DarkSun.Data.Gff;
using DarkSun.Data.Images;
using DarkSun.Data.Regions;
using DarkSun.Game.Animation;
using DarkSun.Game.Movement;
using DarkSun.Game.Scripting;
using DarkSun.Game.World;
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
/// use, look or pick-up trigger. The region's objects live in a
/// <see cref="GameWorld"/>: creatures that can walk carry out their scripted
/// orders and run their no-orders and line-of-sight scripts; every world
/// command is listed in the script log (F2).
/// Click: the selected character walks there; Shift+click: the whole party;
/// Right-click: interact; Tab: select the next character; N: add one; C:
/// change its sprite; arrows, WASD or middle-drag scroll; wheel or +/- zoom;
/// PgUp/PgDn change region; Esc returns to the main menu; F1 shows the <c>GMAP</c> flags, occupied cells and
/// triggers. Run with <c>-- --check-regions</c> to load every region, run its
/// scripts, animate it and walk the party in it, then quit.
/// </summary>
public partial class RegionView : Node2D
{
    public const string ScenePath = "res://scenes/region_view.tscn";

    private const float PanSpeed = 400f;
    private static readonly float[] ZoomLevels = [0.25f, 0.5f, 1f, 2f, 3f, 4f];

    /// <summary>The original game's party size.</summary>
    private const int PartySize = GameWorld.PartySize;

    /// <summary>Most creature scripts started in one frame, so a crowd of idle creatures can't stall it.</summary>
    private const int CreatureScriptsPerFrame = 4;

    /// <summary>Region shown when the view opens; kept across visits. −1: not yet chosen, so a new game starts.</summary>
    private static int _regionIndex = -1;

    private readonly Dictionary<ImageFrame, Texture2D> _textures = [];
    private readonly List<AnimatedObject> _animated = [];
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
    private GameWorld _world = null!;
    private readonly Dictionary<WorldObject, Sprite2D> _creatureNodes = [];
    private readonly Dictionary<RegionObject, WorldObject> _worldObjects = [];
    private SelectionMarker _marker = null!;
    private int _selected;
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
    private int _zoomLevel = Array.IndexOf(ZoomLevels, 1f);
    private Vector2 _mapSize;

    private long Now => (long)(_clock * ScmdAnimator.TicksPerSecond);

    /// <summary>Make the next opening of the view start a new game (region 42).</summary>
    public static void StartNewGame() => _regionIndex = -1;

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
        _creatureNodes.Clear();
        _worldObjects.Clear();
        _clock = 0;

        _region = Region.Load(Session.Open(file), [Session.Open("SEGOBJEX.GFF"), Session.Open("GPLDATA.GFF")]);
        var objectData = new GffSet([Session.Open("SEGOBJEX.GFF"), Session.Open("GPLDATA.GFF")]);
        _world = new GameWorld(_region, objectData, Session.ScriptHost.EngineVariables);
        _world.Changed += OnWorldChanged;
        Session.ScriptHost.Map = _world;
        _occupancy = _world.Occupancy;
        foreach (WorldObject o in _world.Objects.Where(o => o.Source is not null))
        {
            _worldObjects[o.Source!] = o;
        }
        _palette = _region.LoadPalette(Session.Open("GPLDATA.GFF"), Session.Open("RESOURCE.GFF"));
        _mapSize = new Vector2(_region.Width, _region.Height) * Region.TileSize;

        byte[] ground = RegionRenderer.RenderRgb(_region, _palette, RegionLayers.Ground);
        Image groundImage = Image.CreateFromData((int)_mapSize.X, (int)_mapSize.Y, false, Image.Format.Rgb8, ground);
        _ground.AddChild(new Sprite2D { Texture = ImageTexture.CreateFromImage(groundImage), Centered = false, Name = "Ground" });

        // Each sprite's node sits at the point it is sorted by: an object's ETAB
        // position, a wall's cell bottom. The image hangs off it via Offset.
        // Creatures that can walk are drawn from their walker instead, like the party.
        foreach (RegionObject obj in _region.Objects)
        {
            if (_worldObjects.TryGetValue(obj, out WorldObject? wo) && wo.Walker is not null)
            {
                var node = new Sprite2D { Centered = false };
                _sorted.AddChild(node);
                _creatureNodes[wo] = node;
                DrawCreature(wo);
                continue;
            }
            Sprite2D? sprite = AddSprite(obj.Frame, new Vector2(obj.EtabX, obj.EtabY), new Vector2(obj.X, obj.Y), obj.Mirrored);
            if (sprite is not null)
            {
                _objectSprites.Add((obj, sprite));
            }
            if (sprite is not null && obj.Scmd > 0 && objectData.TryRead("SCMD", obj.Scmd, out var data))
            {
                var scmd = Scmd.Parse(data.Span);
                _animated.Add(new AnimatedObject(sprite, obj, scmd, new ScmdAnimator(scmd, obj.Frames.Count, mirrored: obj.Mirrored)));
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
        _world.ActiveMember = _selected;
    }

    /// <summary>
    /// Add a character with the next walking sprite, on the free cell nearest
    /// to <paramref name="near"/>, as party member <paramref name="slot"/>
    /// (the next free one if null).
    /// </summary>
    private PartyMember? AddCharacter(Cell near, int? slot = null)
    {
        List<CharacterSprite> sprites = Characters();
        int index = slot ?? _party.Count;
        if (sprites.Count == 0 || index >= PartySize)
        {
            return null;
        }
        CharacterSprite sprite = sprites[_nextSprite++ % sprites.Count];
        if (_world.AddPartyMember(index, new CharacterSpec(sprite.Script, sprite.Sizes), near) is not { Walker: { } walker })
        {
            return null;
        }
        var member = new PartyMember(walker, sprite, new Sprite2D { Centered = false });
        _sorted.AddChild(member.Node);
        _party.Insert(Math.Min(index, _party.Count), member);
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
        _world.RemovePartyMember(_selected);
        old.Node.QueueFree();
        _party.RemoveAt(_selected);
        AddCharacter(cell, _selected);
        _selected = Math.Min(_selected, Math.Max(0, _party.Count - 1));
    }

    private void DrawMember(PartyMember member)
    {
        Walker walker = member.Walker;
        ImageFrame frame = member.Sprite.Frames[walker.Animator.Frame];
        member.Node.Texture = TextureFor(frame);
        member.Node.FlipH = walker.Animator.Mirrored;
        member.Node.Position = new Vector2(walker.Feet.X, walker.Feet.Y);
        member.Node.Offset = new Vector2(-walker.Animator.HotX, -walker.Animator.HotY);
    }

    /// <summary>Draw a creature that walks: its region object's frames, placed by its walker.</summary>
    private void DrawCreature(WorldObject creature)
    {
        if (!_creatureNodes.TryGetValue(creature, out Sprite2D? node) || creature.Walker is not { } walker
            || creature.Source is not { } source)
        {
            return;
        }
        ImageFrame frame = source.Frames[Math.Clamp(walker.Animator.Frame, 0, source.Frames.Count - 1)];
        node.Visible = !creature.Hidden && frame.Width > 0 && frame.Height > 0;
        node.Texture = TextureFor(frame);
        node.FlipH = walker.Animator.Mirrored;
        node.Position = new Vector2(walker.Feet.X, walker.Feet.Y);
        node.Offset = new Vector2(-walker.Animator.HotX, -walker.Animator.HotY);
    }

    private void WalkTo(Cell goal, bool wholeParty)
    {
        foreach (PartyMember member in wholeParty ? _party : Selected is { } m ? [m] : [])
        {
            member.Walker.WalkTo(goal, _region.IsBlocked);
        }
        _follow = true;
    }


    // -- per frame --------------------------------------------------------------

    /// <summary>Step every animated object and the character to the current tick.</summary>
    private void Animate()
    {
        long now = Now;
        foreach (AnimatedObject animated in _animated)
        {
            (Sprite2D sprite, RegionObject obj, ScmdAnimator animator) = (animated.Sprite, animated.Object, animated.Animator);
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

        // Creatures: orders, then the scripts they asked for.
        _world.ActiveMember = _selected;
        foreach (WorldObject creature in _world.Update(now, Session.Scripts.Creatures))
        {
            DrawCreature(creature);
        }
        RunCreatureScripts();
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
            GetTree().ChangeSceneToFile(MainMenu.ScenePath);
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
        var hit = _objectSprites.Where(o => _worldObjects.ContainsKey(o.Object)).Select(o => (Object: _worldObjects[o.Object], o.Sprite))
            .Concat(_creatureNodes.Select(c => (Object: c.Key, Sprite: c.Value)))
            .Where(o => o.Sprite.Visible && o.Sprite.Texture is { } tex
                && new Rect2(o.Sprite.Position + o.Sprite.Offset, tex.GetSize()).HasPoint(at))
            .OrderByDescending(o => o.Sprite.Position.Y)
            .Select(o => o.Object)
            .FirstOrDefault();
        if (hit is null)
        {
            return;
        }
        // Scripts see the object by its index; triggers match its id (−OJFF).
        int actor = _selected;
        foreach (var (list, verb) in new[] { (GplTriggerList.TalkTo, "talk to"), (GplTriggerList.Use, "use"), (GplTriggerList.Look, "look at"), (GplTriggerList.Pickup, "pick up") })
        {
            if (Session.Scripts.OnObject(list, actor, hit.Index) is { } run)
            {
                ReportScript($"{verb} {Describe(hit)}", run);
                return;
            }
        }
        AddScriptLine($"{Describe(hit)}: no talk, use, look or pick-up trigger");
    }

    private static string Describe(WorldObject obj) =>
        obj.Name.Length > 0 ? $"{obj.Name} (OJFF {-obj.Id})" : $"OJFF {-obj.Id}";

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
        if (Session.ScriptHost.PendingFight is { } fight)
        {
            Session.ScriptHost.PendingFight = null;
            AddScriptLine($"    combat at ({fight.X},{fight.Y}) (not modelled yet)");
        }
        if (_checking)
        {
            _world.PendingRegion = null;
        }
        if (!run.Suspended && _world.PendingRegion is { } go)
        {
            _world.PendingRegion = null;
            AddScriptLine($"    party leaves for region {go.Region} ({go.X},{go.Y})");
            EnterRegion(go.Region, new Cell(go.X, go.Y));
        }
    }

    // -- the world ---------------------------------------------------------------------

    /// <summary>
    /// Scripts creatures asked for (no orders, line of sight), a few per
    /// frame, each with the creature as actor. None start while another
    /// script waits for the player.
    /// </summary>
    private void RunCreatureScripts()
    {
        for (int n = 0; n < CreatureScriptsPerFrame && _world.PendingScripts.Count > 0 && !Session.Scripts.IsSuspended; n++)
        {
            var (entry, actor, why) = _world.PendingScripts.Dequeue();
            if (Session.Scripts.RunFor(entry, actor) is { } run)
            {
                ReportScript(why, run);
            }
            if (_world.PendingRegion is not null || _world.Region != _region)
            {
                return; // the region changed under us
            }
        }
        if (Session.Scripts.IsSuspended)
        {
            _world.PendingScripts.Clear();
        }
    }

    /// <summary>
    /// A script changed an object: moved it, took it off the map, put it
    /// back, or switched its animation (<c>REQUEST(5)</c>).
    /// </summary>
    private void OnWorldChanged(WorldObject obj, int? animationStart)
    {
        if (obj.Kind == ObjectKind.Party)
        {
            if (_party.FirstOrDefault(m => m.Walker == obj.Walker) is { } member)
            {
                member.Node.Visible = !obj.Hidden;
                DrawMember(member);
            }
            return;
        }
        if (_creatureNodes.ContainsKey(obj))
        {
            DrawCreature(obj);
            return;
        }
        if (obj.Walker is not null && obj.Source is not null)
        {
            // A clone of a walking creature: give it a node of its own.
            _creatureNodes[obj] = new Sprite2D { Centered = false };
            _sorted.AddChild(_creatureNodes[obj]);
            DrawCreature(obj);
            return;
        }
        if (obj.Source is not { } source)
        {
            return;
        }
        if (obj.Hidden)
        {
            _hidden.Add(source);
        }
        else
        {
            _hidden.Remove(source);
            if (obj.FixedCell != new Cell(source.EtabX / Region.TileSize, Math.Max(0, source.EtabY - 1) / Region.TileSize))
            {
                // Drawn standing at the cell's bottom centre (an approximation).
                _moved[source] = new Vector2(obj.FixedCell.X * Region.TileSize + Region.TileSize / 2, (obj.FixedCell.Y + 1) * Region.TileSize - 1);
            }
        }
        foreach (var (o, sprite) in _objectSprites.Where(o => o.Object == source))
        {
            sprite.Visible = !obj.Hidden;
            if (_moved.TryGetValue(source, out Vector2 at))
            {
                sprite.Position = at;
            }
        }
        if (animationStart is int start && _animated.FirstOrDefault(a => a.Object == source) is { } animated)
        {
            animated.Animator = new ScmdAnimator(animated.Script, source.Frames.Count, start, source.Mirrored);
        }
    }

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
            var spots = Session.Scripts.Triggers.Records(GplTriggerList.MoveTile).Select(t => (t.A, t.B, W: 1, H: 1, "tile"))
                .Concat(Session.Scripts.Triggers.Records(GplTriggerList.MoveBox).Select(t => (t.A, t.B, W: (int)t.B8, H: (int)t.B9, "box"))).ToList();
            var found = new List<string>();
            foreach (var (x0, y0, width, height, kind) in spots)
            {
                if (_region.Number != number)
                {
                    ShowRegion(index);
                    AnswerForThePlayer();
                }
                // The first cell of the trigger the active member can stand on (creatures hold cells too).
                Walker w = _party[0].Walker;
                var cells = from dy in Enumerable.Range(0, Math.Max(1, height))
                            from dx in Enumerable.Range(0, Math.Max(1, width))
                            select new Cell(x0 + dx, y0 + dy);
                if (cells.FirstOrDefault(c => w.CanStand(c, _region.IsBlocked), new Cell(-1, -1)) is not { X: >= 0 } at
                    || !w.Teleport(at, _region.IsBlocked))
                {
                    continue;
                }
                var (x, y) = (at.X, at.Y);
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
        IReadOnlyList<string> files = Session.Install!.RegionFiles;
        for (_regionIndex = 0; _regionIndex < files.Count; _regionIndex++)
        {
            string file = files[_regionIndex];
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
                + $"{_scriptLines.Skip(linesBefore).Count(l => l.Contains("reached"))} move triggers fired on the walk\n    "
                + $"{_world.Objects.Count(o => o.Kind == ObjectKind.Creature)} creatures ({_creatureNodes.Count} walking, "
                + $"{_world.Objects.Count(o => o.Order.Mode != OrderMode.None)} with orders); "
                + $"{_scriptLines.Skip(linesBefore).Count(l => l.Contains("has no orders") || l.Contains(" sight") || l.Contains(" sees "))} creature scripts run");
        }
        GD.Print($"{Characters().Count} walking character sprites; {failures} overlaps in total; "
            + $"{_answered} dialogue prompts answered, {_scriptLines.Count(l => l.Contains("stopped:"))} scripts stopped with an error");
        foreach (string line in _scriptLines.Where(l => l.Contains("stopped:")))
        {
            GD.Print(line);
        }
        GetTree().Quit(failures == 0 ? 0 : 1);
    }
}

/// <summary>A region object with an <c>SCMD</c> animation; scripts can restart it (<c>REQUEST(5)</c>).</summary>
public sealed class AnimatedObject(Sprite2D sprite, RegionObject obj, Scmd script, ScmdAnimator animator)
{
    public Sprite2D Sprite { get; } = sprite;
    public RegionObject Object { get; } = obj;
    public Scmd Script { get; } = script;
    public ScmdAnimator Animator { get; set; } = animator;
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
