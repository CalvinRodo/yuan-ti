using DarkSun.Data.Items;
using DarkSun.Game.Combat;
using DarkSun.Game.Magic;

namespace DarkSun.Game.World;

/// <summary>What USE on an item does (<see cref="ItemMagic.Plan"/>).</summary>
public enum ItemUseKind
{
    /// <summary>"NOTHING HAPPENS".</summary>
    Nothing,

    /// <summary>It teaches its spell or power (<see cref="GameWorld.LearnFrom"/>).</summary>
    Learn,

    /// <summary>Its spell is aimed, as a cast spell is (the spell pointer, <c>8B7D:04D1</c>).</summary>
    Aim,

    /// <summary>Its spell acts on the user at once (<c>7D80:21C5</c>).</summary>
    OnSelf,

    /// <summary>The user's hit points are made whole (a spell number from 249).</summary>
    FullHeal,
}

/// <param name="Spell">The spell or power (a <c>SPIN</c> number): the item record's +0x0F.</param>
public readonly record struct ItemUse(ItemUseKind Kind, int Spell);

/// <summary>
/// Casting from items: the item window's USE (<c>91F7:03D0</c>). An item's
/// spell is its record's +0x0F (the copy <see cref="ItemRecord.PowerFor"/>
/// checks against); it is cast at level 6 whoever uses it, and each cast
/// uses a charge (<see cref="GameWorld.UseItemCharge"/>). A member's own
/// spell casts and psionic strength aren't touched.
/// </summary>
public static class ItemMagic
{
    /// <summary>The caster level of an item's spell (<c>8B7D:04D1</c>, <c>7D80:21C5</c> are given 6).</summary>
    public const int CasterLevel = 6;

    /// <summary>Spell numbers from here make the user whole (<c>91F7:03D0</c>: the item's +0x0F ≥ 0xF9; a lemon's).</summary>
    public const int FullHeal = 249;

    /// <summary>The last spell with an effect record (the combat effects, <see cref="SpellRules.Effects"/>): past it nothing is aimed.</summary>
    public const int LastEffect = 196;

    /// <summary>
    /// The fruit (<c>RDFF</c> 31017–31040): eaten, its spell acts on whoever eats it, whatever it is.
    /// </summary>
    public static bool IsFruit(int rdff) => Math.Abs(rdff) is >= 31017 and <= 31040;

    /// <summary>
    /// The quest items (<c>RDFF</c> 30029–30999): USE runs their use trigger
    /// (<c>91F7:03D0</c>: when <c>039D:08AA</c> finds one, <c>039D:0319</c>), and without one
    /// goes on as for any item.
    /// </summary>
    public static bool IsScripted(int rdff) => Math.Abs(rdff) is >= 30029 and <= 30999;

    /// <summary>
    /// Does the item window offer USE for <paramref name="item"/> (<c>91F7:0000</c>)?
    /// Quest items and fruit always; otherwise it needs a spell (+0x0F, up to 196),
    /// and a worn item doesn't have it when its spell helps (though worn, it does nothing),
    /// when it is cursed (a bonus below 0) with a spell against a creature, or when
    /// it is a hand's, ammunition or a thrown weapon (worn in places 5, 11, 12).
    /// </summary>
    public static bool Offered(Item item, ItemType? type, SpellRules spells)
    {
        if (IsScripted(item.Rdff) || IsFruit(item.Rdff))
        {
            return true;
        }
        int spell = item.Record.Flags;
        if (spell == 0 || spell > LastEffect)
        {
            return false;
        }
        if (type is { Placement: not ItemPlacement.None and var placement })
        {
            int purpose = spells.Effect(spell)?.Purpose ?? 0;
            if (purpose == SpellEffect.Helps || (item.Record.Bonus < 0 && purpose == 0) || (int)placement is 5 or 11 or 12)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// What USE on <paramref name="item"/> does (<c>91F7:03D0</c>) when no use
    /// trigger takes it: nothing without a spell; an item of <c>RDFF</c> 1400–1499 teaches it; a spell
    /// with an effect that isn't on oneself (target 0 or 7) is aimed, except a
    /// fruit's; from 249 the user is made whole; anything else acts on the user.
    /// </summary>
    public static ItemUse Plan(Item item, SpellRules spells)
    {
        int spell = item.Record.Flags;
        if (spell == 0)
        {
            return new(ItemUseKind.Nothing, 0);
        }
        if (Spellcasting.Teaches(item.Rdff))
        {
            return new(ItemUseKind.Learn, spell);
        }
        if (spell <= LastEffect && !IsFruit(item.Rdff) && spells.Effect(spell) is { Target: not (0 or 7) })
        {
            return new(ItemUseKind.Aim, spell);
        }
        return new(spell >= FullHeal ? ItemUseKind.FullHeal : ItemUseKind.OnSelf, spell);
    }
}
