using DarkSun.Data.Items;
using DarkSun.Game.Magic;
using DarkSun.Game.Movement;
using DarkSun.Game.Rules;
using DarkSun.Game.World;

namespace DarkSun.Game.Combat;

public sealed partial class Battle
{
    /// <summary>The healing spells (<c>80F5:05E6</c>'s cases 71, 112, 127): Cure Light, Serious and Critical Wounds heal their damage roll.</summary>
    public static IReadOnlySet<int> Healing { get; } = new HashSet<int> { 72, 113, 128 };

    /// <summary>Dispel Magic (wizard and priest; <c>80F5:05E6</c> cases 26 and 98).</summary>
    public static IReadOnlySet<int> Dispelling { get; } = new HashSet<int> { 27, 99 };

    /// <summary>
    /// Casting (<c>7D80:21C5</c>): <paramref name="effect"/> (a <c>SPIN</c> number)
    /// at <paramref name="at"/> for a caster of <paramref name="level"/>, on
    /// everyone in its area (<see cref="AffectedBy"/>). A spell the caster had
    /// is used up; its turn ends.
    /// </summary>
    public bool Cast(Combatant caster, int effect, int level, Cell at)
    {
        if (Spells.Effect(effect) is not { } e || caster.Has(Condition.NoSpells) && effect <= Spellcasting.FirstPower - 1)
        {
            return false;
        }
        caster.Spells.Remove(effect);
        Note(new Cast(caster, effect, at));
        if (e.Damaging || e.Condition != Condition.None && !OnSide(caster, at))
        {
            EndCondition(caster, Condition.Invisible);
            EndCondition(caster, Condition.InvisibleToUndead);
        }
        foreach (Combatant t in AffectedBy(caster, e, level, at))
        {
            Apply(caster, t, e, level);
        }
        EndTurn(caster);
        return true;
    }

    private bool OnSide(Combatant caster, Cell at) => At(at) is { } t && !caster.IsEnemyOf(t);

    /// <summary>
    /// Who an effect cast at <paramref name="at"/> reaches (<c>8BEB:0000</c>): the
    /// caster for self spells; one creature for single-target ones; otherwise
    /// by its template's shape, a circle of the area's size around the cell, a
    /// cone from the caster toward it (90°, the area's size long), or a line
    /// (a box half the area's size across). The sizes are DSUN.EXE's; the
    /// shapes are approximate.
    /// </summary>
    public IReadOnlyList<Combatant> AffectedBy(Combatant caster, SpellEffect e, int level, Cell at)
    {
        if (e.Target is 7 or 8)
        {
            return [caster];
        }
        AreaTemplate? template = Spells.TemplateOf(e);
        if (template is null || e.SingleTarget)
        {
            return At(at) is { } one ? [one] : [];
        }
        int size = Math.Max(16, e.AreaSize(level));
        IEnumerable<Combatant> alive = _all.Where(c => c.Alive);
        return template.Shape switch
        {
            AreaTemplate.Circle => [.. alive.Where(c => Distance(c.Cell, at) <= size / 16)],
            AreaTemplate.Cone => [.. alive.Where(c => c != caster && Distance(caster.Cell, c.Cell) <= Math.Min(Rules.SightRange, size / 16)
                && InCone(caster.Cell, at, c.Cell))],
            _ => [.. alive.Where(c => Math.Max(Math.Abs(c.Cell.X - at.X), Math.Abs(c.Cell.Y - at.Y)) <= Math.Max(0, size / 32))],
        };
    }

    private static bool InCone(Cell from, Cell toward, Cell c)
    {
        double a = Math.Atan2(toward.Y - from.Y, toward.X - from.X), b = Math.Atan2(c.Y - from.Y, c.X - from.X);
        double d = Math.Abs(Math.IEEERemainder(a - b, 2 * Math.PI));
        return d <= Math.PI / 4 + 1e-9;
    }

