using DarkSun.Data.Regions;
using DarkSun.Game.Movement;
using DarkSun.Game.Rules;
using DarkSun.Game.World;

namespace DarkSun.Game.Combat;

/// <summary>The ground a fight is on: which cells can be stood on and which block sight.</summary>
public interface ICombatMap
{
    bool Walkable(Cell cell);

    bool BlocksSight(Cell cell);
}

/// <summary>Open ground of a given size, nothing in the way (for tests and arenas).</summary>
public sealed class OpenField(int width, int height) : ICombatMap
{
    public bool Walkable(Cell c) => c.X >= 0 && c.Y >= 0 && c.X < width && c.Y < height;

    public bool BlocksSight(Cell c) => false;
}

/// <summary>A region map: its blocked cells (<c>GMAP</c>) and sight-blocking cells (0x80).</summary>
public sealed class RegionCombatMap(Region region) : ICombatMap
{
    public bool Walkable(Cell c) => !region.IsBlocked(c.X, c.Y);

    public bool BlocksSight(Cell c) => region.BlocksSight(c.X, c.Y);
}

/// <summary>Where a fight's rolls come from.</summary>
public interface ICombatDice
{
    /// <summary>A number in <paramref name="low"/>–<paramref name="high"/> (<c>1A0A:2FA9</c>).</summary>
    int Roll(int low, int high);
}

/// <summary>DSUN.EXE's rolls: <c>rand</c> scaled into the range, as <c>1A0A:2FA9</c> does.</summary>
public sealed class BorlandDice(BorlandRandom random) : ICombatDice
{
    public BorlandRandom Random { get; } = random;

    public int Roll(int low, int high) => high <= low ? low : low + (int)((long)Random.Next() * (high - low + 1) / 0x8000);
}

/// <summary>Something that happened in a fight, for the combat log and the scene.</summary>
public abstract record CombatEvent;

public sealed record RoundStarted(int Round) : CombatEvent;

public sealed record TurnStarted(Combatant Actor) : CombatEvent;

public sealed record Moved(Combatant Actor, Cell From, Cell To) : CombatEvent;

/// <summary>An attack roll: <paramref name="Roll"/> against <paramref name="Needed"/>.</summary>
public sealed record AttackRolled(Combatant Actor, Combatant Target, int Roll, int Needed, bool Hit) : CombatEvent;

public sealed record Damaged(Combatant? Actor, Combatant Target, int Amount) : CombatEvent;

/// <summary>Damage or an effect that didn't land, and why ("immune", "resisted", "saved", "image", …).</summary>
public sealed record Avoided(Combatant? Actor, Combatant Target, string Why) : CombatEvent;

public sealed record Cast(Combatant Actor, int Effect, Cell At) : CombatEvent;

public sealed record ConditionGained(Combatant Target, Condition Condition) : CombatEvent;

public sealed record ConditionEnded(Combatant Target, Condition Condition) : CombatEvent;

public sealed record Healed(Combatant Target, int Amount) : CombatEvent;

/// <summary>"Armor is corroded", "Weapon is corroded", …: an item was destroyed.</summary>
public sealed record ItemDestroyed(Combatant Owner, Item Item, string Message) : CombatEvent;

public sealed record Guarded(Combatant Actor) : CombatEvent;

public sealed record Waited(Combatant Actor) : CombatEvent;

/// <summary>Down (3), dead (5) or destroyed (8).</summary>
public sealed record Fell(Combatant Actor, int Status) : CombatEvent;
