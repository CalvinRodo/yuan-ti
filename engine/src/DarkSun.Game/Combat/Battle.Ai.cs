using DarkSun.Data.Regions;
using DarkSun.Game.Characters;
using DarkSun.Game.Magic;
using DarkSun.Game.Movement;
using DarkSun.Game.Rules;
using DarkSun.Game.World;

namespace DarkSun.Game.Combat;

public sealed partial class Battle
{
    /// <summary>The most choices the AI makes in one turn (DSUN.EXE calls it again while a move or attack goes on).</summary>
    private const int MaxAiPasses = 16;

    /// <summary>
    /// A computer-controlled turn (<c>612E:0BAD</c>; <c>dsun-exe-reference.md</c> §8,
    /// "The AI"). Mind 8 stands by; mind 1 flees from its target. Otherwise it
    /// keeps a target that still fights or picks one (<see cref="PickTarget"/>),
    /// then tries <see cref="CombatRules.AiSteps"/> in order until one acts. A move
    /// that leaves the turn going (the original's return value 3) starts the
    /// choice again. The turn is always over when this returns.
    /// </summary>
    public void RunAi(Combatant c)
    {
        if ((c.Flags & TurnFlags.Reequip) != 0)
        {
            c.Flags &= ~TurnFlags.Reequip;
            Reequip(c);
        }
        for (int pass = 0; pass < MaxAiPasses && Current == c && c.Fights && !Over; pass++)
        {
            if (c.CurrentMind == CombatRecord.MindIdle)
            {
                break;
            }
            if (c.Target is not { } target || !target.Fights || !c.IsEnemyOf(target) || target.Vitals.Status > 2)
            {
                c.Target = PickTarget(c, Rules.SightRange, sight: false);
            }
            if (c.Target is null)
            {
                break;
            }
            if (c.CurrentMind == CombatRecord.MindFlees)
            {
                Flee(c, c.Target);
                break;
            }
            if (!TrySteps(c))
            {
                break;
            }
        }
        if (Current == c)
        {
            EndTurn(c);
        }
    }

    /// <summary>
    /// How much the AI likes a weapon (<c>612E:0350</c>, <see cref="WeaponScoring"/>):
    /// dice × sides + 2 × damage bonus + 4 × item bonus, + 10 if magic (which a
    /// metal weapon always counts as: <see cref="Weapon.ScoresAsMagic"/>); a missile
    /// weapon times its rate, a melee one plus Strength's damage and 3 × the
    /// material's, times attacks a round in halves.
    /// </summary>
    public int WeaponScore(Combatant c, Weapon w)
    {
        WeaponScoring k = Rules.WeaponScore;
        int score = w.Type.DamageDice * w.Type.DamageSides * k.PerDieSide + w.Type.DamageBonus * k.DamageBonus
            + w.Bonus * k.ItemBonus + (w.ScoresAsMagic ? k.Magic : 0);
        return w.IsMissile
            ? score * Math.Max(1, (int)w.Type.Attacks)
            : (score + Abilities.StrDamageOf(c.Strength) + k.Material * w.MaterialDamage) * c.AttackHalves;
    }

    /// <summary>
    /// Re-equipping (flag 0x40, <c>612E:0252</c>, <c>6466:0854</c>): the best melee
    /// weapon it carries goes in its hand (slot 3), if the item types are known.
    /// </summary>
    private void Reequip(Combatant c)
    {
        if (Catalog is null)
        {
            return;
        }
        Weapon? best = c.Items.Select(i => Catalog.TypeOf(i.Record) is { IsWeapon: true } t && t.Range <= 1 ? new Weapon(t, i.Record.Bonus, i) : null)
            .OfType<Weapon>().MaxBy(w => WeaponScore(c, w));
        if (best is null || c.Weapon is { } held && WeaponScore(c, held) >= WeaponScore(c, best))
        {
            return;
        }
        if (c.Weapon?.Item is { } old)
        {
            old.Slot = Equipment.Pack;
        }
        if (best.Item is { } item)
        {
            item.Slot = 3;
        }
        c.Weapon = best;
    }

