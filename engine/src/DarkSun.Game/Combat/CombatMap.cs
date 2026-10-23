using DarkSun.Data.Items;
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

/// <summary>A term of a roll's target number: "THAC0" 18, "STR" −1, "AC" −5 (they add up to it).</summary>
public sealed record RollTerm(string What, int Value);

/// <summary>An attack roll: <paramref name="Roll"/> against <paramref name="Needed"/>, made of <paramref name="Terms"/>.</summary>
public sealed record AttackRolled(Combatant Actor, Combatant Target, int Roll, int Needed, bool Hit, IReadOnlyList<RollTerm>? Terms = null) : CombatEvent;

/// <summary>
/// Damage rolled for a hit or a spell (<paramref name="Effect"/>, 0 for a
/// blow): <paramref name="Dice"/>d<paramref name="Sides"/> + <paramref name="Bonus"/>,
/// times <paramref name="Times"/> (a backstab), <paramref name="Total"/> before
/// saves and resistances.
/// </summary>
public sealed record DamageRolled(Combatant? Actor, Combatant Target, int Dice, int Sides, int Bonus, int Times, int Total, int Effect = 0) : CombatEvent;

/// <summary>A saving throw against <paramref name="Effect"/>: the d20 (<paramref name="Natural"/>), with its bonuses <paramref name="Total"/>, against <paramref name="Needed"/>.</summary>
public sealed record SaveRolled(Combatant Target, int Effect, int Natural, int Total, int Needed, bool Saved) : CombatEvent;

/// <summary>Magic resistance against <paramref name="Effect"/>: a d100 under <paramref name="Percent"/> resists.</summary>
public sealed record ResistanceRolled(Combatant Target, int Effect, int Percent, int Roll, bool Resisted) : CombatEvent;

/// <summary>Damage dealt, and what it was made of (DSUN.EXE picks its damage splash by it, <c>896E:0A0E</c>).</summary>
public sealed record Damaged(Combatant? Actor, Combatant Target, int Amount, DamageElement Elements = DamageElement.None) : CombatEvent;

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

/// <summary>A lingering area was laid on the map.</summary>
public sealed record FieldMade(AreaField Field) : CombatEvent;

/// <summary>A lingering area went: its time ran out, or it was dispelled or burnt away.</summary>
public sealed record FieldEnded(AreaField Field) : CombatEvent;

/// <summary>A charm or Berserk moved a combatant to another side.</summary>
public sealed record SideChanged(Combatant Actor, int Side) : CombatEvent;
