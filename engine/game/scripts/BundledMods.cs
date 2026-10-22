using Godot;

namespace DarkSun.Engine;

/// <summary>
/// The mods that come with the game (<c>res://mods</c>, such as the tabletop
/// rules): packed with the game, copied to <c>user://bundled-mods</c> each time it
/// starts so the mod loader reads them as files like any other mod, and turned
/// on in the mod list (<see cref="Settings.BundledModOn"/>). Edits belong in a copy
/// in the mods folder: the copies here are replaced on the next start.
/// </summary>
public static class BundledMods
{
    private const string Source = "res://mods";
    private const string Target = "user://bundled-mods";

    /// <summary>Copy the bundled mods out (afresh); the folder to load them from, or null if the game has none.</summary>
    public static string? Extract()
    {
        string target = ProjectSettings.GlobalizePath(Target);
        if (!DirAccess.DirExistsAbsolute(Source))
        {
            return null;
        }
        try
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
            Copy(Source, target);
        }
        catch (IOException e)
        {
            GD.PushWarning($"Bundled mods not copied to {target}: {e.Message}");
        }
        return target;
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in DirAccess.GetFilesAt(from))
        {
            // An exported game lists imported resources by their .remap/.import names; mods hold only plain files.
            if (file.EndsWith(".import", StringComparison.Ordinal) || file.EndsWith(".remap", StringComparison.Ordinal) || file.EndsWith(".uid", StringComparison.Ordinal))
            {
                continue;
            }
            byte[] bytes = Godot.FileAccess.GetFileAsBytes($"{from}/{file}");
            File.WriteAllBytes(Path.Combine(to, file), bytes);
        }
        foreach (string dir in DirAccess.GetDirectoriesAt(from))
        {
            Copy($"{from}/{dir}", Path.Combine(to, dir));
        }
    }
}