    /// <summary>The steps in order; true when the turn goes on (a move was made and the AI chooses again).</summary>
    private bool TrySteps(Combatant c)
    {
        foreach (AiStep step in Rules.AiSteps)
        {
            if (Current != c || c.Target is not { } target)
            {
                return false;
            }
            switch (step)
            {
                case AiStep.TooFar when Distance(c, target) > Rules.SightRange:
                    return false;
                case AiStep.Spells when TrySpells(c, target, out bool goesOn):
                    return goesOn;
                case AiStep.Ability when TryAbility(c, target):
                    return false;
                case AiStep.Psionics when TryPsionics(c, target):
                    return false;
                case AiStep.Attack when CanAttack(c, target):
                    Attack(c, target);
                    return false;
                case AiStep.Adjacent when PickTarget(c, 1, sight: true) is { } next && CanAttack(c, next):
                    c.Target = next;
                    Attack(c, next);
                    return false;
                case AiStep.Move when (c.Flags & TurnFlags.MayMove) != 0 && TryMove(c, target):
                    return true;
                case AiStep.Missile when TryMissile(c):
                    return false;
                case AiStep.Guard:
                    if (c.MovementPoints > Rules.SpareMovement)
                    {
                        c.Target = null;
                    }
                    Guard(c);
                    return false;
            }
        }
        return false;
    }

    /// <summary>
    /// A new target among the enemies with status below 3 within
    /// <paramref name="range"/> (<c>612E:1284</c>; measured with no sight test
    /// unless <paramref name="sight"/>): by <see cref="CombatRules.Targeting"/>,
    /// at random in the original.
    /// </summary>
    public Combatant? PickTarget(Combatant c, int range, bool sight)
    {
        List<Combatant> enemies = [.. _all.Where(e => c.IsEnemyOf(e) && e.Vitals.Status < Vitals.Down
            && Distance(c, e) <= range && (!sight || InSight(c, e)))];
        if (enemies.Count == 0)
        {
            return null;
        }
        return Rules.Targeting switch
        {
            TargetChoice.Nearest => enemies.MinBy(e => Distance(c, e)),
            TargetChoice.Weakest => enemies.MinBy(e => (e.Vitals.Hp, Distance(c, e))),
            TargetChoice.Casters => enemies.Where(e => e.Spells.Count > 0 || e.SpellSet > 0 || e.Psp > 0).MinBy(e => Distance(c, e))
                ?? enemies.MinBy(e => Distance(c, e)),
            _ => enemies[Roll(0, enemies.Count - 1)],
        };
    }

    /// <summary>Order mode 1: away from <paramref name="from"/> as far as its points go.</summary>
    private void Flee(Combatant c, Combatant from)
    {
        if (FleeCell(c, from) is { } cell)
        {
            MoveToward(c, cell);
        }
    }

    /// <summary>
    /// Spells (<c>6466:0000</c>): once a round, with Intelligence above
    /// <see cref="CombatRules.SpellIntelligence"/>. Close to its target it first
    /// steps back. Each spell it has rolls 0 … weight − 1 and the highest is cast
    /// where <see cref="AreaTarget"/> says; with nowhere worth it, it gives up its
    /// spells for the round and drops its target.
    /// </summary>
    private bool TrySpells(Combatant c, Combatant target, out bool goesOn)
    {
        goesOn = false;
        CastsFor(c);
        if (c.TriedSpells || c.Stats[3] <= Rules.SpellIntelligence || c.CurrentMind == CombatRecord.MindFlees || c.Spells.Count == 0
            || c.Has(Condition.NoSpells))
        {
            return false;
        }
        if (Distance(c, target) <= Rules.StepBackRange && (c.Flags & TurnFlags.MayMove) != 0 && c.MovementPoints > Rules.SpareMovement)
        {
            Cell before = c.Cell;
            Flee(c, target);
            if (c.Cell != before)
            {
                goesOn = true;
                return true;
            }
        }
        int best = -1, bestRoll = -1;
        Cell bestCell = default;
        foreach (int spell in c.Spells.Distinct())
        {
            int weight = Creatures.WeightOf(spell);
            if (weight <= 0 || Spells.Effect(spell) is not { } e)
            {
                continue;
            }
            int roll = Roll(0, weight - 1);
            if (roll > bestRoll && AreaTarget(c, e, c.Level, target, weight) is { } cell)
            {
                (best, bestRoll, bestCell) = (spell, roll, cell);
            }
        }
        c.TriedSpells = true;
        if (best < 0)
        {
            c.Target = null;
            goesOn = true;
            return true;
        }
        Cast(c, best, c.Level, bestCell);
        return true;
    }

