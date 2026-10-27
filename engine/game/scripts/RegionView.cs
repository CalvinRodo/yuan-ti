using DarkSun.Data.Animation;
using DarkSun.Data.Gff;
using DarkSun.Data.Images;
using DarkSun.Data.Items;
using DarkSun.Data.Regions;
using DarkSun.Game.Animation;
using DarkSun.Game.Cinematics;
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
/// into cells fire move triggers; right-clicking an object opens the original's
/// verb window (<see cref="VerbMenu"/>: Get, Talk, Use, and Look or Open; only
/// Talk, only Use or only Open is done at once), and the selected character walks up to it and does that
/// (Look works from anywhere). The region's objects live in a
/// <see cref="GameWorld"/>: creatures that can walk carry out their scripted
/// orders and run their no-orders and line-of-sight scripts; every world
/// command is listed in the script log (F2).
/// Click: the selected character walks there; Shift+click: the whole party;
/// Right-click: verb menu; Tab: select the next character; N: add one; C:
/// change its sprite; arrows walk; WASD or middle-drag scroll; wheel or +/- zoom;
/// PgUp/PgDn change region; Esc returns to the main menu; F1 shows the <c>GMAP</c> flags, occupied cells and
/// triggers, F3 a grid, F4 object numbers, and with any of them on the
/// hovered cell is described (<see cref="DebugOverlay"/>); F7 the map information
/// lines (region, objects, triggers), hidden at first; <c>-- --debug-overlay</c>
/// starts with all four. Run with <c>-- --check-regions</c> to load every region, run its
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
    private FloorEffects _floor = null!;
    private Node2D _sorted = null!;
    private DebugOverlay _overlay = null!;
    private OverheadMarkers _overheadMarks = null!;

    /// <summary>The view as it was before OVERHEAD MAP, to go back to.</summary>
    private (int Zoom, bool Follow, Vector2 At)? _beforeOverhead;
    private ScriptDebugger _debugger = null!;
    private CharacterScreen _character = null!;
    private readonly Dictionary<int, Texture2D?> _itemIcons = [];
    private JournalWindow _journal = null!;
    private OptionsWindow _options = null!;
    private Control _hudPanel = null!;
    private GameMenu _gameMenu = null!;
    private MessageBox _message = null!;
    private SpellStudyWindow _study = null!;
    private PowerStudyWindow _powerStudy = null!;
    private SavesView _saves = null!;
    private GameMenu.Cursor _cursor;

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
    private Walker? _keyboardWalker;

    /// <summary>A verb waiting for its character to walk up to the object (<c>With</c>: Use with's first object).</summary>
    private (Walker Walker, int Actor, WorldObject Target, Verb Verb, WorldObject? With, int Tries)? _pendingUse;

    private VerbMenu _verbMenu = null!;
    private WorldObject? _menuTarget;
    private Vector2 _menuAt;
    private ContainerWindow _containerWindow = null!;
    private ItemWindow _itemWindow = null!;

    /// <summary>Use with…: the first object, waiting for the player to click the second.</summary>
    private WorldObject? _useWith;

    /// <summary>The inventory item <see cref="_useWith"/> stands for, when it is one.</summary>
    private Item? _useWithItem;
    /// <summary>The held item when it was picked up from the ground, and the cell it lay on (<see cref="HoldFromGround"/>).</summary>
    private (Item Item, Cell From)? _heldFromGround;
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

    /// <summary>Ticks of the time the view has run, held map or not (<see cref="AnimatedObject.OwnClock"/>).</summary>
    private long RunningNow => (long)(_running * ScmdAnimator.TicksPerSecond);

    private double _running;

    /// <summary>
    /// Out of a fight the map runs only under the MOVE pointer (<c>1DF3:29E8</c> counts the clock while
    /// <c>DS:11B8</c> = 1): LOOK, ATTACK and a held item hold everything still while one picks, until a
    /// chosen verb sends the character on its way. Text in the dialogue window holds it too, until read.
    /// </summary>
    /// <summary>The game menu, the save and load screen or the preferences are up: nothing on the map moves, in a fight or out.</summary>
    private bool GamePaused => _gameMenu.IsOpen || _saves.IsOpen || _options.Visible || OverheadShown;

    /// <summary>OVERHEAD MAP is up: the game waits until a click, Esc or O takes it away.</summary>
    public bool OverheadShown => _beforeOverhead is not null;

    private bool MapHeld => _cursor != GameMenu.Cursor.Move && _pendingUse is null || _message.IsOpen || _study.IsOpen || _powerStudy.IsOpen || _dialogueWindow.Visible;

    /// <summary>Make the next opening of the view start a new game (region 42).</summary>
    public static void StartNewGame() => _regionIndex = -1;

    /// <summary>Where a new game starts: region 42 at the party's start, unless changed (development: <c>--region</c>, <c>--at</c>).</summary>
    public static (int Region, Cell At) NewGameStart { get; set; } =
        (GplMachine.StartRegion, new Cell(GplMachine.StartX / Region.TileSize, GplMachine.StartY / Region.TileSize));

    public override void _Ready()
    {
        _checking = OS.GetCmdlineUserArgs().Any(a => a is "--check-arena" or "--check-arena-release" or "--check-party-movement");
        Controls.Register();
        // [game] original_pathfinding in user://settings.cfg (Options): walk as DSUN.EXE plans.
        Walker.UseOriginalPathfinding = Settings.OriginalPathfinding;
        _ground = new Node2D();
        AddChild(_ground);
        _floor = new FloorEffects();
        AddChild(_floor);
        _marker = new SelectionMarker();
        AddChild(_marker);
        _box = new BoxSelection();
        AddChild(_box);
        _sorted = new Node2D { YSortEnabled = true };
        AddChild(_sorted);
        _overlay = new DebugOverlay { Visible = false };
        AddChild(_overlay);
        _overheadMarks = new OverheadMarkers { Visible = false, Marks = OverheadMarks };
        AddChild(_overheadMarks);
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
            (_overlay.Flags, _overlay.Grid, _overlay.Numbers, _hudPanel.Visible) = (true, true, true, true);
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
            foreach (string set in OS.GetCmdlineUserArgs().Where(a => a.StartsWith("--set=")).SelectMany(a => a[6..].Split(';')))
            {
                // Development: script variables after the start-up scripts, as a script would have left them (--set=gnum[12]=1;gflag[11]=1).
                if (GplVariables.Set(Session.Scripts, set) is { } why)
                {
                    GD.PrintErr($"--set {set}: {why}");
                }
            }
            GiveStartingGear();
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
            if (OS.GetCmdlineUserArgs().Contains("--talk")
                && _world.Objects.FirstOrDefault(o => Session.Scripts.Triggers.MatchObject(GplTriggerList.TalkTo, o.Id) is not null) is { } talker)
            {
                // Development: talk to the first object that has a talk trigger (to see dialogue).
                WhenScriptsFree(() => Act(talker, _selected, Verb.Talk));
            }
            if (OS.GetCmdlineUserArgs().Contains("--fight"))
            {
                // Development: a fight with the nearest creature that fights (--auto: the party fights by itself).
                _autoParty = OS.GetCmdlineUserArgs().Contains("--auto");
                Callable.From(() => StartTestFight()).CallDeferred();
            }
            if (OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--screenshot=")) is { } shot)
            {
                // Development: the screen as a PNG after a few seconds (--screenshot-after=N), then quit.
                double after = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--screenshot-after=")) is { } t
                    && double.TryParse(t["--screenshot-after=".Length..], System.Globalization.CultureInfo.InvariantCulture, out double seconds) ? seconds : 5;
                GetTree().CreateTimer(after).Timeout += () =>
                {
                    GetViewport().GetTexture().GetImage().SavePng(shot["--screenshot=".Length..]);
                    GetTree().Quit();
                };
            }
            if (OS.GetCmdlineUserArgs().Contains("--open-items"))
            {
                Callable.From(() => _character.Open(_world, _selected, CharacterScreen.Page.Items)).CallDeferred();
            }
            if (OS.GetCmdlineUserArgs().Contains("--open-sheet"))
            {
                Callable.From(() => _character.Open(_world, _selected, CharacterScreen.Page.Character)).CallDeferred();
            }
            if (OS.GetCmdlineUserArgs().Contains("--open-spells") || OS.GetCmdlineUserArgs().Contains("--open-psionics"))
            {
                CharacterScreen.Page page = OS.GetCmdlineUserArgs().Contains("--open-spells") ? CharacterScreen.Page.Spells : CharacterScreen.Page.Psionics;
                int caster = Enumerable.Range(0, Session.Party.Count).FirstOrDefault(i => page == CharacterScreen.Page.Spells
                    ? new DarkSun.Game.Magic.Spellcasting(Session.Rules).Levels(Session.Party[i]).Any()
                    : Session.Party[i].Classes.Contains(DarkSun.Game.Characters.CharacterClass.Psionicist), _selected);
                Callable.From(() => _character.Open(_world, caster, page)).CallDeferred();
            }
            if (OS.GetCmdlineUserArgs().Contains("--open-menu"))
            {
                Callable.From(() => _gameMenu.Open()).CallDeferred();
            }
            if (OS.GetCmdlineUserArgs().Contains("--open-overhead"))
            {
                Callable.From(OverheadMap).CallDeferred();
            }
            if (OS.GetCmdlineUserArgs().FirstOrDefault(a => a is "--open-save" or "--open-load") is { } saves)
            {
                Callable.From(() => _saves.Open(saves == "--open-save" ? SavesView.Mode.Save : SavesView.Mode.Load)).CallDeferred();
            }
            if (OS.GetCmdlineUserArgs().Contains("--open-preferences"))
            {
                Callable.From(() =>
                {
                    _gameMenu.Open();
                    _gameMenu.Press(10303);
                }).CallDeferred();
            }
            if (OS.GetCmdlineUserArgs().Contains("--open-store") && _world.Objects.FirstOrDefault(o => o.Kind == ObjectKind.Creature && !o.Hidden) is { } merchant)
            {
                // Testing: the first creature keeps a store of water, boots and arrows.
                _world.Give(3, -1095, merchant.Index, GameWorld.None);
                _world.Give(1, -1051, merchant.Index, GameWorld.None);
                _world.Give(20, -1070, merchant.Index, GameWorld.None);
                Callable.From(() => _character.Open(_world, _selected, CharacterScreen.Page.Items, store: merchant)).CallDeferred();
            }
            if (OS.GetCmdlineUserArgs().Contains("--open-body") && _world.Objects.FirstOrDefault(o => o.Kind == ObjectKind.Creature && !o.Hidden) is { } victim)
            {
                // Testing: the first creature dies carrying water and boots; its body is opened.
                _world.Give(2, -1095, victim.Index, GameWorld.None);
                _world.Give(1, -1051, victim.Index, GameWorld.None);
                _world.Hurt(victim, victim.Vitals.Hp + 1);
                if (_world.First(-GameWorld.DeadBody) is { } body)
                {
                    Callable.From(() => _containerWindow.Open(body, _world[_selected]!, _palette, new Vector2(200, 150))).CallDeferred();
                }
            }
            if (OS.GetCmdlineUserArgs().Contains("--check-journal"))
            {
                _instantTravel = true;
                CheckJournal();
                return;
            }
            if (OS.GetCmdlineUserArgs().Contains("--check-dialogue"))
            {
                CheckDialogue();
                return;
            }
            if (OS.GetCmdlineUserArgs().Contains("--check-colours"))
            {
                CheckColours();
                return;
            }
            if (OS.GetCmdlineUserArgs().Contains("--check-prisoners"))
            {
                CheckPrisoners();
                return;
            }
            if (OS.GetCmdlineUserArgs().Contains("--check-gang"))
            {
                CheckGang();
                return;
            }
            if (OS.GetCmdlineUserArgs().Contains("--check-combat"))
            {
                CheckCombat();
                return;
            }
            if (OS.GetCmdlineUserArgs().Contains("--check-arena"))
            {
                CheckArena();
                return;
            }
            if (OS.GetCmdlineUserArgs().Contains("--check-arena-release"))
            {
                CheckArenaRelease();
                return;
            }
            if (OS.GetCmdlineUserArgs().Contains("--check-party-movement"))
            {
                CheckPartyMovement();
                return;
            }
            if (OS.GetCmdlineUserArgs().Contains("--check-character"))
            {
                CheckCharacter();
                return;
            }
            if (OS.GetCmdlineUserArgs().Contains("--check-floor"))
            {
                CheckFloor();
                return;
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
                    + string.Join(", ", _party.Select(m => $"({m.Walker.Cell.X},{m.Walker.Cell.Y})")) + $"; holding things: {HoldingThings()}");
                if (OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--idle=")) is { } idle && int.TryParse(idle[7..], out int ticks))
                {
                    // Development: let time pass, answering as a player would, to see what scripts keep running.
                    for (int tick = 0; tick < ticks; tick++)
                    {
                        _Process(1.0 / ScmdAnimator.TicksPerSecond);
                        AnswerForThePlayer();
                    }
                    GD.Print($"after {ticks} ticks: " + string.Join(", ", _scriptLines.Where(l => l.StartsWith("> "))
                        .GroupBy(l => l[(l.IndexOf(": ") + 2)..].Split(',')[0]).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} ×{g.Count()}")));
                }
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
        var panel = new PanelContainer { Position = new Vector2(4, 4), Visible = false };
        _hud = new Label { LabelSettings = new LabelSettings { FontSize = 10 } };
        panel.AddChild(_hud);
        layer.AddChild(panel);
        _hudPanel = panel;

        _scriptPanel = new PanelContainer { Visible = false };
        _scriptPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomWide);
        _scriptPanel.GrowVertical = Control.GrowDirection.Begin;
        _scriptLog = new Label { LabelSettings = new LabelSettings { FontSize = 9 }, AutowrapMode = TextServer.AutowrapMode.Off };
        _scriptPanel.AddChild(_scriptLog);
        layer.AddChild(_scriptPanel);

        _dialogueWindow = new DialogueWindow
        {
            Answer = answer => ReportScript("answer", Session.Scripts.Resume(answer)),
            // The one INPUT_STRING (GPL 20, the manual's word) has lower-case answers: any case does.
            AnswerText = text => ReportScript("answer", Session.Scripts.ResumeWith(text.Trim().ToLowerInvariant())),
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
        _itemWindow = new ItemWindow();
        _containerWindow = new ContainerWindow { ItemIcon = ItemIcon, Take = TakeFromContainer, Describe = DescribeItem, Covered = () => _itemWindow.Visible };
        layer.AddChild(_containerWindow);
        layer.AddChild(_itemWindow);
        _debugger = new ScriptDebugger
        {
            Ran = run => ReportScript("debugger", run),
            Stop = StopScript,
            Default = () => _region is null ? null : new GplEntry(_region.Number, 0, GplFileKind.Mas),
        };
        layer.AddChild(_debugger);
        _journal = new JournalWindow { Portraits = n => Portrait(n) };
        layer.AddChild(_journal);
        _options = new OptionsWindow();
        layer.AddChild(_options);
        _character = new CharacterScreen
        {
            CharacterOf = slot => slot < Session.Party.Count ? Session.Party[slot] : null,
            MemberName = MemberName,
            ItemIcon = ItemIcon,
            Money = () => Session.ScriptHost.Money,
            SetMoney = money => Session.ScriptHost.Money = money,
            Fighting = () => InCombat,
            EffectsOf = slot => _battle?.Combatants.FirstOrDefault(c => c.PartyMember && c.Source == _world[slot]) is { } member
                ? [.. member.Effects.Select(e => e.Effect)]
                : slot < Session.Party.Count ? [.. Session.Party[slot].Lasting.Where(e => e.EndsAt is not int end || end > _world.Clock).Select(e => e.Effect)] : [],
            CastOnMap = (caster, target, number) => DarkSun.Game.Combat.Encounter.CastOnMap(_world, slot => slot < Session.Party.Count ? Session.Party[slot] : null,
                caster, target, number, new DarkSun.Game.Combat.BorlandDice(new DarkSun.Game.Rules.BorlandRandom((uint)System.Environment.TickCount)), Session.Rules,
                Session.CombatRules, Session.SpellRules, Session.CreatureRules),
            CastInFight = CastItemInFight,
            SpellName = Session.SpellName,
            Did = text => AddScriptLine($"> {text}"),
            Leader = () => _selected,
            SetLeader = MakeLeader,
            OpenGameMenu = () => _gameMenu.Open(),
            Closed = () =>
            {
                // DSUN.EXE's pointer mode 3: an item picked up in the inventory is held, to use with something on the map
                // (one from the ground that wasn't put anywhere is still held from there).
                (Item Item, Cell From)? ground = _heldFromGround;
                _heldFromGround = null;
                if (_character.HeldOnClose is { } held && _world is not null)
                {
                    if (ground is var (item, from) && item == held)
                    {
                        HoldFromGround(held, from);
                    }
                    else
                    {
                        SetUseWithItem(held);
                    }
                }
                // A store's script waits for it to close.
                if (Session.ScriptHost.Dialogue.Prompt is GplShopPrompt && Session.Scripts.IsSuspended && Session.Scripts.PausedFor is null)
                {
                    ReportScript("store closed", Session.Scripts.Resume(0));
                }
            },
        };
        layer.AddChild(_character);
        _gameMenu = new GameMenu
        {
            ShowCharacter = page => OpenInventory(_selected, page),
            OpenSaves = (mode, then) => _saves.Open(mode, then),
            ExitGame = () => GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, MainMenu.ScenePath),
            OverheadMap = OverheadMap,
            CenterOnLeader = () => _follow = true,
            CollapseParty = CollapseParty,
            PartyGathered = () => _world.CollapsedLeader is not null || _world.Party.Count(p => !p.Hidden && p.Vitals.Alive) <= 1,
            CursorChanged = SetCursor,
        };
        layer.AddChild(_gameMenu);
        _message = new MessageBox();
        layer.AddChild(_message);
        _study = new SpellStudyWindow();
        layer.AddChild(_study);
        _powerStudy = new PowerStudyWindow();
        layer.AddChild(_powerStudy);
        Session.ScriptHost.LevelGained += LevelGained;
        _saves = new SavesView
        {
            SaveTo = (name, file) =>
            {
                _notice.Text = "";
                return Save(file ?? NewSaveFile(), name) ? null : _notice.Text; // the notice, under the screen, says why
            },
            LoadSave = save => LoadSaved(save),
        };
        layer.AddChild(_saves);
        _prompt = new Label { Visible = false, HorizontalAlignment = HorizontalAlignment.Center };
        _prompt.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterTop);
        _prompt.GrowHorizontal = Control.GrowDirection.Both;
        _prompt.OffsetTop = 6;
        _prompt.AddThemeFontSizeOverride("font_size", 10);
        _prompt.AddThemeColorOverride("font_outline_color", Colors.Black);
        _prompt.AddThemeConstantOverride("outline_size", 4);
        layer.AddChild(_prompt);
        BuildCombatHud(layer);
        return layer;
    }

    /// <param name="saved">Loading a game: the party as saved, and the region entered without running its script (its triggers are in the save).</param>
    private void ShowRegion(int index, Cell? start = null, SaveGame? saved = null)
    {
        IReadOnlyList<string> files = Session.Install!.RegionFiles;
        _regionIndex = (index % files.Count + files.Count) % files.Count;
        _pendingUse = null;
        ClearPicked();
        _verbMenu.Close();
        _containerWindow.Close();
        _itemWindow.Close();
        SetUseWith(null);
        _whenFree.Clear();
        AbandonCombat();
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
        int? collapsedLeader = _world?.CollapsedLeader;
        if (_world is not null && saved is null)
        {
            Session.Regions.Remember(_world);
        }
        LoadRegion(files[_regionIndex]);
        // The arena's main gate as its scripts take it to be (ArenaGate), before the region script runs.
        if (Settings.ScriptRepairs && ArenaGate.Match(_world, Session.Scripts.Memory, loaded: saved is not null))
        {
            AddScriptLine("    the arena's gate set as its scripts have it (script repair)");
        }
        if (saved is not null)
        {
            PlaceSavedParty(saved);
            AttachScripts();
        }
        else
        {
            PlaceParty(start, carried, selected);
            if (collapsedLeader is { } leader)
            {
                _world!.RestoreCollapsedParty(leader, _world.Party.Where(p => p.Index != leader).Select(p => p.Index).ToList());
            }
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
            + "?: all keys · Arrows: walk · Click: walk · Shift+click: party · Right-click: verbs · Tab: select · Esc: back";
    }

    /// <summary>Replace the map's nodes with <paramref name="file"/>'s region.</summary>
    private void LoadRegion(string file)
    {
        LoadMapEnvironment(file);
        // The map left takes its sounds with it: a repeating one playing, and any its scripts asked for that
        // weren't played yet (a script sending the party on queues them while the screen fades, which stops the frames).
        Sounds.Instance?.StopRepeating();
        Session.ScriptHost.Sounds.Clear();
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
        _keyboardWalker = null;

        _region = Region.Load(Session.Open(file), [Session.Open("SEGOBJEX.GFF"), Session.Open("GPLDATA.GFF")], Session.Assets);
        var objectData = new GffSet([Session.Open("SEGOBJEX.GFF"), Session.Open("GPLDATA.GFF")]);
        _objectData = objectData;
        _world = new GameWorld(_region, objectData, Session.ScriptHost.EngineVariables, Session.ScriptHost.PartyMembers, _checking ? 1 : Settings.Difficulty)
        {
            PartyClasses = slot => slot < Session.Party.Count
                ? new DarkSun.Game.Characters.CharacterCreation(Session.Rules, Session.Names, new Random(0)).UsableItemClasses(Session.Party[slot])
                : null,
            PartyWeaponsOnly = slot => slot < Session.Party.Count && Session.Rules.Race(Session.Party[slot].Race).WeaponsOnly,
        };
        _world.Rested += () =>
        {
            foreach (DarkSun.Game.Characters.Character c in Session.Party)
            {
                DarkSun.Game.Magic.Spellcasting.Rest(c);
            }
            // 87B3:073D says so in the message box (5F6D:05DE, as the doors' messages), from a fire pit or a script.
            _message.Show("THE PARTY RESTS");
        };
        // Back in a region visited before: as the party left it (before its script runs, as in DSUN.EXE).
        // What had an object in the files but not when the party left (killed, picked up) isn't drawn.
        var inFiles = _world.Objects.Where(o => o.Source is not null && !o.IsClone).Select(o => o.Source!).ToHashSet();
        IReadOnlyList<WorldObject>? restored = _checking ? null : Session.Regions.Recall(_world);
        _world.Changed += OnWorldChanged;
        _world.Defeated += creature => Session.ScriptHost.AwardKill(_world, creature);
        Session.ScriptHost.Map = _world;
        Session.ScriptHost.CinematicRequested = PlayCinematic;
        Session.ScriptHost.MoveCursorRequested = _gameMenu.ResetCursor;
        _occupancy = _world.Occupancy;
        foreach (WorldObject o in _world.Objects.Where(o => o.Source is not null && !o.IsClone))
        {
            _worldObjects[o.Source!] = o;
        }
        _itemIcons.Clear();
        _palette = _region.LoadPalette(Session.Open("GPLDATA.GFF"), Session.Open("RESOURCE.GFF"), Session.Assets);

        // The region's music (RDAT +0x2F), changed with the original's 5-second fades.
        RegionInfo? regionInfo = RegionInfo.Read(Session.Open("RESOURCE.GFF"), _region.Number);
        if (!_checking && regionInfo is { Music: > 0 } info)
        {
            Music.Instance?.ChangeTo("RESOURCE.GFF", info.Music);
        }
        // Its colour cycling and the game's, over the map and the screens drawn in PAL 1000 and 1001.
        ColourCycling.Instance?.Start(regionInfo, [_palette, .. new[] { 1000, 1001 }
            .Where(n => Session.Open("RESOURCE.GFF").Contains("PAL ", n))
            .Select(n => Session.Assets.Palette(Session.Open("RESOURCE.GFF").Read("PAL ", n)))]);
        _mapSize = new Vector2(_region.Width, _region.Height) * Region.TileSize;

        byte[] ground = RegionRenderer.RenderRgb(_region, ColourCycling.Keyed(_palette), RegionLayers.Ground);
        Image groundImage = Image.CreateFromData((int)_mapSize.X, (int)_mapSize.Y, false, Image.Format.Rgb8, ground);
        _ground.AddChild(new Sprite2D { Texture = ImageTexture.CreateFromImage(groundImage), Centered = false, Name = "Ground" });

        // The floor as the dust sees it: the ground with the flat objects on it.
        Image floorImage = (Image)groundImage.Duplicate();
        floorImage.Convert(Image.Format.Rgba8);

        // Each sprite's node sits at the point it is sorted by: an object's ETAB
        // position, a wall's cell bottom. The image hangs off it via Offset.
        // Creatures that can walk are drawn from their walker instead, like the party.
        var flats = new List<Sprite2D>();
        foreach (RegionObject obj in _region.Objects)
        {
            if (inFiles.Contains(obj) && !_worldObjects.ContainsKey(obj))
            {
                continue;
            }
            if (_worldObjects.TryGetValue(obj, out WorldObject? wo) && wo.Walker is not null)
            {
                var node = new Sprite2D { Centered = false };
                _sorted.AddChild(node);
                _creatureNodes[wo] = node;
                DrawCreature(wo);
                continue;
            }
            bool flat = LiesFlat(obj, wo);
            Sprite2D? sprite = AddSprite(obj.Frame, new Vector2(obj.EtabX, obj.EtabY), new Vector2(obj.X, obj.Y), obj.Mirrored, flat ? _ground : null);
            if (sprite is not null)
            {
                _objectSprites.Add((obj, sprite));
                if (flat)
                {
                    flats.Add(sprite);
                    LayOnGround(floorImage, sprite);
                }
            }
            if (sprite is not null && obj.Scmd > 0 && objectData.TryRead("SCMD", obj.Scmd, out var data))
            {
                var scmd = Scmd.Parse(data.Span);
                // Doors start closed or open as their state says (the world sets the start).
                int start = _worldObjects.TryGetValue(obj, out WorldObject? state) ? state.AnimationStart : 0;
                _animated.Add(new AnimatedObject(sprite, obj, scmd, AnimatorFor(obj, scmd, start, obj.AnchorX, obj.AnchorY)));
            }
        }
        // The flat ones in the order the sorting drew them: by their top edge, then table order.
        foreach (Sprite2D flat in flats.OrderBy(f => f.Position.Y))
        {
            _ground.MoveChild(flat, -1);
        }
        _floor.SetGround(floorImage);
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

    /// <summary>
    /// Whether an object lies flat on the ground (paving, a rug, an item put
    /// down): hung from its top edge, which DSUN.EXE sorts it by, so whatever
    /// stands on it is drawn over it anyway. Drawn with the ground, under the
    /// floor's shadows, dust and rings, which it would otherwise cover.
    /// Animated objects (their hot spot moves) and creatures stay sorted.
    /// </summary>
    private static bool LiesFlat(RegionObject obj, WorldObject? wo) =>
        obj.EtabY - obj.Y <= FlatAnchor && obj.Scmd == 0 && wo?.Kind != ObjectKind.Creature;

    /// <summary>How far below its picture's top edge a flat object may hang from (<see cref="LiesFlat"/>).</summary>
    private const int FlatAnchor = 2;

    /// <summary>A flat object's picture into the ground's image, where the dust looks for the floor's colour.</summary>
    private static void LayOnGround(Image ground, Sprite2D sprite)
    {
        Image picture = sprite.Texture.GetImage();
        picture.Convert(ground.GetFormat());
        if (sprite.FlipH)
        {
            picture.FlipX();
        }
        var at = (Vector2I)(sprite.Position + sprite.Offset);
        ground.BlendRect(picture, new Rect2I(Vector2I.Zero, picture.GetSize()), at);
    }

    private Sprite2D? AddSprite(ImageFrame frame, Vector2 sortPoint, Vector2 topLeft, bool mirrored, Node2D? parent = null)
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
        (parent ?? _sorted).AddChild(sprite);
        return sprite;
    }

    private Texture2D TextureFor(ImageFrame frame) => Textures.ToTexture(frame, _palette);

    /// <summary>A party member's name: their character's, or the map object's.</summary>
    private string MemberName(int slot) =>
        slot < Session.Party.Count && Session.Party[slot].Name.Length > 0 ? Session.Party[slot].Name : _world?[slot]?.Name ?? $"Character {slot + 1}";

    /// <summary>An item's picture for the character screens: its map sprite (<c>OJFF</c> = <c>RDFF</c>) in the region's palette.</summary>
    private Texture2D? ItemIcon(int rdff)
    {
        if (!_itemIcons.TryGetValue(rdff, out Texture2D? icon))
        {
            var objectData = new GffSet([Session.Open("SEGOBJEX.GFF"), Session.Open("GPLDATA.GFF")]);
            icon = Region.MakeObject(objectData, rdff, 0, 0, d => Session.Assets.Frames(d)) is { Frames: [var frame, ..] } ? TextureFor(frame) : null;
            _itemIcons[rdff] = icon;
        }
        return icon;
    }

    /// <summary>
    /// A new game's characters start with the items they bring
    /// (<see cref="Session.Kit"/>: their class's <see cref="StartingGear"/>,
    /// given when DONE was clicked on the creation screen), each given to its
    /// member if it carries nothing yet.
    /// </summary>
    private void GiveStartingGear()
    {
        for (int i = 0; i < Session.Party.Count; i++)
        {
            if (Session.Party[i] is { } c && _world[i] is { Kind: ObjectKind.Party, Items.Count: 0 } member
                && _world.GiveItems(member, Session.Kit(c)) is { Count: > 0 } given)
            {
                AddScriptLine($"> {c.Name} starts with {string.Join(", ", given.Select(g => (_world.Items.NameOf(g.Record) is { Length: > 0 } n ? n : $"item {g.Rdff}") + (g.Quantity > 1 ? $" ×{g.Quantity}" : "") + (g.Equipped ? "" : " (pack)")))}");
            }
        }
    }

    /// <summary>The character screens are showing (the key help lists theirs).</summary>
    public bool CharacterScreenOpen => _character?.IsOpen == true;

    /// <summary>The game menu is showing.</summary>
    public bool GameMenuOpen => _gameMenu?.IsOpen == true;

    /// <summary>The LOAD or SAVE screen is showing.</summary>
    public bool SavesOpen => _saves?.IsOpen == true;

    /// <summary>The leader button: <paramref name="slot"/> leads (the selected character, <c>gname[5]</c>).</summary>
    private void MakeLeader(int slot)
    {
        if (slot < 0 || slot >= _party.Count)
        {
            return;
        }
        Session.SetLeader(slot);
        ClearPicked();
        if (slot != _selected)
        {
            _world.ExpandParty();
        }
        _selected = slot;
        _world.ActiveMember = slot;
        _follow = true;
        UpdateHud();
    }

    /// <summary>
    /// OVERHEAD MAP (<c>BUTN</c> 10305; the original draws the region's map over the
    /// whole screen, no window of its own): the whole region in view, the game
    /// stopped, the party's members marked and the enemies left on it flashing
    /// (<see cref="OverheadMarkers"/>). Again (or a click, Esc): back to the view as it was.
    /// </summary>
    private void OverheadMap()
    {
        if (_beforeOverhead is { } before)
        {
            _beforeOverhead = null;
            _overheadMarks.Visible = false;
            (_zoomLevel, _follow) = (before.Zoom, before.Follow);
            _camera.Zoom = Vector2.One * ZoomLevels[_zoomLevel];
            _camera.Position = before.At;
            ClampCamera();
            return;
        }
        _beforeOverhead = (_zoomLevel, _follow, _camera.Position);
        Vector2 view = GetViewportRect().Size;
        float fit = Math.Min(view.X / Math.Max(1, _mapSize.X), view.Y / Math.Max(1, _mapSize.Y));
        _camera.Zoom = Vector2.One * fit;
        _follow = false;
        _camera.Position = _mapSize / 2;
        _camera.ResetSmoothing();
        _overheadMarks.Visible = true;
    }

    /// <summary>The overhead map's marks: the party's members shown, and the creatures hostile to the party, standing.</summary>
    private IEnumerable<(Vector2 At, bool Enemy)> OverheadMarks()
    {
        static Vector2 Centre(Cell c) => new((c.X + 0.5f) * Region.TileSize, (c.Y + 0.5f) * Region.TileSize);
        foreach (WorldObject o in _world.Objects.Where(o => !o.Hidden && o.Kind == ObjectKind.Creature && o.Allegiance == 2 && o.Vitals.Alive))
        {
            yield return (Centre(o.Cell), true);
        }
        foreach (WorldObject m in _world.Party.Where(m => !m.Hidden))
        {
            yield return (Centre(m.Cell), false);
        }
    }

    /// <summary>How many times as fast as a walk the members gather with <see cref="Settings.QuickCollapse"/>.</summary>
    private const int QuickCollapseSpeed = 3;

    /// <summary>COLLAPSE PARTY: walk into the leader, then travel as one character; again, spread out.</summary>
    private void CollapseParty()
    {
        if (MapLocked || InCombat)
        {
            Notice("The party cannot collapse during combat or a waiting script.");
            return;
        }
        if (_world.CollapsedLeader is not null)
        {
            _world.ExpandParty();
            Notice("THE PARTY SPREADS OUT");
            return;
        }
        if (Selected is not null)
        {
            _keyboardWalker = null;
            _pendingUse = null;
            ClearPicked();
            _world.CollapseParty(_selected, Settings.QuickCollapse ? QuickCollapseSpeed : 1);
            _follow = true;
        }
    }

    /// <summary>The MOVE, LOOK and ATTACK cursors.</summary>
    private void SetCursor(GameMenu.Cursor cursor)
    {
        _cursor = cursor; // the pointer follows (UpdatePointer)
        Notice(cursor switch
        {
            GameMenu.Cursor.Look => "LOOK CURSOR: the game waits; a click shows what can be done (Esc: back to moving)",
            GameMenu.Cursor.Attack => "ATTACK CURSOR: the game waits; a click attacks (Esc: back to moving)",
            GameMenu.Cursor.Item => _useWith is { } held ? $"USE {Called(held).ToUpperInvariant()} WITH: click what to use it on" : "MOVE CURSOR",
            _ => "MOVE CURSOR",
        });
    }

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
        bool newParty = carried.Count == 0 && Session.Party.Count > 0;
        if (newParty)
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
        // A new party starts with its leader selected (the leader button).
        _selected = Math.Clamp(carried.Count == 0 || newParty ? Session.LeaderIndex : selected, 0, Math.Max(0, _party.Count - 1));
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
        _world.ExpandParty();
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

    /// <summary>
    /// A walker's sprite, its hot spot on the feet. It is sorted a pixel above
    /// them: DSUN.EXE draws row by row (<c>1DBD:0002</c>), a creature before the
    /// walls of its own cell row (its y >> 4), but its feet are the cell's
    /// bottom edge, which would sort as the next row and draw it over the
    /// walls beside it (the slave pens' gate posts).
    /// </summary>
    private static void PlaceWalker(Sprite2D node, Walker walker)
    {
        node.Position = new Vector2(walker.Feet.X, walker.Feet.Y - 1);
        node.Offset = new Vector2(-walker.Animator.HotX, -walker.Animator.HotY + 1);
    }

    private void DrawMember(PartyMember member)
    {
        Walker walker = member.Walker;
        member.Node.Visible = _world[walker.Id] is { Hidden: false, Collapsed: false };
        ImageFrame frame = member.Sprite.Frames[walker.Animator.Frame];
        member.Node.Texture = TextureFor(frame);
        member.Node.FlipH = walker.Animator.Mirrored;
        PlaceWalker(member.Node, walker);
        Silhouette(member.Node);
    }

    /// <summary>
    /// In a fight, a faint copy of a combatant drawn above the scenery, so one
    /// that steps behind a building (a frightened mage fleeing north) can still
    /// be followed. Not in DSUN.EXE.
    /// </summary>
    private void Silhouette(Sprite2D node)
    {
        if (node.GetNodeOrNull<Sprite2D>("Silhouette") is not { } ghost)
        {
            ghost = new Sprite2D
            {
                Name = "Silhouette", Centered = false, ZIndex = 5, ZAsRelative = false, Modulate = new Color(1, 1, 1, 0.4f),
                TextureFilter = node.TextureFilter,
            };
            node.AddChild(ghost);
        }
        (ghost.Texture, ghost.FlipH, ghost.Offset, ghost.Visible) = (node.Texture, node.FlipH, node.Offset, InCombat);
    }

    /// <summary>Show or put away every combatant's <see cref="Silhouette"/> (a fight starts or ends).</summary>
    private void UpdateSilhouettes()
    {
        foreach (PartyMember member in _party)
        {
            Silhouette(member.Node);
        }
        foreach (Sprite2D node in _creatureNodes.Values)
        {
            Silhouette(node);
        }
    }

    /// <summary>
    /// The party takes no walking or verbs while a script waits (a conversation,
    /// the arena's bout) or a bout the party watches is about to start: in
    /// DSUN.EXE the game is inside the script until it is done.
    /// </summary>
    private bool MapLocked => Session.Scripts.IsSuspended || _watchedFightPending || Session.ScriptHost.WatchedFight is not null;

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
        PlaceWalker(node, walker);
        Silhouette(node);
    }

    private void WalkTo(Cell goal, bool wholeParty)
    {
        _keyboardWalker = null;
        _pendingUse = null;
        if (wholeParty)
        {
            _world.ExpandParty();
        }
        List<PartyMember> walking = wholeParty ? _party : Picked.Count > 0 ? Picked : Selected is { } m ? [m] : [];
        foreach (PartyMember member in walking)
        {
            member.Walker.WalkTo(goal, _region.IsBlocked);
        }
        LeadOrWalkAlone(!wholeParty && walking.Count == 1 && walking[0] == Selected);
        _follow = true;
    }

    /// <summary>The leader walking on its own: with <see cref="Settings.FollowLeader"/> the others follow it (UpdateFollowers); else, or when several were sent, each walks alone.</summary>
    private void LeadOrWalkAlone(bool leaderAlone)
    {
        if (leaderAlone && Settings.FollowLeader && !InCombat)
        {
            _world.FollowLeader(_selected);
        }
        else
        {
            _world.StopFollowing();
        }
    }


    // -- per frame --------------------------------------------------------------

    /// <summary>Step every animated object and the character to the current tick.</summary>
    private void Animate()
    {
        long now = Now;
        foreach (AnimatedObject animated in Settings.Animations || _checking ? _animated : [])
        {
            (Sprite2D sprite, RegionObject obj, ScmdAnimator animator) = (animated.Sprite, animated.Object, animated.Animator);
            if (!animator.Update(animated.OwnClock ? RunningNow : now))
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
            if (_world[i] is { Collapsed: false } && member.Walker.Update(now, _region.IsBlocked))
            {
                DrawMember(member);
            }
            PlaySounds(member.Walker.TakeSounds(), member.Node.Position);
        }
        _world.UpdateCollapsedParty();
        if (!InCombat)
        {
            _world.UpdateFollowers();
        }
        for (int i = 0; i < _party.Count && _region.Number == region && !_transitioning; i++)
        {
            PartyMember member = _party[i];
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
        PlayScriptSounds();
        RunCreatureScripts();
        StartWatchedFight();
        if (_overlay.Visible)
        {
            _overlay.QueueRedraw();
        }
        _marker.Follow(Selected?.Walker);
        _box.Show(_boxFrom is { } from ? new Rect2(from, Vector2.Zero).Expand(_boxTo) : null,
            [.. Picked.Where(p => p != Selected).Select(p => p.Walker)]);
    }

    /// <summary>
    /// Keep the camera on the party's active member (the selected character),
    /// as DSUN.EXE scrolls with the active member; the camera eases after it
    /// rather than jumping a cell at a time. Scrolling by hand stops following
    /// until the next click.
    /// </summary>
    /// <summary>GPL SOUND (play_sound_effect, as for SCMD sounds): what the scripts asked for since the last frame.</summary>
    private static void PlayScriptSounds()
    {
        while (Session.ScriptHost.Sounds.TryDequeue(out int sound))
        {
            Sounds.Instance?.Play(sound);
        }
    }

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
        if (_battle is not null && Session.ScriptHost.WatchedFight is { Walker: { } watched })
        {
            _camera.Position = new Vector2(watched.Feet.X, watched.Feet.Y); // 191F:01FF: the view on the bout
        }
        else if (_follow && Selected is { } member)
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
        if (_transitioning || _cinematic is not null)
        {
            return; // fading to the next region, or a script's cinematic plays over the map
        }
        if (GamePaused)
        {
            return; // the game menu (or saving, loading, the preferences) stops the game, a fight too
        }
        if (InCombat || !MapHeld)
        {
            _clock += delta;
        }
        _running += delta;
        UpdatePointer();
        if (InCombat)
        {
            AnimateCombat(delta);
            CombatProcess(delta);
            PlayScriptSounds(); // a fight's scripts (the arena's crowd) are heard as they run, not once it is over
            Vector2 pan = Input.GetVector(Controls.PanLeft, Controls.PanRight, Controls.PanUp, Controls.PanDown);
            if (pan != Vector2.Zero)
            {
                _follow = false;
                _camera.Position += pan * PanSpeed * (float)delta / _camera.Zoom.X;
            }
            FollowCharacter();
            UpdateFloor(delta);
            return;
        }
        MoveWithKeyboard();
        Animate();
        FinishApproach();
        OfferSpellStudy();
        if (Session.ScriptHost.Dialogue.Prompt is GplShopPrompt shop && !_character.IsOpen && _world[shop.Merchant] is { } merchant
            && _world[_selected] is { } who)
        {
            _character.Open(_world, who.Index, CharacterScreen.Page.Items, store: merchant);
        }

        Vector2 direction = Input.GetVector(Controls.PanLeft, Controls.PanRight, Controls.PanUp, Controls.PanDown);
        if (direction != Vector2.Zero)
        {
            _follow = false;
            _camera.Position += direction * PanSpeed * (float)delta / _camera.Zoom.X;
        }
        FollowCharacter();
        UpdateHover(force: false);
        UpdateFloor(delta);
    }

    /// <summary>
    /// When the map is free, a preserver with a level gained chooses a spell (<c>8CCC:0000</c>:
    /// standing, condition below 5) in the study window, a psionicist a power in its own
    /// (<see cref="PowerStudyWindow"/>); with nothing left to learn, the choice goes.
    /// </summary>
    private void OfferSpellStudy()
    {
        if (_study.IsOpen || _powerStudy.IsOpen || KeyboardMovementLocked || _checking)
        {
            return;
        }
        for (int i = 0; i < Session.Party.Count && i < PartySize; i++)
        {
            if (_world[i] is not { Kind: ObjectKind.Party, Hidden: false } member || member.Vitals.Status >= 5)
            {
                continue;
            }
            DarkSun.Game.Characters.Character c = Session.Party[i];
            if (c.SpellsToChoose > 0)
            {
                if (!_study.Open(c, c.Name, new DarkSun.Game.Magic.Spellcasting(Session.Rules)))
                {
                    c.SpellsToChoose = 0;
                }
                return;
            }
            if (c.PowersToChoose > 0)
            {
                if (!_powerStudy.Open(c, c.Name))
                {
                    c.PowersToChoose = 0;
                }
                return;
            }
        }
    }

    /// <summary>A member gained a level (<see cref="WorldGplHost.CheckLevels"/>): the game says so in the message box.</summary>
    private void LevelGained(int slot, string message)
    {
        AddScriptLine($"> {message}");
        if (!_checking)
        {
            _message.Show(message);
        }
    }

    private bool KeyboardMovementLocked => MapLocked || _message.IsOpen || _study.IsOpen || _powerStudy.IsOpen || _dialogueWindow.Visible || _gameMenu.IsOpen || _saves.IsOpen || _character.IsOpen
        || _journal.Visible || _options.Visible || _verbMenu.Visible || _containerWindow.Visible || _itemWindow.Visible || _debugger.Visible
        || GetNodeOrNull<KeyHelp>("/root/KeyHelp")?.IsOpen == true
        || GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit
        || Controls.ModifierHeld; // Ctrl+S, Alt+A … are other keys, not a step

    private void MoveWithKeyboard()
    {
        Vector2 direction = KeyboardMovementLocked ? Vector2.Zero
            : Input.GetVector(Controls.MoveLeft, Controls.MoveRight, Controls.MoveUp, Controls.MoveDown);
        if (direction == Vector2.Zero || Selected is not { } member)
        {
            _keyboardWalker?.Stop();
            _keyboardWalker = null;
            return;
        }
        Walker walker = member.Walker;
        if (_keyboardWalker != walker)
        {
            _keyboardWalker?.Stop();
            walker.Stop();
            _keyboardWalker = walker;
            _pendingUse = null;
            SetUseWith(null);
            LeadOrWalkAlone(leaderAlone: true);
        }
        _follow = true;
        if (walker.IsWalking)
        {
            return;
        }
        int dx = Math.Sign(direction.X), dy = Math.Sign(direction.Y);
        Cell next = new(walker.Cell.X + dx, walker.Cell.Y + dy);
        if (walker.CanStand(next, _region.IsBlocked)
            && (dx == 0 || dy == 0 || walker.CanStand(new Cell(next.X, walker.Cell.Y), _region.IsBlocked)
                && walker.CanStand(new Cell(walker.Cell.X, next.Y), _region.IsBlocked)))
        {
            walker.Follow([next]);
        }
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
        if (_message.IsOpen || _study.IsOpen || _powerStudy.IsOpen)
        {
            return; // a key puts the message away (MessageBox); the study windows take their own
        }
        if (OverheadShown)
        {
            // The overhead map goes with a click or a key that closes things; nothing else reaches the map meanwhile.
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left or MouseButton.Right }
                || e.IsActionPressed(Controls.Back) || e.IsActionPressed(Controls.ToggleOptions) || e.IsActionPressed(Controls.OverheadMap))
            {
                OverheadMap();
            }
            if (e is InputEventMouseButton or InputEventKey)
            {
                GetViewport().SetInputAsHandled();
            }
            return;
        }
        if (CombatInput(e))
        {
            return;
        }
        if (_useWith is not null && e.IsActionPressed(Controls.Back))
        {
            // Put away: an item from a pack stays there; one picked up from the ground goes back where it lay.
            (Item Item, Cell From)? ground = _heldFromGround;
            SetUseWith(null);
            if (ground is var (item, from))
            {
                DropHeld(item, new Vector2((from.X + 0.5f) * Region.TileSize, (from.Y + 0.5f) * Region.TileSize));
            }
        }
        else if (_useWith is not null && _cursor == GameMenu.Cursor.Item && !_dialogueWindow.Visible
            && e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
        {
            // Use with…: the held item's pointer picks the second object; on a party member, an item held from a
            // pack goes in theirs; on the bare ground, it is put down there.
            WorldObject first = _useWith;
            Item? heldItem = _useWithItem;
            SetUseWith(null);
            if (heldItem is not null && MemberAt(GetGlobalMousePosition()) is { } member)
            {
                PutHeldOn(heldItem, member);
            }
            else if (ObjectAt(GetGlobalMousePosition()) is { } second && second != first)
            {
                StartVerb(second, Verb.UseWith, first);
            }
            else if (heldItem is not null)
            {
                DropHeld(heldItem, GetGlobalMousePosition());
            }
        }
        else if (e.IsActionPressed(Controls.Back) && _cursor != GameMenu.Cursor.Move)
        {
            _gameMenu.ResetCursor();
        }
        else if (e.IsActionPressed(Controls.Back) && !_gameMenu.IsOpen && !_saves.IsOpen && !_character.IsOpen)
        {
            _gameMenu.Open();
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
        else if (e.IsActionPressed(Controls.ToggleCombatLog, exactMatch: true))
        {
            _combatLog.Toggle();
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
        else if (e.IsActionPressed(Controls.ToggleMapInfo))
        {
            _hudPanel.Visible = !_hudPanel.Visible;
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
            _world.ExpandParty();
            ClearPicked();
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
        else if (_dialogueWindow.Visible && e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
        {
            _dialogueWindow.ClickOutside(); // the dialogue is modal: a click on the map reads on
        }
        else if ((_dialogueWindow.Visible || MapLocked) && e is InputEventMouseButton { ButtonIndex: MouseButton.Left or MouseButton.Right })
        {
            // A waiting script holds the party: the map takes no clicks.
        }
        else if (BoxInput(e))
        {
            // A box is being dragged, or one was let go (BoxSelect).
        }
        else if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } click)
        {
            Vector2 at = GetGlobalMousePosition();
            if (_cursor == GameMenu.Cursor.Look && ObjectAt(at) is { } looked)
            {
                // The eye opens what can be done with the thing (1587:06D8 mode 2 → 68BD:0040, the verb window).
                if (Sees(looked))
                {
                    OpenVerbMenu(at, click.Position);
                }
                else
                {
                    _message.Show(NoLineOfSight);
                }
            }
            else if (_cursor == GameMenu.Cursor.Attack && ObjectAt(at) is { } target)
            {
                StartVerb(target, Verb.Attack, null);
            }
            else
            {
                ClickToWalk(at, click.ShiftPressed);
            }
        }
        else if (e is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true })
        {
            // 1587:01C7: the next pointer (MOVE, ATTACK, LOOK, the held item).
            _gameMenu.CycleCursor(_useWith is not null);
        }
        else if (e.IsActionPressed(Controls.ToggleInventory) && !_character.IsOpen && !Session.Scripts.IsSuspended)
        {
            OpenInventory(_selected, CharacterScreen.Page.Items);
        }
        else if (e.IsActionPressed(Controls.ToggleCharacter) && !_character.IsOpen && !Session.Scripts.IsSuspended)
        {
            _character.Open(_world, _selected, CharacterScreen.Page.Character);
        }
        else if (e.IsActionPressed(Controls.ToggleJournal) && !_journal.Visible)
        {
            _journal.Open();
        }
        else if (e.IsActionPressed(Controls.ToggleOptions) && !_options.Visible)
        {
            _options.Open();
        }
        else if (e.IsActionPressed(Controls.ToggleScriptLog))
        {
            _scriptPanel.Visible = !_scriptPanel.Visible;
        }
        else if (OriginalKey(e))
        {
            GetViewport().SetInputAsHandled();
        }
        else if (e is InputEventMouseMotion motion && (motion.ButtonMask & MouseButtonMask.Middle) != 0)
        {
            _follow = false;
            _camera.Position -= motion.Relative / _camera.Zoom.X;
            ClampCamera();
        }
    }

    /// <summary>
    /// The original's keys (the guide's list) the engine didn't have: the overhead map, cast, the
    /// leader (1–4), the whole party or the leader alone (5, 6), back to the party, animations,
    /// save, load and exit. True if <paramref name="e"/> was one of them.
    /// </summary>
    private bool OriginalKey(InputEvent e)
    {
        bool free = !_character.IsOpen && !_dialogueWindow.Visible && !_verbMenu.Visible && !Session.Scripts.IsSuspended;
        if (e.IsActionPressed(Controls.OverheadMap) && !InCombat)
        {
            OverheadMap();
        }
        else if (e.IsActionPressed(Controls.CastSpell) && free)
        {
            _character.Open(_world, _selected, CharacterScreen.Page.Spells);
        }
        else if (Controls.LeaderKeys.FirstOrDefault(k => e.IsActionPressed(k)) is { } leader && free)
        {
            MakeLeader(Controls.LeaderKeys.ToList().IndexOf(leader)); // an empty slot does nothing
        }
        else if (e.IsActionPressed(Controls.ShowParty) && free && _world.CollapsedLeader is not null)
        {
            CollapseParty(); // collapsed: spreads out
        }
        else if (e.IsActionPressed(Controls.LeaderOnly) && free && _world.CollapsedLeader is null)
        {
            CollapseParty();
        }
        else if (e.IsActionPressed(Controls.CenterOnLeader))
        {
            _follow = true;
        }
        else if (e.IsActionPressed(Controls.ToggleAnimations))
        {
            Settings.Animations = !Settings.Animations;
            Notice(Settings.Animations ? "ANIMATIONS ON" : "ANIMATIONS OFF");
        }
        else if (e.IsActionPressed(Controls.SaveGame) && free && !InCombat)
        {
            _saves.Open(SavesView.Mode.Save, null);
        }
        else if (e.IsActionPressed(Controls.LoadGame) && free)
        {
            _saves.Open(SavesView.Mode.Load, null);
        }
        else if (e.IsActionPressed(Controls.ExitGame))
        {
            _gameMenu.Open();
            _gameMenu.Press(10301); // EXIT TO DOS: "EXIT: SAVE GAME?"
        }
        else
        {
            return false;
        }
        return true;
    }

    private void Zoom(int step)
    {
        if (OverheadShown)
        {
            return;
        }
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
        _dialogueWindow.Attach(Session.ScriptHost.Dialogue, Portrait, _palette);
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

    /// <summary>
    /// The front-most object with a drawn pixel under <paramref name="at"/>
    /// (map position), if any: DSUN.EXE goes through the drawn objects front to
    /// back and takes the first whose picture has a pixel there, not just its
    /// box (<c>1DF3:2822</c>, <c>147D:0166</c>), so a small thing (coins on the
    /// ground) isn't hidden by the empty corners of a bigger one in front.
    /// </summary>
    private WorldObject? ObjectAt(Vector2 at) =>
        _objectSprites.Where(o => _worldObjects.ContainsKey(o.Object)).Select(o => (Object: _worldObjects[o.Object], o.Sprite))
            .Concat(_creatureNodes.Select(c => (Object: c.Key, Sprite: c.Value)))
            .Concat(_cloneSprites.Select(c => (Object: c.Key, Sprite: c.Value)))
            .Where(o => DrawnAt(o.Sprite, at))
            .OrderByDescending(o => o.Sprite.Position.Y)
            .Select(o => o.Object)
            .FirstOrDefault();

    /// <summary>Does <paramref name="sprite"/> (visible) draw a pixel at map position <paramref name="at"/>?</summary>
    private static bool DrawnAt(Sprite2D sprite, Vector2 at)
    {
        if (!sprite.Visible || sprite.Texture is not { } texture)
        {
            return false;
        }
        Vector2 local = at - (sprite.Position + sprite.GetRect().Position);
        int x = (int)Math.Floor(local.X), y = (int)Math.Floor(local.Y);
        return Textures.Opaque(texture, sprite.FlipH ? texture.GetWidth() - 1 - x : x, y);
    }

    /// <summary>The verbs <paramref name="obj"/> offers (<see cref="ObjectVerbs"/>).</summary>
    private IReadOnlyList<Verb> VerbsOf(WorldObject obj) => ObjectVerbs.For(obj, _world.Items, Session.Scripts.Triggers, _world.IsContainer(obj));

    /// <summary>Right-click: the verb menu of the object under the pointer (<paramref name="screen"/>: where to show it), or its only verb done at once.</summary>
    private void OpenVerbMenu(Vector2 at, Vector2 screen)
    {
        if (ObjectAt(at) is not { } hit || Session.Scripts.IsSuspended)
        {
            return;
        }
        IReadOnlyList<Verb> verbs = VerbsOf(hit);
        _menuTarget = hit;
        _menuAt = screen;
        // 68BD:0040: only Talk, only Use or only a container's Open is done at once; anything else opens
        // the verb window (Get, Talk, Use, and Look or Open). Attack and Use with… are reached otherwise.
        Verb[] windowVerbs = [.. verbs.Where(v => v is Verb.Talk or Verb.Get or Verb.Use or Verb.Open)];
        if (hit.Source is { } source && GameWorld.IsMoney(source.Ojff) && verbs.Contains(Verb.Get))
        {
            // 68BD:0040: coins are picked up at once.
            ChooseVerb(Verb.Get);
            return;
        }
        if (windowVerbs is [Verb.Talk] or [Verb.Use] or [Verb.Open] || verbs.Count == 1)
        {
            ChooseVerb(windowVerbs.Length == 1 ? windowVerbs[0] : verbs[0]);
            return;
        }
        _verbMenu.Open(VerbTargetOf(hit), verbs, _palette, screen);
    }

    /// <summary>What the verb window shows of <paramref name="obj"/> (<see cref="VerbMenu"/>).</summary>
    private VerbTarget VerbTargetOf(WorldObject obj)
    {
        // 91F7:0C29 writes a thing's name only if its record has one (a fire pit may not).
        string name = obj.Name;
        if (obj.Kind != ObjectKind.Creature)
        {
            return new VerbTarget(name, false, 0, 0, 0, [], obj.Source?.Frame is { Width: > 0 } frame ? TextureFor(frame) : null);
        }
        int level = 0;
        if (obj.Character is { } character)
        {
            foreach (byte l in character.Levels)
            {
                level = Math.Max(level, l);
            }
        }
        // 78A9:28DC: each readied weapon's name, then its damage (attacks * dice, 6868:0489's "%d%s*%dD%d%+d").
        var lines = new List<string>();
        foreach (Item item in obj.Items.Where(i => i.Slot is 3 or 10 or 2).OrderBy(i => i.Slot == 2 ? 1 : 0))
        {
            if (_world.Items.TypeOf(item.Record) is { IsWeapon: true } type && (type.Weapon & DarkSun.Data.Items.WeaponKind.Shield) == 0)
            {
                lines.Add(_world.Items.NameOf(item.Record));
                lines.Add($"{Math.Max(1, (int)type.Attacks)}*{type.DamageDice}D{type.DamageSides}{type.DamageBonus + item.Record.Bonus:+0;-0;+0}");
            }
        }
        return new VerbTarget(name, true, level, obj.Vitals.Hp, obj.Vitals.MaxHp, lines, null);
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

    /// <summary>
    /// Use with… an item from the inventory: held on the pointer (<c>191F:0A99</c>
    /// mode 3), then used with what is clicked. Use-with triggers name it by its
    /// item (<c>name(-1095)</c>, a pot of water), which it stands for here.
    /// </summary>
    private void SetUseWithItem(Item item)
    {
        SetUseWith(new WorldObject(item.Id, ObjectKind.Thing, item.Id) { Name = ItemText.Name(_world, item), Item = item.Record });
        _useWithItem = item;
    }

    /// <summary>
    /// An item just picked up from cell <paramref name="from"/> is held on the pointer, not
    /// put away: clicked on a party member it goes in their pack, in the inventory it is put
    /// where it is clicked, on the bare ground it is put down, and Esc puts it back.
    /// </summary>
    private void HoldFromGround(Item item, Cell from)
    {
        SetUseWithItem(item);
        _heldFromGround = (item, from);
        _prompt.Text = $"Holding {ItemText.Name(_world, item)}: click a character or open the inventory to take it, the ground to put it down (Esc: back where it was)";
    }

    /// <summary>The party member whose sprite is drawn at map point <paramref name="at"/>, or null.</summary>
    private WorldObject? MemberAt(Vector2 at) =>
        _party.Where(m => DrawnAt(m.Node, at)).OrderByDescending(m => m.Node.Position.Y)
            .Select(m => _world[m.Walker.Id]).FirstOrDefault(o => o is { Kind: ObjectKind.Party });

    /// <summary>The item held on the pointer, clicked on party member <paramref name="to"/>: it goes in their pack.</summary>
    private void PutHeldOn(Item item, WorldObject to)
    {
        if (_world.Party.FirstOrDefault(m => m.Items.Contains(item)) is not { } holder)
        {
            return;
        }
        string name = ItemText.Name(_world, item);
        item.Slot = Equipment.Pack;
        if (holder != to && !_world.MoveItem(holder, to, item))
        {
            Say($"give {name} to {to.Name}", $"{to.Name} can't carry any more.");
            return;
        }
        AddScriptLine($"> {to.Name} puts {name} in the pack");
    }

    /// <summary>
    /// The inventory on <paramref name="page"/>: with an item held on the pointer, its holder's,
    /// the item still picked up there (and back on the pointer if it is still picked when it closes).
    /// </summary>
    private void OpenInventory(int member, CharacterScreen.Page page)
    {
        if (page == CharacterScreen.Page.Items && _useWithItem is { } item
            && _world.Party.FirstOrDefault(m => m.Items.Contains(item)) is { } holder)
        {
            (Item Item, Cell From)? ground = _heldFromGround;
            SetUseWith(null);
            _heldFromGround = ground;
            _character.Open(_world, holder.Index, page, held: item);
            return;
        }
        _character.Open(_world, member, page);
    }

    /// <summary>
    /// Put <paramref name="item"/>, held on the pointer from a member's pack, on the
    /// ground under map point <paramref name="at"/>; not on a blocked cell or off the map.
    /// </summary>
    private void DropHeld(Item item, Vector2 at)
    {
        var cell = new Cell((int)Math.Floor(at.X / Region.TileSize), (int)Math.Floor(at.Y / Region.TileSize));
        if (_world.Party.FirstOrDefault(m => m.Items.Contains(item)) is not { } holder)
        {
            return;
        }
        string name = ItemText.Name(_world, item);
        if (_region.IsBlocked(cell.X, cell.Y))
        {
            Say($"drop {name}", $"{name} can't be put down there.");
            return;
        }
        item.Slot = Equipment.Pack;
        if (_world.DropItem(holder, item, Math.Max(1, item.Quantity), cell) is null)
        {
            Say($"drop {name}", $"{name} can't be put down there.");
            return;
        }
        AddScriptLine($"> {holder.Name} drops {name} at ({cell.X},{cell.Y})");
    }

    /// <summary>Use with…: wait for the second object (null: stop waiting).</summary>
    private void SetUseWith(WorldObject? first)
    {
        _useWith = first;
        _useWithItem = null;
        _heldFromGround = null;
        _prompt.Visible = first is not null;
        _prompt.Text = first is null ? "" : $"Use {Called(first)} with… (click an object; right-click: other pointers; Esc puts it away)";
        // DSUN.EXE's pointer mode 3: the item is held on the pointer, which shows it.
        if (first is not null)
        {
            _gameMenu.SetCursor(GameMenu.Cursor.Item);
        }
        else if (_cursor == GameMenu.Cursor.Item)
        {
            _gameMenu.ResetCursor();
        }
    }

    /// <summary>
    /// Do <paramref name="verb"/> to <paramref name="target"/>: Look at once;
    /// the others once the selected character has walked up to it
    /// (<see cref="Approach"/>). Without a party it acts at once.
    /// </summary>
    /// <summary>
    /// A MOVE click at map point <paramref name="at"/> (<c>1587:0427</c>): nothing on a blocked
    /// cell (the pointer is crossed out), "NO PATH FROM HERE" when no way leads there, else walk
    /// (an object's cell: up to it).
    /// </summary>
    private void ClickToWalk(Vector2 at, bool wholeParty)
    {
        var cell = new Cell((int)Math.Floor(at.X / Region.TileSize), (int)Math.Floor(at.Y / Region.TileSize));
        if (ObjectAt(at) is null && _region.IsBlocked(cell.X, cell.Y))
        {
            return;
        }
        if (ObjectAt(at) is null && Selected is { } leader && !leader.Walker.CanReach(cell, _region.IsBlocked))
        {
            _message.Show(NoPath);
            return;
        }
        WalkTo(cell, wholeParty);
    }

    /// <summary>DSUN.EXE's words when a click can't be carried out (<c>DS:0BD9</c>, <c>DS:0BF8</c>), shown in the message box.</summary>
    private const string NoPath = "NO PATH FROM HERE", NoLineOfSight = "NO LINE OF SIGHT";

    /// <summary>Can the leader see <paramref name="target"/> (any of its cells; <c>0898:00A4</c>)? Without a leader, yes.</summary>
    private bool Sees(WorldObject target) =>
        Selected is not { } m || _world.CellsOf(target).Any(c => GplGeometry.LineOfSight(_region, m.Walker.Cell.X, m.Walker.Cell.Y, c.X, c.Y));

    private void StartVerb(WorldObject target, Verb verb, WorldObject? with)
    {
        _pendingUse = null;
        if (verb != Verb.Look && !InCombat && !Sees(target))
        {
            // Out of sight, nothing is done: "NO LINE OF SIGHT".
            _message.Show(NoLineOfSight);
            return;
        }
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
        if (verb == Verb.Open && _world[actor] is { } opener)
        {
            // 6527:002C: the container's own window on the map.
            AddScriptLine($"> {opener.Name} opens {Describe(target)}");
            _world.ShowOpen(target); // a chest shows open while its window is up, as USE leaves it
            _containerWindow.Open(target, opener, _palette, _menuAt);
            return;
        }
        if (verb == Verb.Get && target.Item is not null && _world[actor] is { } taker)
        {
            if (_world.PickUpMoney(target) is > 0 and var coins)
            {
                // 7BEB:0A60: coins go to the party's money, not a pack.
                GainMoney(coins, $"{taker.Name} picks up {coins} coins");
                return;
            }
            // 7BEB:0A60: the item goes into the pack first, then its pickup trigger runs.
            Cell from = target.Cell;
            if (!_world.PickUp(taker, target))
            {
                Say(what, target.Items.Count > 0 ? $"{Called(target, start: true)} has something in it: open it and take that first." : $"{taker.Name} can't carry any more.");
                return;
            }
            Item item = taker.Items[^1]; // PickUp adds it last
            AddScriptLine($"> {taker.Name} picks up {Describe(target)}");
            if (scripts.OnObject(GplTriggerList.Pickup, actor, target.Index) is { } picked)
            {
                ReportScript(what, picked);
            }
            // It rides on the pointer until it is put on a member, in the inventory or on the map.
            if (taker.Items.Contains(item))
            {
                HoldFromGround(item, from);
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
            case Verb.Look when target.Name.Length > 0:
                Say(what, target.Name); // no look script: the engine names it
                break;
            case Verb.Look:
                AddScriptLine($"> {what}: nothing to see"); // DSUN.EXE shows nothing for a nameless thing without a look script
                break;
            case Verb.Get:
                Say(what, $"{Called(target, start: true)} can't be picked up.");
                break;
            case Verb.Attack:
                if (_checkingVerbs)
                {
                    AddScriptLine($"> {what}: would start a fight"); // the verb check is about scripts: no fight, no hostile creature
                }
                else if (!StartCombat(target, what))
                {
                    Say(what, $"{Called(target, start: true)} won't fight.");
                }
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

    /// <summary>
    /// A click on <paramref name="item"/> in the open container on the map
    /// (<see cref="ContainerWindow"/>): coins become the party's money, anything
    /// else goes into the opener's pack if it has room.
    /// </summary>
    private void TakeFromContainer(Item item)
    {
        if (_containerWindow is not { Container: { } container, Opener: { } taker })
        {
            return;
        }
        if (_world.TakeMoney(container, item) is > 0 and var coins)
        {
            GainMoney(coins, $"{taker.Name} takes {coins} coins from {Describe(container)}");
        }
        else if (_world.MoveItem(container, taker, item))
        {
            AddScriptLine($"> {taker.Name} takes {ItemText.Name(_world, item)} from {Describe(container)}");
        }
        else
        {
            Say($"take {ItemText.Name(_world, item)}", $"{taker.Name} can't carry any more.");
        }
    }

    /// <summary>The item window (<see cref="ItemWindow"/>) for <paramref name="item"/> right-clicked in a container on the map.</summary>
    private void DescribeItem(Item item)
    {
        Vector2 screen = GetViewport().GetVisibleRect().Size;
        float scale = Math.Max(1f, MathF.Floor(Math.Min(screen.X / RetroScreen.Width, screen.Y / RetroScreen.Height)));
        List<string> users = [.. _world.Party.Where(p => !p.Hidden && p.Index < Session.Party.Count && _world.CanUse(p, item)).Select(p => MemberName(p.Index))];
        _itemWindow.Open(_world, item, ItemIcon(item.Rdff), users, _palette, GetViewport().GetMousePosition() / scale, screen / scale, scale);
    }

    /// <summary>
    /// Coins taken: the party's money grows by <paramref name="coins"/>, and the
    /// message is the new total, as DSUN.EXE's ("%d…$", <c>7BEB:0A60</c>; the
    /// game font's $ is the coin).
    /// </summary>
    private void GainMoney(int coins, string what)
    {
        Session.ScriptHost.Money += coins;
        Say(what, $"{Session.ScriptHost.Money}$");
    }

    private static string VerbText(Verb verb) => verb switch
    {
        Verb.Talk => "talk to",
        Verb.Get => "get",
        Verb.Use => "use",
        Verb.UseWith => "use with",
        Verb.Attack => "attack",
        Verb.Open => "open",
        _ => "look at",
    };

    /// <summary>The object's name, or "that" (capitalised at the start of a sentence) for a thing without one.</summary>
    private static string Called(WorldObject obj, bool start = false) => obj.Name.Length > 0 ? obj.Name : start ? "That" : "that";

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
        if (!obj.IsDoor && !use.Toggled && !use.Rested)
        {
            return false;
        }
        AddScriptLine(obj.IsDoor
            ? $"use {Describe(obj)}: {(use.Toggled ? (use.WasClosed ? "opens" : "closes") : "stays " + (use.WasClosed ? "closed" : "open"))}"
            : use.Rested ? $"use {Describe(obj)}: the party rests" : $"use {Describe(obj)}: toggled");
        _restsTaken += use.Rested ? 1 : 0;
        if (Session.Scripts.OnObject(GplTriggerList.Use, actor, obj.Index) is { } run)
        {
            ReportScript($"use {Describe(obj)}", run);
        }
        if (_world.DoorRefusal(use) is { } message && !Session.Scripts.IsSuspended)
        {
            AddScriptLine($"    {message}");
            _message.Show(message); // 6BB8's door messages go to the message box (5F6D:05DE)
        }
        ShowOpenedContainer(obj, actor, use);
        _overlay.QueueRedraw();
        return true;
    }

    /// <summary>
    /// A container used open (a chest's second picture) shows what it holds, as
    /// Open does; used shut, its window goes. DSUN.EXE's USE only swaps the picture
    /// (<c>6BB8:0B56</c>), and its verb window has no button for a usable
    /// container's contents.
    /// </summary>
    private void ShowOpenedContainer(WorldObject obj, int actor, DoorUse use)
    {
        if (!use.Toggled || obj.IsDoor || !_world.IsContainer(obj) || Session.Scripts.IsSuspended)
        {
            return;
        }
        if (obj.SecondState && _world[actor] is { } opener)
        {
            _containerWindow.Open(obj, opener, _palette, _menuAt);
        }
        else if (_containerWindow.Container == obj)
        {
            _containerWindow.Close();
        }
    }

    private static string Describe(WorldObject obj) =>
        obj.Name.Length > 0 ? $"{obj.Name} (OJFF {-obj.Id})" : $"OJFF {-obj.Id}";

    /// <summary>Log a script run and the world commands it issued (PRINT_STRING as its text).</summary>
    private void ReportScript(string what, GplRunResult run)
    {
        _scriptRuns++;
        _scriptErrors += run.Error is null || run.Suspended ? 0 : 1;
        if (_checking && run.Error is not null && !run.Suspended) GD.Print($"TEMPERR {what}: {run.Entry.Kind} {run.Entry.File} @{run.Entry.Address:x4}: {run.Error}");
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
            AddScriptLine($"    FIGHT at ({fight.X},{fight.Y})");
            if (Session.ScriptHost.WatchedFight is not null)
            {
                _watchedFightPending = true; // once the announcement is read (StartWatchedFight)
            }
            else if (!_checkingVerbs)
            {
                StartCombat(null, "FIGHT");
            }
        }
        if (!run.Suspended && Session.ScriptHost.GameOverRequested)
        {
            Session.ScriptHost.GameOverRequested = false;
            AddScriptLine("    game over");
            GameOver();
            return;
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

    /// <summary>A script's cinematic (<c>REQUEST(6)</c>) playing over the map, which waits meanwhile.</summary>
    private IntroView? _cinematic;

    /// <summary>
    /// <c>REQUEST(6, n)</c>: play cinematic n over the map, then the region's music again and the
    /// script goes on. False (the script goes straight on) for the pictures 200 and 300, which
    /// aren't decoded (a <c>CMAT</c> / <c>CPAL</c>), without <c>CINE.GFF</c>, and in headless checks.
    /// </summary>
    private bool PlayCinematic(int number)
    {
        if (_checking || _cinematic is not null || !CinematicPlayer.Cinematics.ContainsKey(number) || Session.Install?.FindFile("CINE.GFF") is null)
        {
            AddScriptLine($"    cinematic {number} not played");
            return false;
        }
        var layer = new CanvasLayer { Layer = 60 };
        _cinematic = new IntroView
        {
            Overlay = number,
            Done = () =>
            {
                layer.QueueFree();
                _cinematic = null;
                if (RegionInfo.Read(Session.Open("RESOURCE.GFF"), _region.Number) is { Music: > 0 } info)
                {
                    Music.Instance?.ChangeTo("RESOURCE.GFF", info.Music);
                }
                if (Session.Scripts.IsSuspended && Session.Scripts.PausedFor is null)
                {
                    ReportScript("cinematic over", Session.Scripts.Resume());
                }
            },
        };
        layer.AddChild(_cinematic);
        AddChild(layer);
        return true;
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
            // A change of state (a chest opening) plays out even while the pointer holds the map: under USE
            // the window over it shows the chest open, not waiting for the window to go.
            animated.OwnClock = true;
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
                if (Settings.Autosave)
                {
                    Save(AutoSave, "Autosave", quiet: true);
                }
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
    /// Save to <paramref name="file"/> in the saves folder (true if saved). Not while a script
    /// waits for the player or the party is between regions: the save would
    /// have to capture a script half run.
    /// </summary>
    private bool Save(string file, string name, bool quiet = false)
    {
        if (_checking)
        {
            return false;
        }
        if (InCombat)
        {
            if (!quiet)
            {
                Notice("Can't save in a fight.");
            }
            return false;
        }
        if (Session.Scripts.IsSuspended || _transitioning || _world.PendingRegion is not null)
        {
            if (!quiet)
            {
                Notice(Session.Scripts.PausedFor is not null
                    ? "Can't save now: a script is paused in the debugger (F8 continues)."
                    : "Can't save now: finish the conversation first.");
            }
            return false;
        }
        try
        {
            var party = _party.Select((m, i) => new PartyMemberSave(m.Sprite.Ojff, m.Walker.Cell.X, m.Walker.Cell.Y, m.Walker.Facing,
                i < Session.Party.Count ? Session.Party[i] : null));
            SaveGame save = SaveGame.Capture(name, Session.Scripts, Session.ScriptHost.EngineVariables, _world, Session.Regions,
                party, _selected, Session.Mods.Mods.Select(m => m.Name), Session.ScriptHost.PartyMembers, Session.ScriptHost.Journal);
            save.Write(Session.SavePath(file));
            AddScriptLine($"    saved to {Session.SavePath(file)}");
            if (!quiet)
            {
                Notice($"{name}: saved.");
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Notice($"Couldn't save: {e.Message}");
            return false;
        }
    }

    /// <summary>A file key for a new saved game: when it was saved, made unique if two are saved in the same second.</summary>
    private static string NewSaveFile()
    {
        string stamp = $"save-{DateTime.Now:yyyyMMdd-HHmmss}", file = stamp;
        for (int n = 2; File.Exists(Session.SavePath(file)); n++)
        {
            file = $"{stamp}-{n}";
        }
        return file;
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
        save.RestoreParty(Session.ScriptHost.PartyMembers, i => _world.NewItem(i));
        Session.ScriptHost.Journal.Load(save.Journal);
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
        if (save.CollapsedLeader is { } leader)
        {
            _world.RestoreCollapsedParty(leader, save.Party.Select((member, slot) => (member, slot))
                .Where(p => p.member.Collapsed).Select(p => p.slot));
        }
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
    /// a creature into an item (<c>REQUEST(9)</c>), refuse an item to a class,
    /// loot a dead creature's body, hurt and rest, and save
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

        // Coins the arena crowd throws (OJFF 1388, GPL 2), with a big pot dropped on the same cell after them,
        // drawn in front: the pointer on a coin's pixels finds the coin, the empty part of its box finds nothing.
        const int Coins = 1388;
        if (_world.Spawn(Coins, me.Cell) is { } coins && _world.DropItem(me, party[0].Items.First(i => i.Rdff == Water), 1) is { } cover
            && _cloneSprites.TryGetValue(coins, out Sprite2D? coinSprite) && _cloneSprites.TryGetValue(cover, out Sprite2D? coverSprite))
        {
            Rect2 box = new(coinSprite.Position + coinSprite.GetRect().Position, coinSprite.GetRect().Size);
            List<Vector2> points = [.. Enumerable.Range(0, (int)box.Size.Y).SelectMany(y => Enumerable.Range(0, (int)box.Size.X)
                .Select(x => box.Position + new Vector2(x + 0.5f, y + 0.5f)))];
            bool InCoverBox(Vector2 p) => new Rect2(coverSprite.Position + coverSprite.GetRect().Position, coverSprite.GetRect().Size).HasPoint(p);
            // The pot moved until an empty corner of its picture lies over some of the coins (still in front of them).
            Vector2 start = coverSprite.Position;
            foreach (Vector2 shift in Enumerable.Range(1, 12).SelectMany(dy => Enumerable.Range(-16, 33).Select(dx => new Vector2(dx, dy))))
            {
                coverSprite.Position = start + shift;
                if (points.Any(p => DrawnAt(coinSprite, p) && InCoverBox(p) && !DrawnAt(coverSprite, p)))
                {
                    break;
                }
            }
            List<Vector2> onCoins = [.. points.Where(p => DrawnAt(coinSprite, p) && !DrawnAt(coverSprite, p))];
            Expect(onCoins.Any(InCoverBox), "an empty corner of the pot's picture lies over the coins");
            Expect(onCoins.Count > 0 && onCoins.All(p => ObjectAt(p) == coins), $"the pointer on the coins' pixels finds them ({onCoins.Count(p => ObjectAt(p) == coins)} of {onCoins.Count})");
            Expect(points.Where(p => !DrawnAt(coinSprite, p) && !DrawnAt(coverSprite, p)).All(p => ObjectAt(p) != coins), "and not the empty corners of their picture");
            // Get on coins: the party's money, not an item (7BEB:0A60).
            int money = Session.ScriptHost.Money, packed = me.Items.Count;
            Act(coins, 0, Verb.Get);
            Expect(_world[coins.Index] != coins && Session.ScriptHost.Money == money + coins.Item!.Value && me.Items.Count == packed,
                $"picking up the coins adds their {coins.Item!.Value} to the party's money ({money} → {Session.ScriptHost.Money})");
            _world.PickUp(me, cover);
        }
        else
        {
            problems.Add("no coins dropped beside a pot");
        }

        // A chest on the map (OJFF 1564, the slave pens' zombie room: scale chest armour and +3 arrows) and a coffin
        // (1574: a gem and 50 in coins) hold what their RDFFs link to them; Open shows them in the container's own
        // window (6527:002C, WIND 13501), six slots; a click takes an item into the pack, coins into the party's money.
        const int ZombieChest = 1564, Coffin = 1574, ScaleChestArmor = 1033, PlusThreeArrows = 2273, Gem = 1504, FiftyCoins = 1386;
        if (_world.Spawn(ZombieChest, me.Cell) is { } chest && _world.Spawn(Coffin, me.Cell) is { } coffin)
        {
            Expect(chest.Items.Select(i => i.Rdff).SequenceEqual([ScaleChestArmor, PlusThreeArrows]), $"the chest holds scale chest armour and +3 arrows ({string.Join(", ", chest.Items.Select(i => i.Rdff))})");
            Expect(coffin.Items.Select(i => i.Rdff).SequenceEqual([Gem, FiftyCoins]), $"the coffin holds a gem and coins ({string.Join(", ", coffin.Items.Select(i => i.Rdff))})");
            Expect(_world.IsContainer(chest) && VerbsOf(chest).Contains(Verb.Open), "the chest offers Open");
            Act(chest, 0, Verb.Open);
            Expect(_containerWindow.Visible && _containerWindow.Container == chest && _containerWindow.GetNodeOrNull("WIND 13501") is not null
                && _containerWindow.ItemAt(0)?.Rdff == ScaleChestArmor && _containerWindow.ItemAt(1)?.Rdff == PlusThreeArrows,
                "Open shows the chest's armour and arrows in its window");
            Expect(chest.SecondState, "Open shows the chest open while its window is up");
            Expect(_animated.FirstOrDefault(a => a.Object == chest.Source) is not { } opening || opening.OwnClock,
                "the chest's opening plays while the pointer holds the map");
            int packed = me.Items.Count;
            _containerWindow.Click(0);
            Expect(me.Items.Any(i => i.Rdff == ScaleChestArmor) && me.Items.Count == packed + 1 && _containerWindow.ItemAt(0)?.Rdff == PlusThreeArrows,
                "clicking the armour takes it into the pack; the arrows move up");
            _containerWindow.Click(0, MouseButton.Right);
            Expect(_itemWindow.Visible && _itemWindow.Item?.Rdff == PlusThreeArrows && _containerWindow.Visible, "right-clicking the arrows shows them in the item window");
            _itemWindow.Close();
            _containerWindow.Close();
            Expect(chest.SecondState, "the chest stays open when its window goes");
            // Used, the open chest shuts; used again, it swaps to its open picture and shows what it holds.
            Expect(VerbsOf(chest).Contains(Verb.Use), "the chest offers Use");
            Act(chest, 0, Verb.Use);
            Expect(!chest.SecondState && !_containerWindow.Visible, "using the open chest shuts it");
            Act(chest, 0, Verb.Use);
            Expect(chest.SecondState && _containerWindow.Visible && _containerWindow.Container == chest && _containerWindow.ItemAt(0)?.Rdff == PlusThreeArrows,
                "using it again opens it and shows the arrows left in it");
            _containerWindow.Close();
            Act(coffin, 0, Verb.Open);
            int money = Session.ScriptHost.Money;
            _containerWindow.Click(1);
            Expect(Session.ScriptHost.Money == money + 50 && coffin.Items.Select(i => i.Rdff).SequenceEqual([Gem]), $"clicking the coffin's coins adds 50 to the party's money ({money} → {Session.ScriptHost.Money})");
            _containerWindow.Close();
            _world.Remove(chest);
            _world.Remove(coffin);
        }
        else
        {
            problems.Add("no chest or coffin");
        }

        // An item held on the pointer from the pack (pointer mode 3) and clicked on the bare ground is put down there.
        Cell free = new[] { new Cell(1, 0), new Cell(-1, 0), new Cell(0, 1), new Cell(0, -1) }
            .Select(d => new Cell(me.Cell.X + d.X, me.Cell.Y + d.Y))
            .FirstOrDefault(c => !_region.IsBlocked(c.X, c.Y) && !_world.Objects.Any(o => !o.Hidden && o.Cell == c), me.Cell);
        if (me.Items.FirstOrDefault(i => !i.Equipped && i.Rdff is not (Boots or Water)) is { } held)
        {
            SetUseWithItem(held);
            int heldRdff = held.Rdff;
            DropHeld(held, new Vector2((free.X + 0.5f) * Region.TileSize, (free.Y + 0.5f) * Region.TileSize));
            Expect(!me.Items.Contains(held) && _world.Objects.Any(o => !o.Hidden && o.Cell == free && o.Item is not null && o.Source?.Ojff == heldRdff),
                $"a held item clicked on the ground at ({free.X},{free.Y}) is put down there");

            // Get on it: it is held on the pointer, not put away. Esc puts it back where it lay; in the inventory it is
            // still picked up, and back on the pointer when that closes; clicked on a member, it goes in their pack.
            WorldObject? Lying() => _world.Objects.FirstOrDefault(o => !o.Hidden && o.Cell == free && o.Item is not null && o.Source?.Ojff == heldRdff);
            if (Lying() is { } lying)
            {
                Act(lying, 0, Verb.Get);
                Expect(_useWithItem?.Rdff == heldRdff && _heldFromGround?.From == free && _cursor == GameMenu.Cursor.Item && Lying() is null,
                    "Get holds the item on the pointer");
                _UnhandledInput(new InputEventAction { Action = Controls.Back, Pressed = true });
                Expect(_useWith is null && Lying() is not null && !me.Items.Any(i => i.Rdff == heldRdff), "Esc puts it back where it lay");
            }
            if (Lying() is { } again)
            {
                Act(again, 0, Verb.Get);
                Item? got = _useWithItem;
                OpenInventory(_selected, CharacterScreen.Page.Items);
                Expect(got is not null && _character.IsOpen && _character.Picked == got && _useWith is null, "the inventory opens with it picked up");
                _character.Close();
                Expect(got is not null && _useWithItem == got && _heldFromGround?.From == free, "closed with it still picked, it is held from the ground again");
                WorldObject to = _world.Party.FirstOrDefault(m => m != me && m.Items.Count < Equipment.Capacity) ?? me;
                if (got is not null)
                {
                    SetUseWith(null);
                    PutHeldOn(got, to);
                }
                Expect(got is not null && to.Items.Contains(got) && Lying() is null, $"put on {to.Name}, it is in their pack");
            }
            else
            {
                problems.Add("the dropped item isn't on the ground to pick up");
            }
        }
        else
        {
            problems.Add("nothing in the pack to hold and drop");
        }

        // The game menu stops the game: the map's clock doesn't run under it.
        _gameMenu.Open();
        double clockBefore = _clock;
        _Process(0.5);
        Expect(_clock == clockBefore, $"the map's clock stands still while the game menu is open ({clockBefore} → {_clock})");
        _gameMenu.Close();
        _Process(0.5);
        Expect(_clock > clockBefore || MapHeld, "and runs again when it closes");

        // OVERHEAD MAP: the whole region in view, the game stopped, the party and the enemies marked; again, back as it was.
        Vector2 zoomBefore = _camera.Zoom;
        OverheadMap();
        double overheadClock = _clock;
        _Process(0.5);
        var marks = OverheadMarks().ToList();
        Expect(OverheadShown && _overheadMarks.Visible && _clock == overheadClock && _camera.Zoom.X <= zoomBefore.X
            && marks.Count(m => !m.Enemy) == _world.Party.Count(p => !p.Hidden),
            $"OVERHEAD MAP shows the whole region, stopped, the party marked ({marks.Count(m => !m.Enemy)} members, {marks.Count(m => m.Enemy)} enemies)");
        OverheadMap();
        Expect(!OverheadShown && !_overheadMarks.Visible && _camera.Zoom == zoomBefore, "and goes back to the view as it was");

        Item boots = party[0].Items.First(i => i.Rdff == Boots);
        Expect(_world.Equip(me, boots) == EquipResult.Worn && boots.Slot == 13, $"boots worn on the feet (slot {boots.Slot})");
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

        // A creature killed with something on it leaves a body to open.
        if (_world.Objects.FirstOrDefault(o => o.Kind == ObjectKind.Creature && !o.Hidden) is { } victim)
        {
            _world.Give(1, -Water, victim.Index, GameWorld.None);
            _world.Hurt(victim, victim.Vitals.Hp + 1);
            WorldObject? body = _world.First(-GameWorld.DeadBody);
            Expect(body is not null && _world.IsContainer(body) && body.Items.Any(i => i.Rdff == Water), "the dead creature left a body holding its pot of water");
            Expect(body is not null && ObjectVerbs.For(body, _world.Items, Session.Scripts.Triggers, _world.IsContainer(body)).Contains(Verb.Open), "the body offers Open");
            Expect(body is not null && !_world.PickUp(me, body), "a body with something in it isn't picked up");
            Expect(body is not null && _world.MoveItem(body, me, body.Items[0]) && body.Items.Count == 0, "took the pot out of the body");
        }
        else
        {
            problems.Add("no creature to leave a body");
        }

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

        // A class that can't use it: the Helm of Might isn't for thieves.
        const int Helm = 1519;
        Func<int, ItemClasses?> classes = _world.PartyClasses;
        _world.PartyClasses = _ => ItemClasses.Thief;
        Expect(_world.Give(1, -Helm, 0, GameWorld.None) == 1, "GIVE made the Helm of Might");
        Item helm = party[0].Items.First(i => i.Rdff == Helm);
        Expect(_world.Equip(me, helm) == EquipResult.WrongClass && !helm.Equipped, "a thief can't wear the Helm of Might");
        _world.PartyClasses = _ => ItemClasses.Fighter;
        Expect(_world.Equip(me, helm) == EquipResult.Worn, "a fighter can");
        _world.PartyClasses = classes;
        helm.Slot = Equipment.Pack;
        _world.Give(1, -Helm, GameWorld.None, 0);

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
            ? $"items: made, dropped, picked up, coins to money, a chest and a coffin opened and emptied, worn, refused by class, searched, left and found, transformed, hurt, looted from a body, rested, saved and loaded ({carried[0]}; {carried[1]})"
            : "items: " + string.Join("; ", problems));
        GetTree().Quit(problems.Count > 0 ? 1 : 0);
    }

    /// <summary>
    /// <c>--check-character</c>: the character screens, clicked as the player
    /// would: wear boots by clicking them then the feet, take them off with a
    /// double-click, be refused the Helm of Might as a thief, give a pot to
    /// the next member through their box, split a stack of arrows, loot a dead
    /// body, and turn to the character sheet.
    /// </summary>
    private void CheckCharacter()
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
        const int Water = 1095, Boots = 1051, Helm = 1519, Arrows = 1070, Feet = 13, Head = 7;
        const int Worn = 0, Pack = 1, Inside = 2;
        if (_world.Party.Count() < 2)
        {
            AddCharacter(_world[0]!.Cell);
        }
        WorldObject me = _world[0]!, next = _world.Party.First(p => p.Index != 0);
        me.Items.Clear();
        _world.Give(1, -Boots, 0, GameWorld.None);
        _world.Give(1, -Helm, 0, GameWorld.None);
        _world.Give(1, -Water, 0, GameWorld.None);
        _world.Give(20, -Arrows, 0, GameWorld.None);
        Item boots = me.Items.First(i => i.Rdff == Boots), helm = me.Items.First(i => i.Rdff == Helm);
        Item pot = me.Items.First(i => i.Rdff == Water), arrows = me.Items.First(i => i.Rdff == Arrows);
        CharacterScreen screen = _character;
        screen.Open(_world, 0, CharacterScreen.Page.Items);
        int PackIndex(Item item) => Enumerable.Range(0, 12).First(n => screen.ItemAt(Pack, n) == item);
        // 74F1:17AF: the member's figure, BMP 20000 + their look, in the body's place.
        Expect(screen.Figure is >= 20000 and < 20014 && Session.Party[0].Sprite - Session.Rules.IconBase == screen.Figure - 20000,
            $"the member's figure shows where a container opens (BMP {screen.Figure}, map icon OJFF {Session.Party[0].Sprite})");

        screen.Click(Pack, PackIndex(boots));
        Expect(screen.Picked == boots && screen.Info.Contains("Boots"), "clicking the boots picks them and describes them");
        screen.Click(Worn, Feet);
        Expect(boots.Slot == Feet && screen.ItemAt(Worn, Feet) == boots && screen.Picked is null, "clicking the feet wears them");
        screen.Click(Worn, Feet, doubleClick: true);
        Expect(!boots.Equipped, "a double-click takes them off");

        Func<int, ItemClasses?> classes = _world.PartyClasses;
        _world.PartyClasses = _ => ItemClasses.Thief;
        screen.Click(Pack, PackIndex(helm));
        screen.Click(Worn, Head);
        Expect(!helm.Equipped && screen.Info.Contains("can't use"), $"a thief is refused the Helm of Might ({screen.Info.Split('\n')[0]})");
        // Right-click: the item window (WIND 15502, 91F7:0000); INFO turns to who may use it (91F7:12D8).
        ItemWindow window = screen.ItemWindow;
        string Shown() => string.Join(" / ", window.Lines);
        screen.Click(Pack, PackIndex(helm), button: MouseButton.Right);
        Expect(window.Visible && window.Item == helm && window.GetNodeOrNull("WIND 15502") is not null && window.Lines.Contains(ItemText.Name(_world, helm)),
            $"right-clicking the helm shows it in the item window ({Shown()})");
        window.ToggleUsers();
        Expect(window.ShowingUsers && window.Lines.SequenceEqual(["USEABLE BY:", "NO ONE"]), $"INFO: no thief may use it ({Shown()})");
        window.Close();
        _world.PartyClasses = _ => ItemClasses.Fighter;
        screen.Click(Worn, Head);
        Expect(helm.Slot == Head, "a fighter wears it");
        screen.Click(Worn, Head, button: MouseButton.Right);
        window.ToggleUsers();
        Expect(window.Lines is ["USEABLE BY:", ..] && window.Lines.Contains(MemberName(0)), $"INFO: a fighter may ({Shown()})");
        window.Close();

        // USE (91F7:03D0): a lemon (RDFF 31028) makes its eater whole and is eaten; a wand (31007) waits for a
        // fight; boots have no spell and no USE.
        const int Lemon = 31028, Wand = 31007;
        _world.Give(1, -Lemon, 0, GameWorld.None);
        _world.Give(1, -Wand, 0, GameWorld.None);
        Item lemon = me.Items.Last(i => i.Rdff == Lemon), wand = me.Items.Last(i => i.Rdff == Wand);
        screen.Open(_world, 0, CharacterScreen.Page.Items);
        Vitals vitals = me.Vitals;
        (int hp, int maxHp) = (vitals.Hp, vitals.MaxHp);
        (vitals.MaxHp, vitals.Hp) = (20, 5);
        screen.Click(Pack, PackIndex(lemon), button: MouseButton.Right);
        Expect(window.UseOffered, "the lemon's item window has USE");
        window.PressUse();
        Expect(vitals.Hp == 20 && !me.Items.Contains(lemon), $"USE: the lemon makes its eater whole and is gone ({vitals.Hp}/20; {screen.Status})");
        (vitals.MaxHp, vitals.Hp) = (maxHp, hp);
        screen.Click(Pack, PackIndex(wand), button: MouseButton.Right);
        window.PressUse();
        Expect(screen.IsOpen && screen.Status == "ONLY IN COMBAT" && wand.Charges == wand.Record.Charges, $"the wand waits for a fight ({screen.Status}, {wand.Charges} charges)");
        screen.Click(Pack, PackIndex(boots), button: MouseButton.Right);
        Expect(window.Visible && !window.UseOffered, "boots have no USE");
        window.Close();
        me.Items.Remove(wand);
        // A scroll (RDFF 1410) teaches its spell (66) and is used up.
        const int Scroll = 1410, ScrollSpell = 66;
        var learner = Session.Party[0];
        bool knew = learner.Spellbook.Remove(ScrollSpell);
        _world.Give(1, -Scroll, 0, GameWorld.None);
        Item scroll = me.Items.Last(i => i.Rdff == Scroll);
        screen.Open(_world, 0, CharacterScreen.Page.Items);
        screen.Click(Pack, PackIndex(scroll), button: MouseButton.Right);
        window.PressUse();
        Expect(learner.Spellbook.Contains(ScrollSpell) && !me.Items.Contains(scroll), $"USE on a scroll teaches its spell ({screen.Status})");
        if (!knew)
        {
            learner.Spellbook.Remove(ScrollSpell);
        }
        screen.Click(Pack, PackIndex(boots));
        screen.Click(Worn, Head);
        Expect(!boots.Equipped && helm.Slot == Head, "boots don't go on the head");
        _world.PartyClasses = classes;
        screen.Click(Pack, PackIndex(boots)); // put them back down
        Expect(screen.Picked is null, "clicking their own place puts them back down");

        // A picked item rides on the pointer and goes into the slot it is put in; a taken slot swaps.
        screen.Click(Pack, PackIndex(boots));
        Expect(screen.HeldIcon is not null && screen.ItemAt(Pack, PackIndex(boots)) == boots, "the picked boots are the pointer's picture");
        screen.Click(Pack, 9);
        Expect(screen.Picked is null && screen.ItemAt(Pack, 9) == boots && boots.Cell == 9, "and go into the empty slot they are put in (10th)");
        int potWas = PackIndex(pot);
        screen.Click(Pack, potWas);
        screen.Click(Pack, 9);
        Expect(screen.ItemAt(Pack, 9) == pot && screen.ItemAt(Pack, potWas) == boots, "put on the boots' slot, the pot and the boots change places");

        screen.Click(Pack, PackIndex(pot));
        screen.ClickBox(next.Index);
        Expect(next.Items.Contains(pot) && !me.Items.Contains(pot), "clicking another member's box gives them the pot");

        screen.Click(Pack, PackIndex(arrows));
        screen.Press(13302);
        Expect(me.Items.Count(i => i.Rdff == Arrows) == 2 && me.Items.Where(i => i.Rdff == Arrows).Sum(i => i.Quantity) == 20, "SPLIT makes two stacks of 10 arrows");

        // A dead body, opened: take what is in it.
        if (_world.Objects.FirstOrDefault(o => o.Kind == ObjectKind.Creature && !o.Hidden) is { } victim)
        {
            _world.Give(1, -Water, victim.Index, GameWorld.None);
            _world.Hurt(victim, victim.Vitals.Hp + 1);
            if (_world.First(-GameWorld.DeadBody) is { } body)
            {
                screen.Open(_world, 0, CharacterScreen.Page.Items, body);
                Item inBody = body.Items[0];
                screen.Click(Inside, 0);
                screen.Click(Pack, 11);
                Expect(body.Items.Count == 0 && me.Items.Contains(inBody) && screen.ItemAt(Pack, 11) == inBody,
                    "clicking the body's pot, then the pack's last slot, takes it there");
                screen.Click(Pack, PackIndex(inBody));
                screen.Click(Inside, 5);
                Expect(body.Items.Contains(inBody) && screen.ItemAt(Inside, 5) == inBody, "and clicking it then the body's 6th slot puts it back there");
                screen.Click(Inside, 5, doubleClick: true);
                Expect(me.Items.Contains(inBody), "a double-click takes it again");
            }
            else
            {
                problems.Add("no body");
            }
        }

        // A carried bag (OJFF 1077): double-clicked it opens in the body's place (6527:12BD); things go in,
        // a container doesn't; double-clicked again, or an empty slot right-clicked, it closes.
        const int CarriedBag = 1077;
        // (The last member, whose pack has room on its first page.)
        int carrier = _world.Party.Last().Index;
        WorldObject holder = _world[carrier]!;
        if (_world.Give(2, -CarriedBag, carrier, GameWorld.None) == 2 && holder.Items.Where(i => i.Rdff == CarriedBag).ToList() is [var bag, var otherBag]
            && _world.Give(1, -Water, carrier, GameWorld.None) == 1 && holder.Items.LastOrDefault(i => i.Rdff == Water) is { } bagged)
        {
            screen.Open(_world, carrier, CharacterScreen.Page.Items);
            screen.Click(Pack, PackIndex(bag), doubleClick: true);
            Expect(screen.OpenItem == bag && screen.ContainerShown, "double-clicking a carried bag opens it");
            screen.Click(Pack, PackIndex(bagged));
            screen.Click(Inside, 0);
            Expect(bag.Contents.Contains(bagged) && !holder.Items.Contains(bagged) && screen.ItemAt(Inside, 0) == bagged, "the pot goes in the bag");
            screen.Click(Pack, PackIndex(otherBag));
            screen.Click(Inside, 1);
            Expect(!bag.Contents.Contains(otherBag) && holder.Items.Contains(otherBag), "a bag doesn't go in a bag");
            screen.Click(Pack, PackIndex(otherBag)); // put it down again
            screen.Click(Pack, PackIndex(bag), doubleClick: true);
            Expect(screen.OpenItem is null && !screen.ContainerShown, "double-clicking it again closes it");
            screen.Click(Pack, PackIndex(bag), doubleClick: true);
            screen.Click(Inside, 3, button: MouseButton.Right);
            Expect(!screen.ContainerShown, "right-clicking an empty slot of the open bag closes it");
            screen.Click(Pack, PackIndex(bag), button: MouseButton.Right);
            Expect(screen.OpenItem == bag && screen.ContainerShown && !screen.ItemWindowShown, "right-clicking the carried bag opens it");
            screen.Click(Pack, PackIndex(bag), button: MouseButton.Right);
            Expect(!screen.ContainerShown, "and right-clicking it again closes it");
        }
        else
        {
            problems.Add("no bag to carry");
        }

        screen.Press(10300);
        Expect(screen.Showing == CharacterScreen.Page.Character && screen.Sheet.Contains("HP"), "the Character icon turns to the sheet");
        screen.OpenDual(0);
        Expect(screen.Dual.Visible && screen.Dual.Title.Length > 0, $"a box's DUAL opens the class choice in the game too ({screen.Dual.Title})");
        screen.Dual.Close();
        screen.Press(10308);
        Expect(!screen.IsOpen, "Return closes it");

        CheckMagicAndMenus(Expect);

        GD.Print(problems.Count == 0
            ? "character: wore and took off, refused by class, gave, split, looted a body, used a lemon, a wand and a scroll, turned to the sheet; "
              + "cast, rested, learned, chose a power to use, effects, leader, computer control, game menu, preferences, store: OK"
            : "character: " + string.Join("; ", problems));
        GetTree().Quit(problems.Count > 0 ? 1 : 0);
    }

    /// <summary>
    /// The rest of <c>--check-character</c>, with characters of its own (the
    /// party's are put back after): a water cleric casts Cure Light Wounds on
    /// the next member, nothing memorized; a preserver learns Magic Missile; a
    /// psionicist picks Detonate; the leader and computer-control buttons; the
    /// game menu, its preferences and questions; a store.
    /// </summary>
    private void CheckMagicAndMenus(Action<bool, string> Expect)
    {
        const int Above = 0, CureLight = 72, MagicMissile = 9, Detonate = 139;
        var keep = Session.Party.ToList();
        var cleric = new DarkSun.Game.Characters.Character { Name = "Cleric", Classes = [DarkSun.Game.Characters.CharacterClass.Cleric], Sphere = DarkSun.Game.Characters.Sphere.Water, Rolled = [12, 12, 12, 12, 16, 12], Experience = 3000 };
        var preserver = new DarkSun.Game.Characters.Character { Name = "Preserver", Classes = [DarkSun.Game.Characters.CharacterClass.Preserver], Rolled = [12, 12, 12, 16, 12, 12], Experience = 3000 };
        var psionicist = new DarkSun.Game.Characters.Character { Name = "Psionicist", Classes = [DarkSun.Game.Characters.CharacterClass.Psionicist], Disciplines = [.. Enum.GetValues<DarkSun.Game.Characters.Discipline>()], Rolled = [12, 12, 12, 12, 12, 12], Experience = 3000 };
        new DarkSun.Game.Characters.CharacterCreation(Session.Rules, Session.Names, new Random()).GiveStartingPowers(psionicist);
        Session.Party.Clear();
        Session.Party.AddRange([cleric, preserver, psionicist]);
        while (_party.Count < 3 && AddCharacter(_world[0]!.Cell) is not null)
        {
        }
        CharacterScreen screen = _character;
        int SlotOf(bool ready, int spell) => Enumerable.Range(0, 21).FirstOrDefault(i => screen.SpellAt(ready, i) == spell, -1);
        try
        {
            // The cleric: no memorizing, a click picks Cure Light Wounds and the hurt second member's box casts it.
            screen.Open(_world, 0, CharacterScreen.Page.Spells);
            Expect(SlotOf(false, CureLight) >= 0 && screen.Status.EndsWith("LEVEL 1 SPELLS TO CAST"),
                $"a water cleric's 1st level shows Cure Light Wounds and the casts left ({screen.Status})");
            Vitals hurt = _world[1]!.Vitals;
            int casts = new DarkSun.Game.Magic.Spellcasting(Session.Rules).CastsLeft(cleric, DarkSun.Game.Magic.SpellKind.Priest, 1);
            for (int i = 0; i < casts; i++)
            {
                (hurt.MaxHp, hurt.Hp, hurt.Status) = (20, 5, Vitals.Well);
                screen.ClickSpell(false, SlotOf(false, CureLight));
                Expect(screen.PickedSpell == CureLight, $"clicking the spell picks it to cast ({screen.Status})");
                screen.ClickBox(1);
                Expect(hurt.Hp > 5 && cleric.Cast.Count == i + 1, $"then the second member's box heals them ({hurt.Hp} hit points; {screen.Status})");
            }
            screen.ClickSpell(false, SlotOf(false, CureLight));
            Expect(screen.PickedSpell is null && screen.Status.Contains("REST"), $"the level's casts used: rest first ({screen.Status})");
            Expect(screen.CrossedOut(false, SlotOf(false, CureLight)), "and the level's spells are crossed out");
            _world.Rest(raiseDead: false);
            Expect(cleric.Cast.Count == 0, "resting gives the casts back");
            Expect(_message.Shown == "THE PARTY RESTS", $"and the message box says THE PARTY RESTS ({_message.Shown})");
            _message.Close();

            // The preserver: a level gained opens the study window (8CCC:0000); a click learns, then it casts what the book holds.
            screen.Close();
            preserver.SpellsToChoose = 1;
            _checking = false;
            OfferSpellStudy();
            _checking = true;
            Expect(_study.IsOpen && _study.Lines == "CHOOSE A SPELL, PRESERVER" && _study.Offered.Contains(MagicMissile),
                $"a level gained opens CHOOSE A SPELL with Magic Missile ({_study.Lines}; {_study.Offered.Count} offered)");
            _study.Choose(_study.Offered.ToList().IndexOf(MagicMissile));
            Expect(preserver.Spellbook.Contains(MagicMissile) && preserver.SpellsToChoose == 0 && _study.Lines == "MAGIC MISSILE IS LEARNED!",
                $"clicking it learns it ({_study.Lines})");
            _study.Exit();
            Expect(!_study.IsOpen, "EXIT after choosing closes it");
            screen.Open(_world, 1, CharacterScreen.Page.Spells);
            screen.ClickSpell(false, SlotOf(false, MagicMissile));
            Expect(screen.PickedSpell == MagicMissile, $"and it can be cast at once ({screen.Status})");
            screen.ClickSpell(false, SlotOf(false, MagicMissile), button: MouseButton.Right);
            Expect(screen.SpellInfo?.StartsWith("MAGIC MISSILE:") == true, $"right-clicking it shows what it does ({screen.SpellInfo})");
            screen._Input(new InputEventKey { Keycode = Key.Escape, PhysicalKeycode = Key.Escape, Pressed = true });
            Expect(screen.SpellInfo is null && screen.Visible, "Esc closes it, not the screen");

            // The psionicist: CAST SPELL/USE PSIONIC shows the powers it was given; a click picks one to use.
            screen.ClickSpell(false, SlotOf(false, MagicMissile)); // put Magic Missile down: a box click would cast it
            screen.ClickBox(2);
            Expect(screen.Showing == CharacterScreen.Page.Psionics && SlotOf(false, Detonate) >= 0
                && Enumerable.Range(0, 21).All(i => screen.SpellAt(false, i) is not int n || psionicist.Powers.Contains(n)),
                $"the psionicist's page shows the powers it knows, Detonate first ({screen.Showing}, {screen.Status})");
            screen.ClickSpell(false, SlotOf(false, Detonate));
            Expect(screen.PickedSpell == Detonate && psionicist.Powers.Count == 5, $"clicking one picks it to use, learning nothing ({screen.Status})");

            // CURRENT SPELL EFFECTS: out of a fight, none.
            screen.Press(11306);
            Expect(screen.Showing == CharacterScreen.Page.Effects && screen.SpellAt(false, 0) is null && screen.Status == "CURRENT SPELL EFFECTS",
                $"the fourth button shows the current spell effects, none out of a fight ({screen.Showing}, {screen.Status})");

            // Leader and computer control.
            screen.Press(11310);
            Expect(_selected == 1 && screen.Status.Contains("IS LEADER"), $"the second leader button makes it leader ({screen.Status})");
            screen.Press(11313);
            Expect(cleric.ComputerControl && screen.Status == "COMPUTER CONTROL IS ON", $"computer control turns on ({screen.Status})");
            screen.Press(11313);
            Expect(!cleric.ComputerControl, "and off");

            // The game menu, from the wide button.
            screen.Press(11308);
            Expect(!screen.IsOpen && _gameMenu.Showing == "menu", $"the wide button opens the game menu ({_gameMenu.Showing})");
            int difficulty = Settings.Difficulty;
            _gameMenu.Press(10303);
            Expect(_gameMenu.Showing == "preferences", "SET PREFERENCES");
            _gameMenu.Press(difficulty < 3 ? 16308 : 16309);
            Expect(Settings.Difficulty != difficulty, $"the difficulty arrows change it ({_gameMenu.Difficulty})");
            Settings.Difficulty = difficulty;
            Settings.Save();
            _gameMenu.Press(11308);
            _gameMenu.Press(10302);
            Expect(_gameMenu.Showing == "ask: LOAD/SAVE GAME", $"LOAD/SAVE GAME asks ({_gameMenu.Showing})");
            _gameMenu.Press(14001);
            Expect(_saves.IsOpen && !_gameMenu.IsOpen, "its SAVE opens the save screen over the map");
            _saves.Close();
            Expect(!_saves.IsOpen, "which closes again");
            _gameMenu.Open();
            _gameMenu.Press(10311);
            Expect(!_gameMenu.IsOpen && _cursor == GameMenu.Cursor.Look, "the eye is the LOOK cursor");
            _gameMenu.ResetCursor();
            _gameMenu.Open();
            _gameMenu.Press(11304);
            Expect(screen.IsOpen && screen.Showing == CharacterScreen.Page.Items, "VIEW INVENTORY opens the items");
            screen.Close();

            // A store: buy, then sell.
            if (_world.Objects.FirstOrDefault(o => o.Kind == ObjectKind.Creature && !o.Hidden) is { } merchant)
            {
                const int Boots = 1051, Water = 1095;
                _world.Give(1, -Boots, merchant.Index, GameWorld.None);
                _world[0]!.Items.Clear();
                _world.Give(1, -Water, 0, GameWorld.None);
                Session.ScriptHost.Money = 1000;
                screen.Open(_world, 0, CharacterScreen.Page.Items, store: merchant);
                Item goods = Enumerable.Range(0, 12).Select(i => screen.ItemAt(2, i)).OfType<Item>().First(i => i.Rdff == Boots);
                int index = Enumerable.Range(0, 12).First(i => screen.ItemAt(2, i) == goods);
                screen.Click(2, index);
                screen.Click(1, 11);
                int paid = 1000 - Session.ScriptHost.Money;
                Expect(_world[0]!.Items.Any(i => i.Rdff == Boots) && paid == goods.Record.Value, $"buying boots costs their value ({paid})");
                Item pot = _world[0]!.Items.First(i => i.Rdff == Water);
                int before = Session.ScriptHost.Money;
                screen.Click(1, Enumerable.Range(0, 12).First(i => screen.ItemAt(1, i) == pot));
                screen.Click(3, 0);
                Expect(screen.ItemAt(3, 0) == pot, "the pot goes in a sell slot");
                screen.Press(13304);
                Expect(screen.Info.Contains("give you") || screen.Info.Contains("COULDN'T"), $"SELL: the merchant's offer ({screen.Info.Replace('\n', ' ')})");
                screen.Press(13304);
                Expect(Shop.Offer(pot) == 0 || (merchant.Items.Contains(pot) && Session.ScriptHost.Money == before + Shop.Offer(pot)),
                    $"TAKE IT: sold ({Session.ScriptHost.Money - before})");
                screen.Close();
            }
        }
        finally
        {
            Session.Party.Clear();
            Session.Party.AddRange(keep);
        }
    }

    /// <summary>
    /// <c>--check-prisoners</c>: in the arena, untie each bound prisoner
    /// (GPL 5 by way of their talk triggers) and see what the script leaves:
    /// the empty frame (<c>OJFF</c> 2603) and the prisoner, freed (280 or 319)
    /// or dead (his body, 1258); then leave the arena and come back to them.
    /// </summary>
    private async void CheckPrisoners()
    {
        var problems = new List<string>();
        async Task Frames(int n)
        {
            for (int i = 0; i < n; i++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
        }
        GplDialogue dialogue = Session.ScriptHost.Dialogue;
        foreach (int prisoner in new[] { -1202, -318 })
        {
            if (_world.First(prisoner) is not { } bound)
            {
                problems.Add($"no prisoner {-prisoner}");
                continue;
            }
            Cell at = bound.Cell;
            _world.Teleport(DialogueGplHost.WholeParty, _world.Region.Number, at.X + 1, at.Y + 1);
            await Frames(3);
            Act(bound, _selected, Verb.Talk);
            var said = new List<string>();
            for (int i = 0; i < 40 && (dialogue.Prompt is not null || Session.Scripts.IsSuspended || _dialogueWindow.Visible); i++)
            {
                await Frames(3);
                said.Add(string.Concat(dialogue.Text.Select(t => t.Text)).Trim());
                if (_dialogueWindow.ShowingChoices)
                {
                    var choices = _dialogueWindow.Choices.ToList();
                    int untie = choices.FindIndex(c => c.Contains("untie", StringComparison.OrdinalIgnoreCase) || c.Contains("free you", StringComparison.OrdinalIgnoreCase));
                    int bye = choices.FindIndex(c => c.TrimStart().StartsWith("Goodbye", StringComparison.OrdinalIgnoreCase));
                    GD.Print($"prisoners: {-prisoner} offers [{string.Join(" | ", choices.Select(c => c.Trim()))}]");
                    _dialogueWindow.Pick(untie >= 0 ? untie : bye >= 0 ? bye : choices.Count - 1);
                }
                else
                {
                    _dialogueWindow.PressMore();
                }
            }
            await Frames(3);
            string near = string.Join(", ", _world.Objects.Where(o => !o.Hidden && Math.Abs(o.Cell.X - at.X) <= 2 && Math.Abs(o.Cell.Y - at.Y) <= 2)
                .Select(o => $"{-o.Id} {o.Kind} {o.Cell.X},{o.Cell.Y}"));
            GD.Print($"prisoners: {-prisoner} at {at.X},{at.Y}: said \"{said.LastOrDefault(s => s.Length > 0)}\"; near now: {near}");
            if (_world.First(prisoner) is not null)
            {
                problems.Add($"prisoner {-prisoner} is still tied up");
            }
            if (!_world.Objects.Any(o => !o.Hidden && o.Id is -1258 or -280 or -319 && Math.Abs(o.Cell.X - at.X) <= 2 && Math.Abs(o.Cell.Y - at.Y) <= 2))
            {
                problems.Add($"prisoner {-prisoner} left neither a body (1258) nor a freed prisoner (280, 319)");
            }
        }
        // Back in the arena later (by way of region 43): the body and the empty frame are still there.
        int region = _region.Number;
        // The main gate left open after the first bout, as GPL 2 opens it: it is shut again on coming back (ArenaGate).
        WorldObject? gate = _world.Objects.FirstOrDefault(o => o.Source?.Ojff == ArenaGate.Ojff);
        if (gate is { SecondState: true })
        {
            _world.ToggleState(gate);
        }
        foreach (int x in new[] { 30, 31 })
        {
            _world.SetCellFlags(x, 21, CellFlags.Blocked, false);
        }
        GplVariables.Set(Session.Scripts, "gflag[5]=0");
        GplVariables.Set(Session.Scripts, "gflag[16]=1");
        // A sound the arena's scripts asked for but that wasn't played yet (the fade stops the frames) is left with it.
        const int Unplayed = int.MinValue;
        Session.ScriptHost.Sounds.Enqueue(Unplayed);
        EnterRegion(region + 1, new Cell(10, 10));
        if (Session.ScriptHost.Sounds.Contains(Unplayed))
        {
            problems.Add("a sound queued in the arena would play in the next region");
        }
        await Frames(3);
        EnterRegion(region, new Cell(30, 30));
        await Frames(3);
        CheckGateCameBackShut(problems);
        foreach (int left in new[] { -1258, -2603 })
        {
            WorldObject? o = _world.First(left);
            GD.Print($"prisoners: back in region {_region.Number}: {-left} {(o is null ? "gone" : $"at {o.Cell.X},{o.Cell.Y} sprite={(_cloneSprites.TryGetValue(o, out var sp) ? $"{sp.Position} visible={sp.Visible}" : "none")}")}");
            if (o is null || !_cloneSprites.TryGetValue(o, out var drawn) || !drawn.Visible)
            {
                problems.Add($"{-left} isn't shown after coming back");
            }
        }
        foreach (string p in problems)
        {
            GD.Print($"  PROBLEM: {p}");
        }
        GD.Print(problems.Count == 0 ? "prisoners: OK" : $"prisoners: {problems.Count} problems");
        GetTree().Quit(problems.Count == 0 ? 0 : 1);
    }

    /// <summary>The step of <c>--check-prisoners</c> after coming back: the arena's main gate, left open, is shut and drawn shut.</summary>
    private void CheckGateCameBackShut(List<string> problems)
    {
        if (!Settings.ScriptRepairs)
        {
            GD.Print("prisoners: script repairs are off in the options; the gate isn't checked");
            return;
        }
        if (_world.Objects.FirstOrDefault(o => o.Source?.Ojff == ArenaGate.Ojff) is not { } gate)
        {
            problems.Add($"no main gate ({ArenaGate.Ojff}) in region {_region.Number}");
            return;
        }
        AnimatedObject? drawn = _animated.FirstOrDefault(a => a.Object == gate.Source);
        for (int step = 0; step < 100 && drawn is { Animator.Animating: true }; step++)
        {
            _running += 60;
            Animate();
        }
        bool blocked = _region.IsBlocked(30, 21) && _region.IsBlocked(31, 21);
        int gflag5 = Session.Scripts.Memory.Read(new GplVariable(GplVarType.GlobalFlag, 5));
        GD.Print($"prisoners: main gate back in the arena: {(gate.SecondState ? "shut" : "open")}, frame {drawn?.Animator.Frame}, "
            + $"cells {(blocked ? "blocked" : "free")}, gflag[5] {gflag5}");
        // Frame 0 is the shut gate, 1 the open arch (BMP 1875).
        if (!gate.SecondState || drawn?.Animator.Frame != 0 || !blocked || gflag5 != 1)
        {
            problems.Add("the main gate, left open, didn't come back shut as gflag[5] says");
        }
    }

    /// <summary>
    /// <c>--check-colours</c> (best with <c>--region=41</c>): the colour cycling runs on the map
    /// with the original's ranges and speeds, an icon's turning colours carry their codes, and,
    /// with a window (not headless), the screen changes as they turn and shows no code.
    /// </summary>
    private async void CheckColours()
    {
        var problems = new List<string>();
        void Expect(bool ok, string what)
        {
            if (!ok)
            {
                problems.Add(what);
            }
        }
        ColourCycling cycling = ColourCycling.Instance!;
        Expect(cycling.Running, "the colour cycling runs on the map");
        // 1587:1F83: 11–15 turn every step, 240–248 every second step.
        int blue = cycling.ShownAt(11), lava = cycling.ShownAt(240);
        cycling.Step();
        Expect(cycling.ShownAt(11) != blue, $"colours 11–15 turn every step ({blue} → {cycling.ShownAt(11)})");
        cycling.Step();
        Expect(cycling.ShownAt(240) != lava, $"colours 240–248 turn every second step ({lava} → {cycling.ShownAt(240)})");
        if (RegionInfo.Read(Session.Open("RESOURCE.GFF"), _region.Number) is { Cycle1: { Count: > 1, Speed: > 0 } first })
        {
            int was = cycling.ShownAt(first.First);
            for (int i = 0; i < first.Speed; i++)
            {
                cycling.Step();
            }
            Expect(cycling.ShownAt(first.First) != was, $"the region's first range turns every {first.Speed} steps");
        }
        // The spell button's highlighted icon (ICON 11103 frame 1, its flames 242–247) is drawn in colours that turn: its texture carries their codes.
        GffFile resources = Session.Open("RESOURCE.GFF");
        Palette ui = Session.Assets.Palette(resources.Read("PAL ", 1001));
        Image icon = Textures.ToImage(Session.Assets.Frames(resources.Read("ICON", 11103))[1], ui);
        static int Code(Color c) => (c.R8 & 3) | (c.G8 & 3) << 2 | (c.B8 & 3) << 4;
        int coded = Enumerable.Range(0, icon.GetWidth() * icon.GetHeight()).Count(i => Code(icon.GetPixel(i % icon.GetWidth(), i / icon.GetWidth())) is > 0 and <= 40);
        Expect(coded > 0, "the spell icon's turning colours carry their codes");
        string screen = "headless: not looked at";
        if (DisplayServer.GetName() != "headless")
        {
            await ToSignal(GetTree().CreateTimer(1), SceneTreeTimer.SignalName.Timeout);
            Image before = GetViewport().GetTexture().GetImage();
            await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);
            Image after = GetViewport().GetTexture().GetImage();
            int changed = 0, codes = 0;
            for (int y = 0; y < before.GetHeight(); y++)
            {
                for (int x = 0; x < before.GetWidth(); x++)
                {
                    changed += before.GetPixel(x, y) != after.GetPixel(x, y) ? 1 : 0;
                    codes += cycling.ShowsCode(after.GetPixel(x, y)) ? 1 : 0;
                }
            }
            Expect(changed > 0, "the screen changes as the colours turn");
            Expect(codes == 0, $"no code shows on the screen ({codes} pixels)");
            screen = $"{changed} pixels changed in 0.4 s";
        }
        GD.Print($"colours: region {_region.Number}, {coded} coded pixels in the spell icon, {screen}");
        foreach (string p in problems)
        {
            GD.Print($"  PROBLEM: {p}");
        }
        GD.Print(problems.Count == 0 ? "colours: OK" : $"colours: {problems.Count} problems");
        GetTree().Quit(problems.Count == 0 ? 0 : 1);
    }

    /// <summary>
    /// <c>--check-dialogue</c>: talk to the first creature with a talk trigger,
    /// read its text, go on to the choices and say goodbye; then page through a
    /// text longer than the window.
    /// </summary>
    private async void CheckDialogue()
    {
        var problems = new List<string>();
        void Expect(bool ok, string what)
        {
            if (!ok)
            {
                problems.Add(what);
            }
        }
        async Task Frames(int n)
        {
            for (int i = 0; i < n; i++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
        }
        GplDialogue dialogue = Session.ScriptHost.Dialogue;
        WorldObject? talker = _world.Objects.FirstOrDefault(o => Session.Scripts.Triggers.MatchObject(GplTriggerList.TalkTo, o.Id) is not null);
        Expect(talker is not null, "something to talk to");
        string said = "", chose = "";
        if (talker is not null)
        {
            Act(talker, _selected, Verb.Talk);
            await Frames(3);
            Expect(_dialogueWindow.Visible && !_dialogueWindow.ShowingChoices && dialogue.Text.Count > 0, "the text window shows the text first");
            Expect(dialogue.Portrait is not null, "with a portrait");
            // A click on the map (away from the window) reads on; the map holds while the text shows.
            var (version, paging, choices) = (dialogue.Version, _dialogueWindow.Paging, _dialogueWindow.ShowingChoices);
            Vector2 onMap = GetViewportRect().Size * new Vector2(0.5f, 0.35f);
            foreach (bool down in new[] { true, false })
            {
                GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = down, Position = onMap, GlobalPosition = onMap });
            }
            await Frames(3);
            Expect(choices || dialogue.Version != version || _dialogueWindow.Paging != paging || _dialogueWindow.ShowingChoices || !_dialogueWindow.Visible,
                "a click on the map reads on");
            int clock = _world.Clock;
            await ToSignal(GetTree().CreateTimer(1.5), Godot.Timer.SignalName.Timeout);
            Expect(!_dialogueWindow.Visible || _world.Clock == clock, $"the map holds while the text shows (clock {clock} → {_world.Clock})");
            said = string.Concat(dialogue.Text.Select(t => t.Text)).Trim();
            for (int i = 0; i < 10 && !_dialogueWindow.ShowingChoices && dialogue.Prompt is not GplMenuPrompt; i++)
            {
                _dialogueWindow.PressMore(); // read on (CONTINUE, pages)
                await Frames(3);
            }
            if (dialogue.Prompt is GplMenuPrompt)
            {
                for (int i = 0; i < 5 && !_dialogueWindow.ShowingChoices; i++)
                {
                    _dialogueWindow.PressMore();
                    await Frames(3);
                }
                Expect(_dialogueWindow.ShowingChoices && _dialogueWindow.Choices.Count > 0, "MORE goes on to the choices");
                int bye = _dialogueWindow.Choices.ToList().FindIndex(c => c.TrimStart().StartsWith("Goodbye", StringComparison.OrdinalIgnoreCase) || c.TrimStart().StartsWith("Bye", StringComparison.OrdinalIgnoreCase));
                chose = bye >= 0 ? _dialogueWindow.Choices[bye] : _dialogueWindow.Choices.LastOrDefault() ?? "";
                _dialogueWindow.Pick(bye >= 0 ? bye : _dialogueWindow.Choices.Count - 1);
                await Frames(3);
                Expect(dialogue.Prompt is not GplMenuPrompt || !_dialogueWindow.ShowingChoices || Session.Scripts.IsSuspended, "the choice was taken");
            }
            for (int i = 0; i < 20 && (dialogue.Prompt is not null || Session.Scripts.IsSuspended); i++)
            {
                if (dialogue.Prompt is GplMenuPrompt or GplYesNoPrompt)
                {
                    _dialogueWindow.Pick(_dialogueWindow.Choices.Count - 1);
                }
                else
                {
                    _dialogueWindow.PressMore();
                }
                await Frames(3);
            }
            Expect(!Session.Scripts.IsSuspended, "the conversation ended");
        }

        // The game's font: decoded, and the same again through its picture form.
        Expect(GameFonts.Font is { Height: 9 } f && f.Glyphs['A'].Width == 6, "FONT 100 decodes (9 high, 'A' 6 wide)");
        if (Session.Open("RESOURCE.GFF").TryRead("FONT", 100, out var fontData))
        {
            byte[] again = DarkSun.Data.Text.FontSheet.Import(DarkSun.Data.Text.FontSheet.Export(DarkSun.Data.Text.GameFont.Parse(fontData.Span)));
            Expect(again.AsSpan().SequenceEqual(fontData.Span), "FONT 100 comes back byte for byte from its picture");
        }

        // A long text, paged.
        dialogue.Clear();
        for (int i = 1; i <= 12; i++)
        {
            dialogue.Append($"Line {i} of a long story that goes on and on across the window. ", 115);
        }
        await Frames(3);
        var (top, lines, page) = _dialogueWindow.Paging;
        Expect(lines > page && top == 0, $"long text: {lines} lines, {page} a page");
        _dialogueWindow.PressMore();
        await Frames(2);
        Expect(_dialogueWindow.Paging.Top == page, $"MORE shows the next page (top {_dialogueWindow.Paging.Top})");
        _dialogueWindow.PressUp();
        await Frames(2);
        Expect(_dialogueWindow.Paging.Top == 0, "the arrow goes back");
        for (int i = 0; i < 20 && _dialogueWindow.Visible; i++)
        {
            _dialogueWindow.PressMore();
            await Frames(2);
        }
        Expect(!_dialogueWindow.Visible && dialogue.IsEmpty, "read to the end, MORE puts the window away");

        GD.Print($"dialogue: {(talker is null ? "-" : Describe(talker))} said \"{(said.Length > 60 ? said[..60] + "…" : said)}\"; chose \"{chose}\"; "
            + $"a {lines}-line text in pages of {page}");
        foreach (string p in problems)
        {
            GD.Print($"  PROBLEM: {p}");
        }
        GD.Print(problems.Count == 0 ? "dialogue: OK" : $"dialogue: {problems.Count} problems");
        GetTree().Quit(problems.Count == 0 ? 0 : 1);
    }

    /// <summary>
    /// <c>--check-journal</c>: talk to someone, then check the journal has the
    /// conversation (speaker, portrait, replies) and the first region; begin
    /// the example quest by its flag; save and load the journal; open the
    /// journal and options windows; rebind a key and put it back (the
    /// settings file isn't written).
    /// </summary>
    private async void CheckJournal()
    {
        var problems = new List<string>();
        void Expect(bool ok, string what)
        {
            if (!ok)
            {
                problems.Add(what);
            }
        }
        // The map information lines are a debug helper: hidden at first, F7 shows and hides them.
        bool infoHidden = !_hudPanel.Visible;
        foreach (bool down in new[] { true, false })
        {
            GetViewport().PushInput(new InputEventKey { Keycode = Key.F7, PhysicalKeycode = Key.F7, Pressed = down });
        }
        Expect(infoHidden && _hudPanel.Visible, "the map information starts hidden and F7 shows it");
        _hudPanel.Visible = false;
        DarkSun.Game.History.Journal journal = Session.ScriptHost.Journal;
        Expect(journal.Events.Any(e => e.Text.Contains($"region {_region.Number}")), "the first region is in the events");
        if (_world.Objects.FirstOrDefault(o => Session.Scripts.Triggers.MatchObject(GplTriggerList.TalkTo, o.Id) is not null) is { } talker)
        {
            Act(talker, _selected, Verb.Talk);
            for (int i = 0; i < 30 && Session.Scripts.IsSuspended; i++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                if (Session.ScriptHost.Dialogue.Prompt is GplMenuPrompt or GplYesNoPrompt)
                {
                    _dialogueWindow.PressMore(); // to the choices
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    _dialogueWindow.Pick(_dialogueWindow.Choices.Count - 1);
                }
                else
                {
                    _dialogueWindow.PressMore();
                }
            }
            var c = journal.Conversations.LastOrDefault();
            Expect(c is not null && c.Speaker == talker.Name && c.Portrait is not null, $"the conversation is with {talker.Name}, portrait and all ({c?.Speaker})");
            Expect(c is not null && c.Lines.Any(l => l.Reply) && c.Lines.Any(l => !l.Reply), "it has what was said and a reply");
        }

        // The example quest begins with its flag.
        Expect(Session.Quests.Quests.Count > 0, "the quest file was read");
        int? Read(string name) => int.TryParse(GplVariables.Get(Session.Scripts, name), out int v) ? v : null;
        int before = Session.Quests.Status(Read).Count;
        GplVariables.Set(Session.Scripts, "gflag[28] = 1");
        Expect(Session.Quests.Status(Read).Count == before + 1, "setting gflag[28] begins Semyon's quest");

        // Saved and loaded.
        int conversations = journal.Conversations.Count, events = journal.Events.Count;
        string file = "check-" + Guid.NewGuid().ToString("N")[..8];
        Save(file, "Check");
        string path = Session.SavePath(file);
        LoadSaved(SaveGame.Read(path));
        File.Delete(path);
        journal = Session.ScriptHost.Journal;
        Expect(journal.Conversations.Count == conversations && journal.Events.Count == events,
            $"the journal came back from the save ({journal.Conversations.Count}/{conversations} conversations, {journal.Events.Count}/{events} events)");

        // The windows open; a key rebinds and comes back.
        _journal.Open();
        _options.Open();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Expect(_journal.Visible && _options.Visible, "the journal and options windows open");
        bool shadows = Settings.Shadows;
        Expect(_options.Press("Shadows") == OnOffWord(!shadows) && Settings.Shadows == !shadows
            && _options.Press("Shadows") == OnOffWord(shadows) && Settings.Shadows == shadows, "an options bar turns its option over and back");
        Expect(_options.WindowFits, "the options window is inside the 320 × 200 screen");
        static string OnOffWord(bool on) => on ? "ON" : "OFF";
        _journal.Close();
        _options.Visible = false; // closing would save the settings
        Controls.Rebind(Controls.QuickSave, new InputEventKey { PhysicalKeycode = Key.F12 });
        Expect(KeyHelp.Keys(Controls.QuickSave) == "F12", $"quick save rebinds to F12 ({KeyHelp.Keys(Controls.QuickSave)})");
        Controls.ResetKeys();
        Expect(KeyHelp.Keys(Controls.QuickSave) == "F5", "and goes back to F5");

        GD.Print($"journal: {journal.Conversations.Count} conversations, {journal.Events.Count} events, "
            + $"{Session.Quests.Status(Read).Count} of {Session.Quests.Quests.Count} quests begun");
        foreach (string p in problems)
        {
            GD.Print($"  PROBLEM: {p}");
        }
        GD.Print(problems.Count == 0 ? "journal: OK" : $"journal: {problems.Count} problems");
        GetTree().Quit(problems.Count == 0 ? 0 : 1);
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
        // Saved from the save screen, under a name of its own.
        string name = "Check " + Guid.NewGuid().ToString("N")[..8];
        _saves.Open(SavesView.Mode.Save);
        _saves.Type(name);
        _saves.Press(2057);
        string? path = Session.ListSaves().FirstOrDefault(s => s.Save?.Name == name).Path;
        if (path is null || _saves.IsOpen)
        {
            GD.Print($"save check: nothing was saved as {name} from the save screen");
            GetTree().Quit(1);
            return;
        }

        // Change everything, then load it from the load screen.
        Session.Scripts.Memory.Area(GplArea.GlobalFlags)[50] = 0;
        ShowRegion(home + 2);
        AnswerForThePlayer();
        _saves.Open(SavesView.Mode.Load);
        // The map gets no keys under the screen: quick load (F9) does nothing, not even say there is none.
        _notice.Text = "";
        foreach (bool down in new[] { true, false })
        {
            GetViewport().PushInput(new InputEventKey { Keycode = Key.F9, PhysicalKeycode = Key.F9, Pressed = down });
        }
        bool keysKept = _notice.Text.Length == 0 && _saves.IsOpen;
        bool listed = _saves.SelectTitle(name);
        _saves.Press(2057);
        File.Delete(path);

        var problems = new List<string>();
        void Expect<T>(string what, T want, T got)
        {
            if (!EqualityComparer<T>.Default.Equals(want, got))
            {
                problems.Add($"{what}: {got} instead of {want}");
            }
        }
        Expect("listed by its name", true, listed);
        Expect("F9 kept from the map under the load screen", true, keysKept);
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
    /// map, kill another, move a third and clone a thing; leave for another region and come
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
            // Killed: gone from the region, maybe a body left where it fell.
            WorldObject? killed = creatures.Where(c => c != gone && c != moved && c.Source is not null && !c.IsClone).Skip(1).FirstOrDefault();
            RegionObject? killedSource = killed?.Source;
            if (killed is not null)
            {
                _world.Hurt(killed, killed.Vitals.Hp + 5);
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
            if (killedSource is not null && (_objectSprites.Any(o => o.Object == killedSource) || _creatureNodes.Keys.Any(c => c.Source == killedSource)))
            {
                problems.Add($"the killed {killed!.Name} (#{killed.Index}) is drawn again");
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
            GD.Print($"{files[index]}: {doors.Count} doors, {(gone is null ? 0 : 1)} gone, {(killed is null ? 0 : 1)} killed, {(movedTo is null ? 0 : 1)} moved, {clones} cloned: "
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
    /// <summary>Rests taken by using a fire pit (counted for the verbs check).</summary>
    private int _restsTaken;

    private void CheckVerbs()
    {
        IReadOnlyList<string> files = Session.Install!.RegionFiles;
        var offered = Enum.GetValues<Verb>().ToDictionary(v => v, _ => 0);
        var menuProblems = new List<string>();
        int objects = 0;
        (_scriptRuns, _scriptErrors) = (0, 0);
        for (int index = 0; index < files.Count; index++)
        {
            ShowRegion(index);
            AnswerForThePlayer();
            int number = _region.Number;
            var targets = _world.Objects.Where(o => o.Kind != ObjectKind.Party && !o.Hidden).ToList();
            // The right-click menu: the original's window for the kind of object, its verbs on its buttons.
            foreach (WorldObject obj in targets.Where(o => VerbsOf(o).Count > 1).DistinctBy(o => o.Kind).ToList())
            {
                _menuTarget = obj;
                _verbMenu.Open(VerbTargetOf(obj), VerbsOf(obj), _palette, Vector2.Zero);
                string window = obj.Kind == ObjectKind.Creature ? "WIND 3020" : "WIND 15500";
                if (!_verbMenu.Visible || _verbMenu.GetNodeOrNull(window) is null)
                {
                    menuProblems.Add($"region {number}: no {window} for {Called(obj)}");
                }
                _verbMenu.Pick(VerbsOf(obj).Contains(Verb.Open) ? Verb.Open : Verb.Look); // the fourth button
                if (_character.IsOpen)
                {
                    _character.Close();
                }
                _containerWindow.Close();
                AnswerForThePlayer();
                if (_verbMenu.Visible)
                {
                    menuProblems.Add($"region {number}: choosing the fourth button didn't close the menu");
                }
            }
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
            + string.Join(", ", offered.Select(o => $"{o.Key} {o.Value}")) + $"; {_scriptRuns} script runs, {_scriptErrors} stopped with an error; "
            + $"{_restsTaken} rests at fire pits");
        foreach (string p in menuProblems.Take(10))
        {
            GD.Print($"  PROBLEM: {p}");
        }
        GetTree().Quit(menuProblems.Count == 0 ? 0 : 1);
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
                        _running += 1.0 / ScmdAnimator.TicksPerSecond;
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
                    _running += 1.0 / ScmdAnimator.TicksPerSecond;
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
        _message.Close();
        for (int i = 0; i < 50 && Session.Scripts.IsSuspended; i++)
        {
            int answer = Session.ScriptHost.Dialogue.Prompt switch
            {
                GplMenuPrompt menu => menu.Options.ToList().FindIndex(o => o.Enabled),
                GplYesNoPrompt => yes ? 1 : 0,
                _ => 0,
            };
            _answered++;
            ReportScript("auto answer", Session.ScriptHost.Dialogue.Prompt is GplTextPrompt
                ? Session.Scripts.ResumeWith("")
                : Session.Scripts.Resume(answer));
        }
        Session.ScriptHost.Dialogue.Clear();
    }

    /// <summary>
    /// For every region: load it, place the party, send everyone to a cell 20
    /// columns away for 15 simulated seconds, and check after every tick that
    /// no two characters' footprints overlap.
    /// </summary>
    /// <summary>For the checks: the things on the map holding items (containers, bodies), and what each holds.</summary>
    private string HoldingThings() =>
        string.Join("; ", _world.Objects.Where(o => o.Kind == ObjectKind.Thing && o.Items.Count > 0).Select(o =>
            $"{Describe(o)}: {string.Join(", ", o.Items.Select(i => _world.Items.NameOf(i.Record) is { Length: > 0 } n ? n : $"RDFF {i.Rdff}"))}"));

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
            if (HoldingThings() is { Length: > 0 } holding)
            {
                GD.Print($"    holding things: {holding}");
            }
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

    /// <summary>Its animation runs on the view's running time, not the map's clock (a change of state, not held by the pointer).</summary>
    public bool OwnClock { get; set; }
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
