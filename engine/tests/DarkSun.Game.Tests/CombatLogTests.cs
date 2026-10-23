using DarkSun.Game.Combat;
using DarkSun.Game.Movement;
using DarkSun.Game.World;
using static DarkSun.Game.Tests.CombatTests;

namespace DarkSun.Game.Tests;

public class CombatLogTests
{
    /// <summary>Dice rolled from <see cref="Next"/> once it is filled (the lowest until then).</summary>
    private sealed class QueuedDice : ICombatDice
    {
        public Queue<int> Next { get; } = new();

        public int Roll(int low, int high) => Next.TryDequeue(out int v) ? Math.Clamp(v, low, high) : low;
    }

    private static Battle Started(ICombatDice dice, params Combatant[] all)
    {
        var b = new Battle(all, new OpenField(30, 30), dice);
        b.StartRound();
        return b;
    }

    /// <summary>A log of <paramref name="b"/>'s events so far, all shown.</summary>
    private static CombatLog LogOf(Battle b, Action fight)
    {
        var log = new CombatLog(n => $"spell {n}", _ => "item");
        b.Happened += log.Add;
        fight();
        log.Reveal(b.Log[^1]);
        return log;
    }

    [Fact]
    public void TheTermsAddUpToTheNumberNeeded()
    {
        Combatant a = Fighter("a", 1, 5, 5, str: 18), t = Fighter("t", 2, 6, 5, ac: 5);
        Battle b = Started(new ScriptedDice(), a, t);
        foreach (int facing in new[] { 6, 2 })
        {
            t.Facing = facing;
            IReadOnlyList<RollTerm> terms = b.ToHitTerms(a, t, Sword(bonus: 1));
            Assert.Equal(b.ToHitNeeded(a, t, Sword(bonus: 1)), terms.Sum(x => x.Value));
            Assert.Equal(("THAC0", 20), (terms[0].What, terms[0].Value));
            Assert.Equal("AC", terms[^1].What);
        }
        // Facing away: from behind, and no Dexterity.
        string words = CombatLog.Terms(b.ToHitTerms(a, t, Sword(bonus: 1)));
        Assert.StartsWith("THAC0 20 - weapon 1 - STR ", words);
        Assert.EndsWith(" - from behind 2 - AC 5", words);
    }

    [Fact]
    public void AnAttackReadsAsObsidianEditionsLogAtEachLevel()
    {
        Combatant a = Fighter("Halton", 1, 5, 5, party: true), t = Fighter("Slig", 2, 6, 5, hp: 30);
        a.Weapon = Sword();
        var dice = new QueuedDice();
        Battle b = Started(dice, a, t);
        foreach (int roll in new[] { 15, 6 })
        {
            dice.Next.Enqueue(roll);
        }
        CombatLog log = LogOf(b, () => b.Attack(a, t));

        // 20 − 5 = 15 needed; a 15 hits for 1d8 = 6.
        Assert.Equal("Halton attacks Slig: 15 vs 15+ HIT, 6 damage (1d8)", log.At(LogLevel.Short)[0].Text);
        Assert.Equal("Slig now 24/30 HP (-6)", log.At(LogLevel.Short)[1].Text);
        Assert.Equal(["Halton hits Slig for 6"], log.At(LogLevel.Least).Select(l => l.Text));
        Assert.Equal(["Halton attacks Slig: d20 = 15, needs 15+ -> HIT", "THAC0 20 - AC 5 = 15", "damage 1d8 = 6", "Slig now 24/30 HP (-6)"],
            log.At(LogLevel.Detail).Select(l => l.Text));

        LogLine attack = log.At(LogLevel.Short)[0];
        Assert.Equal((LogKind.Hit, a), (attack.Kind, attack.Whose));
        Assert.Same(t, attack.Runs.Single(r => r.Text == "Slig").Name);
    }

    [Fact]
    public void LinesWaitForThePlaybackAndKeepTheHitPointsOfTheirTime()
    {
        Combatant a = Fighter("a", 1, 5, 5), t = Fighter("t", 2, 6, 5, hp: 30);
        a.Weapon = Sword();
        var dice = new QueuedDice();
        Battle b = Started(dice, a, t);
        foreach (int roll in new[] { 20, 8 })
        {
            dice.Next.Enqueue(roll);
        }
        var log = new CombatLog(n => $"spell {n}", _ => "item");
        b.Happened += log.Add;
        b.Attack(a, t);
        t.Vitals.Hp = 1; // later on

        Assert.True(log.IsEmpty);
        log.Reveal(b.Log.OfType<AttackRolled>().Single());
        Assert.Single(log.At(LogLevel.Short));
        log.Reveal(b.Log[^1]);
        Assert.Equal("t now 22/30 HP (-8)", log.At(LogLevel.Short)[^1].Text);
    }

    [Fact]
    public void FiltersKeepTheRoundsAndTheirKind()
    {
        Combatant a = Fighter("a", 1, 5, 5, party: true), t = Fighter("t", 2, 6, 5);
        a.Weapon = Sword();
        var dice = new QueuedDice();
        Battle b = Started(dice, a, t);
        foreach (int roll in new[] { 1 })
        {
            dice.Next.Enqueue(roll);
        }
        var log = new CombatLog(n => $"spell {n}", _ => "item");
        b.Happened += log.Add;
        log.Add(new RoundStarted(1));
        log.Add(new TurnStarted(t));
        b.Attack(a, t);
        log.Reveal(b.Log[^1]);

        Assert.Equal(["ROUND 1", "t's turn", "a attacks t: 1 vs 15+ miss"], log.At(LogLevel.Short).Select(l => l.Text));
        Assert.Equal(["ROUND 1", "a attacks t: 1 vs 15+ miss"], log.At(LogLevel.Short, LogFilter.Party).Select(l => l.Text));
        Assert.Equal(["ROUND 1"], log.At(LogLevel.Short, LogFilter.Damage).Select(l => l.Text));
        Assert.Equal((1, t), (log.Round, log.Acting));
    }

    [Fact]
    public void DiceReadAsTheyAreRolled()
    {
        Combatant a = Fighter("a", 1, 0, 0), t = Fighter("t", 2, 1, 0);
        Assert.Equal("2d4+1", CombatLog.Dice(new DamageRolled(a, t, 2, 4, 1, 1, 6)));
        Assert.Equal("1d8-1 x3", CombatLog.Dice(new DamageRolled(a, t, 1, 8, -1, 3, 9)));
        Assert.Equal("4", CombatLog.Dice(new DamageRolled(a, t, 0, 0, 4, 1, 4)));
    }
}