    /// <summary>
    /// A special ability (<c>6466:067E</c>): one turn in
    /// <see cref="CreatureRules.AbilityChance"/>, the first of
    /// <see cref="CreatureRules.Abilities"/> its type has, cast where
    /// <see cref="AreaTarget"/> says.
    /// </summary>
    private bool TryAbility(Combatant c, Combatant target)
    {
        if ((c.Flags & TurnFlags.MayUseAbility) == 0 || c.Type == 0)
        {
            return false;
        }
        c.Flags &= ~TurnFlags.MayUseAbility;
        if (Roll(0, Creatures.AbilityChance - 1) != 0 || Creatures.AbilitiesOf(c.Type).FirstOrDefault() is not { } ability
            || Spells.Effect(ability.Spell) is not { } e || AreaTarget(c, e, c.Level, target, Creatures.WeightOf(ability.Spell)) is not { } cell)
        {
            return false;
        }
        return Cast(c, ability.Spell, c.Level, cell);
    }

    /// <summary>
    /// Psionics (<c>6466:0453</c>, monsters only): a power drawn at random from
    /// its set, used on the target in sight and range if it has the points
    /// (<see cref="UsePower"/>).
    /// </summary>
    private bool TryPsionics(Combatant c, Combatant target)
    {
        if ((c.Flags & TurnFlags.MayUsePsionics) == 0 || c.PartyMember || c.TriedSpells)
        {
            return false;
        }
        c.Flags &= ~TurnFlags.MayUsePsionics;
        IReadOnlyList<int> powers = Creatures.PowersOf(c.PsionicSet);
        if (powers.Count == 0)
        {
            return false;
        }
        int number = powers[Roll(0, powers.Count - 1)];
        if (Spells.Power(number) is not { } power || Spells.Effect(number) is not { } e || power.Cost > c.Psp
            || !InSight(c, target) || Distance(c, target) > e.RangeCells(c.Level))
        {
            return false;
        }
        UsePower(c, number, e.Target is 7 or 8 ? c.Cell : target.Cell);
        return true;
    }

    /// <summary>
    /// A psionic power (<c>6698:0280</c>): it costs its record's points (Enhanced
    /// Strength (24 − Strength) × 2; the dominations 3, 8, 13 or 18 by the
    /// target's level), and needs a power check (<see cref="PowerCheck"/>). A
    /// failed check costs half; a success the whole, and the power acts. A party
    /// member keeps up a power with a maintenance cost (<see cref="KeepUpPowers"/>).
    /// The turn ends either way. False: not enough points.
    /// </summary>
    public bool UsePower(Combatant c, int number, Cell at)
    {
        if (Spells.Power(number) is not { } power || Spells.Effect(number) is not { } e)
        {
            return false;
        }
        int cost = number switch
        {
            154 => (24 - c.Strength) * 2,
            159 or 160 => At(at)?.Level switch { < 6 => 3, < 11 => 8, < 16 => 13, _ => 18 },
            _ => power.Cost,
        };
        if (Math.Max(0, cost) > c.Psp)
        {
            return false;
        }
        _powerRoll = PowerCheck(c, number, power);
        if (_powerRoll <= 0)
        {
            Note(new Avoided(c, c, _powerRoll < 0 ? "psionics blocked" : "power failed"));
            c.Psp -= power.Cost / 2;
            EndTurn(c);
            return true;
        }
        c.Psp -= Math.Max(0, cost);
        if (c.PartyMember && power.Maintenance != NoMaintenance)
        {
            c.Maintained.Add(number);
        }
        Cast(c, number, c.Level, at);
        _powerRoll = 0;
        return true;
    }

    private const int NoMaintenance = 99;

    /// <summary>The successful power check's roll, which a psionic defence must beat (<c>DS:47F0:0076</c>).</summary>
    private int _powerRoll;

