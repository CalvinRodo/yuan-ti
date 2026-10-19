using DarkSun.Data.Animation;
using DarkSun.Data.Gff;
using DarkSun.Data.Images;
using DarkSun.Data.Regions;
using DarkSun.Game.Animation;
using DarkSun.Game.Movement;
using DarkSun.Game.Saves;
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
/// into cells fire move triggers; right-clicking an object opens its verb
/// menu (<see cref="VerbMenu"/>: Talk, Get, Use, Use with…, Attack, Look, as
/// it offers them), and the selected character walks up to it and does that
/// (Look works from anywhere). The region's objects live in a
/// <see cref="GameWorld"/>: creatures that can walk carry out their scripted
/// orders and run their no-orders and line-of-sight scripts; every world
/// command is listed in the script log (F2).
/// Click: the selected character walks there; Shift+click: the whole party;
/// Right-click: verb menu; Tab: select the next character; N: add one; C:
/// change its sprite; arrows, WASD or middle-drag scroll; wheel or +/- zoom;
/// PgUp/PgDn change region; Esc returns to the main menu; F1 shows the <c>GMAP</c> flags, occupied cells and
/// triggers, F3 a grid, F4 object numbers, and with any of them on the
/// hovered cell is described (<see cref="DebugOverlay"/>; <c>-- --debug-overlay</c>
/// starts with all three). Run with <c>-- --check-regions</c> to load every region, run its
/// scripts, animate it and walk the party in it, then quit.
/// </summary>
public partial class RegionView : Node2D
{
    public const string ScenePath = "res://scenes/region_view.tscn";

    private const float PanSpeed = 400f;
    /// <summary>
    /// Map pixels per viewport pixel. The viewport is 640 × 400, twice the
    /// original's 320 × 200, so zoom 2 shows what DSUN.EXE showed; the window
    /// scales the viewport by a whole number too (stretch scale mode
    /// "integer"), so every map pixel stays a crisp block. ½ is an overview.
    /// </summary>
    private static readonly float[] ZoomLevels = [0.5f, 1f, 2f, 3f, 4f];

    /// <summary>How quickly the camera catches up with the character it follows.</summary>
    private const float CameraSmoothing = 6f;

    /// <summary>The original game's party size.</summary>
    private const int PartySize = GameWorld.PartySize;

    /// <summary>Most creature scripts started in one frame, so a crowd of idle creatures can't stall it.</summary>
    private const int CreatureScriptsPerFrame = 4;

    /// <summary>Region shown when the view opens; kept across visits. −1: not yet chosen, so a new game starts.</summary>
    private static int _regionIndex = -1;

    private readonly List<AnimatedObject> _animated = [];
    private Region _region = null!;
    private Palette _palette = null!;
    private double _clock;
    private Node2D _ground = null!;
    private Node2D _sorted = null!;
    private DebugOverlay _overlay = null!;
    private ScriptDebugger _debugger = null!;
    private InventoryWindow _inventory = null!;
    private ShopWindow _shop = null!;

    /// <summary>Scripts to start once none is paused (the region's after the game-start script, …): see <see cref="WhenScriptsFree"/>.</summary>
    private readonly Queue<Action> _whenFree = new();
    private PanelContainer _hoverPanel = null!;
    private Label _hoverText = null!;
    private double _hoverRefresh;
    private Camera2D _camera = null!;
    private Label _hud = null!;
    private readonly List<PartyMember> _party = [];
    private Occupancy _occupancy = new();
    private GameWorld _world = null!;
    private readonly Dictionary<WorldObject, Sprite2D> _creatureNodes = [];
    private readonly Dictionary<RegionObject, WorldObject> _worldObjects = [];

    /// <summary>Things made by CLONE, each with a sprite of its own (they share their original's region object).</summary>
    private readonly Dictionary<WorldObject, Sprite2D> _cloneSprites = [];
    private SelectionMarker _marker = null!;
    private int _selected;
    private int _nextSprite;
    private bool _follow = true;

    /// <summary>A verb waiting for its character to walk up to the object (<c>With</c>: Use with's first object).</summary>
    private (Walker Walker, int Actor, WorldObject Target, Verb Verb, WorldObject? With, int Tries)? _pendingUse;

    private VerbMenu _verbMenu = null!;
    private WorldObject? _menuTarget;

    /// <summary>Use with…: the first object, waiting for the player to click the second.</summary>
    private WorldObject? _useWith;
    private Label _prompt = null!;
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
    private int _zoomLevel = Array.IndexOf(ZoomLevels, 2f);
    private Vector2 _mapSize;

    private long Now => (long)(_clock * ScmdAnimator.TicksPerSecond);

    /// <summary>Make the next opening of the view start a new game (region 42).</summary>
    public static void StartNewGame() => _regionIndex = -1;

    /// <summary>Where a new game starts: region 42 at the party's start, unless changed (development: <c>--region</c>, <c>--at</c>).</summary>
    public static (int Region, Cell At) NewGameStart { get; set; } =
        (GplMachine.StartRegion, new Cell(GplMachine.StartX / Region.TileSize, GplMachine.StartY / Region.TileSize));

    public override void _Ready()
    {
        Controls.Register();
        // [game] original_pathfinding=true in user://settings.cfg: walk as DSUN.EXE plans.
        var settings = new ConfigFile();
        settings.Load("user://settings.cfg");
        Walker.UseOriginalPathfinding = settings.GetValue("game", "original_pathfinding", false).AsBool();
        _ground = new Node2D();
        AddChild(_ground);
        _marker = new SelectionMarker();
        AddChild(_marker);
        _sorted = new Node2D { YSortEnabled = true };
        AddChild(_sorted);
        _overlay = new DebugOverlay { Visible = false };
        AddChild(_overlay);
        _camera = new Camera2D
        {
            Zoom = Vector2.One * ZoomLevels[_zoomLevel],
            PositionSmoothingEnabled = true,
            PositionSmoothingSpeed = CameraSmoothing,
        };
        AddChild(_camera);
        AddChild(BuildHud());
        if (OS.GetCmdlineUserArgs().Contains("--debug-overlay"))
        {
            (_overlay.Flags, _overlay.Grid, _overlay.Numbers) = (true, true, true);
        }

        if (OS.GetCmdlineUserArgs().Contains("--break-on-start"))
        {
            Session.Scripts.BreakOnNextRun = true;
        }
        if (OS.GetCmdlineUserArgs().Contains("--check-debugger"))
        {
            _checking = true;
            CheckDebugger();
            return;
        }
        if (OS.GetCmdlineUserArgs().Contains("--check-regions"))
        {
            _checking = true;
            CheckAllRegions();
            return;
        }
        if (OS.GetCmdlineUserArgs().Contains("--check-exits"))
        {
            _instantTravel = true;
            CheckExits();
            return;
        }
        if (OS.GetCmdlineUserArgs().Contains("--check-memory"))
        {
            _instantTravel = true;
            CheckMemory();
            return;
        }
        if (OS.GetCmdlineUserArgs().Contains("--check-verbs"))
        {
            _checking = _checkingVerbs = true;
            CheckVerbs();
            return;
        }
        if (PendingLoad is { } save)
        {
            PendingLoad = null;
            if (!LoadSaved(save))
            {
                // Back to the list, which says why.
                SavesView.Message = _notice.Text;
                GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, SavesView.ScenePath);
            }
            return;
        }
        if (_regionIndex < 0)
        {
            // A new game begins where DSUN.EXE's does: region 42, the party at pixel (488, 152).
            _regionIndex = 0;
            EnterRegion(NewGameStart.Region, NewGameStart.At);
            FireArrivalTriggers();
            if (Session.Party.Count > 0 && !_checking)
            {
                // They have adventured now: only their names can change (the manual's EDIT NAME).
                foreach (var c in Session.Party)
                {
                    c.Adventured = true;
                    Session.Roster.Save(c);
                }
                Session.SaveParty();
            }
            if (OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--give=")) is { } give)
            {
                // Development: items for the active member, by RDFF number (--give=1051,1095).
                foreach (string n in give["--give=".Length..].Split(','))
                {
                    if (int.TryParse(n, out int rdff) && _world.Give(1, -rdff, _selected, GameWorld.None) == 0)
                    {
                        AddScriptLine($"    --give: RDFF {rdff} is not an item");
                    }
                }
            }
            if (OS.GetCmdlineUserArgs().Contains("--open-items"))
            {
                _inventory.World = _world;
                _inventory.CallDeferred(InventoryWindow.MethodName.Open, _selected);
            }
            if (OS.GetCmdlineUserArgs().Contains("--check-items"))
            {
                _instantTravel = true;
                CheckItems();
                return;
            }
            if (OS.GetCmdlineUserArgs().Contains("--check-save"))
            {
                _instantTravel = true;
                CheckSave();
                return;
            }
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

        _notice = new Label { Visible = false, HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
        _notice.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterTop);
        _notice.GrowHorizontal = Control.GrowDirection.Both;
        _notice.OffsetTop = 22;
        _notice.AddThemeFontSizeOverride("font_size", 10);
        _notice.AddThemeColorOverride("font_outline_color", Colors.Black);
        _notice.AddThemeConstantOverride("outline_size", 4);
        layer.AddChild(_notice);
        _hoverPanel = new PanelContainer { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _hoverPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight);
        _hoverPanel.GrowHorizontal = Control.GrowDirection.Begin;
        _hoverPanel.GrowVertical = Control.GrowDirection.Begin;
        _hoverText = new Label { LabelSettings = new LabelSettings { FontSize = 9 }, MouseFilter = Control.MouseFilterEnum.Ignore };
        _hoverPanel.AddChild(_hoverText);
        layer.AddChild(_hoverPanel);
        _fade = new ColorRect { Color = new Color(0, 0, 0, 0), MouseFilter = Control.MouseFilterEnum.Ignore };
        _fade.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(_fade);
        _verbMenu = new VerbMenu { Chosen = ChooseVerb };
        layer.AddChild(_verbMenu);
        _debugger = new ScriptDebugger
        {
            Ran = run => ReportScript("debugger", run),
            Stop = StopScript,
            Default = () => _region is null ? null : new GplEntry(_region.Number, 0, GplFileKind.Mas),
        };
        layer.AddChild(_debugger);
        _inventory = new InventoryWindow
        {
            MemberName = slot => _world?[slot]?.Name ?? $"Character {slot + 1}",
            Money = () => Session.ScriptHost.Money,
            Did = text => AddScriptLine($"> {text}"),
        };
        layer.AddChild(_inventory);
        _shop = new ShopWindow
        {
            Money = () => Session.ScriptHost.Money,
            SetMoney = money => Session.ScriptHost.Money = money,
            Did = text => AddScriptLine($"    {text}"),
            Closed = () =>
            {
                if (Session.ScriptHost.Dialogue.Prompt is GplShopPrompt && Session.Scripts.IsSuspended && Session.Scripts.PausedFor is null)
                {
                    ReportScript("shop closed", Session.Scripts.Resume(0));
                }
            },
        };
        layer.AddChild(_shop);
        _prompt = new Label { Visible = false, HorizontalAlignment = HorizontalAlignment.Center };
        _prompt.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterTop);
        _prompt.GrowHorizontal = Control.GrowDirection.Both;
        _prompt.OffsetTop = 6;
        _prompt.AddThemeFontSizeOverride("font_size", 10);
        _prompt.AddThemeColorOverride("font_outline_color", Colors.Black);
        _prompt.AddThemeConstantOverride("outline_size", 4);
        layer.AddChild(_prompt);
        return layer;
    }

