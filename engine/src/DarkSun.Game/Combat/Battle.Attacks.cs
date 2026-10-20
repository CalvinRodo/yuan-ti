using DarkSun.Data.Items;
using DarkSun.Data.Regions;
using DarkSun.Game.Rules;
using DarkSun.Game.World;

namespace DarkSun.Game.Combat;

public sealed partial class Battle
{
    /// <summary>What a natural attack's hit is made of: the three physical kinds (none of DSUN.EXE's tables says).</summary>
    public const DamageElement NaturalElements = DamageElement.Bludgeoning | DamageElement.Slashing | DamageElement.Piercing;

    /// <summary>How far <paramref name="c"/> reaches with what it would attack with: a missile weapon's range, else 1.</summary>
    public static int Reach(Combatant c, bool missile = false) =>
        missile && c.MissileWeapon is { } w ? Math.Max(1, (int)w.Type.Range) : c.Weapon is { IsMissile: true } m ? m.Type.Range : 1;

    /// <summary>Can <paramref name="a"/> attack <paramref name="t"/> from where it stands: within reach, in sight, its attack flag up.</summary>
    public bool CanAttack(Combatant a, Combatant t, bool missile = false) =>
        (a.Flags & TurnFlags.MayAttack) != 0 && t.Alive && a != t && Distance(a, t) <= Reach(a, missile) && InSight(a, t)
        && !a.Has(Condition.CantAttack) && !a.Has(Condition.Paralyzed)
        && (!missile || a.MissileWeapon is not null && a.Ammunition > 0);

    /// <summary>Attacks this round from <paramref name="halves"/> (halves a round): Hasted doubles, Slowed halves, odd halves alternate.</summary>
    public int AttacksThisRound(Combatant c, int halves)
    {
        int h = c.Has(Condition.Hasted) ? halves * 2 : c.Has(Condition.Slowed) ? halves / 2 : halves;
        return (h + (Round & 1)) / 2;
    }

    /// <summary>Is <paramref name="a"/> behind <paramref name="t"/>: does <paramref name="t"/> face away from it (<c>612E:1B5E</c>)?</summary>
    public static bool FromBehind(Combatant a, Combatant t) => (Direction(t.Cell, a.Cell) + 4) % 8 == t.Facing;

    /// <summary>
    /// Can't <paramref name="a"/> see <paramref name="t"/> (<c>80F5:31AB</c>): it is
    /// Blind, or the target is Invisible (or Invisible to Undead, against the
    /// undead) and it neither detects nor sees invisible things.
    /// </summary>
    public bool CantSee(Combatant a, Combatant t) =>
        a.Has(Condition.Blind)
        || (t.Has(Condition.Invisible) || t.Has(Condition.InvisibleToUndead) && a.IsUndead)
            && !a.Has(Condition.DetectInvisible) && !Creatures.HasAbility(a.Type, CreatureAbilityBit.SeesInvisible);

    /// <summary>
    /// <paramref name="t"/>'s armour class against <paramref name="a"/>
    /// (<c>612E:2113</c>, <c>80F5:39B7</c>): its own, plus Dexterity's adjustment
    /// unless attacked from behind, without shields from behind; Armor caps it at
    /// 6, Shield at 4; −4 when the attacker can't see it.
    /// </summary>
    public int ArmorClassAgainst(Combatant t, Combatant? a)
    {
        bool behind = a is not null && FromBehind(a, t);
        int ac = t.ArmorClass + (behind ? t.ShieldClass : AbilityTables.DexAc(t.Stats[1]));
        if (t.Has(Condition.ImprovedAc))
        {
            ac = Math.Min(ac, 6);
        }
        if (t.Has(Condition.Shielded))
        {
            ac = Math.Min(ac, 4);
        }
        if (a is not null && CantSee(a, t))
        {
            ac -= 4;
        }
        return ac;
    }

    private bool Backstabs(Combatant a, Combatant t, Weapon? w) =>
        a.ThiefLevel > 0 && w is not { IsMissile: true } && (w is null || w.Type.Weight < 41) && FromBehind(a, t);

    /// <summary>
    /// The d20 roll <paramref name="a"/> needs to hit <paramref name="t"/>
    /// (<c>612E:1359</c>): THAC0 less the attacker's bonus, less the target's
    /// armour class. The bonus: the weapon's (its item bonus and material),
    /// Strength's to-hit in melee or Dexterity's reaction at range, difficulty − 1
    /// for monsters, and 2 from behind (a thief's backstab 4).
    /// </summary>
    public int ToHitNeeded(Combatant a, Combatant t, Weapon? w)
    {
        bool missile = w is { IsMissile: true };
        int bonus = (w?.Bonus ?? 0) + (w?.MaterialToHit ?? 0)
            + (missile ? AbilityTables.DexReaction(a.Stats[1]) : AbilityTables.StrToHit(a.Stats[0]))
            + (a.PartyMember ? 0 : Difficulty - 1);
        if (FromBehind(a, t))
        {
            bonus += Backstabs(a, t, w) ? Rules.Backstab : Rules.FromBehind;
        }
        return a.Thac0 - bonus - ArmorClassAgainst(t, a);
    }

