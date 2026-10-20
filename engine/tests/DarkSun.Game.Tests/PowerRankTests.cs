using DarkSun.Data.Items;
using DarkSun.Game.Characters;
using DarkSun.Game.Combat;
using DarkSun.Game.Magic;
using DarkSun.Game.World;
using static DarkSun.Game.Tests.CombatTests;

namespace DarkSun.Game.Tests;

/// <summary>Learning from items and power ranks; the fire elementals' resistance fix.</summary>
public class PowerRankTests
{
    [Fact]
    public void AnItemTeachesAPowerThenRaisesItsRank()
    {
        var c = new Character();
        Assert.Equal("EGO WHIP LEARNED", Spellcasting.LearnFromItem(c, 164, "EGO WHIP", out bool used));
        Assert.True(used);
        Assert.Equal(1, c.PowerRank(164));
        Assert.Equal("EGO WHIP ENHANCED TO 2", Spellcasting.LearnFromItem(c, 164, "EGO WHIP", out _));
        Assert.Equal(2, c.PowerRank(164));

        c.PowerRanks[164] = Spellcasting.MaxPowerRank;
        Assert.Equal("EGO WHIP ENHANCED TO 30", Spellcasting.LearnFromItem(c, 164, "EGO WHIP", out _));
        Assert.Equal(0, c.PowerRank(165));
    }

    [Fact]
    public void AnItemTeachesASpellOnce()
    {
        var c = new Character();
        Assert.Equal("YOU LEARN THE SPELL", Spellcasting.LearnFromItem(c, 28, "FIREBALL", out bool used));
        Assert.True(used);
        Assert.Equal("YOU ALREADY KNOW THIS SPELL", Spellcasting.LearnFromItem(c, 28, "FIREBALL", out used));
        Assert.False(used);
        Assert.Equal("CANNOT LEARN FROM THIS ITEM", Spellcasting.LearnFromItem(c, 0, "", out used));
        Assert.False(used);
        Assert.True(Spellcasting.Teaches(1400) && !Spellcasting.Teaches(1500));
    }

    [Fact]
    public void RanksAreSavedAndGoToTheCombatant()
    {
        var c = new Character { Powers = [161, 164], PowerRanks = { [164] = 3 } };
        Character back = CharacterRoster.FromJson(CharacterRoster.ToJson(c));
        Assert.Equal(3, back.PowerRank(164));

        var psion = new Combatant { Name = "psion", PartyMember = true, PowerRanks = Combatant.PowerRanksOf(back) };
        Assert.Equal((1, 3), (psion.PowerRank(161), psion.PowerRank(164)));
    }

    [Fact]
    public void TheFireElementalFixSwapsWhichNeedsPlusTwo()
    {
        Combatant fire = Fighter("fire", 2, 6, 10, type: 32), lesser = Fighter("lesser", 2, 7, 10, type: 33);
        DamageElement plus1 = DamageElement.Slashing | DamageElement.Plus1, plus2 = plus1 | DamageElement.Plus2;
        var original = new Battle([fire, lesser], new OpenField(10, 20), new ScriptedDice());
        var fixedBattle = new Battle([fire, lesser], new OpenField(10, 20), new ScriptedDice(),
            new CombatRules { Fixes = new CombatFixes(FireElementalResistances: true) });

        Assert.Equal((100, 0), (original.DamagePercent(fire, plus1), original.DamagePercent(lesser, plus1)));
        Assert.Equal((0, 100), (fixedBattle.DamagePercent(fire, plus1), fixedBattle.DamagePercent(lesser, plus1)));
        Assert.Equal(100, fixedBattle.DamagePercent(fire, plus2));
        Assert.Equal((0, 0), (fixedBattle.DamagePercent(fire, DamageElement.Fire), fixedBattle.DamagePercent(lesser, DamageElement.Fire)));
    }
}