    /// <param name="saved">Loading a game: the party as saved, and the region entered without running its script (its triggers are in the save).</param>
    private void ShowRegion(int index, Cell? start = null, SaveGame? saved = null)
    {
        IReadOnlyList<string> files = Session.Install!.RegionFiles;
        _regionIndex = (index % files.Count + files.Count) % files.Count;
        _pendingUse = null;
        _verbMenu.Close();
        SetUseWith(null);
        _whenFree.Clear();
        if (Session.Scripts.IsSuspended)
        {
            // Leaving (PgUp/PgDn, or the debugger paused a script mid-way): what it had left to do is dropped.
            AddScriptLine($"> left a paused script before {GplBreakpoints.Format(Session.Scripts.Current)}");
            Session.Scripts.Abandon();
            Session.ScriptHost.Dialogue.Clear();
        }
        // The party keeps its looks, facing and selection from region to region.
        var carried = _party.Select(m => (m.Sprite, m.Walker.Facing)).ToList();
        int selected = _selected;
        if (_world is not null && saved is null)
        {
            Session.Regions.Remember(_world);
        }
        LoadRegion(files[_regionIndex]);
        if (saved is not null)
        {
            PlaceSavedParty(saved);
            AttachScripts();
        }
        else
        {
            PlaceParty(start, carried, selected);
            RunRegionScripts();
        }
        UpdateHud();
        _follow = true;
        FollowCharacter();
        SnapCamera();
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
            + "?: all keys · Click: walk · Shift+click: party · Right-click: verbs · Tab: select · Esc: back";
    }

