using DarkSun.Game.Combat;
using DarkSun.Game.Movement;
using DarkSun.Game.World;
using static DarkSun.Game.Tests.CombatTests;

namespace DarkSun.Game.Tests;

/// <summary>The bundled Sunstroke, Inflict Pain and Sandstorms mods (issues #142, #145, #144).</summary>
[Collection(SpellCatalogCollection.Name)]
public class EffectIdeasTests
{
    private const int Sunstroke = 203, InflictPain = 204, MagicMissile = 9, StinkingCloud = 23;

    private static string ModFile(string mod, string file) => SpellcastingTests.BundledModFile(mod, Path.Combine("rules", file));

    private static SpellRules Rules(params string[] mods) => SpellRules.Read([.. mods.Select(m => ModFile(m, "spells.json"))]);

    private static Sandstorm Storm => WeatherRules.Read(ModFile("Sandstorms", "weather.json")).Sandstorm!;

    private static Battle Started(ICombatDice dice, SpellRules? spells, Sandstorm? storm, params Combatant[] all)
    {
        var b = new Battle(all, new OpenField(30, 30), dice, null, spells) { Sandstorm = storm };
        b.StartRound();
        return b;
    }

    // -- Sunstroke (#142) --------------------------------------------------------------------------------------------

    [Fact]
    public void SunstrokeWeakensAndTakesTheStrengthSpellsGive()
    {
        Combatant priest = Fighter("priest", 1, 5, 10), t = Fighter("t", 2, 8, 10), other = Fighter("other", 1, 9, 10);
        t.Effects.Add(new ActiveEffect(24, Condition.Strength, 5, null) { Value = 4, EndsAt = 9999 });
        t.Effects.Add(new ActiveEffect(4, Condition.Weakened, 5, null) { Value = -1, EndsAt = 9999 });
        Battle b = Started(new ScriptedDice(), Rules("Sunstroke"), null, priest, t, other);
        int toHit = b.ToHitNeeded(t, other, null), hitIt = b.ToHitNeeded(other, t, null);
        Assert.Equal(13, t.Strength);

        Assert.True(b.Cast(priest, Sunstroke, 6, t.Cell)); // a d20 of 1 fails the save

        Assert.True(t.Has(Condition.Sunstroke));
        Assert.Equal(b.Clock + 6 * SpellEffect.Round, t.Effects.Single(e => e.Condition == Condition.Sunstroke).EndsAt);
        Assert.Equal(toHit + 2, b.ToHitNeeded(t, other, null));
        Assert.Equal(hitIt - 2, b.ToHitNeeded(other, t, null)); // its armour class 2 worse
        Assert.Equal(9, t.Strength); // the spell's +4 gone, the weakness kept
    }

    [Fact]
    public void SunstrokeSparesTheUndeadElementalsAndThoseProtectedFromFire()
    {
        Combatant priest = Fighter("priest", 1, 5, 10), zombie = Fighter("zombie", 2, 8, 10, race: 9);
        Combatant elemental = Fighter("elemental", 2, 8, 12, race: 14), guarded = Fighter("guarded", 2, 8, 14);
        guarded.Effects.Add(new ActiveEffect(88, Condition.ProtectionFromFire, 5, null) { EndsAt = 9999 });
        Battle b = Started(new ScriptedDice(), Rules("Sunstroke"), null, priest, zombie, elemental, guarded);

        foreach (Combatant t in new[] { zombie, elemental, guarded })
        {
            b.Cast(priest, Sunstroke, 6, t.Cell);
            Assert.False(t.Has(Condition.Sunstroke));
            Assert.Contains(b.Log, e => e is Avoided { Why: "immune" } a && a.Target == t);
        }
    }

    // -- Inflict Pain (#145) -------------------------------------------------------------------------------------------

    [Fact]
    public void InflictPainStopsSpellsAndLastsWhileItIsKeptUp()
    {
        Combatant psion = Fighter("psion", 1, 5, 10, party: true), foe = Fighter("foe", 2, 8, 10), other = Fighter("other", 1, 9, 10);
        psion.Psp = 8;
        Battle b = Started(new ScriptedDice(), Rules("InflictPain"), null, psion, foe, other);
        int toHit = b.ToHitNeeded(foe, other, null);

        Assert.True(b.UsePower(psion, InflictPain, foe.Cell)); // the power check's 1 isn't beaten by the defence's rolls of 1
        Assert.True(foe.Has(Condition.Pained) && foe.Has(Condition.NoSpells));
        Assert.Equal(toHit + 4, b.ToHitNeeded(foe, other, null));
        Assert.False(b.Cast(foe, MagicMissile, 5, other.Cell));
        Assert.Equal(6, psion.Psp);

        b.StartRound();
        b.StartRound(); // kept up twice: 2 points each
        Assert.True(foe.Has(Condition.Pained) && foe.Has(Condition.NoSpells));
        Assert.Equal(2, psion.Psp);

        psion.Psp = 1;
        b.StartRound(); // dropped
        Assert.False(foe.Has(Condition.Pained) || foe.Has(Condition.NoSpells));
    }

