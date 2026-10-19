using Godot;

namespace DarkSun.Engine;

/// <summary>
/// Input actions and their default keys. Defined in code rather than in
/// project.godot so that a remapping screen can change them at run time.
/// </summary>
public static class Controls
{
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
    public const string ToggleScriptDebugger = "debug_toggle_script_debugger";
    public const string DebugContinue = "debug_continue";
    public const string DebugStepOver = "debug_step_over";
    public const string DebugStepInto = "debug_step_into";
    public const string DebugStepOut = "debug_step_out";
    public const string Back = "ui_cancel";

    /// <summary>Every action above (the key help lists them all).</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        PanLeft, PanRight, PanUp, PanDown, ZoomIn, ZoomOut, NextRegion, PreviousRegion, ToggleMapFlags, NextCharacter,
        AddCharacter, ChangeSprite, ToggleScriptLog, ToggleGrid, QuickSave, QuickLoad, ToggleObjectNumbers,
        ToggleScriptDebugger, DebugContinue, DebugStepOver, DebugStepInto, DebugStepOut, Back,
    ];

    public static void Register()
    {
        Add(PanLeft, Key.Left, Key.A);
        Add(PanRight, Key.Right, Key.D);
        Add(PanUp, Key.Up, Key.W);
        Add(PanDown, Key.Down, Key.S);
        Add(ZoomIn, Key.Equal, Key.KpAdd);
        Add(ZoomOut, Key.Minus, Key.KpSubtract);
        Add(NextRegion, Key.Pagedown);
        Add(PreviousRegion, Key.Pageup);
        Add(ToggleMapFlags, Key.F1);
        Add(NextCharacter, Key.Tab);
        Add(AddCharacter, Key.N);
        Add(ChangeSprite, Key.C);
        Add(ToggleScriptLog, Key.F2);
        Add(ToggleGrid, Key.F3);
        Add(QuickSave, Key.F5);
        Add(QuickLoad, Key.F9);
        Add(ToggleObjectNumbers, Key.F4);
        Add(ToggleScriptDebugger, Key.F6);
        Add(DebugContinue, Key.F8);
        Add(DebugStepOver, Key.F10);
        Add(DebugStepInto, Key.F11);
        Add(DebugStepOut, new InputEventKey { PhysicalKeycode = Key.F11, ShiftPressed = true });
    }

    private static void Add(string action, InputEventKey key)
    {
        if (!InputMap.HasAction(action))
        {
            InputMap.AddAction(action);
            InputMap.ActionAddEvent(action, key);
        }
    }

    private static void Add(string action, params Key[] keys)
    {
        if (InputMap.HasAction(action))
        {
            return;
        }
        InputMap.AddAction(action);
        foreach (Key key in keys)
        {
            InputMap.ActionAddEvent(action, new InputEventKey { PhysicalKeycode = key });
        }
    }
}
