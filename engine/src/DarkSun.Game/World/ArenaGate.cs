using DarkSun.Game.Movement;
using DarkSun.Game.Scripting;

namespace DarkSun.Game.World;

/// <summary>
/// A repair for the arena's main gate (region 42, <c>OJFF</c> 2059), whose
/// scripts only toggle it (<c>REQUEST(5)</c>, its two pictures: 1 open, 0
/// closed) and block or free cells (30, 21) and (31, 21), keeping whether it
/// is closed in <c>gflag[5]</c> (GPL 2, GPL 4). Entering the region,
/// <c>MAS 42</c> toggles it shut only while <c>gflag[16]</c> is 0 (before the
/// first bout), yet sets <c>gflag[5]</c> to 1 every time: back from region 41
/// with the gate left open (the escape alarm, GPL 3, sends the party there), the
/// scripts take the open gate for closed, and from then on each toggle draws it
/// the wrong way round. Region 41's script (GPL 137) uses <c>gflag[5]</c> as a
/// timer, so the flag can't tell the region script what the gate is like.
/// </summary>
public static class ArenaGate
{
    public const int Region = 42, Ojff = 2059;

    private static readonly Cell[] Cells = [new(30, 21), new(31, 21)];

    /// <summary>
    /// Put the gate as the arena's scripts will take it to be: entering the
    /// region, before its script runs, open while <c>gflag[16]</c> is 0 (the
    /// script shuts it) and shut after; in a loaded game, which runs no region
    /// script, as <c>gflag[5]</c> says. Returns whether it changed.
    /// </summary>
    public static bool Match(GameWorld world, GplMemory memory, bool loaded)
    {
        if (world.Region.Number != Region || world.Objects.FirstOrDefault(o => o.Source?.Ojff == Ojff && !o.Hidden) is not { } gate)
        {
            return false;
        }
        bool closed = Flag(memory, loaded ? 5 : 16);
        bool changed = gate.SecondState != closed;
        if (changed)
        {
            world.ToggleState(gate);
        }
        foreach (Cell cell in Cells)
        {
            changed |= world.Region.IsBlocked(cell.X, cell.Y) != closed;
            world.SetCellFlags(cell.X, cell.Y, Data.Regions.CellFlags.Blocked, closed);
        }
        return changed;
    }

    private static bool Flag(GplMemory memory, int n) => memory.Read(new GplVariable(GplVarType.GlobalFlag, n)) != 0;
}