    [Fact]
    public void InflictPainNotKeptUpLastsTheRoundAndTheNext()
    {
        Combatant psion = Fighter("psion", 2, 5, 10), foe = Fighter("foe", 1, 8, 10);
        psion.Psp = 8;
        Battle b = Started(new ScriptedDice(), Rules("InflictPain"), null, psion, foe);
        Assert.True(b.UsePower(psion, InflictPain, foe.Cell));

        b.StartRound();
        Assert.True(foe.Has(Condition.Pained) && foe.Has(Condition.NoSpells));
        b.StartRound();
        Assert.False(foe.Has(Condition.Pained) || foe.Has(Condition.NoSpells));
    }

    [Fact]
    public void ADefendedMindFeelsNoPain()
    {
        Combatant psion = Fighter("psion", 1, 5, 10, party: true), foe = Fighter("foe", 2, 8, 10);
        psion.Psp = 8;
        // The power check's d20 a 1; the defence (level 5) rolls 5s, over it.
        Battle b = Started(new ScriptedDice((l, h) => h == 20 ? 1 : h), Rules("InflictPain"), null, psion, foe);

        Assert.True(b.UsePower(psion, InflictPain, foe.Cell));
        Assert.Contains(b.Log, e => e is Avoided { Why: "mind defended" });
        Assert.False(foe.Has(Condition.Pained) || foe.Has(Condition.NoSpells));
    }

    [Fact]
    public void TheModsConditionModifiersAddUp()
    {
        SpellRules both = Rules("Sunstroke", "InflictPain", "SweetWater");

        Assert.Equal([Condition.Sunstroke, Condition.Pained], both.ConditionModifiers.Select(m => m.Condition).Order());
        Assert.Equal([197, 203, 204], both.Spells.Select(s => s.Number));
        Assert.Empty(SpellRules.Default.ConditionModifiers);
    }

    // -- Sandstorms (#144) -----------------------------------------------------------------------------------------------

    [Fact]
    public void ASandstormBlowsOnceAPeriodInTheOpenDesertOnly()
    {
        Sandstorm s = Storm;
        const int Period = 6 * 3600;
        for (int period = 0; period < 40; period++)
        {
            int minutes = Enumerable.Range(0, Period / 60).Count(m => s.BlowsAt(2, period * Period + m * 60));
            Assert.InRange(minutes, 10, 30);
            var (start, length) = s.StormIn(2, period);
            Assert.True(start >= period * Period && start + length <= (period + 1) * Period);
        }
        Assert.DoesNotContain(Enumerable.Range(0, 40 * Period / 60), m => s.BlowsAt(3, m * 60)); // the village isn't open desert
        Assert.Null(WeatherRules.Default.StormAt(2, s.StormIn(2, 0).Start));
        Assert.Same(s, new WeatherRules { Sandstorm = s }.StormAt(2, s.StormIn(2, 0).Start));
    }

    [Fact]
    public void InASandstormNoOneSeesFarAndMissilesGoAstray()
    {
        Combatant archer = Fighter("archer", 1, 5, 10), near = Fighter("near", 2, 9, 10), far = Fighter("far", 2, 12, 10);
        Battle b = Started(new ScriptedDice(), null, Storm, archer, near, far);
        Weapon bow = Sword() with { Type = Sword().Type with { Range = 8 } };

        Assert.True(b.InSight(archer, near));
        Assert.False(b.InSight(archer, far));
        Assert.Equal(SpellAim.NoLineOfSight, b.Aim(archer, SpellRules.Default.Effect(MagicMissile)!, 5, far.Cell));
        Assert.Contains(new RollTerm("sandstorm", 4), b.ToHitTerms(archer, near, bow));
        Assert.DoesNotContain(b.ToHitTerms(archer, near, Sword()), t => t.What == "sandstorm");
        Assert.False(b.Over); // those out of sight in the sand still fight

        Battle clear = Started(new ScriptedDice(), null, null, archer, near, far);
        Assert.True(clear.InSight(archer, far));
    }

    [Fact]
    public void ASandstormBlowsCloudsAwayAfterARound()
    {
        Combatant mage = Fighter("mage", 1, 2, 2), foe = Fighter("foe", 2, 20, 20);
        Battle b = Started(new ScriptedDice((l, h) => h), null, Storm, mage, foe);

        b.Cast(mage, StinkingCloud, 6, new Cell(4, 4));
        Assert.Equal(b.Clock + SpellEffect.Round, Assert.Single(b.Fields).EndsAt);
        b.StartRound();
        Assert.Empty(b.Fields);
    }
}
