namespace DarkSun.Game.Scripting;

/// <summary>A REQUEST code (<c>87B3:0000</c>) and its name.</summary>
public sealed record GplRequest(int Code, string Name, string Summary);

/// <summary>
/// The names of the REQUEST codes. Scripts still store the number; the host
/// dispatches by name (<c>WorldGplHost.Request</c>), the script log and the
/// debugger show it, and the assembler takes it for the number
/// (<c>REQUEST(SET_DOOR, -98, 0, 0)</c>). This table is the only place the
/// numbers are written, so a mod could later add or remap codes here.
/// See <c>dsun-exe-reference.md</c> §4 for what each does.
/// </summary>
public static class GplRequests
{
    public static readonly IReadOnlyDictionary<int, GplRequest> All = new GplRequest[]
    {
        new(1, "REST_RAISE", "the party rests; fallen members get up too"),
        new(2, "SET_DOOR", "open (a = 0) or close (a = 1) the matching doors"),
        new(3, "SKILL_CHECK", "a thief skill check: skill a, situation b"),
        new(4, "REST", "the party rests"),
        new(5, "TOGGLE_STATE", "toggle the object's state and play its animation"),
        new(6, "CINEMATIC", "play cinematic a; the script goes on after it"),
        new(7, "TRAP", "a trap: item −who at (a, b), avoided or set off"),
        new(8, "RANDOM_MONSTER", "the region's random monster (a = 0), or how many of it (a ≠ 0)"),
        new(9, "TRANSFORM", "replace the object with OJFF a (b ≠ 0: at its start)"),
        new(10, "BLOCK_CELL", "cell (a, b) blocks movement"),
        new(11, "UNBLOCK_CELL", "cell (a, b) no longer blocks movement"),
        new(12, "BLOCK_SIGHT", "cell (a, b) blocks sight"),
        new(13, "UNBLOCK_SIGHT", "cell (a, b) no longer blocks sight"),
        new(14, "ARENA_BOUT", "the arena bout with Celgor at (a, b); the script goes on after it"),
        new(15, "MOVE_CURSOR", "back to the move pointer"),
        new(16, "MUSIC_LEVEL", "the music's intensity level a"),
        new(17, "PLACE_OBJECT", "place OJFF −who at cell (a, b)"),
        new(18, "SET_ALLEGIANCE", "the matching creatures' allegiance = a"),
        new(19, "GAME_OVER", "the game is over"),
        new(20, "COUNT_CREATURES", "how many creatures have an allegiance bit of a (all with 0)"),
    }.ToDictionary(r => r.Code);

    private static readonly Dictionary<string, GplRequest> ByName = All.Values.ToDictionary(r => r.Name);

    /// <summary>The name of <paramref name="code"/>, or null if it has none.</summary>
    public static string? Name(int code) => All.TryGetValue(code, out GplRequest? r) ? r.Name : null;

    public static bool TryCode(string name, out int code)
    {
        code = ByName.TryGetValue(name, out GplRequest? r) ? r.Code : 0;
        return r is not null;
    }
}
