using DarkSun.Data.Items;
using DarkSun.Game.Characters;
using DarkSun.Data.Regions;
using DarkSun.Game.Rules;
using DarkSun.Game.World;

namespace DarkSun.Game.Combat;

public sealed partial class Battle
{
    /// <summary>A thri-kreen's race code.</summary>
    public const int ThriKreen = 8;

    /// <summary>The physical elements, what Stoneskin and Ironskin stop.</summary>
    public const DamageElement PhysicalElements = DamageElement.Bludgeoning | DamageElement.Slashing | DamageElement.Piercing;

    /// <summary>
    /// What a hit without a weapon is made of (<c>612E:1359</c>): bludgeoning
    /// (a thri-kreen's slashing, with <paramref name="clawsCut"/>: <see cref="CombatFixes.ThriKreenClawsCut"/>);
    /// a creature that isn't of a player race (race 0 or above 8) counts as
    /// magic of + (level − 2) ÷ 2 for resistances.
    /// </summary>
    public static DamageElement NaturalHit(Combatant a, bool clawsCut = false)
    {
        int plus = a.Race is 0 or > 8 ? (a.Level - 2) / 2 : 0;
        return (clawsCut && a.Race == ThriKreen ? DamageElement.Slashing : DamageElement.Bludgeoning) | plus switch
        {
            <= 0 => DamageElement.None,
            1 => DamageElement.Plus1,
            2 => DamageElement.Plus1 | DamageElement.Plus2,
            _ => DamageElement.Plus1 | DamageElement.Plus2 | DamageElement.Plus3,
        };
    }

    /// <summary>How far <paramref name="c"/> reaches with what it would attack with: a missile weapon's range, else 1.</summary>
    public static int Reach(Combatant c, bool missile = false) =>
        missile && c.MissileWeapon is { } w ? Math.Max(1, (int)w.Type.Range) : c.Weapon is { IsMissile: true } m ? m.Type.Range : 1;

    /// <summary>Can <paramref name="a"/> attack <paramref name="t"/> from where it stands: within reach, in sight, its attack flag up.</summary>
    public bool CanAttack(Combatant a, Combatant t, bool missile = false) =>
        (a.Flags & TurnFlags.MayAttack) != 0 && t.Alive && a != t && Distance(a, t) <= Reach(a, missile) && InSight(a, t)
        && !a.Has(Condition.CantAttack) && !a.Has(Condition.Paralyzed)
        && (!missile || a.CanShoot);

    /// <summary>Attacks this round from <paramref name="halves"/> (halves a round): Hasted doubles, Slowed halves, odd halves alternate.</summary>
    public int AttacksThisRound(Combatant c, int halves) => (HalvesFor(c, halves) + (Round & 1)) / 2;

    /// <summary><paramref name="halves"/> (attacks a round in halves) as <paramref name="c"/>'s Haste doubles them and Slow halves them.</summary>
    public static int HalvesFor(Combatant c, int halves) =>
        c.Has(Condition.Hasted) ? halves * 2 : c.Has(Condition.Slowed) ? halves / 2 : halves;

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
    /// unless attacked from behind, without shields from behind. Spells cap it:
    /// Spirit Armor at 4 (else Armor at 6), Barkskin at 6 − level ÷ 4, Shield at
    /// 4 from the front, Magical Vestments at 5 − (level − 5) ÷ 3 from level 6,
    /// Flesh Armor at 10 − level. Then Displacement −2, Dispelling Evil −7
    /// against a summoned creature (race 14), Protection from Evil −2 against an
    /// evil attacker, Biofeedback −1, −4 when the attacker can't see it; +4 when
    /// it is blind or slowed.
    /// </summary>
    public int ArmorClassAgainst(Combatant t, Combatant? a)
    {
        bool behind = a is not null && FromBehind(a, t);
        int ac = t.ArmorClass + (behind ? t.ShieldClass : 0);
        int adjust = behind ? 0 : Abilities.DexDefenceOf(t.Score(1));
        int LevelOf(Condition c) => t.Effects.Where(e => e.Condition == c).Sum(e => e.Level);
        foreach (ConditionModifier m in ModifiersOf(t))
        {
            if (m.ArmorClassCap is { } cap && !(behind && m.CapFromFrontOnly))
            {
                ac = Math.Min(ac, cap);
            }
            adjust += m.ArmorClass;
        }
        if (t.Has(Condition.Barkskin))
        {
            ac = Math.Min(ac, 6 - LevelOf(Condition.Barkskin) / 4);
        }
        if (t.Has(Condition.MagicalVestments))
        {
            int level = LevelOf(Condition.MagicalVestments);
            ac = Math.Min(ac, 5 - (level < 6 ? 0 : (level - 5) / 3));
        }
        if (t.Has(Condition.FleshArmor))
        {
            ac = Math.Min(ac, 10 - LevelOf(Condition.FleshArmor));
        }
        if (t.Has(Condition.DispellingEvil) && a is { Race: 14 })
        {
            adjust -= 7;
        }
        if (t.Has(Condition.ProtectionFromEvil) && a is { Alignment: 3 or 6 or 9 })
        {
            adjust -= 2;
        }
        if (a is not null && CantSee(a, t))
        {
            adjust -= 4;
        }
        return ac + adjust;
    }

