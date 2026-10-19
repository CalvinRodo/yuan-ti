using DarkSun.Data.Gff;
using DarkSun.Data.Images;
using DarkSun.Data.Ui;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// The title menu, built as DSUN.EXE builds it (<c>8015:0208</c>): a black
/// screen, <c>BMP</c> 20028 at (47, 25) and 20029 at (4, 45) in palette 1001,
/// and window 3000's buttons (Start Game, Create Characters, Load Saved
/// Game, Exit to DOS), each an <c>ICON</c> whose frames are its normal,
/// highlighted, disabled and pressed looks. The window's accelerator table
/// gives the keys S, C, L and E; Load Saved Game opens <see cref="SavesView"/>. Arrow keys and Enter work too. A small
/// Mods button (M), not in the original, opens the mod list (<see cref="ModsView"/>).
/// Run with <c>-- --check-menu</c> to print the menu and quit.
/// </summary>
public partial class MainMenu : RetroScreen
{
    public const string ScenePath = "res://scenes/main_menu.tscn";

    private const int WindowNumber = 3000;
    private const int PaletteNumber = 1001;

    /// <summary>The menu's music (<c>8015:0208</c> calls <c>1A0A:0672(0x11)</c>).</summary>
    private const int MusicNumber = 17;
    private static readonly (int Bmp, int X, int Y)[] Backdrop = [(20028, 47, 25), (20029, 4, 45)];

    /// <summary>What the menu's buttons do, in window order.</summary>
    private enum Command { StartGame, CreateCharacters, LoadGame, Exit }

    private readonly List<TextureButton> _buttons = [];
    private readonly Dictionary<char, int> _keys = [];
    private Label _status = null!;

    public override void _Ready()
    {
        base._Ready();
        if (Session.Install is null)
        {
            GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, "res://scenes/main.tscn");
            return;
        }
        Music.Instance?.ChangeTo("RESOURCE.GFF", MusicNumber);
        GffFile resources = Session.Open("RESOURCE.GFF");
        Palette palette = Session.Assets.Palette(resources.Read("PAL ", PaletteNumber));

        foreach (var (bmp, x, y) in Backdrop)
        {
            if (Session.Assets.Frame(resources, "BMP ", bmp) is { } frame)
            {
                Canvas.AddChild(new TextureRect
                {
                    Texture = Textures.ToTexture(frame, palette),
                    Position = new Vector2(x, y),
                    MouseFilter = MouseFilterEnum.Ignore,
                });
            }
        }

        UiWindow window = UiWindow.Parse(resources.Read("WIND", WindowNumber).Span);
        foreach (WindowItem item in window.Items)
        {
            if (item.Tag == "BUTN" && AddButton(resources, palette, item) is { } button)
            {
                int index = _buttons.Count;
                button.Pressed += () => Run((Command)index);
                _buttons.Add(button);
            }
            else if (item.Tag == "ACCL" && resources.TryRead("ACCL", item.Number, out var accl))
            {
                // Commands come in pairs (upper and lower case) for each button, in order.
                foreach (Accelerator key in AcceleratorTable.Parse(accl.Span))
                {
                    _keys[char.ToUpperInvariant(key.Key)] = (key.Command - item.Number) / 2;
                }
            }
        }
        for (int i = 0; i < _buttons.Count; i++)
        {
            _buttons[i].FocusNeighborTop = _buttons[(i + _buttons.Count - 1) % _buttons.Count].GetPath();
            _buttons[i].FocusNeighborBottom = _buttons[(i + 1) % _buttons.Count].GetPath();
        }

        _status = new Label
        {
            Position = new Vector2(0, 168),
            Size = new Vector2(Width, 24),
            HorizontalAlignment = HorizontalAlignment.Center,
            LabelSettings = new LabelSettings { FontSize = 8, FontColor = new Color(0.9f, 0.75f, 0.5f) },
        };
        Canvas.AddChild(_status);

        var mods = new LinkButton
        {
            Text = Session.Mods.Mods.Count > 0 ? $"Mods ({Session.Mods.Mods.Count})" : "Mods",
            Underline = LinkButton.UnderlineMode.OnHover,
            FocusMode = FocusModeEnum.None,
        };
        mods.AddThemeFontSizeOverride("font_size", 8);
        mods.AddThemeColorOverride("font_color", new Color(0.75f, 0.6f, 0.4f));
        mods.Pressed += OpenMods;
        Canvas.AddChild(mods);
        Vector2 size = mods.GetCombinedMinimumSize();
        mods.Position = new Vector2(Width - size.X - 4, Height - size.Y - 2);

        if (OS.GetCmdlineUserArgs().Contains("--check-menu"))
        {
            GD.Print($"main menu: window {window.Number}, {window.Items.Count} items; buttons "
                + string.Join(", ", _buttons.Select(b => $"{b.Name} at ({b.Position.X},{b.Position.Y}) {b.Size.X}x{b.Size.Y}"))
                + "; keys " + string.Join(" ", _keys.Select(k => $"{k.Key}={(Command)k.Value}")));
            GetTree().Quit(_buttons.Count == 4 ? 0 : 1);
            return;
        }
        if (_buttons.Count > 0)
        {
            _buttons[0].CallDeferred(Control.MethodName.GrabFocus);
        }
    }

    /// <summary>A window's button: its <c>BUTN</c>, drawn with the frames of its <c>ICON</c>.</summary>
    private TextureButton? AddButton(GffFile resources, Palette palette, WindowItem item)
    {
        if (!resources.TryRead("BUTN", item.Number, out var butn))
        {
            return null;
        }
        UiButton def = UiButton.Parse(butn.Span);
        if (!resources.TryRead("ICON", def.Icon, out var icon))
        {
            return null;
        }
        IReadOnlyList<ImageFrame> frames = Session.Assets.Frames(icon);
        Texture2D? Frame(int n) => n < frames.Count && frames[n].Width > 1 ? Textures.ToTexture(frames[n], palette) : null;
        var button = new TextureButton
        {
            Name = $"BUTN{item.Number}",
            Position = new Vector2(item.X, item.Y),
            TextureNormal = Frame(UiButton.NormalFrame),
            TextureHover = Frame(UiButton.HighlightFrame),
            TextureFocused = Frame(UiButton.HighlightFrame),
            TexturePressed = Frame(UiButton.PressedFrame),
            TextureDisabled = Frame(UiButton.DisabledFrame),
            FocusMode = FocusModeEnum.All,
        };
        Canvas.AddChild(button);
        return button;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.M })
        {
            GetViewport().SetInputAsHandled();
            OpenMods();
            return;
        }
        if (e is InputEventKey { Pressed: true, Echo: false } key && key.Unicode != 0
            && _keys.TryGetValue(char.ToUpperInvariant((char)key.Unicode), out int index) && index < _buttons.Count)
        {
            _buttons[index].GrabFocus();
            Run((Command)index);
            GetViewport().SetInputAsHandled();
        }
    }

    private void OpenMods() => GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, ModsView.ScenePath);

    private void Run(Command command)
    {
        switch (command)
        {
            case Command.StartGame:
                // A new game: the party arrives in region 42 after cinematic 1 (1587:1F83).
                Session.NewGame();
                RegionView.StartNewGame();
                IntroView.Cinematic = 1;
                IntroView.NextScene = RegionView.ScenePath;
                GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, IntroView.ScenePath);
                break;
            case Command.CreateCharacters:
                _status.Text = "Character creation is not ported yet.";
                break;
            case Command.LoadGame:
                GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, SavesView.ScenePath);
                break;
            case Command.Exit:
                GetTree().Quit();
                break;
        }
    }
}
