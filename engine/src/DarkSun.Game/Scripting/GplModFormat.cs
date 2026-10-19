using DarkSun.Data.Mods;

namespace DarkSun.Game.Scripting;

/// <summary>The mod file formats the game adds to <see cref="ModSet"/>: <c>.gpl</c>, script source for <c>GPL</c> and <c>MAS</c> resources.</summary>
public static class GplModFormat
{
    public const string Extension = ".gpl";

    public static IReadOnlyDictionary<string, ModBuilder> Builders { get; } = new Dictionary<string, ModBuilder>
    {
        [Extension] = (tag, path, _) => tag.Trim().ToUpperInvariant() is "GPL" or "MAS"
            ? GplAssembler.Assemble(File.ReadAllText(path))
            : throw new InvalidDataException(".gpl files are scripts: put them in a GPL or MAS folder"),
    };
}