    /// <summary>
    /// An effect reaching <paramref name="t"/> (<c>80F5:0079</c>): immunities
    /// (<see cref="Immune"/>), a spell turned or struck at an image, resistances
    /// (0 % stops it), magic resistance for spells, the save; on a failed save
    /// the condition, then the damage (halved on a save, or none), the healing
    /// and the effect's own handling (<c>80F5:05E6</c>). A caster's effects on
    /// its own side skip magic resistance and saves.
    /// </summary>
    public void Apply(Combatant caster, Combatant t, SpellEffect e, int level)
    {
        if (!t.Alive)
        {
            return;
        }
        if (Immune(t, e))
        {
            Note(new Avoided(caster, t, "immune"));
            return;
        }
        bool friendly = caster == t || !caster.IsEnemyOf(t);
        if (!friendly && e.SingleTarget && e.Template < 0)
        {
            if (t.Has(Condition.SpellTurning) && caster != t)
            {
                UseCharge(t, Condition.SpellTurning);
                Note(new Avoided(caster, t, "turned"));
                Apply(t, caster, e, level);
                return;
            }
            if (e.IsTouch && t.Has(Condition.MirrorImages) && Roll(1, 100) <= Rules.MirrorImageChance)
            {
                UseCharge(t, Condition.MirrorImages);
                Note(new Avoided(caster, t, "image"));
                return;
            }
        }
        if (e.Damaging && Skin(caster, t, e.Elements))
        {
            return;
        }
        int percent = DamagePercent(t, e.Elements);
        if (percent == 0)
        {
            Note(new Avoided(caster, t, "immune"));
            return;
        }
        if (!friendly && e.Number < Spellcasting.FirstPower && Roll(1, 100) < MagicResistance(t, e, level))
        {
            Note(new Avoided(caster, t, "resisted"));
            return;
        }
        bool saved = !friendly && Saved(t, e);
        if (saved)
        {
            Note(new Avoided(caster, t, "saved"));
        }
        else if (e.Condition != Condition.None)
        {
            AddCondition(caster, t, e, level);
        }
        if (e.Damaging && !Healing.Contains(e.Number))
        {
            var (dice, flat) = e.DamageAt(level);
            int damage = RollDice(dice, e.Sides) + flat;
            damage = saved ? (e.SaveCancels ? 0 : damage / 2) : damage;
            Hurt(caster, t, damage * percent / 100);
        }
        Special(caster, t, e, level);
    }

    /// <summary>
    /// What makes <paramref name="t"/> untouched by <paramref name="e"/>
    /// (<c>80F5:161F</c>): Wisdom 19+ against mind spells and Intelligence 19+
    /// against illusions of a low enough level (spell level ≤ score − 18); the
    /// undead against mind spells and flags 0x100 and 1; ability bit 5 against
    /// charm and hold, bit 21 against Wall of Fog; bits 0 and 1 (burrowing,
    /// flying) against lingering areas cast from range, bit 9 against those on
    /// the ground; type 12 against Web.
    /// </summary>
    public bool Immune(Combatant t, SpellEffect e)
    {
        bool mind = (e.Flags & SpellFlags.MindAffecting) != 0;
        if (Spellcasting.Get(e.Number) is { Kind: not SpellKind.Psionic } spell)
        {
            if (mind && spell.Level <= t.Stats[4] - 18 || (e.Flags & SpellFlags.Illusion) != 0 && spell.Level <= t.Stats[3] - 18)
            {
                return true;
            }
        }
        if (t.IsUndead && (mind || (e.Flags & 0x0100) != 0 || (e.Flags & SpellFlags.Summons) != 0))
        {
            return true;
        }
        if (Creatures.HasAbility(t.Type, CreatureAbilityBit.NoCharmOrHold) && (e.Flags & SpellFlags.CharmOrHold) != 0
            || Creatures.HasAbility(t.Type, CreatureAbilityBit.ParalysingCone) && e.Number == 12)
        {
            return true;
        }
        if (e.AreaSize(1) > 8 && Spells.TemplateOf(e) is { Lingers: true } template)
        {
            if (Creatures.HasAbility(t.Type, CreatureAbilityBit.Burrows) || Creatures.HasAbility(t.Type, CreatureAbilityBit.Flies)
                || template.Kind == AreaTemplate.Ground && Creatures.HasAbility(t.Type, CreatureAbilityBit.NotStuck))
            {
                return true;
            }
        }
        return e.Number == 25 && t.Type == 12;
    }

