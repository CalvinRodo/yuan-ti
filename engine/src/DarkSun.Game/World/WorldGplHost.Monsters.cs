using DarkSun.Data.Regions;
using DarkSun.Game.Rules;
using DarkSun.Game.Scripting;

namespace DarkSun.Game.World;

public sealed partial class WorldGplHost
{
    /// <summary>The random monsters of each region (<c>RESOURCE.GFF</c> <c>MONR</c> 1), for <c>REQUEST(8)</c>.</summary>
    public MonsterTable Monsters { get; set; } = MonsterTable.Empty;

    /// <summary>The monster <c>REQUEST(8, …, 0)</c> picked last (<c>4FAA:00B0</c>), which <c>REQUEST(8, …, 1)</c> counts.</summary>
    private int _randomMonster;

    /// <summary>
    /// <c>REQUEST(8, who, a, b)</c> (<c>87B3:0CB4</c>) reads the region's <see cref="MonsterTable"/>
    /// (b ≠ 0: the one used anywhere). a = 0 picks a monster: three random filled slots, keeping
    /// the one whose strength is nearest the party's (<see cref="PartyStrength"/>), and returns its
    /// <c>OJFF</c>. Otherwise it returns how many of the last pick to make: twice the party's
    /// strength over the slot's strength, 1 to 6. The EXE divides by the strength of the slot
    /// <em>after</em> the pick's (its search loop counts one past the match; <c>87B3:0D3F</c>), as
    /// here; where that is 0 (an empty slot, or past the record's end) it would divide by zero,
    /// and the pick's own strength is used. 0 if the region has no table or the pick isn't in it.
    /// GPL 99 and 106 then CLONE that many.
    /// </summary>
    private int RandomMonster(GplMachine machine, GameWorld world, int a, int b)
    {
        if (Monsters.For(b != 0 ? MonsterTable.AnyRegion : world.Region.Number) is not { } slots)
        {
            return 0;
        }
        int party = PartyStrength(world);
        if (a == 0)
        {
            int pick = 0, nearest = 999;
            for (int i = 0; i < 3; i++)
            {
                MonsterTable.Slot slot;
                do
                {
                    slot = slots[machine.Random.Next() * MonsterTable.Slots / (BorlandRandom.RandMax + 1)];
                }
                while (slot.Ojff == 0 && slots.Any(s => s.Ojff != 0));
                if (Math.Abs(slot.Strength - party) < nearest)
                {
                    (pick, nearest) = (slot.Ojff, Math.Abs(slot.Strength - party));
                    _randomMonster = pick;
                }
            }
            return pick;
        }
        int at = Enumerable.Range(0, slots.Count).FirstOrDefault(i => slots[i].Ojff == _randomMonster, -1);
        if (at < 0)
        {
            return 0;
        }
        int strength = at + 1 < slots.Count && slots[at + 1].Strength != 0 ? slots[at + 1].Strength : slots[at].Strength;
        return strength == 0 ? 1 : Math.Clamp(2 * party / strength, 1, 6);
    }

    /// <summary>
    /// The party's strength for random monsters (<c>87B3:0E96</c>): for each member, their highest
    /// class level plus one for each class past the first.
    /// </summary>
    private int PartyStrength(GameWorld world)
    {
        int strength = 0;
        foreach (WorldObject member in world.Party)
        {
            if (SheetOf(member) is { } sheet && Creation.ActiveLevels(sheet) is { Count: > 0 } levels)
            {
                strength += levels.Max(l => l.Level) + levels.Count(l => l.Level > 0) - 1;
            }
        }
        return strength;
    }
}
