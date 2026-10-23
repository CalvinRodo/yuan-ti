using DarkSun.Game.Movement;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// The player's options, in <c>user://settings.cfg</c> (an INI file, also
/// editable by hand), read once and applied where they matter. The Options
/// window (<see cref="OptionsWindow"/>) changes them; <see cref="Save"/> writes
/// them back, keeping the file's other keys (the game folder, the SoundFont).
/// <list type="bullet">
/// <item><c>[audio]</c> <c>music_volume</c>, <c>sound_volume</c> (0–1), <c>music_on</c>, <c>sound_on</c>;</item>
/// <item><c>[display]</c> <c>fullscreen</c>, <c>debug_hud</c> (the region view's information lines), <c>shadows</c>,
/// <c>dust</c>, <c>enemy_rings</c> (0 none, 1 under the enemy chosen with Tab, 2 under every enemy);</item>
/// <item><c>[game]</c> <c>skip_intro</c>, <c>autosave</c> (on entering a region), <c>original_pathfinding</c>,
/// <c>difficulty</c> (0–3: easy, balanced, hard, hideous), <c>animations</c> (the map's animated things),
/// <c>choose_enemy_keys</c> (Tab and Enter choose and attack an enemy in a fight);</item>
/// <item><c>[mods]</c> <c>bundled_on</c>: the mods that come with the game turned on (the tabletop rules).</item>
/// <item><c>[keys]</c> an action's keys, as the key help names them (<c>quick_save = "F5"</c>, <c>"Shift+F11"</c>, several with " / ").</item>
/// </list>
/// </summary>
public static class Settings
{
    public const string Path = "user://settings.cfg";

    private static ConfigFile? _file;

    private static ConfigFile File
    {
        get
        {
            if (_file is null)
            {
                _file = new ConfigFile();
                _file.Load(Path); // missing: all defaults
            }
            return _file;
        }
    }

    private static T Get<[MustBeVariant] T>(string section, string key, T fallback) where T : notnull =>
        File.HasSectionKey(section, key) ? File.GetValue(section, key).As<T>() : fallback;

    private static void Set(string section, string key, Variant value) => File.SetValue(section, key, value);

    public static float MusicVolume
    {
        get => Math.Clamp(Get("audio", "music_volume", 1f), 0f, 1f);
        set => Set("audio", "music_volume", Math.Clamp(value, 0f, 1f));
    }

    public static float SoundVolume
    {
        get => Math.Clamp(Get("audio", "sound_volume", 1f), 0f, 1f);
        set => Set("audio", "sound_volume", Math.Clamp(value, 0f, 1f));
    }

    public static bool MusicOn
    {
        get => Get("audio", "music_on", true);
        set => Set("audio", "music_on", value);
    }

    public static bool SoundOn
    {
        get => Get("audio", "sound_on", true);
        set => Set("audio", "sound_on", value);
    }

    /// <summary>GAME DIFFICULTY: 0 easy, 1 balanced, 2 hard, 3 hideous (creatures' hit points 50–200 %).</summary>
    public static int Difficulty
    {
        get => Math.Clamp(Get("game", "difficulty", 1), 0, 3);
        set => Set("game", "difficulty", Math.Clamp(value, 0, 3));
    }

    /// <summary>ANIMATIONS ON/OFF: the map's animated things move.</summary>
    public static bool Animations
    {
        get => Get("game", "animations", true);
        set => Set("game", "animations", value);
    }

    public static bool Fullscreen
    {
        get => Get("display", "fullscreen", false);
        set => Set("display", "fullscreen", value);
    }

    public static bool DebugHud
    {
        get => Get("display", "debug_hud", true);
        set => Set("display", "debug_hud", value);
    }

    /// <summary>Living figures on the map cast shadows (<see cref="FloorEffects"/>; after the Obsidian Edition's).</summary>
    public static bool Shadows
    {
        get => Get("display", "shadows", true);
        set => Set("display", "shadows", value);
    }