    /// <summary>
    /// <paramref name="t"/>'s magic resistance against <paramref name="e"/>
    /// (<c>80F5:1901</c>; character record +0x29): +75 against mind spells with
    /// Mind Bar, halved with Low Resistance, less 30 + the level against Lower
    /// Resistance; a d100 below it resists (<c>80F5:1025</c>).
    /// </summary>
    public int MagicResistance(Combatant t, SpellEffect e, int level)
    {
        int mr = t.MagicResistance;
        if (t.Has(Condition.MindBar) && (e.Flags & SpellFlags.MindAffecting) != 0)
        {
            mr += 75;
        }
        if (mr == 0)
        {
            return 0;
        }
        if (t.Has(Condition.LowResistance))
        {
            mr /= 2;
        }
        if (e.Number == LowerResistance)
        {
            mr -= level + 30;
        }
        return Math.Max(0, mr);
    }

    public const int LowerResistance = 65;

    /// <summary>
    /// A saving throw (<c>80F5:0ED1</c>): only for effects that allow one, not
    /// for the paralysed or those down. The save is the target's, by the effect's
    /// category (<see cref="SpellRules.SaveCategories"/>). A 1 fails, a 20 saves;
    /// otherwise the d20 (doubled against fire, cold and electricity, as the
    /// original does) + the bonus + the effect's modifier must reach it. The
    /// bonus (part of <c>80F5:119C</c>): Wisdom's magical defence against mind
    /// spells, Dexterity against areas, Constitution against poison (dwarves and
    /// halflings Con × 2 / 7 more), and 4 against fear for the Brave.
    /// </summary>
    public bool Saved(Combatant t, SpellEffect e)
    {
        if (e.SaveAllowed == 0 || e.SaveCategory == 6 || t.Has(Condition.Paralyzed) || t.Vitals.Status == Vitals.Down)
        {
            return false;
        }
        int save = Spells.SaveCategories[Math.Clamp(e.SaveCategory, 0, Spells.SaveCategories.Count - 1)];
        int score = t.Saves[Math.Clamp(save - 1, 0, t.Saves.Count - 1)];
        int roll = Roll(1, 20);
        if (roll is 1 or 20)
        {
            return roll == 20;
        }
        if ((e.Elements & (DamageElement.Fire | DamageElement.Cold | DamageElement.Electricity)) != 0)
        {
            roll *= 2;
        }
        int bonus = e.SaveModifier;
        if ((e.Flags & SpellFlags.MindAffecting) != 0)
        {
            bonus += AbilityTables.WisDefence(t.Stats[4]);
        }
        if (!e.SingleTarget)
        {
            bonus -= AbilityTables.DexAc(t.Stats[1]);
        }
        if ((e.Elements & DamageElement.Poison) != 0)
        {
            bonus += AbilityTables.ConPoison(t.Stats[2]) + (t.Race is 2 or 6 ? t.Stats[2] * 2 / 7 : 0);
        }
        if ((e.Flags & SpellFlags.Fear) != 0 && t.Has(Condition.Brave))
        {
            bonus += 4;
            UseCharge(t, Condition.Brave);
        }
        return (byte)(roll + bonus) >= score;
    }

    // -- conditions, durations and charges ----------------------------------------------------------

    /// <summary>
    /// How long an effect lasts (<c>7D80:1BD3</c>): unit × (its dice + per level ×
    /// (level + bonus) / levels a step), at most 32767; −9999 permanent. With
    /// unit −1 this is minus a count of charges.
    /// </summary>
    public int Duration(SpellEffect e, int level)
    {
        if (e.Unit == SpellEffect.Permanent)
        {
            return SpellEffect.Permanent;
        }
        int lv = Math.Max(1, level);
        long total = (long)e.Unit * (RollDice(e.DurationCount, e.DurationSides)
            + (long)e.DurationPerLevel * (lv + e.LevelBonus) / Math.Max(1, e.LevelsPerStep));
        return (int)Math.Min(total, 32767);
    }