    /// <summary>
    /// <paramref name="a"/>'s attacks on <paramref name="t"/> this turn
    /// (<c>612E:1359</c>, <c>612E:1B7A</c>): with its weapon (missile weapons
    /// use ammunition), else each natural attack. Each is a d20: 20 hits, 1
    /// misses, otherwise it hits at the number needed. Afterwards it can
    /// neither attack nor move again this round, and its invisibility ends.
    /// </summary>
    public bool Attack(Combatant a, Combatant t, bool missile = false)
    {
        if (!CanAttack(a, t, missile))
        {
            return false;
        }
        a.Facing = Direction(a.Cell, t.Cell);
        EndCondition(a, Condition.Invisible);
        EndCondition(a, Condition.InvisibleToUndead);
        Weapon? w = missile ? a.MissileWeapon : a.Weapon;
        if (w is not null)
        {
            int count = AttacksThisRound(a, missile ? Math.Max(2, w.Type.Attacks * 2) : a.AttackHalves);
            if (missile)
            {
                count = Math.Min(count, a.Ammunition);
            }
            for (int i = 0; i < count && t.Alive; i++)
            {
                if (missile)
                {
                    a.Ammunition--;
                }
                Swing(a, t, w, null, i == 0, false);
            }
        }
        else
        {
            int last = a.NaturalAttacks.Count - 1;
            for (int n = 0; n <= last && t.Alive; n++)
            {
                int count = AttacksThisRound(a, a.NaturalAttacks[n].Halves);
                for (int i = 0; i < count && t.Alive; i++)
                {
                    Swing(a, t, null, a.NaturalAttacks[n], n == 0 && i == 0, n == last && i == count - 1);
                }
            }
        }
        a.Flags &= ~(TurnFlags.MayAttack | TurnFlags.MayMove);
        return true;
    }

    private void Swing(Combatant a, Combatant t, Weapon? w, NaturalAttack? natural, bool first, bool last)
    {
        int needed = ToHitNeeded(a, t, w);
        int roll = Roll(1, 20);
        bool hit = roll == 20 || roll != 1 && roll >= needed;
        Note(new AttackRolled(a, t, roll, needed, hit));
        if (!hit)
        {
            return;
        }
        bool melee = w is not { IsMissile: true };
        int damage;
        if (w is not null)
        {
            damage = RollDice(w.Type.DamageDice, w.Type.DamageSides) + w.Type.DamageBonus + w.Bonus + w.MaterialDamage;
        }
        else
        {
            NaturalAttack n = natural ?? new(2, 1, 2, 0);
            damage = RollDice(n.Dice, n.Sides) + n.Bonus;
        }
        if (melee)
        {
            damage += AbilityTables.StrDamage(a.Stats[0]);
        }
        damage = Math.Max(1, damage);
        if (Backstabs(a, t, w))
        {
            damage *= Math.Min(5, (a.ThiefLevel - 1) / 4 + 2);
        }
        Hit(a, t, damage, w?.Elements ?? NaturalElements, first, last && natural is not null);
    }

    /// <summary>
    /// A weapon or natural hit landing (<c>80F5:0079</c> with no spell): the
    /// target's charges (Armor's, a mirror image's, a Cloak of Fear's;
    /// <c>80F5:1999</c>), the attacker's on-hit abilities, Stoneskin and
    /// Ironskin, then the damage times the target's resistance percent.
    /// </summary>
    public void Hit(Combatant a, Combatant t, int damage, DamageElement elements, bool first = true, bool lastNatural = false)
    {
        UseCharge(t, Condition.ImprovedAc);
        if (t.Has(Condition.MirrorImages) && Roll(1, 100) <= Rules.MirrorImageChance)
        {
            UseCharge(t, Condition.MirrorImages);
            Note(new Avoided(a, t, "image"));
            return;
        }
        if (t.Has(Condition.CloakOfFear))
        {
            UseCharge(t, Condition.CloakOfFear);
            if (Spells.Effect(CauseFear) is { } fear)
            {
                Apply(t, a, fear, t.Level);
            }
        }
        OnHitAbilities(a, t, first, lastNatural);
        if (!t.Alive)
        {
            return;
        }
        if (Skin(a, t, elements))
        {
            return;
        }
        int percent = DamagePercent(t, elements);
        if (percent == 0)
        {
            Note(new Avoided(a, t, "immune"));
            return;
        }
        Hurt(a, t, damage * percent / 100);
    }

    /// <summary>Cause Fear, what a Cloak of Fear gives an attacker (<c>80F5:1999</c>: effect 0x4E).</summary>
    public const int CauseFear = 79;