    /// <summary>Replace the map's nodes with <paramref name="file"/>'s region.</summary>
    private void LoadRegion(string file)
    {
        foreach (Node child in _ground.GetChildren().Concat(_sorted.GetChildren()))
        {
            child.QueueFree();
        }
        _animated.Clear();
        _party.Clear();
        _objectSprites.Clear();
        _hidden.Clear();
        _moved.Clear();
        _lastCells.Clear();
        _creatureNodes.Clear();
        _worldObjects.Clear();
        _cloneSprites.Clear();
        _clock = 0;

        _region = Region.Load(Session.Open(file), [Session.Open("SEGOBJEX.GFF"), Session.Open("GPLDATA.GFF")], Session.Assets);
        var objectData = new GffSet([Session.Open("SEGOBJEX.GFF"), Session.Open("GPLDATA.GFF")]);
        _world = new GameWorld(_region, objectData, Session.ScriptHost.EngineVariables, Session.ScriptHost.PartyMembers);
        // Back in a region visited before: as the party left it (before its script runs, as in DSUN.EXE).
        IReadOnlyList<WorldObject>? restored = _checking ? null : Session.Regions.Recall(_world);
        _world.Changed += OnWorldChanged;
        Session.ScriptHost.Map = _world;
        _occupancy = _world.Occupancy;
        foreach (WorldObject o in _world.Objects.Where(o => o.Source is not null && !o.IsClone))
        {
            _worldObjects[o.Source!] = o;
        }
        _palette = _region.LoadPalette(Session.Open("GPLDATA.GFF"), Session.Open("RESOURCE.GFF"), Session.Assets);

        // The region's music (RDAT +0x2F), changed with the original's 5-second fades.
        if (!_checking && RegionInfo.Read(Session.Open("RESOURCE.GFF"), _region.Number) is { Music: > 0 } info)
        {
            Music.Instance?.ChangeTo("RESOURCE.GFF", info.Music);
        }
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
                // Doors start closed or open as their state says (the world sets the start).
                int start = _worldObjects.TryGetValue(obj, out WorldObject? state) ? state.AnimationStart : 0;
                _animated.Add(new AnimatedObject(sprite, obj, scmd, AnimatorFor(obj, scmd, start, obj.AnchorX, obj.AnchorY)));
            }
        }
        // DSUN.EXE draws row by row (draw_objects_and_walls, 1DBD:0002): before
        // an object whose ETAB y is in row y >> 4 it draws the walls of every
        // row above. So a wall on cell row r sorts just before y = (r + 1) * 16,
        // the bottom of its cell (where it is drawn bottom-aligned): objects
        // and characters standing on that line or lower are drawn over it.
        foreach (PlacedSprite wall in _region.Walls)
        {
            var sortPoint = new Vector2(wall.X + wall.Frame.Width / 2, wall.Y + wall.Frame.Height - 0.5f);
            AddSprite(wall.Frame, sortPoint, new Vector2(wall.X, wall.Y), wall.Mirrored);
        }
        _overlay.Show(_region, _occupancy, Session.Scripts.Triggers, _world);
        foreach (WorldObject o in restored ?? [])
        {
            OnWorldChanged(o, _world.SecondStateAnimation(o));
        }
        if (restored is not null)
        {
            AddScriptLine($"    region {_region.Number} as the party left it ({restored.Count} objects)");
        }
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

    private Texture2D TextureFor(ImageFrame frame) => Textures.ToTexture(frame, _palette);

    /// <summary>
    /// An object's animation, hanging from <paramref name="hotX"/>, <paramref name="hotY"/>
    /// until a record moves its hot spot. Creatures that stand still are
    /// drawn as DSUN.EXE draws all creatures: from the bottom centre of each frame.
    /// </summary>
    private ScmdAnimator AnimatorFor(RegionObject obj, Scmd scmd, int start, int hotX, int hotY) =>
        _worldObjects.TryGetValue(obj, out WorldObject? wo) && wo.Kind == ObjectKind.Creature
            ? ScmdAnimator.ForCreature(scmd, [.. obj.Frames.Select(f => (f.Width, f.Height))], start)
            : new ScmdAnimator(scmd, obj.Frames.Count, start, obj.Mirrored, hotX, hotY);

    // -- character ------------------------------------------------------------

    /// <summary>
    /// Object definitions that can walk: an <c>SCMD</c> with a moving walk
    /// sequence in every direction. One per <c>BMP </c>, in <c>OJFF</c> order.
    /// </summary>
    private static List<CharacterSprite> Characters() => Session.CharacterSprites;

    private PartyMember? Selected => _party.Count > 0 ? _party[_selected] : null;

    /// <summary>Put a party of <see cref="PartySize"/> near <paramref name="start"/>, or the middle of the map.</summary>
    /// <summary>
    /// The party at <paramref name="start"/> (else the middle of the map):
    /// <paramref name="carried"/> (the members coming from the last region,
    /// with their sprites and facing), or a new party of four.
    /// </summary>
    private void PlaceParty(Cell? start, IReadOnlyList<(CharacterSprite Sprite, Direction Facing)> carried, int selected)
    {
        _nextSprite = 0;
        Cell centre = start ?? new(_region.Width / 2, _region.Height / 2);
        // A new game's party: the characters made on Create Characters, else four placeholders.
        if (carried.Count == 0 && Session.Party.Count > 0)
        {
            carried = [.. Session.Party.Take(PartySize).Select((c, i) =>
                (Session.CharacterSprites.FirstOrDefault(s => s.Ojff == c.Sprite) ?? Characters()[i % Characters().Count], Direction.South))];
        }
        for (int i = 0; i < (carried.Count > 0 ? carried.Count : PartySize); i++)
        {
            Cell near = _party.Count > 0 ? _party[^1].Walker.Cell : centre;
            if (i < carried.Count)
            {
                AddCharacter(near, sprite: carried[i].Sprite, facing: carried[i].Facing);
            }
            else
            {
                AddCharacter(near);
            }
        }
        _nextSprite = _party.Count;
        _selected = Math.Clamp(selected, 0, Math.Max(0, _party.Count - 1));
        _world.ActiveMember = _selected;
    }

    /// <summary>
    /// When the adventure screen is first built (a new game, <c>1587:1F83</c>),
    /// DSUN.EXE checks the move triggers of each party member's cell, after
    /// the region's script has registered them. A region change by TELEPORT
    /// (<c>7155:1487</c>) doesn't: it only runs the region's script, so a
    /// party arriving inside the new region's exit box isn't sent straight back.
    /// </summary>
    private void FireArrivalTriggers() => WhenScriptsFree(() =>
    {
        int region = _region.Number;
        for (int i = 0; i < _party.Count && _region.Number == region && _world.PendingRegion is null; i++)
        {
            Cell cell = _party[i].Walker.Cell;
            _lastCells[_party[i].Walker] = cell;
            if (Session.Scripts.OnMove(i, cell.X, cell.Y, i == _selected) is { } run)
            {
                ReportScript($"character {i + 1} starts at ({cell.X},{cell.Y})", run);
            }
        }
    });

    /// <summary>
    /// Add a character with the next walking sprite, on the free cell nearest
    /// to <paramref name="near"/>, as party member <paramref name="slot"/>
    /// (the next free one if null).
    /// </summary>
    private PartyMember? AddCharacter(Cell near, int? slot = null, CharacterSprite? sprite = null, Direction facing = Direction.South)
    {
        List<CharacterSprite> sprites = Characters();
        int index = slot ?? _party.Count;
        if (sprites.Count == 0 || index >= PartySize)
        {
            return null;
        }
        sprite ??= sprites[_nextSprite++ % sprites.Count];
        if (_world.AddPartyMember(index, new CharacterSpec(sprite.Script, sprite.Sizes, sprite.Footprint), near, facing) is not { Walker: { } walker })
        {
            return null;
        }
        if (index < Session.Party.Count && _world[index] is { } created)
        {
            created.Name = Session.Party[index].Name; // what dialogue calls the speaker
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
        _pendingUse = null;
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
            sprite.Offset = -new Vector2(animator.HotX, animator.HotY + obj.YAdjust);
            PlaySounds(animator.TakeSounds(), sprite.Position);
        }
        int region = _region.Number;
        for (int i = 0; i < _party.Count && _region.Number == region && !_transitioning; i++)
        {
            PartyMember member = _party[i];
            if (member.Walker.Update(now, _region.IsBlocked))
            {
                DrawMember(member);
            }
            PlaySounds(member.Walker.TakeSounds(), member.Node.Position);
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
        foreach (var (creature, node) in _creatureNodes)
        {
            if (creature.Walker is { } walker)
            {
                PlaySounds(walker.TakeSounds(), node.Position);
            }
        }
        // GPL SOUND (play_sound_effect, as for SCMD sounds).
        while (Session.ScriptHost.Sounds.TryDequeue(out int sound))
        {
            Sounds.Instance?.Play(sound);
        }
        RunCreatureScripts();
        if (_overlay.Visible)
        {
            _overlay.QueueRedraw();
        }
        _marker.Follow(Selected?.Walker);
    }

    /// <summary>
    /// Keep the camera on the party's active member (the selected character),
    /// as DSUN.EXE scrolls with the active member; the camera eases after it
    /// rather than jumping a cell at a time. Scrolling by hand stops following
    /// until the next click.
    /// </summary>
    /// <summary>
    /// Play an animation's sound effects (<c>SCMD</c> record byte 7) if it is
    /// on screen; DSUN.EXE animates, and so sounds, the objects it draws.
    /// Off-screen sounds are dropped.
    /// </summary>
    private void PlaySounds(IEnumerable<int> sounds, Vector2 at)
    {
        if (_checking || Sounds.Instance is null)
        {
            _ = sounds.Count();
            return;
        }
        Vector2 half = GetViewportRect().Size / _camera.Zoom / 2 + new Vector2(Region.TileSize * 2, Region.TileSize * 2);
        Vector2 centre = _camera.GetScreenCenterPosition();
        bool seen = Math.Abs(at.X - centre.X) <= half.X && Math.Abs(at.Y - centre.Y) <= half.Y;
        foreach (int sound in sounds)
        {
            if (seen)
            {
                Sounds.Instance.Play(sound);
            }
        }
    }

    private void FollowCharacter()
    {
        if (_follow && Selected is { } member)
        {
            _camera.Position = new Vector2(member.Walker.Feet.X, member.Walker.Feet.Y);
        }
        ClampCamera();
    }

    /// <summary>Put the view where the camera is at once (new region, teleport), without easing.</summary>
    private void SnapCamera()
    {
        ClampCamera();
        _camera.ResetSmoothing();
    }

    public override void _Process(double delta)
    {
        if (_transitioning)
        {
            return; // fading to the next region
        }
        _clock += delta;
        Animate();
        FinishApproach();
        if (Session.ScriptHost.Dialogue.Prompt is GplShopPrompt shop && !_shop.IsOpen && _world[shop.Merchant] is { } merchant
            && _world[_selected] is { } who)
        {
            _shop.Open(_world, merchant, who);
        }

        Vector2 direction = Input.GetVector(Controls.PanLeft, Controls.PanRight, Controls.PanUp, Controls.PanDown);
        if (direction != Vector2.Zero)
        {
            _follow = false;
            _camera.Position += direction * PanSpeed * (float)delta / _camera.Zoom.X;
        }
        FollowCharacter();
        UpdateHover(force: false);
    }

    /// <summary>
    /// While a debug layer is on: outline the cell under the pointer and
    /// describe it in the panel (refreshed as the pointer moves, and a few
    /// times a second, since things move).
    /// </summary>
    private void UpdateHover(bool force)
    {
        _hoverPanel.Visible = _overlay.Active;
        if (!_overlay.Active)
        {
            _overlay.Hovered = null;
            return;
        }
        Vector2 at = GetGlobalMousePosition();
        var cell = new Cell((int)Math.Floor(at.X / Region.TileSize), (int)Math.Floor(at.Y / Region.TileSize));
        _hoverRefresh -= GetProcessDeltaTime();
        if (!force && _overlay.Hovered == cell && _hoverRefresh > 0)
        {
            return;
        }
        _hoverRefresh = 0.25;
        _overlay.Hovered = cell;
        string text = _overlay.Describe(cell);
        _hoverText.Text = (text.Length > 0 ? text + "\n" : "")
            + $"F1 flags {(_overlay.Flags ? "on" : "off")} · F3 grid {(_overlay.Grid ? "on" : "off")} · F4 numbers {(_overlay.Numbers ? "on" : "off")} · F6 scripts";
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (_useWith is not null && e.IsActionPressed(Controls.Back))
        {
            SetUseWith(null);
        }
        else if (_useWith is not null && !_dialogueWindow.Visible && e is InputEventMouseButton { ButtonIndex: MouseButton.Left or MouseButton.Right, Pressed: true })
        {
            // Use with…: the click picks the second object.
            WorldObject first = _useWith;
            SetUseWith(null);
            if (ObjectAt(GetGlobalMousePosition()) is { } second && second != first)
            {
                StartVerb(second, Verb.UseWith, first);
            }
        }
        else if (e.IsActionPressed(Controls.Back))
        {
            GetTree().ChangeSceneToFile(MainMenu.ScenePath);
        }
        else if (e.IsActionPressed(Controls.ToggleMapFlags))
        {
            _overlay.Flags = !_overlay.Flags;
            UpdateHover(force: true);
        }
        else if (e.IsActionPressed(Controls.QuickSave))
        {
            Save(QuickSave, "Quick save");
        }
        else if (e.IsActionPressed(Controls.QuickLoad))
        {
            QuickLoadGame();
        }
        else if (e.IsActionPressed(Controls.ToggleGrid))
        {
            _overlay.Grid = !_overlay.Grid;
            UpdateHover(force: true);
        }
        else if (e.IsActionPressed(Controls.ToggleObjectNumbers))
        {
            _overlay.Numbers = !_overlay.Numbers;
            UpdateHover(force: true);
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
        else if (e is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } right)
        {
            OpenVerbMenu(GetGlobalMousePosition(), right.Position);
        }
        else if (e.IsActionPressed(Controls.ToggleInventory) && !_inventory.Visible && !Session.Scripts.IsSuspended)
        {
            _inventory.Open(_world, _selected);
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
                && Session.Assets.Frames(data) is [var frame, ..]
                ? Textures.ToTexture(frame, _palette)
                : null;
            _portraits[number] = texture;
        }
        return texture;
    }

    /// <summary>The game-start script once per session, then the region's <c>MAS</c> script.</summary>
    /// <summary>Connect the dialogue window and the overlay to this region's scripts.</summary>
    private void AttachScripts()
    {
        _portraits.Clear();
        _dialogueWindow.Attach(Session.ScriptHost.Dialogue, Portrait);
        _overlay.Show(_region, _occupancy, Session.Scripts.Triggers, _world);
    }

    private void RunRegionScripts()
    {
        GplMachine scripts = Session.Scripts;
        AttachScripts();
        if (!Session.GameStarted)
        {
            Session.GameStarted = true;
            ReportScript("new game", scripts.NewGame());
        }
        int region = _region.Number;
        WhenScriptsFree(() =>
        {
            if (_region.Number == region)
            {
                ReportScript($"entered region {region}", scripts.EnterRegion(region));
                _overlay.Show(_region, _occupancy, scripts.Triggers, _world);
            }
        });
    }

    /// <summary>The front-most object whose sprite is under <paramref name="at"/> (map position), if any.</summary>
    private WorldObject? ObjectAt(Vector2 at) =>
        _objectSprites.Where(o => _worldObjects.ContainsKey(o.Object)).Select(o => (Object: _worldObjects[o.Object], o.Sprite))
            .Concat(_creatureNodes.Select(c => (Object: c.Key, Sprite: c.Value)))
            .Concat(_cloneSprites.Select(c => (Object: c.Key, Sprite: c.Value)))
            .Where(o => o.Sprite.Visible && o.Sprite.Texture is { } tex
                && new Rect2(o.Sprite.Position + o.Sprite.Offset, tex.GetSize()).HasPoint(at))
            .OrderByDescending(o => o.Sprite.Position.Y)
            .Select(o => o.Object)
            .FirstOrDefault();

    /// <summary>The verbs <paramref name="obj"/> offers (<see cref="ObjectVerbs"/>).</summary>
    private IReadOnlyList<Verb> VerbsOf(WorldObject obj) => ObjectVerbs.For(obj, _world.Items, Session.Scripts.Triggers);

    /// <summary>Right-click: the verb menu of the object under the pointer (<paramref name="screen"/>: where to show it), or its only verb done at once.</summary>
    private void OpenVerbMenu(Vector2 at, Vector2 screen)
    {
        if (ObjectAt(at) is not { } hit || Session.Scripts.IsSuspended)
        {
            return;
        }
        IReadOnlyList<Verb> verbs = VerbsOf(hit);
        _menuTarget = hit;
        if (verbs.Count == 1)
        {
            // Only one thing to do: do it, as DSUN.EXE does.
            ChooseVerb(verbs[0]);
            return;
        }
        _verbMenu.Open(hit.Name.Length > 0 ? hit.Name : $"Object {-hit.Id}", verbs, hit.Kind == ObjectKind.Creature, screen);
    }

    private void ChooseVerb(Verb verb)
    {
        if (_menuTarget is not { } target)
        {
            return;
        }
        _menuTarget = null;
        if (verb == Verb.UseWith)
        {
            SetUseWith(target);
            return;
        }
        StartVerb(target, verb, null);
    }

    /// <summary>Use with…: wait for the second object (null: stop waiting).</summary>
    private void SetUseWith(WorldObject? first)
    {
        _useWith = first;
        _prompt.Visible = first is not null;
        _prompt.Text = first is null ? "" : $"Use {Called(first)} with… (click an object; Esc cancels)";
    }

    /// <summary>
    /// Do <paramref name="verb"/> to <paramref name="target"/>: Look at once;
    /// the others once the selected character has walked up to it
    /// (<see cref="Approach"/>). Without a party it acts at once.
    /// </summary>
    private void StartVerb(WorldObject target, Verb verb, WorldObject? with)
    {
        _pendingUse = null;
        if (verb == Verb.Look || Selected is not { } member)
        {
            Act(target, _selected, verb, with);
            return;
        }
        StartApproach(member.Walker, _selected, target, verb, with, tries: 0);
    }

    /// <summary>Walk <paramref name="walker"/> up to <paramref name="target"/>, or act at once if it is in reach.</summary>
    private void StartApproach(Walker walker, int actor, WorldObject target, Verb verb, WorldObject? with, int tries)
    {
        IReadOnlyList<Cell> cells = _world.CellsOf(target);
        if (Approach.Distance(walker.Cell, cells) <= Approach.Reach)
        {
            walker.Stop();
            Act(target, actor, verb, with);
            return;
        }
        Cell? goal = Approach.Goal(c => c == walker.Cell || walker.CanStand(c, _region.IsBlocked), walker.Cell, [.. cells]);
        if (goal is not { } cell || cell == walker.Cell)
        {
            // Out of reach: act from here, so no script is ever out of reach.
            AddScriptLine($"{Describe(target)}: can't get nearer, acting from here");
            walker.Stop();
            Act(target, actor, verb, with);
            return;
        }
        walker.WalkTo(cell, _region.IsBlocked);
        _pendingUse = (walker, actor, target, verb, with, tries);
        _follow = true;
    }

    /// <summary>When the character walking up to an object stops, do the verb (or try again if the object moved away).</summary>
    private void FinishApproach()
    {
        if (_pendingUse is not { } pending || pending.Walker.IsWalking)
        {
            return;
        }
        _pendingUse = null;
        if (!_party.Any(m => m.Walker == pending.Walker) || Session.Scripts.IsSuspended)
        {
            return;
        }
        if (Approach.Distance(pending.Walker.Cell, _world.CellsOf(pending.Target)) > Approach.MaxReach && pending.Tries < 2)
        {
            StartApproach(pending.Walker, pending.Actor, pending.Target, pending.Verb, pending.With, pending.Tries + 1); // it walked off
            return;
        }
        Act(pending.Target, pending.Actor, pending.Verb, pending.With);
    }

    /// <summary>
    /// Do a verb: run the object's trigger for it (scripts see the object by
    /// its index; triggers match its id, −<c>OJFF</c>). Use works a door or a
    /// lever first (<see cref="UseObject"/>); Look without a look trigger
    /// names the object; Use with runs the trigger pairing the two objects.
    /// What the engine can't do yet (carrying items, combat) is said so.
    /// </summary>
    private void Act(WorldObject target, int actor, Verb verb, WorldObject? with = null)
    {
        GplMachine scripts = Session.Scripts;
        GplTriggerList? list = verb switch
        {
            Verb.Talk => GplTriggerList.TalkTo,
            Verb.Get => GplTriggerList.Pickup,
            Verb.Use => GplTriggerList.Use,
            Verb.Attack => GplTriggerList.Attack,
            Verb.Look => GplTriggerList.Look,
            _ => null,
        };
        string what = $"{VerbText(verb)} {Describe(target)}";
        if (verb == Verb.Use && UseObject(target, actor))
        {
            return;
        }
        if (verb == Verb.UseWith && with is not null)
        {
            if (scripts.OnUseWith(actor, with.Index, target.Index) is { } paired)
            {
                ReportScript($"use {Describe(with)} with {Describe(target)}", paired);
            }
            else
            {
                Say($"use {Describe(with)} with {Describe(target)}", "Nothing happens.");
            }
            return;
        }
        if (verb == Verb.Get && target.Item is not null && _world[actor] is { } taker)
        {
            // 7BEB:0A60: the item goes into the pack first, then its pickup trigger runs.
            if (!_world.PickUp(taker, target))
            {
                Say(what, $"{taker.Name} can't carry any more.");
                return;
            }
            AddScriptLine($"> {taker.Name} picks up {Describe(target)}");
            if (scripts.OnObject(GplTriggerList.Pickup, actor, target.Index) is { } picked)
            {
                ReportScript(what, picked);
            }
            return;
        }
        if (list is { } l && scripts.OnObject(l, actor, target.Index) is { } run)
        {
            ReportScript(what, run);
            return;
        }
        switch (verb)
        {
            case Verb.Look:
                Say(what, Called(target));
                break;
            case Verb.Get:
                Say(what, $"{Called(target)} can't be picked up.");
                break;
            case Verb.Attack:
                Say(what, "Combat isn't in the engine yet.");
                break;
            default:
                Say(what, "Nothing happens.");
                break;
        }
    }

    /// <summary>Tell the player something (narration in the dialogue window) and log it.</summary>
    private void Say(string what, string message)
    {
        AddScriptLine($"> {what}: {message}");
        if (!_checking)
        {
            Session.ScriptHost.Dialogue.Append(message, 98);
        }
    }

    private static string VerbText(Verb verb) => verb switch
    {
        Verb.Talk => "talk to",
        Verb.Get => "get",
        Verb.Use => "use",
        Verb.UseWith => "use with",
        Verb.Attack => "attack",
        _ => "look at",
    };

    private static string Called(WorldObject obj) => obj.Name.Length > 0 ? obj.Name : "it";

    /// <summary>
    /// Use a door or a lever-like object (<c>6BB8:0B56</c>): a door opens or
    /// closes, then its use trigger runs, then, if the door stayed as it was,
    /// the game's message ("THE DOOR IS LOCKED", …). Returns false for other objects.
    /// </summary>
    private bool UseObject(WorldObject obj, int actor)
    {
        if (Session.Scripts.IsSuspended)
        {
            return false;
        }
        DoorUse use = _world.UseObject(obj);
        if (!obj.IsDoor && !use.Toggled)
        {
            return false;
        }
        AddScriptLine(obj.IsDoor
            ? $"use {Describe(obj)}: {(use.Toggled ? (use.WasClosed ? "opens" : "closes") : "stays " + (use.WasClosed ? "closed" : "open"))}"
            : $"use {Describe(obj)}: toggled");
        if (Session.Scripts.OnObject(GplTriggerList.Use, actor, obj.Index) is { } run)
        {
            ReportScript($"use {Describe(obj)}", run);
        }
        if (_world.DoorRefusal(use) is { } message && !Session.Scripts.IsSuspended)
        {
            AddScriptLine($"    {message}");
            Session.ScriptHost.Dialogue.Append(message, 98);
        }
        _overlay.QueueRedraw();
        return true;
    }

    private static string Describe(WorldObject obj) =>
        obj.Name.Length > 0 ? $"{obj.Name} (OJFF {-obj.Id})" : $"OJFF {-obj.Id}";

    /// <summary>Log a script run and the world commands it issued (PRINT_STRING as its text).</summary>
    private void ReportScript(string what, GplRunResult run)
    {
        _scriptRuns++;
        _scriptErrors += run.Error is null || run.Suspended ? 0 : 1;
        string status = run.Paused ? $", paused by the debugger ({Session.Scripts.PausedFor}) before {GplBreakpoints.Format(Session.Scripts.Current)}"
            : run.Suspended ? ", waiting for the player" : run.Error is null ? "" : $", stopped: {run.Error}";
        AddScriptLine($"> {what}: {run.Entry.Kind} {run.Entry.File} @{run.Entry.Address:x4}, {run.Instructions} instructions{status}");
        if (_checkingVerbs && run.Error is not null && !run.Suspended)
        {
            GD.Print($"  {Session.Install!.RegionFiles[_regionIndex]}: {what}: {run.Error}");
        }
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
        _debugger.ScriptsRan();
        if (!run.Suspended)
        {
            RunWhenFree();
        }
        if (!run.Suspended && !Session.Scripts.IsSuspended && _world.PendingRegion is { } go && !_transitioning)
        {
            _world.PendingRegion = null;
            AddScriptLine($"    party leaves for region {go.Region} ({go.X},{go.Y})");
            Travel(go.Region, new Cell(go.X, go.Y));
        }
    }

    /// <summary>
    /// Run <paramref name="start"/> now, or, while a script is paused (for the
    /// player or in the debugger), once it has finished: scripts the game
    /// starts one after another, like the region's after the game-start one,
    /// would otherwise be refused.
    /// </summary>
    private void WhenScriptsFree(Action start)
    {
        if (Session.Scripts.IsSuspended)
        {
            _whenFree.Enqueue(start);
        }
        else
        {
            start();
        }
    }

    private void RunWhenFree()
    {
        while (_whenFree.Count > 0 && !Session.Scripts.IsSuspended)
        {
            _whenFree.Dequeue()();
        }
    }

    /// <summary>The debugger's Stop: drop the paused script and whatever it was saying.</summary>
    private void StopScript()
    {
        if (!Session.Scripts.IsSuspended)
        {
            return;
        }
        AddScriptLine($"> stopped by the debugger before {GplBreakpoints.Format(Session.Scripts.Current)}");
        Session.Scripts.Abandon();
        Session.ScriptHost.Dialogue.Clear();
        _debugger.ScriptsRan();
        RunWhenFree();
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
            if (Selected?.Walker == obj.Walker)
            {
                FollowCharacter();
                SnapCamera();
            }
            return;
        }
        if (_creatureNodes.ContainsKey(obj))
        {
            DrawCreature(obj);
            return;
        }
        if (obj.IsClone && obj.Walker is null && obj.Source is { } cloned)
        {
            // A cloned thing: a sprite of its own, standing at its cell's bottom centre.
            if (!_cloneSprites.TryGetValue(obj, out Sprite2D? copy))
            {
                copy = AddSprite(cloned.Frame, new Vector2(cloned.EtabX, cloned.EtabY), new Vector2(cloned.X, cloned.Y), cloned.Mirrored);
                if (copy is null)
                {
                    return;
                }
                _cloneSprites[obj] = copy;
            }
            copy.Position = new Vector2(obj.FixedCell.X * Region.TileSize + Region.TileSize / 2, (obj.FixedCell.Y + 1) * Region.TileSize - 1);
            copy.Visible = !obj.Hidden;
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
            ScmdAnimator old = animated.Animator;
            animated.Animator = AnimatorFor(source, animated.Script, start, old.HotX, old.HotY);
        }
    }

    /// <summary>
    /// Leave for region <paramref name="number"/>: fade to black, enter it,
    /// fade back in (at once in headless checks). Nothing moves meanwhile.
    /// </summary>
    private void Travel(int number, Cell start)
    {
        if (_checking || _instantTravel || _fade is null || _entering)
        {
            // Headless, or already black (an arrival trigger sending the party on): go at once.
            EnterRegion(number, start);
            return;
        }
        _transitioning = true;
        _verbMenu.Close();
        Tween tween = CreateTween();
        tween.TweenProperty(_fade, "color:a", 1f, FadeSeconds);
        tween.TweenCallback(Callable.From(() =>
        {
            _transitioning = false;
            _entering = true;
            try
            {
                EnterRegion(number, start);
            }
            finally
            {
                _entering = false;
            }
            if (!Session.Scripts.IsSuspended && _world.PendingRegion is null)
            {
                Save(AutoSave, "Autosave", quiet: true);
            }
        }));
        tween.TweenProperty(_fade, "color:a", 0f, FadeSeconds);
    }

    /// <summary>A free cell 3–6 cells from <paramref name="at"/> where <paramref name="w"/> can stand (for the exits check).</summary>
    private Cell? NearestFree(Walker w, Cell at)
    {
        for (int r = 3; r <= 6; r++)
        {
            foreach (var (dx, dy) in new[] { (0, r), (r, 0), (0, -r), (-r, 0), (r, r), (-r, r), (r, -r), (-r, -r) })
            {
                var c = new Cell(at.X + dx, at.Y + dy);
                if (w.CanStand(c, _region.IsBlocked))
                {
                    return c;
                }
            }
        }
        return null;
    }

    private const float FadeSeconds = 0.25f;
    private bool _transitioning, _entering;

    /// <summary>Change region at once, without the fade (headless checks that step the game themselves).</summary>
    private bool _instantTravel;
    private ColorRect? _fade;

    // -- saved games -------------------------------------------------------------------

    public const string QuickSave = "quick", AutoSave = "auto";

    /// <summary>A game to load when the view opens (set by the load screen).</summary>
    public static SaveGame? PendingLoad { get; set; }

    private Label _notice = null!;
    private Tween? _noticeTween;

    /// <summary>
    /// Save to <paramref name="file"/> in the saves folder. Not while a script
    /// waits for the player or the party is between regions: the save would
    /// have to capture a script half run.
    /// </summary>
    private void Save(string file, string name, bool quiet = false)
    {
        if (_checking)
        {
            return;
        }
        if (Session.Scripts.IsSuspended || _transitioning || _world.PendingRegion is not null)
        {
            if (!quiet)
            {
                Notice(Session.Scripts.PausedFor is not null
                    ? "Can't save now: a script is paused in the debugger (F8 continues)."
                    : "Can't save now: finish the conversation first.");
            }
            return;
        }
        try
        {
            var party = _party.Select((m, i) => new PartyMemberSave(m.Sprite.Ojff, m.Walker.Cell.X, m.Walker.Cell.Y, m.Walker.Facing,
                i < Session.Party.Count ? Session.Party[i] : null));
            SaveGame save = SaveGame.Capture(name, Session.Scripts, Session.ScriptHost.EngineVariables, _world, Session.Regions,
                party, _selected, Session.Mods.Mods.Select(m => m.Name), Session.ScriptHost.PartyMembers);
            save.Write(Session.SavePath(file));
            AddScriptLine($"    saved to {Session.SavePath(file)}");
            if (!quiet)
            {
                Notice($"{name}: saved.");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Notice($"Couldn't save: {e.Message}");
        }
    }

    private void QuickLoadGame()
    {
        string path = Session.SavePath(QuickSave);
        if (!File.Exists(path))
        {
            Notice("No quick save yet (F5 saves).");
            return;
        }
        try
        {
            LoadSaved(SaveGame.Read(path));
        }
        catch (InvalidDataException e)
        {
            Notice($"Couldn't load the quick save: {e.Message}");
        }
    }

    /// <summary>
    /// Load <paramref name="save"/>: a fresh game with the saved scripts'
    /// state and regions, then its region as saved, without running its
    /// script, and the party where it stood.
    /// </summary>
    /// <returns>False, with a notice saying why, if it can't be loaded; the game in progress then goes on.</returns>
    private bool LoadSaved(SaveGame save)
    {
        int index = Session.Install!.RegionFiles.ToList().FindIndex(f => f.Equals($"RGN{save.Region:X2}.GFF", StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            Notice($"Can't load: no region file for region {save.Region}.");
            return false;
        }
        try
        {
            // Try it on a scratch machine first, so a bad save leaves the game in progress alone.
            save.Restore(new GplMachine(new GffScripts(new GffSet([])), new LoggingGplHost(), [], new DarkSun.Game.Rules.BorlandRandom()),
                new int[15], new RegionMemory());
        }
        catch (InvalidDataException e)
        {
            Notice($"Can't load: {e.Message}");
            return false;
        }
        Session.NewGame();
        save.Restore(Session.Scripts, Session.ScriptHost.EngineVariables, Session.Regions);
        Session.GameStarted = true;
        if (save.Party.Any(m => m.Character is not null))
        {
            Session.Party.Clear();
            Session.Party.AddRange(save.Party.Select(m => m.Character).OfType<DarkSun.Game.Characters.Character>());
        }
        _world = null!; // nothing of the game being left is remembered
        _transitioning = false;
        _fade?.Set("color", new Color(0, 0, 0, 0));
        ShowRegion(index, saved: save);
        save.RestoreParty(Session.ScriptHost.PartyMembers, i => _world.NewItem(i.Rdff, i.Quantity, i.Slot));
        _scriptLogSeen = Session.ScriptHost.Log.Count;
        var missing = save.Mods.Except(Session.Mods.Mods.Select(m => m.Name)).ToList();
        Notice($"Loaded {save.Name} ({save.Saved.LocalDateTime:g})." + (missing.Count > 0 ? $" Saved with mods now off: {string.Join(", ", missing)}." : ""));
        return true;
    }

    /// <summary>The party as saved, each where it stood.</summary>
    private void PlaceSavedParty(SaveGame save)
    {
        List<CharacterSprite> sprites = Characters();
        _nextSprite = 0;
        foreach (PartyMemberSave m in save.Party.Take(PartySize))
        {
            CharacterSprite? sprite = sprites.FirstOrDefault(c => c.Ojff == m.Sprite);
            AddCharacter(new Cell(m.X, m.Y), sprite: sprite, facing: m.Facing);
        }
        _nextSprite = _party.Count;
        _selected = Math.Clamp(save.Selected, 0, Math.Max(0, _party.Count - 1));
        _world.ActiveMember = _selected;
        foreach (PartyMember member in _party)
        {
            _lastCells[member.Walker] = member.Walker.Cell;
        }
    }

    /// <summary>A line at the top of the screen for a few seconds.</summary>
    private void Notice(string text)
    {
        AddScriptLine($"    {text}");
        _notice.Text = text;
        _notice.Visible = true;
        _notice.Modulate = Colors.White;
        _noticeTween?.Kill();
        _noticeTween = CreateTween();
        _noticeTween.TweenInterval(2.5);
        _noticeTween.TweenProperty(_notice, "modulate:a", 0f, 0.5);
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
    private int _scriptRuns, _scriptErrors;
    private bool _checkingVerbs;

    /// <summary>
    /// A new game, a region visited and left, a door moved, a global flag
    /// set and the party moved; saved, all of it changed again, loaded back;
    /// prints what didn't come back.
    /// </summary>
    /// <summary>
    /// <c>--check-debugger</c>: pause the game-start script at its first
    /// command, step it, stop the region script at a breakpoint, set a
    /// variable, run on; then show every script as source and check its line
    /// map covers each command.
    /// </summary>
    private async void CheckDebugger()
    {
        var problems = new List<string>();
        void Expect(bool ok, string what)
        {
            if (!ok)
            {
                problems.Add(what);
            }
        }
        GplMachine m = Session.Scripts;
        m.BreakOnNextRun = true;
        _regionIndex = 0;
        EnterRegion(NewGameStart.Region, NewGameStart.At);
        int region = _region.Number;
        Expect(m.PausedFor == "step" && m.Current == new GplEntry(GplMachine.GameStartFile, 0, GplFileKind.Mas), $"paused at the game start: {m.PausedFor} {GplBreakpoints.Format(m.Current)}");
        Expect(_debugger.Visible && _debugger.Shown?.Script == new GplEntry(GplMachine.GameStartFile, 0, GplFileKind.Mas), "the debugger shows MAS 99");
        Expect(_debugger.Shown?.LineAt(0) is int first && _debugger.IsExecuting(first), "the first command is marked");
        Expect(_whenFree.Count == 1, $"the region script waits for it ({_whenFree.Count} queued)");
        for (int i = 0; i < 3; i++)
        {
            _debugger.Go(GplStep.Over);
        }
        Expect(m.PausedFor == "step" && m.Current.Address > 0, $"stepped: {GplBreakpoints.Format(m.Current)}");
        Expect(_debugger.Shown?.LineAt(m.Current.Address) is int now && _debugger.IsExecuting(now), "the stepped-to command is marked");

        var regionStart = new GplEntry(region, 0, GplFileKind.Mas);
        Session.Breakpoints.Add(regionStart);
        _debugger.Go(GplStep.None); // MAS 99 runs out; the region's script starts and stops at once
        Expect(m.PausedFor == "breakpoint" && m.Current == regionStart, $"stopped at the region script: {m.PausedFor} {GplBreakpoints.Format(m.Current)}");
        Expect(_debugger.Shown?.Script == regionStart && _debugger.IsBreakpointShown(_debugger.Shown.LineAt(0)!.Value), "the breakpoint shows in the gutter");
        // A click in the gutter, as the player makes it, once the panel is laid out.
        for (int i = 0; i < 2; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        int clickLine = _debugger.Shown!.LineAt(_debugger.Shown.LineOf.Keys.Order().Skip(2).First())!.Value;
        GplEntry clicked = regionStart with { Address = _debugger.Shown.AddressAt(clickLine)!.Value };
        Vector2 point = _debugger.GutterPoint(clickLine);
        foreach (bool down in new[] { true, false })
        {
            GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = down, Position = point, GlobalPosition = point });
        }
        Expect(Session.Breakpoints.Contains(clicked) && _debugger.IsBreakpointShown(clickLine), $"a gutter click at line {clickLine} sets a breakpoint");
        Session.Breakpoints.Remove(clicked);
        Expect(GplVariables.Set(m, "gnum[199] = 1234") is null && GplVariables.Get(m, "gnum[199]") == "1234", "set gnum[199]");
        Session.Breakpoints.Remove(regionStart);
        _debugger.Go(GplStep.None);
        Expect(!m.IsSuspended && _whenFree.Count == 0, "ran on to the end");
        Expect(_scriptLines.Count(l => l.StartsWith($"> entered region {region}") || l.StartsWith("> debugger")) >= 2, "the region script was reported");
        Expect(GplVariables.Get(m, "gnum[199]") == "1234", "gnum[199] kept");

        // Every script, as source.
        GffFile gpl = Session.Open("GPLDATA.GFF");
        int scripts = 0, commands = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var r in gpl.DescribeResources().Where(r => r.Tag is "GPL " or "MAS "))
        {
            var kind = r.Tag == "MAS " ? GplFileKind.Mas : GplFileKind.Gpl;
            GplSource? source = GplSource.For(m, r.Number, kind, Session.Mods.ReplacementPath("GPLDATA.GFF", r.Tag, r.Number));
            byte[] code = m.Code(r.Number, kind)!;
            var decoder = new GplDecoder(code, m.FieldTypes);
            int count = 0;
            try
            {
                for (int at = 0; at < code.Length; count++)
                {
                    at += decoder.DecodeAt(at).Length;
                }
            }
            catch (GplDecodeException)
            {
            }
            scripts++;
            commands += count;
            if (source is null || source.Origin == "listing" || source.LineOf.Count < count)
            {
                problems.Add($"{r.Tag.Trim()} {r.Number}: {source?.Origin ?? "no source"}, {source?.LineOf.Count ?? 0} of {count} commands mapped");
            }
        }
        GD.Print($"debugger: paused at MAS 99, stepped, stopped at MAS {region} @0000 and ran on; "
            + $"{scripts} scripts shown as source in {watch.Elapsed.TotalSeconds:F1} s, {commands} commands mapped to lines");
        foreach (string p in problems)
        {
            GD.Print($"  PROBLEM: {p}");
        }
        GD.Print(problems.Count == 0 ? "debugger: OK" : $"debugger: {problems.Count} problems");
        GetTree().Quit(problems.Count == 0 ? 0 : 1);
    }

    /// <summary>
    /// <c>--check-items</c>, with the game's own items: make some for the
    /// active member, drop one on the map and pick it up, wear boots and
    /// SEARCH for them, leave an item on the ground and come back to it, turn
    /// a creature into an item (<c>REQUEST(9)</c>), hurt and rest, and save
    /// and load what the party carries.
    /// </summary>
    private void CheckItems()
    {
        AnswerForThePlayer();
        var problems = new List<string>();
        void Expect(bool ok, string what)
        {
            if (!ok)
            {
                problems.Add(what);
            }
        }
        const int Water = 1095, Boots = 1051, Grain = 1254;
        IReadOnlyList<PartyMemberState> party = Session.ScriptHost.PartyMembers;
        WorldObject me = _world[0]!;
        Expect(_world.Give(2, -Water, 0, GameWorld.None) == 2, "GIVE made two pots of water");
        Expect(_world.Give(1, -Boots, 0, GameWorld.None) == 1, "GIVE made boots");
        Expect(_world.Give(5, -Grain, 1, GameWorld.None) == 5, "GIVE made grain");
        Item pot = party[0].Items.First(i => i.Rdff == Water);

        WorldObject? ground = _world.DropItem(me, pot, 1);
        Expect(ground is { IsClone: true, Source.EtabIndex: -1 } && ground.Item is not null, "dropped: a new item object on the map");
        Expect(ground is not null && _cloneSprites.ContainsKey(ground), "the dropped item has a sprite");
        Expect(ground is not null && _world.PickUp(me, ground) && _world[ground.Index] is null, "picked it up again");
        Expect(party[0].Items.Count(i => i.Rdff == Water) == 2, "two pots again");

        Item boots = party[0].Items.First(i => i.Rdff == Boots);
        Expect(_world.Equip(me, boots) && boots.Slot == 13, $"boots worn on the feet (slot {boots.Slot})");
        Expect(_world.Search(GameWorld.WholeParty, [new(72, 4, -Boots), new(85, 4, 13)]) == 1, "SEARCH finds the boots worn");
        Expect(_world.Search(GameWorld.WholeParty, [new(72, 4, -Water), new(72, 3, 0)]) == 2, "SEARCH counts two pots");

        // Left on the ground, there on the next visit.
        WorldObject? left = _world.DropItem(me, party[0].Items.First(i => i.Rdff == Water), 1);
        int leftAt = left?.Index ?? -1;
        int home = _regionIndex;
        ShowRegion(home + 1);
        AnswerForThePlayer();
        ShowRegion(home);
        AnswerForThePlayer();
        Expect(_world[leftAt] is { Item: not null } back && back.Id == -Water && _cloneSprites.ContainsKey(back), "the dropped pot is still there after leaving");

        // A creature turned into an item, then the party hurt and rested.
        if (_world.Objects.FirstOrDefault(o => o.Kind == ObjectKind.Creature && !o.Hidden) is { } creature)
        {
            int before = _world.Objects.Count();
            _world.Transform(creature.Index, Boots, atStart: false);
            Expect(_world[creature.Index] is null && _world.Objects.Count() == before
                && _world.First(-Boots) is { Item: not null }, "REQUEST(9) made boots where the creature stood");
        }
        party[0].Vitals.Hp = party[0].Vitals.MaxHp = 12;
        _world.Damage(0, 5, percent: false);
        Expect(party[0].Vitals.Hp == 7, "DAMAGE took 5 hit points");
        _world.Rest(raiseDead: false);
        Expect(party[0].Vitals.Hp == 12, "REQUEST(4) healed");

        // Saved and loaded.
        var carried = party.Select(p => string.Join(",", p.Items.Select(i => $"{i.Rdff}x{i.Quantity}@{i.Slot}"))).ToList();
        string file = "check-" + Guid.NewGuid().ToString("N")[..8];
        Save(file, "Check");
        string path = Session.SavePath(file);
        party[0].Items.Clear();
        LoadSaved(SaveGame.Read(path));
        File.Delete(path);
        var loaded = Session.ScriptHost.PartyMembers.Select(p => string.Join(",", p.Items.Select(i => $"{i.Rdff}x{i.Quantity}@{i.Slot}"))).ToList();
        Expect(carried.SequenceEqual(loaded), $"the party's items came back from the save ({string.Join(" | ", loaded)})");
        Expect(Session.ScriptHost.PartyMembers[0].Vitals.Hp == 12, "hit points came back");

        GD.Print(problems.Count == 0
            ? $"items: made, dropped, picked up, worn, searched, left and found, transformed, hurt, rested, saved and loaded ({carried[0]}; {carried[1]})"
            : "items: " + string.Join("; ", problems));
        GetTree().Quit(problems.Count > 0 ? 1 : 0);
    }

    private void CheckSave()
    {
        AnswerForThePlayer();
        int home = _regionIndex;
        ShowRegion(home + 1);
        AnswerForThePlayer();
        ShowRegion(home);
        AnswerForThePlayer();
        WorldObject? door = _world.Objects.FirstOrDefault(o => o.IsDoor);
        if (door is not null)
        {
            _world.SetDoor(door, (door.DoorFlags & Doors.Closed) != 0 ? 0 : Doors.Closed, force: true);
        }
        Session.Scripts.Memory.Area(GplArea.GlobalFlags)[50] = 0x5A;
        Walker leader = _party[0].Walker;
        if (_world.NearestCell(new Cell(leader.Cell.X + 4, leader.Cell.Y + 2), c => leader.CanStand(c, _region.IsBlocked)) is { } cell)
        {
            leader.Teleport(cell, _region.IsBlocked);
        }
        var expected = (Region: _region.Number, Door: door?.DoorFlags, Leader: leader.Cell, Facing: leader.Facing,
            Flag: (byte)0x5A, Remembered: Session.Regions.States.Count(r => r.Region != _region.Number) + 1, Random: Session.Scripts.Random.State,
            Talk: Session.Scripts.Triggers.Count(GplTriggerList.TalkTo), Party: _party.Count);
        string file = "check-" + Guid.NewGuid().ToString("N")[..8];
        Save(file, "Check");
        string path = Session.SavePath(file);
        if (!File.Exists(path))
        {
            GD.Print("save check: nothing was saved");
            GetTree().Quit(1);
            return;
        }

        // Change everything, then load.
        Session.Scripts.Memory.Area(GplArea.GlobalFlags)[50] = 0;
        ShowRegion(home + 2);
        AnswerForThePlayer();
        LoadSaved(SaveGame.Read(path));
        File.Delete(path);

        var problems = new List<string>();
        void Expect<T>(string what, T want, T got)
        {
            if (!EqualityComparer<T>.Default.Equals(want, got))
            {
                problems.Add($"{what}: {got} instead of {want}");
            }
        }
        Expect("region", expected.Region, _region.Number);
        Expect("door flags", expected.Door, door is null ? null : _world[door.Index]?.DoorFlags);
        Expect("leader's cell", expected.Leader, _party[0].Walker.Cell);
        Expect("leader's facing", expected.Facing, _party[0].Walker.Facing);
        Expect("global flag byte", expected.Flag, Session.Scripts.Memory.Area(GplArea.GlobalFlags)[50]);
        Expect("regions remembered", expected.Remembered, Session.Regions.Count);
        Expect("random state", expected.Random, Session.Scripts.Random.State);
        Expect("talk triggers", expected.Talk, Session.Scripts.Triggers.Count(GplTriggerList.TalkTo));
        Expect("party size", expected.Party, _party.Count);
        GD.Print(problems.Count == 0
            ? $"save check: region {expected.Region}, door, party, globals, triggers, random and {Session.Regions.Count} regions came back"
            : "save check: " + string.Join("; ", problems));
        GetTree().Quit(problems.Count > 0 ? 1 : 0);
    }

    /// <summary>
    /// For every region: open or close every door, send a creature off the
    /// map, move another and clone a thing; leave for another region and come
    /// back; and check that all of it is as it was left (in the world, the
    /// map flags and the drawn sprites).
    /// </summary>
    private void CheckMemory()
    {
        IReadOnlyList<string> files = Session.Install!.RegionFiles;
        int failures = 0;
        for (int index = 0; index < files.Count; index++)
        {
            ShowRegion(index);
            AnswerForThePlayer();
            var doors = _world.Objects.Where(o => o.IsDoor).ToList();
            foreach (WorldObject door in doors)
            {
                _world.SetDoor(door, (door.DoorFlags & Doors.Closed) != 0 ? 0 : Doors.Closed, force: true);
            }
            var creatures = _world.Objects.Where(o => o.Kind == ObjectKind.Creature && !o.Hidden).ToList();
            WorldObject? gone = creatures.FirstOrDefault();
            WorldObject? moved = creatures.Skip(1).FirstOrDefault(c => c.Walker is not null);
            if (gone is not null)
            {
                // Off the map: to region 255, or (in region 255 itself) to another one.
                _world.Teleport(gone.Index, _region.Number == Region.TestRegion ? 2 : Region.TestRegion, 0, 0);
            }
            Cell? movedTo = null;
            if (moved is not null && _world.NearestCell(new Cell(moved.Cell.X + 3, moved.Cell.Y), c => moved.Walker!.CanStand(c, _region.IsBlocked)) is { } target)
            {
                _world.Teleport(moved.Index, _region.Number, target.X, target.Y);
                movedTo = moved.Cell;
            }
            WorldObject? thing = _world.Objects.FirstOrDefault(o => o.Kind == ObjectKind.Thing && !o.IsDoor && !o.Hidden && o.Id != 0);
            int clones = thing is null ? 0 : _world.Clone(thing.Id, 1, thing.Cell.X + 1, thing.Cell.Y);
            RegionState left = _world.Snapshot();
            var doorStates = doors.ToDictionary(d => d.Index, d => d.DoorFlags);

            ShowRegion(index + 1);
            AnswerForThePlayer();
            ShowRegion(index);
            AnswerForThePlayer();

            var problems = new List<string>();
            foreach (var (i, flags) in doorStates)
            {
                if (_world[i]?.DoorFlags != flags)
                {
                    problems.Add($"door #{i} flags {_world[i]?.DoorFlags} instead of {flags}");
                }
            }
            byte[] now = _world.Snapshot().Flags;
            int differing = Enumerable.Range(0, Math.Min(now.Length, left.Flags.Length)).Count(i => now[i] != left.Flags[i]);
            if (differing > 0)
            {
                problems.Add($"{differing} map cells' flags differ");
            }
            if (gone is not null && _world[gone.Index] is { Hidden: false })
            {
                problems.Add($"#{gone.Index} is back on the map");
            }
            if (gone is not null && _creatureNodes.FirstOrDefault(c => c.Key.Index == gone.Index).Value is { Visible: true })
            {
                problems.Add($"#{gone.Index} is still drawn");
            }
            if (movedTo is { } cell && moved is not null && _world[moved.Index]?.Cell != cell)
            {
                problems.Add($"#{moved.Index} at {_world[moved.Index]?.Cell} instead of {cell}");
            }
            int clonesNow = _world.Objects.Count(o => o.IsClone);
            if (clonesNow < clones)
            {
                problems.Add($"{clonesNow} clones instead of {clones}");
            }
            if (clones > 0 && _world.Objects.First(o => o.IsClone) is { Walker: null } c && !_cloneSprites.ContainsKey(c) && thing!.Source!.Frame.Width > 0)
            {
                problems.Add("the cloned thing isn't drawn");
            }
            failures += problems.Count > 0 ? 1 : 0;
            GD.Print($"{files[index]}: {doors.Count} doors, {(gone is null ? 0 : 1)} gone, {(movedTo is null ? 0 : 1)} moved, {clones} cloned: "
                + (problems.Count == 0 ? "as left" : string.Join("; ", problems)));
        }
        GD.Print($"region memory: {files.Count - failures}/{files.Count} regions came back as left");
        GetTree().Quit(failures > 0 ? 1 : 0);
    }

    /// <summary>
    /// For every region: the verbs each object's menu offers, and each of them
    /// done (without walking) to every object that offers it; prints the
    /// counts and how many scripts ran or stopped with an error.
    /// </summary>
    private void CheckVerbs()
    {
        IReadOnlyList<string> files = Session.Install!.RegionFiles;
        var offered = Enum.GetValues<Verb>().ToDictionary(v => v, _ => 0);
        int objects = 0;
        (_scriptRuns, _scriptErrors) = (0, 0);
        for (int index = 0; index < files.Count; index++)
        {
            ShowRegion(index);
            AnswerForThePlayer();
            int number = _region.Number;
            var targets = _world.Objects.Where(o => o.Kind != ObjectKind.Party && !o.Hidden).ToList();
            foreach (WorldObject obj in targets)
            {
                objects++;
                foreach (Verb verb in VerbsOf(obj))
                {
                    offered[verb]++;
                    if (verb == Verb.UseWith || _region.Number != number)
                    {
                        continue;
                    }
                    Act(obj, 0, verb);
                    AnswerForThePlayer();
                }
            }
        }
        GD.Print($"verbs: {objects} objects in {files.Count} regions; offered "
            + string.Join(", ", offered.Select(o => $"{o.Key} {o.Value}")) + $"; {_scriptRuns} script runs, {_scriptErrors} stopped with an error");
        GetTree().Quit();
    }

    /// <summary>
    /// For every region: walk the active member into each move trigger as a
    /// player would, one step from a neighbouring free cell (so it faces the
    /// way it moved, which the map-edge exits of <c>GPL 80</c> test), and use
    /// every object with a use trigger; answer Yes, and report what takes the
    /// party to another region, and the regions no exit leads to.
    /// </summary>
    private void CheckExits()
    {
        IReadOnlyList<string> files = Session.Install!.RegionFiles;
        int exits = 0;
        var reached = new HashSet<int>();
        (Cell Step, string Name)[] steps = [(new Cell(0, -1), "north"), (new Cell(1, 0), "east"), (new Cell(0, 1), "south"), (new Cell(-1, 0), "west")];
        string? only = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--only="))?[7..];
        bool verbose = OS.GetCmdlineUserArgs().Contains("-v");
        for (int index = 0; index < files.Count; index++)
        {
            if (only is not null && !files[index].StartsWith(only, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            ShowRegion(index);
            AnswerForThePlayer();
            int number = _region.Number;
            var spots = Session.Scripts.Triggers.Records(GplTriggerList.MoveTile).Select(t => (t.A, t.B, W: 1, H: 1, Kind: "tile"))
                .Concat(Session.Scripts.Triggers.Records(GplTriggerList.MoveBox).Select(t => (t.A, t.B, W: (int)t.B8, H: (int)t.B9, Kind: "box"))).ToList();
            var found = new List<string>();
            foreach (var (x0, y0, width, height, kind) in spots)
            {
                var cells = (from dy in Enumerable.Range(0, Math.Max(1, height))
                             from dx in Enumerable.Range(0, Math.Max(1, width))
                             select new Cell(x0 + dx, y0 + dy)).ToList();
                bool Inside(Cell c) => c.X >= x0 && c.X < x0 + Math.Max(1, width) && c.Y >= y0 && c.Y < y0 + Math.Max(1, height);
                // A few cells spread over the trigger, each entered from every side that is outside it.
                var tries = cells.Where((_, i) => i % Math.Max(1, cells.Count / 6) == 0)
                    .SelectMany(c => steps.Select(s => (To: c, From: new Cell(c.X - s.Step.X, c.Y - s.Step.Y), s.Name)))
                    .Where(t => !Inside(t.From));
                foreach (var (to, from, direction) in tries)
                {
                    if (_region.Number != number)
                    {
                        ShowRegion(index);
                        AnswerForThePlayer();
                    }
                    Walker w = _party[_selected].Walker;
                    if (!w.CanStand(from, _region.IsBlocked) || !w.CanStand(to, _region.IsBlocked) || !w.Teleport(from, _region.IsBlocked))
                    {
                        continue;
                    }
                    _lastCells[w] = from;
                    if (!w.WalkTo(to, _region.IsBlocked))
                    {
                        continue;
                    }
                    for (int tick = 0; tick < 600 && _region.Number == number && (w.IsWalking || Session.Scripts.IsSuspended); tick++)
                    {
                        _clock += 1.0 / ScmdAnimator.TicksPerSecond;
                        Animate();
                        AnswerForThePlayer(yes: true);
                    }
                    if (verbose)
                    {
                        GD.Print($"  {kind} ({x0},{y0}) {width}x{height}: ({from.X},{from.Y}) -> ({to.X},{to.Y}) {direction}: now region {_region.Number} at {_party[0].Walker.Cell}, facing {_party[_selected].Walker.Facing}");
                        GD.Print("    " + string.Join("\n    ", _scriptLines.TakeLast(6)));
                    }
                    if (_region.Number != number)
                    {
                        found.Add($"{kind} ({to.X},{to.Y}) going {direction} -> region {_region.Number} at ({_party[0].Walker.Cell.X},{_party[0].Walker.Cell.Y})");
                        reached.Add(_region.Number);
                        break;
                    }
                }
            }
            // Exits behind a use trigger: doors, stairs, portals.
            if (_region.Number != number)
            {
                ShowRegion(index);
                AnswerForThePlayer();
            }
            List<int> Usable() => [.. _world.Objects.Where(o => o.Kind != ObjectKind.Party && !o.Hidden && Session.Scripts.Triggers.MatchObject(GplTriggerList.Use, o.Id) is not null)
                .Select(o => o.Index)];
            var usable = Usable();
            for (int u = 0; u < usable.Count; u++)
            {
                int objIndex = usable[u];
                if (u == usable.Count - 1 && _region.Number == number)
                {
                    // Objects the uses made (CLONE) get their turn too.
                    usable.AddRange(Usable().Except(usable));
                }
                if (_region.Number != number)
                {
                    ShowRegion(index);
                    AnswerForThePlayer();
                }
                if (_world[objIndex] is not { } obj)
                {
                    continue;
                }
                // Walk up to it from nearby, as the player would (scripts may test where the user stands).
                Walker w = _party[_selected].Walker;
                if (_world.CellsOf(obj) is [var at, ..] && NearestFree(w, at) is { } near)
                {
                    w.Teleport(near, _region.IsBlocked);
                    _lastCells[w] = near;
                }
                StartVerb(obj, Verb.Use, null);
                for (int tick = 0; tick < 30 * ScmdAnimator.TicksPerSecond && _region.Number == number && (_pendingUse is not null || w.IsWalking || Session.Scripts.IsSuspended); tick++)
                {
                    _clock += 1.0 / ScmdAnimator.TicksPerSecond;
                    Animate();
                    FinishApproach();
                    AnswerForThePlayer(yes: true);
                }
                AnswerForThePlayer(yes: true);
                if (verbose)
                {
                    GD.Print($"  use {Describe(obj)} at {string.Join(" ", _world.CellsOf(obj).Take(1))}: user at {w.Cell}; now region {_region.Number}");
                    GD.Print("    " + string.Join("\n    ", _scriptLines.TakeLast(5)));
                }
                if (_region.Number != number)
                {
                    found.Add($"use {Describe(obj)} -> region {_region.Number} at ({_party[0].Walker.Cell.X},{_party[0].Walker.Cell.Y})");
                    reached.Add(_region.Number);
                }
            }
            exits += found.Count;
            GD.Print($"{files[index]}: {spots.Count} move triggers, {usable.Count} usable objects, {found.Count} exits{(found.Count > 0 ? ": " + string.Join("; ", found) : "")}");
        }
        var unreached = files.Select(f => Convert.ToInt32(f[3..5], 16)).Where(n => n != 255 && !reached.Contains(n)).ToList();
        GD.Print($"{exits} region exits found (move triggers and use triggers); no exit leads to region{(unreached.Count == 1 ? "" : "s")} {string.Join(", ", unreached)}");
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
            PlaceParty(null, [], 0);
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
public sealed class CharacterSprite(int ojff, Scmd script, IReadOnlyList<ImageFrame> frames, Footprint footprint)
{
    public int Ojff { get; } = ojff;

    /// <summary>From the object's character record (its <c>RDFF</c>); one cell if it has none.</summary>
    public Footprint Footprint { get; } = footprint;
    public Scmd Script { get; } = script;
    public IReadOnlyList<ImageFrame> Frames { get; } = frames;
    public IReadOnlyList<(int Width, int Height)> Sizes { get; } = [.. frames.Select(f => (f.Width, f.Height))];
}
