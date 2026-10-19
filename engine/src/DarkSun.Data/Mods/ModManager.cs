namespace DarkSun.Data.Mods;

/// <summary>Changes to a mods folder, for the in-game mod list: turning mods on and off, and their order.</summary>
public static class ModManager
{
    /// <summary>
    /// Turn <paramref name="mod"/> on or off by renaming its folder (a leading
    /// <c>_</c> turns it off), and return it as it now is. Throws
    /// <see cref="IOException"/> if a folder with the new name exists.
    /// </summary>
    public static Mod SetEnabled(Mod mod, bool enabled)
    {
        if (mod.Enabled == enabled)
        {
            return mod;
        }
        string target = Path.Combine(mod.Root, enabled ? mod.Name : "_" + mod.Name);
        if (Directory.Exists(target))
        {
            throw new IOException($"can't rename {Path.GetFileName(mod.Directory)}: {Path.GetFileName(target)} already exists");
        }
        Directory.Move(mod.Directory, target);
        return mod with { Directory = target, Enabled = enabled };
    }

    /// <summary>Write <paramref name="root"/>'s <see cref="ModSet.LoadOrderFile"/>: mod names, highest priority first.</summary>
    public static void SaveLoadOrder(string root, IEnumerable<string> names) =>
        File.WriteAllLines(Path.Combine(root, ModSet.LoadOrderFile),
            ["# Mods, highest priority first. Written by the game's mod list; edit freely.", .. names]);
}
