using Godot;

namespace DarkSun.Engine;

/// <summary>
/// Input actions and their default keys. Defined in code rather than in
/// project.godot so that a remapping screen can change them at run time.
/// </summary>
public static class Controls
{
    public const string MoveLeft = "party_move_left";
    public const string MoveRight = "party_move_right";
    public const string MoveUp = "party_move_up";
    public const string MoveDown = "party_move_down";
    public const string PanLeft = "map_pan_left";
    public const string PanRight = "map_pan_right";
    public const string PanUp = "map_pan_up";
    public const string PanDown = "map_pan_down";
    public const string ZoomIn = "map_zoom_in";
    public const string ZoomOut = "map_zoom_out";
    public const string NextRegion = "debug_next_region";
    public const string PreviousRegion = "debug_previous_region";
    public const string ToggleMapFlags = "debug_toggle_map_flags";
    public const string NextCharacter = "party_select_next";
    public const string AddCharacter = "debug_add_character";
    public const string ChangeSprite = "debug_change_sprite";
    public const string ToggleScriptLog = "debug_toggle_script_log";
    public const string ToggleGrid = "debug_toggle_grid";
    public const string QuickSave = "quick_save";
    public const string QuickLoad = "quick_load";
    public const string ToggleObjectNumbers = "debug_toggle_object_numbers";
    public const string ToggleInventory = "inventory";
    public const string ToggleCharacter = "character";
    public const string ToggleScriptDebugger = "debug_toggle_script_debugger";
    public const string DebugContinue = "debug_continue";
    public const string DebugStepOver = "debug_step_over";
    public const string DebugStepInto = "debug_step_into";
    public const string DebugStepOut = "debug_step_out";
    public const string ToggleJournal = "journal";
    public const string ToggleOptions = "options";
    public const string CombatEndTurn = "combat_end_turn";
    public const string CombatGuard = "combat_guard";
    public const string CombatWait = "combat_wait";
    public const string CombatCast = "combat_cast";
    public const string CombatAuto = "combat_auto";
    public const string CombatChooseEnemy = "combat_choose_enemy";
    public const string CombatAttackChosen = "combat_attack_chosen";
    public const string ToggleCombatLog = "combat_log";
    public const string Back = "ui_cancel";

