using DarkSun.Game.Movement;

namespace DarkSun.Game.World;

/// <summary>An object as a region left it (<see cref="GameWorld.Snapshot"/>); <paramref name="Etab"/> is its region object, null for one made at run time from nothing.</summary>
public sealed record ObjectState(
    int Index, int? Etab, int Id, string Name, bool Hidden, Cell Cell, int Allegiance, int Status,
    int DoorFlags, bool SecondState, IReadOnlyDictionary<int, int> Fields);

/// <summary>A region as the party left it: its <c>GMAP</c> flags (without the run-time occupied bit) and objects.</summary>
public sealed record RegionState(int Region, byte[] Flags, IReadOnlyList<ObjectState> Objects);

/// <summary>
/// The regions the party has left, as it left them, for one game. The view
/// remembers a region on leaving it and restores it on coming back, before
/// the region's script runs.
/// </summary>
public sealed class RegionMemory
{
    private readonly Dictionary<int, RegionState> _regions = [];

    public int Count => _regions.Count;

    public void Remember(GameWorld world) => _regions[world.Region.Number] = world.Snapshot();

    /// <summary>Restore <paramref name="world"/> if its region was visited; the objects that changed, or null if it wasn't.</summary>
    public IReadOnlyList<WorldObject>? Recall(GameWorld world) =>
        _regions.TryGetValue(world.Region.Number, out RegionState? state) ? world.Restore(state) : null;

    public bool Knows(int region) => _regions.ContainsKey(region);

    public void Forget() => _regions.Clear();

    /// <summary>Every region remembered (for a saved game).</summary>
    public IReadOnlyCollection<RegionState> States => _regions.Values;

    /// <summary>Remember <paramref name="states"/> instead of what was remembered (a saved game's).</summary>
    public void Load(IEnumerable<RegionState> states)
    {
        _regions.Clear();
        foreach (RegionState state in states)
        {
            _regions[state.Region] = state;
        }
    }
}