    /// <summary>Walking on sand or dirt raises dust (<see cref="FloorEffects"/>; after the Obsidian Edition's).</summary>
    public static bool Dust
    {
        get => Get("display", "dust", true);
        set => Set("display", "dust", value);
    }

    /// <summary>Red rings in a fight: 0 none, 1 under the enemy chosen with Tab, 2 under every enemy (the chosen one's thicker).</summary>
    public static int EnemyRings
    {
        get => Math.Clamp(Get("display", "enemy_rings", 1), 0, 2);
        set => Set("display", "enemy_rings", Math.Clamp(value, 0, 2));
    }

    /// <summary>In a fight, Tab chooses an enemy (Shift+Tab goes back) and Enter attacks it (after the Obsidian Edition's).</summary>
    public static bool ChooseEnemyKeys
    {
        get => Get("game", "choose_enemy_keys", true);
        set => Set("game", "choose_enemy_keys", value);
    }

    public static bool SkipIntro
    {
        get => Get("game", "skip_intro", false);
        set => Set("game", "skip_intro", value);
    }

    public static bool Autosave
    {
        get => Get("game", "autosave", true);
        set => Set("game", "autosave", value);
    }

    /// <summary>Repair the shipped scripts' known bugs as they load (<c>GplRepairs</c>: dead triggers in Darkhold, the Lava Rifts ranger, Linara).</summary>
    public static bool ScriptRepairs
    {
        get => Get("game", "script_repairs", true);
        set => Set("game", "script_repairs", value);
    }

    /// <summary>Is the bundled mod <paramref name="name"/> on (<c>[mods] bundled_on</c>, names separated by commas)?</summary>
    public static bool BundledModOn(string name) =>
        Get("mods", "bundled_on", "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Turn the bundled mod <paramref name="name"/> on or off (saved with <see cref="Save"/>).</summary>
    public static void SetBundledMod(string name, bool on)
    {
        var names = Get("mods", "bundled_on", "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(n => !n.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (on)
        {
            names.Add(name);
        }
        Set("mods", "bundled_on", string.Join(",", names));
    }

    public static bool OriginalPathfinding
    {
        get => Get("game", "original_pathfinding", false);
        set => Set("game", "original_pathfinding", value);
    }

    /// <summary>The keys saved for <paramref name="action"/> ("F5", "Shift+F11", "↑ / W"), or null for its defaults.</summary>
    public static string? KeysFor(string action) =>
        File.HasSectionKey("keys", action) ? File.GetValue("keys", action).AsString() : null;

    public static void SetKeys(string action, string? keys)
    {
        if (keys is null)
        {
            if (File.HasSectionKey("keys", action))
            {
                File.EraseSectionKey("keys", action);
            }
        }
        else
        {
            Set("keys", action, keys);
        }
    }

    /// <summary>Write the options; false if the file can't be written.</summary>
    public static bool Save() => File.Save(Path) == Error.Ok;

    /// <summary>Put the options into effect (at start-up, and after the Options window changes them).</summary>
    public static void Apply()
    {
        if (Music.Instance is { } music)
        {
            music.Volume = MusicOn ? MusicVolume : 0;
        }
        if (Sounds.Instance is { } sounds)
        {
            sounds.Volume = SoundOn ? SoundVolume : 0;
        }
        Walker.UseOriginalPathfinding = OriginalPathfinding;
        if (DisplayServer.GetName() != "headless")
        {
            DisplayServer.WindowMode wanted = Fullscreen ? DisplayServer.WindowMode.Fullscreen : DisplayServer.WindowMode.Windowed;
            if (DisplayServer.WindowGetMode() != wanted && !(wanted == DisplayServer.WindowMode.Windowed && DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Maximized))
            {
                DisplayServer.WindowSetMode(wanted);
            }
        }
        Controls.ApplySavedKeys();
    }

    /// <summary>Forget the loaded file (tests, or after editing it by hand).</summary>
    public static void Reload() => _file = null;
}