    /// <summary>The <see cref="CombatRules.ConditionModifiers"/> of the conditions <paramref name="c"/> has.</summary>
    private IEnumerable<ConditionModifier> ModifiersOf(Combatant c) => Rules.ConditionModifiers.Where(m => c.Has(m.Condition));

    private bool Backstabs(Combatant a, Combatant t, Weapon? w) =>
        a.ThiefLevel > 0 && w is not { IsMissile: true } && (w is null || w.Type.Weight < 41) && FromBehind(a, t);

    /// <summary>
    /// The d20 roll <paramref name="a"/> needs to hit <paramref name="t"/>
    /// (<c>612E:1359</c>): THAC0 less the attacker's bonus, less the target's
    /// armour class. The bonus: the weapon's (its item bonus and material),
    /// Strength's to-hit in melee or Dexterity's reaction at range, difficulty − 1
    /// for monsters, and 2 from behind (a thief's backstab 4); and from effects
    /// (<c>80F5:3849</c>) Bless +1, Curse −1, Slow −4, a grafted weapon +1,
    /// Prayer +1 when its priest is on the attacker's side and −1 when not, −2
    /// against a blurred target, and +1 for a halfling with a weapon at more than
    /// 28 cells (beyond any combat range, so never).
    /// </summary>
    public int ToHitNeeded(Combatant a, Combatant t, Weapon? w) => ToHitTerms(a, t, w).Sum(term => term.Value);

    /// <summary>
    /// <see cref="ToHitNeeded"/> as its terms, for the combat log: THAC0, each
    /// bonus that isn't 0 (less), and the armour class (less); they add up to the
    /// number needed.
    /// </summary>
    public IReadOnlyList<RollTerm> ToHitTerms(Combatant a, Combatant t, Weapon? w)
    {
        bool missile = w is { IsMissile: true };
        var terms = new List<RollTerm> { new("THAC0", a.Thac0) };
        void Less(string what, int bonus)
        {
            if (bonus != 0)
            {
                terms.Add(new RollTerm(what, -bonus));
            }
        }
        Less("weapon", (w?.Bonus ?? 0) + (w?.MaterialToHit ?? 0));
        Less(missile ? "DEX" : "STR", missile ? Abilities.DexReactionOf(a.Score(1)) : Abilities.StrToHitOf(a.Strength));
        Less("difficulty", a.PartyMember ? 0 : Difficulty - 1);
        Less("effects", ModifiersOf(a).Sum(m => m.ToHit) + ModifiersOf(t).Sum(m => m.ToHitAgainst));
        if (a.Effects.FirstOrDefault(e => e.Condition == Condition.Prayer) is { Caster: { } priest })
        {
            Less("Prayer", priest.Side == a.Side ? 1 : -1);
        }
        if (a.Race == 6 && w is not null && Distance(a, t) > 28)
        {
            Less("range", 1);
        }
        if (FromBehind(a, t))
        {
            bool backstab = Backstabs(a, t, w);
            Less(backstab ? "backstab" : "from behind", backstab ? Rules.Backstab : Rules.FromBehind);
        }
        terms.Add(new RollTerm("AC", -ArmorClassAgainst(t, a)));
        return terms;
    }

