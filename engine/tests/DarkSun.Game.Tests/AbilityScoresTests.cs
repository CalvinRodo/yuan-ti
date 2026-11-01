using DarkSun.Data.Items;
using DarkSun.Game.Combat;
using DarkSun.Game.World;

namespace DarkSun.Game.Tests;

public class AbilityScoresTests
{
    private static readonly int[] Own = [12, 13, 14, 15, 16, 17];

    /// <summary>An item whose record +0x0F is <paramref name="special"/> and +0x0E <paramref name="charges"/>, as the shipped ones.</summary>
    private static Item Special(int special, int charges, int slot = Equipment.Pack) =>
        new(30017, new ItemRecord(-30017, 0, 0, 0, 0, 35, 0, (byte)charges, (byte)special, 6, 255, 0, 0), slot: slot);

    [Fact]
    public void AnItemAddsItsChargesToTheAbilityItsValueNames()
    {
        // El's Ring (0xFB, 3): Dex +3; Ring of Insight (0xFE, 2): Wis +2; Dag's Dagger (0xFF, 1): Cha +1.
        Assert.Equal([12, 16, 14, 15, 18, 18], AbilityScores.Of(Own, [], [Special(0xFB, 3, slot: 5), Special(0xFE, 2), Special(0xFF, 1)]));
    }

    [Fact]
    public void CarriedAnywhereCountsEvenInAContainer()
    {
        // 80F5:2BB8 walks the holder's whole tree; it never asks for a slot.
        var bag = new Item(1100, new ItemRecord(-1100, 1, 0, 0, 0, 0, 0, 0, 0, 6, 255, 0, 0)) { Contents = [Special(0xFC, 3)] };
        Assert.Equal(17, AbilityScores.Of(Own, [], [bag])[2]);
    }

    [Fact]
    public void OnlyWornItemsCountWhenTheRulesSaySo()
    {
        // CharacterRules.AbilityItemsWhenWorn: El's Ring on a finger counts; the Steadfast Ring in the pack or a bag doesn't.
        var bag = new Item(1100, new ItemRecord(-1100, 1, 0, 0, 0, 0, 0, 0, 0, 6, 255, 0, 0)) { Contents = [Special(0xFC, 3)] };
        Assert.Equal([12, 16, 14, 15, 16, 17], AbilityScores.Of(Own, [], [Special(0xFB, 3, slot: 5), Special(0xFC, 3), bag], wornOnly: true));
    }

    [Fact]
    public void ChargesFrom16AreAFloorAndFrom32ALoss()
    {
        // Belt of Might (0xFA, 24): Strength at least 24, and with a floor each ability is the higher of the two.
        Assert.Equal(24, AbilityScores.Of(Own, [], [Special(0xFA, 24)])[0]);
        Assert.Equal(25, AbilityScores.Of([23, 10, 10, 10, 10, 10], [], [Special(0xFA, 24), Special(0xFA, 3)])[0]);
        Assert.Equal(9, AbilityScores.Of(Own, [], [Special(0xFB, 35)])[1]); // 35: − 4
        // A floor anywhere stops a loss going below the ability's own.
        Assert.Equal(13, AbilityScores.Of(Own, [], [Special(0xFB, 35), Special(0xFA, 24)])[1]);
    }

    [Fact]
    public void EffectsWorkOnStrengthAndFeeblemindMakesIntAndWis3()
    {
        var self = new Combatant { Name = "Self" };
        int[] scores = AbilityScores.Of(Own, [
            new ActiveEffect(154, Condition.StrengthEnhanced, 5, self),
            new ActiveEffect(63, Condition.Feebleminded, 5, self),
        ], [Special(0xFD, 4)]);
        Assert.Equal([24, 13, 14, 3, 3, 17], scores);
        // Strength's own effects stay within 3–24.
        Assert.Equal(24, AbilityScores.Of([20, 10, 10, 10, 10, 10], [new ActiveEffect(24, Condition.Strength, 5, self) { Value = 6 }], [])[0]);
        Assert.Equal(3, AbilityScores.Of([5, 10, 10, 10, 10, 10], [new ActiveEffect(158, Condition.StrengthLent, 5, self) { Value = -8 }], [])[0]);
    }

    [Fact]
    public void ACombatantsScoresCountWhatItCarries()
    {
        var c = new Combatant { Name = "Belted", Stats = [12, 13, 14, 15, 16, 17], Items = [Special(0xFA, 24, slot: 2)] };
        Assert.Equal(24, c.Strength);
        Assert.Equal(12, c.Stats[0]);
    }
}