    /// <summary>
    /// A condition on a failed save (<c>7D80:1301</c>, <c>8053:03C0</c>): blocked
    /// by what prevents it (Free Action against Stuck, Paralyzed and Slowed;
    /// Protection from Paralysis against Paralyzed; ability bit 9 against Stuck)
    /// or cancelling its opposite instead (Bless and Curse, Haste and Slow,
    /// Brave and Afraid). A timed condition replaces the same one; charges
    /// (unit −1) are counted, not timed. Paralyzed and Stuck stop movement.
    /// </summary>
    public bool AddCondition(Combatant? caster, Combatant t, SpellEffect e, int level)
    {
        Condition c = e.Condition;
        if (c == Condition.Stuck && (t.Has(Condition.FreeAction) || Creatures.HasAbility(t.Type, CreatureAbilityBit.NotStuck))
            || c is Condition.Paralyzed && (t.Has(Condition.FreeAction) || t.Has(Condition.ProtectionFromParalysis))
            || c is Condition.Slowed && t.Has(Condition.FreeAction))
        {
            Note(new Avoided(caster, t, "free"));
            return false;
        }
        Condition? opposite = c switch
        {
            Condition.Blessed => Condition.Cursed,
            Condition.Cursed => Condition.Blessed,
            Condition.Hasted => Condition.Slowed,
            Condition.Slowed => Condition.Hasted,
            Condition.Brave => Condition.Afraid,
            Condition.Afraid => Condition.Brave,
            _ => null,
        };
        if (opposite is { } o && t.Has(o))
        {
            EndCondition(t, o);
            return false;
        }
        int duration = Duration(e, level);
        if (duration == 0)
        {
            return false;
        }
        if (!e.UsesCharges)
        {
            EndCondition(t, c);
        }
        var effect = new ActiveEffect(e.Number, c, level, caster);
        if (duration < 0 && duration != SpellEffect.Permanent)
        {
            effect.Charges = -duration;
        }
        else if (duration > 0)
        {
            effect.EndsAt = Clock + duration;
        }
        t.Effects.Add(effect);
        if (c is Condition.Paralyzed or Condition.Stuck)
        {
            t.Flags &= ~TurnFlags.MayMove;
        }
        Note(new ConditionGained(t, c));
        return true;
    }

    /// <summary>One charge off <paramref name="c"/>'s first effect with charges (<c>7D80:1723</c>); it ends at 0.</summary>
    public void UseCharge(Combatant t, Condition c)
    {
        if (t.Effects.FirstOrDefault(e => e.Condition == c && e.Charges > 0) is { } effect && --effect.Charges == 0)
        {
            EndEffect(t, effect);
        }
    }

    /// <summary>Ends every effect giving <paramref name="c"/> (<c>7D80:15CB</c>).</summary>
    public void EndCondition(Combatant t, Condition c)
    {
        foreach (ActiveEffect e in t.Effects.Where(e => e.Condition == c).ToList())
        {
            EndEffect(t, e);
        }
    }

    private void EndEffect(Combatant t, ActiveEffect e)
    {
        if (t.Effects.Remove(e))
        {
            Note(new ConditionEnded(t, e.Condition));
        }
    }

    /// <summary>
    /// A combatant's effects each round (<c>8053:0163</c>): regeneration (ability
    /// bit 17, up to its most hit points), a Poisoned charge (1000 damage when the
    /// last goes) and an Acid charge with 2d4 damage.
    /// </summary>
    private void RoundEffects(Combatant c)
    {
        if (Creatures.HasAbility(c.Type, CreatureAbilityBit.Regenerates) && c.Vitals.Hp < c.Vitals.MaxHp)
        {
            int gain = Math.Min(Rules.Regeneration, c.Vitals.MaxHp - c.Vitals.Hp);
            c.Vitals.Hp += gain;
            Note(new Healed(c, gain));
        }
        if (c.Has(Condition.Poisoned))
        {
            UseCharge(c, Condition.Poisoned);
            if (!c.Has(Condition.Poisoned))
            {
                Hurt(null, c, 1000);
            }
        }
        if (c.Alive && c.Has(Condition.Acid))
        {
            UseCharge(c, Condition.Acid);
            Hurt(null, c, RollDice(2, 4));
        }
    }