    /// <summary>
    /// <paramref name="a"/>'s attacks on <paramref name="t"/> this turn
    /// (<c>612E:1359</c>, <c>612E:1B7A</c>): with its weapon (missile weapons
    /// use ammunition), else each natural attack. A thri-kreen with a weapon in
    /// hand swings it with its limbs and still bites (its last natural attack;
    /// the guide: five attacks a round, the poison bite the one left extra). Each is a d20: 20 hits, 1
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
            if (missile && a.ShootsAmmunition)
            {
                count = Math.Min(count, a.Ammunition);
            }
            for (int i = 0; i < count && t.Alive; i++)
            {
                if (missile && a.ShootsAmmunition)
                {
                    a.Ammunition--;
                }
                Swing(a, t, w, null, i == 0, false);
            }
            if (!missile && a.NaturalAttacks.Count > 1 && t.Alive)
            {
                NaturalAttack bite = a.NaturalAttacks[^1];
                int bites = AttacksThisRound(a, bite.Halves);
                for (int i = 0; i < bites && t.Alive; i++)
                {
                    Swing(a, t, null, bite, false, i == bites - 1);
                }
            }
        }
        else
        {
            int last = a.NaturalAttacks.Count - 1;
            for (int n = 0; n <= last && t.Alive; n++)
            {
                NaturalAttack natural = a.NaturalAttacks[n];
                if (n == 0)
                {
                    // 80F5:2ACC: Body Weaponry makes the first attack 2d4; Animal Affinity at least 1d10.
                    natural = a.Has(Condition.BodyWeaponry) ? natural with { Dice = 2, Sides = 4 }
                        : a.Has(Condition.AnimalAffinity) ? natural with { Dice = Math.Max(1, natural.Dice), Sides = Math.Max(10, natural.Sides) }
                        : natural;
                }
                int count = AttacksThisRound(a, natural.Halves);
                for (int i = 0; i < count && t.Alive; i++)
                {
                    Swing(a, t, null, natural, n == 0 && i == 0, n == last && i == count - 1);
                }
            }
        }
        a.Flags &= ~(TurnFlags.MayAttack | TurnFlags.MayMove);
        return true;
    }

    private void Swing(Combatant a, Combatant t, Weapon? w, NaturalAttack? natural, bool first, bool last)
    {
        IReadOnlyList<RollTerm> terms = ToHitTerms(a, t, w);
        int needed = terms.Sum(term => term.Value);
        int roll = Roll(1, 20);
        bool hit = roll == 20 || roll != 1 && roll >= needed;
        Note(new AttackRolled(a, t, roll, needed, hit, terms));
        if (!hit)
        {
            return;
        }
        bool melee = w is not { IsMissile: true };
        int dice, sides, bonus;
        if (w is not null)
        {
            (dice, sides, bonus) = (w.Type.DamageDice, w.Type.DamageSides, w.Type.DamageBonus + w.Bonus + w.MaterialDamage);
        }
        else
        {
            NaturalAttack n = natural ?? new(2, 1, 1, 0);
            (dice, sides, bonus) = (n.Dice, n.Sides, n.Bonus);
        }
        if (melee)
        {
            bonus += Abilities.StrDamageOf(a.Strength);
        }
        int damage = Math.Max(1, RollDice(dice, sides) + bonus);
        int times = Backstabs(a, t, w) ? Math.Min(5, (a.ThiefLevel - 1) / 4 + 2) : 1;
        damage *= times;
        Note(new DamageRolled(a, t, dice, sides, bonus, times, damage));
        Hit(a, t, damage, w?.Elements ?? NaturalHit(a, Rules.Fixes.ThriKreenClawsCut), first, last && natural is not null, missile: !melee);
        if (w?.Item is { } item)
        {
            WeaponSpell(a, t, item);
        }
    }

    /// <summary>
    /// A weapon's spell (its record's +0x0F) reaching whoever it hit
    /// (<c>612E:1B7A</c>, after the damage: <c>7D80:21C5</c> at level 6 on
    /// the target alone), unless it is a help and the weapon isn't cursed. No
    /// charge is used (<c>78A9:33A6</c> only copies the spell back).
    /// </summary>
    private void WeaponSpell(Combatant a, Combatant t, Item item)
    {
        if (item.Record.Flags is 0 or > ItemMagic.LastEffect || Spells.Effect(item.Record.Flags) is not { } e
            || e.Purpose == SpellEffect.Helps && item.Record.Bonus >= 0 || !t.Alive)
        {
            return;
        }
        Note(new Cast(a, e.Number, t.Cell));
        Apply(a, t, e, ItemMagic.CasterLevel);
    }

    /// <summary>
    /// A weapon or natural hit landing (<c>80F5:0079</c> with no spell): nothing
    /// touches a Blinking target that has acted this round (<see cref="Blinked"/>); a missile
    /// can't touch the Protected from Missiles, and a thri-kreen above level 6
    /// dodges one when 1d20 beats 8 (<c>80F5:14E5</c>); Armor spends a charge, a
    /// Cloak of Fear gives the attacker Cause Fear at level 6 (<c>80F5:1999</c>);
    /// the attacker's on-hit abilities; Negative Plane Protection; Dispelling Evil
    /// slays a summoned attacker's target (race 14); Stoneskin and Ironskin; then
    /// the damage times the target's resistance percent. Mirror images take
    /// only spells in DSUN.EXE (a hit has no spell, and <c>80F5:1999</c> tests
    /// them for spells alone), blows too with <see cref="CombatFixes.MirrorImagesTakeBlows"/>.
    /// </summary>
    public void Hit(Combatant a, Combatant t, int damage, DamageElement elements, bool first = true, bool lastNatural = false, bool missile = false)
    {
        if (Blinked(t))
        {
            Note(new Avoided(a, t, "blinked"));
            return;
        }
        if (Rules.Fixes.MirrorImagesTakeBlows && t.Has(Condition.MirrorImages) && Roll(1, 100) <= Rules.MirrorImageChance)
        {
            UseCharge(t, Condition.MirrorImages);
            Note(new Avoided(a, t, "image"));
            return;
        }
        if (missile && (t.Has(Condition.ProtectionFromMissiles) || t.Race == 8 && t.Level > 6 && Roll(1, 20) > 8))
        {
            Note(new Avoided(a, t, "missile"));
            return;
        }
        if (!Rules.Fixes.OneArmorChargePerWound)
        {
            UseCharge(t, Condition.ImprovedAc);
        }
        if (t.Has(Condition.CloakOfFear))
        {
            UseCharge(t, Condition.CloakOfFear);
            if (Spells.Effect(CauseFear) is { } fear)
            {
                Apply(t, a, fear, CloakOfFearLevel);
            }
        }
        OnHitAbilities(a, t, first, lastNatural);
        if (!t.Alive || NegativePlaneProtection(a, t, elements))
        {
            return;
        }
        if (a.Has(Condition.DispellingEvil) && t.Race == 14)
        {
            Hurt(a, t, 1000);
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
        Hurt(a, t, damage * percent / 100, elements);
    }

    private const int CloakOfFearLevel = 6;

    /// <summary>Cause Fear, what a Cloak of Fear gives an attacker (<c>80F5:1999</c>: effect 0x4E).</summary>
    public const int CauseFear = 79;

    /// <summary>
    /// The attacker's abilities that ride on a hit (<c>80F5:1999</c>;
    /// <see cref="CreatureRules.OnHit"/>): on the first attack cold, paralysis,
    /// acid, corrosion and (one in 10) disease; on the last natural attack the
    /// poisons, and a thri-kreen's paralysing bite above level 4.
    /// </summary>
    private void OnHitAbilities(Combatant a, Combatant t, bool first, bool last)
    {
        if (a.Type == 0 && a.Race != 8)
        {
            return;
        }
        foreach (OnHitEffect h in Creatures.OnHit.Where(h => h.LastAttack ? last : first))
        {
            if (h.OneIn is > 1 and int n && Roll(1, n) != 1)
            {
                continue;
            }
            if (t.Alive && (h.Bit is not { } bit || Creatures.HasAbility(a.Type, bit)) && (h.Race is not { } race || a.Race == race) && (h.AboveLevel == 0 || a.Level > h.AboveLevel)
                && Spells.Effect(h.Effect) is { } e)
            {
                Apply(a, t, e, a.Level);
            }
        }
    }

    /// <summary>Stoneskin takes a charge from every hit and stops a physical one; Ironskin stops (and is spent by) a physical one (<c>80F5:161F</c>).</summary>
    private bool Skin(Combatant? a, Combatant t, DamageElement elements)
    {
        bool physical = (elements & PhysicalElements) != 0;
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
    /// alone with Protection from Energy. With <see cref="CombatFixes.FireElementalResistances"/>
    /// the fire elementals use <see cref="FireElementalRows"/>.
    /// </summary>
    public int DamagePercent(Combatant t, DamageElement elements)
    {
        int percent = t.PartyMember ? 100
            : Rules.Fixes.FireElementalResistances && FireElementalRows.TryGetValue(t.Type, out var row) ? CreatureRules.PercentOf(row, elements)
            : Creatures.DamagePercent(t.Type, elements);
        DamageElement protection = DamageElement.None, stops = DamageElement.None;
        foreach (ConditionModifier m in ModifiersOf(t))
        {
            protection |= m.Halves;
            stops |= m.Stops;
        }
        if ((elements & protection) != 0 && (elements & ~(protection | DamageElement.Magic)) == 0)
        {
            percent /= 2;
        }
        if (elements != DamageElement.None && stops != DamageElement.None && (elements & ~stops) == 0)
        {
            percent = 0;
        }
        return percent;
    }

    /// <summary>
    /// The fire elementals' resistances as the clue book has them ("lesser
    /// elementals can only be affected by +1 or greater weapons, the regular
    /// elementals +2"), for <see cref="CombatFixes.FireElementalResistances"/>:
    /// the lesser one (33) gets the row DSUN.EXE gives the fire elemental (fire
    /// and unmagical blows do nothing, +1 does), the fire elemental (32) that row
    /// with +1 weapons doing nothing too. In the original type 32 takes +1
    /// weapons and type 33 needs +2, and only 32 is untouched by fire.
    /// </summary>
    public static IReadOnlyDictionary<int, IReadOnlyList<CreatureResistance>> FireElementalRows { get; } =
        new Dictionary<int, IReadOnlyList<CreatureResistance>>
        {
            [32] = [new(PhysicalElements | DamageElement.Fire | DamageElement.Plus1, 0), new(DamageElement.Plus2 | DamageElement.Plus3, 100)],
            [33] = [new(PhysicalElements | DamageElement.Fire, 0), new(DamageElement.Plus1 | DamageElement.Plus2 | DamageElement.Plus3, 100)],
        };

    /// <summary>
    /// Damage (<c>612E:18DA</c>): less than the hit points takes that many; up to
    /// 9 more leaves it down (status 3), more kills it (5), 10 000 or more
    /// destroys it (8). Armor loses a charge to every wound (<c>80F5:0E61</c>), on top of
    /// the one the blow or spell took unless <see cref="CombatFixes.OneArmorChargePerWound"/>.
    /// </summary>
    public void Hurt(Combatant? a, Combatant t, int damage, DamageElement elements = DamageElement.None)
    {
        if (damage < 1 || !t.Alive)
        {
            return;
        }
        UseCharge(t, Condition.ImprovedAc);
        Note(new Damaged(a, t, damage, elements));
        t.WasHit = true;
        Vitals v = t.Vitals;
        if (damage < v.Hp)
        {
            v.Hp -= damage;
            return;
        }
        // With PsionicBlastWearsOff the hit points it took still count: losing the rest only knocks one out.
        v.Status = damage < v.Hp + t.ImaginedLoss + 10 ? Vitals.Down : damage < 10000 ? Vitals.Dead : Vitals.Destroyed;
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
