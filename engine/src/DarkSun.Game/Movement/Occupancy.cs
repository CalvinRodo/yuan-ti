namespace DarkSun.Game.Movement;

/// <summary>
/// Which creature stands on which cell, so creatures don't overlap. This plays
/// the part of the <c>GMAP</c> occupied bit (0x20), which DSUN.EXE sets
/// together with the blocked bit at creatures' cells (<c>gmap_mark_party</c>,
/// <c>142F:02B9</c>); keeping it separate leaves the region map unchanged and
/// records who is where.
/// </summary>
public sealed class Occupancy
{
    private readonly Dictionary<Cell, int> _owners = [];

    public int Count => _owners.Count;

    /// <summary>The creature on a cell, or null.</summary>
    public int? OwnerOf(Cell cell) => _owners.TryGetValue(cell, out int id) ? id : null;

    /// <summary>Whether a creature other than <paramref name="self"/> is on the cell.</summary>
    public bool IsOccupiedByOther(Cell cell, int self) => _owners.TryGetValue(cell, out int id) && id != self;

    /// <summary>Claim cells for <paramref name="id"/>; returns false and claims none if another creature has one.</summary>
    public bool TryClaim(int id, IEnumerable<Cell> cells)
    {
        List<Cell> list = [.. cells];
        if (list.Any(c => IsOccupiedByOther(c, id)))
        {
            return false;
        }
        foreach (Cell c in list)
        {
            _owners[c] = id;
        }
        return true;
    }

    /// <summary>Release cells held by <paramref name="id"/>; cells of other creatures are left alone.</summary>
    public void Release(int id, IEnumerable<Cell> cells)
    {
        foreach (Cell c in cells)
        {
            if (_owners.TryGetValue(c, out int owner) && owner == id)
            {
                _owners.Remove(c);
            }
        }
    }

    public void ReleaseAll(int id)
    {
        foreach (Cell c in _owners.Where(p => p.Value == id).Select(p => p.Key).ToList())
        {
            _owners.Remove(c);
        }
    }

    public IEnumerable<KeyValuePair<Cell, int>> Cells => _owners;
}