    /// <summary>
    /// The mind for this round (<c>8053:000E</c>): Afraid flees (1); Berserk
    /// picks a side at random and fights (7); Confused rolls d10: 1 flees, 2–6
    /// acts normally (0), 7–9 as Berserk, 10 as it would; else combat record +0x1E.
    /// </summary>
    public int MindOf(Combatant c)
    {
        if (c.Has(Condition.Afraid))
        {
            return 1;
        }
        int Berserk()
        {
            c.Side = Roll(1, 2);
            return 7;
        }
        if (c.Has(Condition.Berserk))
        {
            return Berserk();
        }
        if (c.Has(Condition.Confused))
        {
            int r = Roll(1, 10);
            if (r == 1)
            {
                return 1;
            }
            if (r <= 6)
            {
                return 0;
            }
            if (r <= 9)
            {
                return Berserk();
            }
        }
        return c.Mind;
    }

    // -- effects' own handling (80F5:05E6) ----------------------------------------------------------------

    private void Special(Combatant caster, Combatant t, SpellEffect e, int level)
    {
        switch (e.Number)
        {
            case var n when Healing.Contains(n):
                var (dice, flat) = e.DamageAt(level);
                int heal = Math.Min(RollDice(dice, e.Sides) + flat, t.Vitals.MaxHp - t.Vitals.Hp);
                if (heal > 0)
                {
                    t.Vitals.Hp += heal;
                    Note(new Healed(t, heal));
                }
                break;
            case var n when Dispelling.Contains(n):
                foreach (ActiveEffect a in t.Effects.Where(a => !a.Permanent).ToList())
                {
                    EndEffect(t, a);
                }
                break;
            case 179 or 187:
                CorrodeArmor(t);
                break;
            case 188:
                CorrodeWeapon(t);
                break;
            case 139:
                BreakItem(t);
                break;
        }
    }

    /// <summary>
    /// "Armor is corroded" (<c>80F5:2024</c>): the first item worn on the arms,
    /// legs or chest (slots 0, 6, 9) is destroyed unless its record +0x0F is v
    /// ≥ 1 and 1d20 ≥ 10 − v.
    /// </summary>
    private void CorrodeArmor(Combatant t)
    {
        foreach (Item item in t.Items.Where(i => i.Slot is 0 or 6 or 9))
        {
            int v = (sbyte)item.Record.Flags;
            if (v == 0 || Roll(1, 20) < 10 - v)
            {
                Destroy(t, item, "Armor is corroded");
                if (Catalog?.TypeOf(item.Record) is { } type)
                {
                    t.ArmorClass += type.ArmorClass + Math.Max(0, (int)item.Record.Bonus);
                }
                return;
            }
        }
    }

    /// <summary>"Weapon is corroded" (<c>80F5:1EBA</c>): the weapon in hand is destroyed unless 1d20 ≥ 8 − its bonus.</summary>
    private void CorrodeWeapon(Combatant t)
    {
        if (t.Weapon is { } w && Roll(1, 20) < 8 - w.Bonus)
        {
            if (w.Item is { } item)
            {
                Destroy(t, item, "Weapon is corroded");
            }
            t.Weapon = null;
        }
    }

    /// <summary>
    /// Detonate's shrapnel (<c>80F5:1F4B</c>): of the items worn, the one that
    /// breaks most easily (record +0x10 lowest, below 6) breaks when 1d20 is
    /// under 15 − its bonus. The original makes it a "Broken Weapon"; here it is gone.
    /// </summary>
    private void BreakItem(Combatant t)
    {
        if (t.Items.Where(i => i.Equipped && i.Record.Breakable).OrderBy(i => i.Record.Special).FirstOrDefault() is { } item
            && Roll(1, 20) < 15 - item.Record.Bonus)
        {
            Destroy(t, item, "is broken");
            if (t.Weapon?.Item == item)
            {
                t.Weapon = null;
            }
        }
    }

    private void Destroy(Combatant t, Item item, string message)
    {
        t.Items.Remove(item);
        Note(new ItemDestroyed(t, item, message));
    }
}
