using DarkSun.Game.Characters;
using DarkSun.Game.Magic;

namespace DarkSun.Game.Combat;

/// <summary>
/// A spell's effect on a party member outside a fight: cast on the map before one
/// (the guide's Bless, Haste, Enlarge, Strength, Protection from Evil), or still
/// running when a fight ended. Kept on its sheet (<see cref="Character.Lasting"/>);
/// <paramref name="EndsAt"/> is on the game clock (<see cref="World.GameWorld.Clock"/>,
/// seconds, a minute a combat round), null for one that lasts by charges or for good.
/// </summary>
public sealed record LastingEffect(int Effect, Condition Condition, int Level, int Value = 0, int Charges = 0, int? EndsAt = null);

/// <summary>Moving <see cref="LastingEffect"/>s between a party member's sheet and its combatant.</summary>
public static class LastingEffects
{
    /// <summary>Conditions that don't outlast a fight: being held, charmed or stuck in a web.</summary>
    private static readonly Condition[] FightOnly = [Condition.Paralyzed, Condition.Charmed, Condition.Stuck];

    /// <summary>
    /// <paramref name="sheet"/>'s lasting effects onto <paramref name="c"/> at game time
    /// <paramref name="clock"/> (those already over are dropped from the sheet), their
    /// ends counted on the battle's clock from <paramref name="battleClock"/>.
    /// </summary>
    public static void Apply(Character sheet, Combatant c, int clock, int battleClock = 0)
    {
        sheet.Lasting.RemoveAll(e => e.EndsAt <= clock);
        foreach (LastingEffect e in sheet.Lasting)
        {
            c.Effects.Add(new ActiveEffect(e.Effect, e.Condition, e.Level, null)
            {
                Value = e.Value,
                Charges = e.Charges,
                EndsAt = e.EndsAt is int end ? battleClock + end - clock : null,
            });
        }
    }

    /// <summary>
    /// The spells' effects still on <paramref name="c"/> back to <paramref name="sheet"/>, at game
    /// time <paramref name="clock"/> and battle time <paramref name="battleClock"/>. Psionic powers
    /// end with the fight (the guide: "All psionics expire at the end of combat"), and so do a
    /// field's effects and <see cref="FightOnly"/>. With <paramref name="powers"/> (a power used on
    /// the map) they are kept too, to last into the next fight and end with it.
    /// </summary>
    public static void Keep(Character sheet, Combatant c, int clock, int battleClock, bool powers = false)
    {
        sheet.Lasting =
        [
            .. c.Effects.Where(e => e.Effect > 0 && (powers || e.Effect < Spellcasting.FirstPower) && e.Field is null && !FightOnly.Contains(e.Condition)
                    && (e.EndsAt is not int end || end > battleClock))
                .Select(e => new LastingEffect(e.Effect, e.Condition, e.Level, e.Value, e.Charges,
                    e.EndsAt is int end ? clock + end - battleClock : null)),
        ];
    }
}