    /// <summary>
    /// The power check (<c>6698:0CED</c>): d20 ≤ the ability + the power's
    /// modifier + its rank − 1 (<see cref="Combatant.PowerRank"/>) succeeds (a 20
    /// always fails, a 1 always succeeds); the result is the roll, 0 a failure.
    /// While someone else keeps Synaptic Static up, psionics are blocked (−1)
    /// unless the user beats it: d20 ≤ its rank in the power, and the holder's
    /// d20 over its Intelligence + Synaptic Static's modifier + rank − 1 or under
    /// the user's. The holder itself can use none.
    /// </summary>
    public int PowerCheck(Combatant c, int number, PsionicPower power)
    {
        if (_all.FirstOrDefault(o => o.Alive && o.Has(Condition.SynapticStatic)) is { } holder)
        {
            if (holder == c)
            {
                return -1;
            }
            int mine = Roll(1, 20), theirs = Roll(1, 20);
            int static_ = holder.Stats[3] + (Spells.Power(SynapticStatic)?.Modifier ?? 0) + holder.PowerRank(SynapticStatic) - 1;
            if (mine > c.PowerRank(number) || theirs <= static_ && theirs >= mine)
            {
                return -1;
            }
        }
        int score = c.Stats[Math.Clamp(power.Ability, 0, 5)] + power.Modifier + c.PowerRank(number) - 1;
        int roll = Roll(1, 20);
        return roll == 20 ? 0 : roll <= score || roll == 1 ? roll : 0;
    }

    public const int SynapticStatic = 171;

    /// <summary>
    /// A psionic attack on <paramref name="t"/> (effects of the psionic element,
    /// <c>6698:0E68</c>): its defence is Wisdom − 7; a party member's best defence
    /// mode kept up (its rank + its cost ÷ 2, if better and paid for again now), a
    /// monster's level if better; at least 1. It holds when two rolls of 1 … the
    /// defence both beat the attacker's power check.
    /// </summary>
    public bool PsionicDefence(Combatant t)
    {
        int defence = t.Stats[4] - 7;
        if (t.PartyMember)
        {
            PsionicPower? best = null;
            foreach (PsionicPower p in t.Maintained.Select(n => Spells.Power(n)).OfType<PsionicPower>().Where(p => p.AttackMode == DefenceMode))
            {
                int score = t.PowerRank(p.Number) + p.Cost / 2;
                if (score > defence && p.Cost <= t.Psp)
                {
                    (best, defence) = (p, score);
                }
            }
            t.Psp -= best?.Cost ?? 0;
        }
        else
        {
            defence = Math.Max(defence, t.Level);
        }
        defence = Math.Max(1, defence);
        return _powerRoll < Roll(1, defence) && _powerRoll < Roll(1, defence);
    }

    /// <summary>Power record +5 for the defence modes (254).</summary>
    private const int DefenceMode = -2;

    /// <summary>
    /// Each round, a party member pays for the powers it keeps up (<c>6698:04C7</c>);
    /// one it can't pay for ends with its effects (<c>6698:060D</c>).
    /// </summary>
    private void KeepUpPowers(Combatant c)
    {
        foreach (int number in c.Maintained.ToList())
        {
            if (Spells.Power(number) is { } p && p.Maintenance <= c.Psp)
            {
                c.Psp -= p.Maintenance;
                continue;
            }
            c.Maintained.Remove(number);
            foreach (ActiveEffect e in c.Effects.Where(e => e.Effect == number).ToList())
            {
                EndEffect(c, e);
            }
        }
    }