    /// <summary>Every action above (the key help lists them all).</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        MoveLeft, MoveRight, MoveUp, MoveDown,
        PanLeft, PanRight, PanUp, PanDown, ZoomIn, ZoomOut, NextRegion, PreviousRegion, ToggleMapFlags, NextCharacter,
        AddCharacter, ChangeSprite, ToggleScriptLog, ToggleGrid, QuickSave, QuickLoad, ToggleObjectNumbers,
        ToggleInventory, ToggleCharacter, ToggleJournal, ToggleOptions, ToggleScriptDebugger, DebugContinue, DebugStepOver, DebugStepInto,
        DebugStepOut, CombatEndTurn, CombatGuard, CombatWait, CombatCast, CombatAuto, CombatChooseEnemy, CombatAttackChosen, ToggleCombatLog, Back,
    ];

    /// <summary>The default keys of the actions this game adds (<c>ui_cancel</c>, Esc, is Godot's own).</summary>
    private static readonly Dictionary<string, InputEventKey[]> Defaults = new()
    {
        [MoveLeft] = Keys(Key.Left),
        [MoveRight] = Keys(Key.Right),
        [MoveUp] = Keys(Key.Up),
        [MoveDown] = Keys(Key.Down),
        [PanLeft] = Keys(Key.A),
        [PanRight] = Keys(Key.D),
        [PanUp] = Keys(Key.W),
        [PanDown] = Keys(Key.S),
        [ZoomIn] = Keys(Key.Equal, Key.KpAdd),
        [ZoomOut] = Keys(Key.Minus, Key.KpSubtract),
        [NextRegion] = Keys(Key.Pagedown),
        [PreviousRegion] = Keys(Key.Pageup),
        [ToggleMapFlags] = Keys(Key.F1),
        [NextCharacter] = Keys(Key.Tab),
        [AddCharacter] = Keys(Key.N),
        [ChangeSprite] = [new InputEventKey { PhysicalKeycode = Key.C, ShiftPressed = true }],
        [ToggleScriptLog] = Keys(Key.F2),
        [ToggleGrid] = Keys(Key.F3),
        [QuickSave] = Keys(Key.F5),
        [QuickLoad] = Keys(Key.F9),
        [ToggleObjectNumbers] = Keys(Key.F4),
        [ToggleInventory] = Keys(Key.I),
        [ToggleCharacter] = Keys(Key.C),
        [ToggleJournal] = Keys(Key.J),
        [ToggleOptions] = Keys(Key.O),
        [ToggleScriptDebugger] = Keys(Key.F6),
        [DebugContinue] = Keys(Key.F8),
        [DebugStepOver] = Keys(Key.F10),
        [DebugStepInto] = Keys(Key.F11),
        [DebugStepOut] = [new InputEventKey { PhysicalKeycode = Key.F11, ShiftPressed = true }],
        [CombatEndTurn] = Keys(Key.E),
        [CombatGuard] = Keys(Key.G),
        [CombatWait] = Keys(Key.T),
        [CombatCast] = Keys(Key.Q),
        [CombatAuto] = Keys(Key.U),
        [CombatChooseEnemy] = Keys(Key.Tab),
        [CombatAttackChosen] = Keys(Key.Enter, Key.KpEnter),
        [ToggleCombatLog] = Keys(Key.L),
    };

    private static InputEventKey[] Keys(params Key[] keys) => [.. keys.Select(k => new InputEventKey { PhysicalKeycode = k })];

    /// <summary>The actions the player can rebind (all but Esc, which Godot and every screen use to go back).</summary>
    public static IEnumerable<string> Rebindable => Defaults.Keys;

    /// <summary>Add the actions with their default keys (once), then the player's own (<see cref="Settings"/>).</summary>
    public static void Register()
    {
        foreach (var (action, keys) in Defaults)
        {
            if (!InputMap.HasAction(action))
            {
                InputMap.AddAction(action);
                foreach (InputEventKey key in keys)
                {
                    InputMap.ActionAddEvent(action, (InputEventKey)key.Duplicate());
                }
            }
        }
        ApplySavedKeys();
    }

    /// <summary>Bind each action to the keys saved for it (<c>[keys]</c>), or its defaults.</summary>
    public static void ApplySavedKeys()
    {
        foreach (var (action, defaults) in Defaults)
        {
            if (!InputMap.HasAction(action))
            {
                continue;
            }
            IEnumerable<InputEventKey> keys = Settings.KeysFor(action) is { } saved
                ? saved.Split(" / ", StringSplitOptions.RemoveEmptyEntries).Select(Parse).OfType<InputEventKey>()
                : defaults;
            InputMap.ActionEraseEvents(action);
            foreach (InputEventKey key in keys)
            {
                InputMap.ActionAddEvent(action, (InputEventKey)key.Duplicate());
            }
        }
    }

    /// <summary>Make <paramref name="key"/> <paramref name="action"/>'s only key, and keep it in the settings.</summary>
    public static void Rebind(string action, InputEventKey key)
    {
        var bound = new InputEventKey
        {
            PhysicalKeycode = key.PhysicalKeycode != Key.None ? key.PhysicalKeycode : key.Keycode,
            ShiftPressed = key.ShiftPressed,
            CtrlPressed = key.CtrlPressed,
            AltPressed = key.AltPressed,
        };
        Settings.SetKeys(action, Name(bound));
        ApplySavedKeys();
    }

    /// <summary>Back to the default keys of every action.</summary>
    public static void ResetKeys()
    {
        foreach (string action in Defaults.Keys)
        {
            Settings.SetKeys(action, null);
        }
        ApplySavedKeys();
    }

    /// <summary>A key as the settings keep it: modifiers, then the key's name on a US keyboard (it is bound by position).</summary>
    public static string Name(InputEventKey k) =>
        (k.CtrlPressed ? "Ctrl+" : "") + (k.AltPressed ? "Alt+" : "") + (k.ShiftPressed ? "Shift+" : "")
        + OS.GetKeycodeString(k.PhysicalKeycode != Key.None ? k.PhysicalKeycode : k.Keycode);

    /// <summary>The key a <see cref="Name"/> names, or null.</summary>
    public static InputEventKey? Parse(string text)
    {
        var key = new InputEventKey();
        string[] parts = text.Trim().Split('+');
        // "Ctrl++" style names don't occur: the last part is the key, the others modifiers.
        foreach (string mod in parts[..^1])
        {
            switch (mod.Trim().ToLowerInvariant())
            {
                case "ctrl": key.CtrlPressed = true; break;
                case "alt": key.AltPressed = true; break;
                case "shift": key.ShiftPressed = true; break;
                default: return null;
            }
        }
        Key code = OS.FindKeycodeFromString(parts[^1].Trim());
        if (code == Key.None)
        {
            return null;
        }
        key.PhysicalKeycode = code;
        return key;
    }
}