    /// <summary>
    /// The attacker's abilities that ride on a hit (<c>80F5:1999</c>, by ability
    /// bit; <see cref="CreatureAbilityBit"/>): on the first attack cold, paralysis,
    /// acid, corrosion and (one in 10) disease; on the last natural attack the
    /// poisons, and a thri-kreen's paralysing bite above level 4.
    /// </summary>
    private void OnHitAbilities(Combatant a, Combatant t, bool first, bool last)
    {
        if (a.Type == 0 && a.Race != 8)
        {
            return;
        }
        void Ride(CreatureAbilityBit bit, int effect)
        {
            if (t.Alive && Creatures.HasAbility(a.Type, bit) && Spells.Effect(effect) is { } e)
            {
                Apply(a, t, e, a.Level);
            }
        }
        if (first)
        {
            Ride(CreatureAbilityBit.ColdTouch, 173);
            Ride(CreatureAbilityBit.ParalysingChill, 174);
            Ride(CreatureAbilityBit.AcidTouch, 175);
            Ride(CreatureAbilityBit.CorrodesGear, 187);
            Ride(CreatureAbilityBit.Acid20, 179);
            if (Roll(1, 10) == 1)
            {
                Ride(CreatureAbilityBit.Disease, 185);
            }
            Ride(CreatureAbilityBit.CorrodesGear, 188);
        }
        if (last)
        {
            Ride(CreatureAbilityBit.Poison10, 177);
            Ride(CreatureAbilityBit.Poison30, 178);
            Ride(CreatureAbilityBit.DeadlyPoison, 120);
            if (a.Race == 8 && a.Level > 4 && t.Alive && Spells.Effect(190) is { } bite)
            {
                Apply(a, t, bite, a.Level);
            }
        }
    }

    /// <summary>Stoneskin takes a charge from every hit and stops a physical one; Ironskin stops (and is spent by) a physical one (<c>80F5:161F</c>).</summary>
    private bool Skin(Combatant? a, Combatant t, DamageElement elements)
    {
        bool physical = (elements & NaturalElements) != 0;
        if (t.Has(Condition.Stoneskin))
        {
            UseCharge(t, Condition.Stoneskin);
            if (physical)
            {
                Note(new Avoided(a, t, "stoneskin"));
                return true;
            }
        }
        if (physical && t.Has(Condition.Ironskin))
        {
            UseCharge(t, Condition.Ironskin);
            Note(new Avoided(a, t, "ironskin"));
            return true;
        }
        return false;
    }

    /// <summary>
    /// The percent of damage of <paramref name="elements"/> that <paramref name="t"/>
    /// takes (<c>80F5:28F2</c>): its type's resistances, halved by a protection
    /// that covers all the elements (Protection from Cold, Fire, Lightning; Fire
    /// Shield covers fire and cold), 0 against fire, cold, electricity and magic
    /// alone with Protection from Energy.
    /// </summary>
    public int DamagePercent(Combatant t, DamageElement elements)
    {
        int percent = t.PartyMember ? 100 : Creatures.DamagePercent(t.Type, elements);
        DamageElement protection = DamageElement.None;
        if (t.Has(Condition.ProtectionFromCold))
        {
            protection |= DamageElement.Cold;
        }
        if (t.Has(Condition.ProtectionFromFire))
        {
            protection |= DamageElement.Fire;
        }
        if (t.Has(Condition.ProtectionFromLightning))
        {
            protection |= DamageElement.Electricity;
        }
        if (t.Has(Condition.FireShield))
        {
            protection |= DamageElement.Fire | DamageElement.Cold;
        }
        if ((elements & protection) != 0 && (elements & ~(protection | DamageElement.Magic)) == 0)
        {
            percent /= 2;
        }
        const DamageElement energy = DamageElement.Fire | DamageElement.Cold | DamageElement.Electricity | DamageElement.Magic;
        if (elements != DamageElement.None && (elements & ~energy) == 0 && t.Has(Condition.ProtectionFromEnergy))
        {
            percent = 0;
        }
        return percent;
    }

    /// <summary>
    /// Damage (<c>612E:18DA</c>): less than the hit points takes that many; up to
    /// 9 more leaves it down (status 3), more kills it (5), 10 000 or more
    /// destroys it (8). Armor loses a charge to every wound (<c>80F5:0E61</c>).
    /// </summary>
    public void Hurt(Combatant? a, Combatant t, int damage)
    {
        if (damage < 1 || !t.Alive)
        {
            return;
        }
        UseCharge(t, Condition.ImprovedAc);
        Note(new Damaged(a, t, damage));
        Vitals v = t.Vitals;
        if (damage < v.Hp)
        {
            v.Hp -= damage;
            return;
        }
        v.Status = damage < v.Hp + 10 ? Vitals.Down : damage < 10000 ? Vitals.Dead : Vitals.Destroyed;
        v.Hp = 0;
        t.Effects.Clear();
        t.Guarding = false;
        t.Initiative = -1;
        t.Flags = TurnFlags.None;
        foreach (Combatant c in _all.Where(c => c.Target == t))
        {
            c.Target = null;
        }
        Note(new Fell(t, v.Status));
        Defeated?.Invoke(t);
    }
}