    /// <summary>
    /// A creature's casts, counted the first time it thinks of a spell
    /// (<c>6798:0000</c>, <c>6798:0059</c>): its spells per spell level by
    /// <see cref="CombatRules.SpellSlots"/>, scaled by its hit points ÷ its most,
    /// then dealt a cast at a time to the spells of its set (in order, round and
    /// round) while their levels have any left. A combatant given
    /// <see cref="Combatant.Spells"/> keeps them.
    /// </summary>
    public void CastsFor(Combatant c)
    {
        if (c.SpellsCounted)
        {
            return;
        }
        c.SpellsCounted = true;
        IReadOnlyList<int> set = Creatures.SpellsOf(c.SpellSet);
        if (c.Spells.Count > 0 || set.Count == 0)
        {
            return;
        }
        long health = ((long)c.Vitals.Hp << 8) / Math.Max(1, c.Vitals.MaxHp);
        int[] left = new int[10];
        for (int level = 1; level < 10; level++)
        {
            int slots = 0;
            for (int i = 0; i < c.Classes.Count; i++)
            {
                var (code, classLevel) = c.Classes[i];
                if (i > 0 && c.Race == 1 && classLevel >= c.Classes[0].Level)
                {
                    continue;
                }
                slots += Rules.SpellSlots.Where(r => r.Classes.Contains(code)).Sum(r => r.Slots(r.Wisdom ? c.Stats[4] : classLevel, level));
            }
            left[level] = (int)(slots * health >> 8);
        }
        int[] casts = new int[set.Count];
        bool dealt = true;
        while (dealt)
        {
            dealt = false;
            for (int i = 0; i < set.Count; i++)
            {
                if (Spellcasting.Get(set[i]) is { Level: > 0 and < 10 } spell && left[spell.Level] > 0)
                {
                    left[spell.Level]--;
                    casts[i]++;
                    dealt = true;
                }
            }
        }
        for (int i = 0; i < set.Count; i++)
        {
            c.Spells.AddRange(Enumerable.Repeat(set[i], casts[i]));
        }
    }

    /// <summary>Order mode 0xF: to a free cell next to the target; if it can't move, the nearest enemy it sees becomes the target.</summary>
    private bool TryMove(Combatant c, Combatant target)
    {
        if (FreeCellNextTo(target, c) is { } cell && cell != c.Cell && MoveToward(c, cell) > 0)
        {
            return true;
        }
        if (_all.Where(e => c.IsEnemyOf(e) && e.Vitals.Status < Vitals.Down && e != target && Distance(c, e) <= Rules.SightRange && InSight(c, e))
            .MinBy(e => Distance(c, e)) is { } other && FreeCellNextTo(other, c) is { } near && MoveToward(c, near) > 0)
        {
            c.Target = other;
            return true;
        }
        return false;
    }

    /// <summary>A missile weapon (<c>6466:0AE8</c>): an enemy in sight and range, at random, is shot.</summary>
    private bool TryMissile(Combatant c)
    {
        if (c.MissileWeapon is null || c.Ammunition <= 0)
        {
            return false;
        }
        List<Combatant> inRange = [.. _all.Where(e => c.IsEnemyOf(e) && e.Vitals.Status < Vitals.Down && CanAttack(c, e, missile: true))];
        if (inRange.Count == 0)
        {
            return false;
        }
        Combatant t = inRange[Roll(0, inRange.Count - 1)];
        c.Target = t;
        return Attack(c, t, missile: true);
    }

    /// <summary>
    /// Where to cast <paramref name="e"/> (<c>6466:0179</c>): self and helpful
    /// touch spells on itself; otherwise, of the cells within ± 2 of the target in
    /// sight and range, the one whose area holds the most hit points of the other
    /// side. A monster (side 2) scores the party side's hit points less its own
    /// side's; a party-side caster only scores cells with no allies and more
    /// enemy hit points than |weight| / 2. Null: nowhere worth it.
    /// </summary>
    public Cell? AreaTarget(Combatant c, SpellEffect e, int level, Combatant target, int weight)
    {
        if (e.Target is 7 or 8 || e.IsTouch && !e.Damaging && e.Condition is Condition.None or Condition.ImprovedAc or Condition.Shielded)
        {
            return c.Cell;
        }
        int range = Math.Min(e.RangeCells(level), Rules.SightRange);
        Cell? best = null;
        int bestScore = 0;
        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                var cell = new Cell(target.Cell.X + dx, target.Cell.Y + dy);
                if (!Map.Walkable(cell) && At(cell) is null || Distance(c.Cell, cell) > Math.Max(range, 1) || !InSight(c.Cell, cell))
                {
                    continue;
                }
                IReadOnlyList<Combatant> hit = AffectedBy(c, e, level, cell);
                int enemies = hit.Where(c.IsEnemyOf).Sum(h => h.Vitals.Hp);
                int friends = hit.Where(h => !c.IsEnemyOf(h)).Sum(h => h.Vitals.Hp);
                int score = (c.Side & 3) == 2 ? enemies - friends
                    : friends == 0 && enemies > Math.Abs(weight) / (c.PartyMember ? 2 : 32) ? enemies : 0;
                if (score > bestScore)
                {
                    (best, bestScore) = (cell, score);
                }
            }
        }
        return best;
    }
}
