using DarkSun.Data.Items;
using DarkSun.Game.Characters;
using DarkSun.Game.Magic;
using DarkSun.Game.Movement;
using DarkSun.Game.Rules;
using DarkSun.Game.World;

namespace DarkSun.Game.Combat;

public sealed partial class Battle
{
    /// <summary>
    /// A cast of the caster's own: a creature's copy of the spell is gone; a
    /// party member's level has one cast less, and at none every spell of that
    /// level is gone for the fight (<c>78A9:5344</c>).
    /// </summary>
    private static void UseCast(Combatant caster, int effect)
    {
        if (caster.CastsLeft is { } left && caster.SpellLevels!.TryGetValue(effect, out int level) && caster.Spells.Contains(effect))
        {
            caster.SpellsCast.Add(effect);
            if (--left[level] <= 0)
            {
                caster.Spells.RemoveAll(s => caster.SpellLevels.GetValueOrDefault(s) == level);
            }
        }
        else if (caster.Spells.Remove(effect))
        {
            caster.SpellsCast.Add(effect);
        }
    }

    /// <summary>
    /// Casting (<c>7D80:003B</c>): <paramref name="effect"/> (a <c>SPIN</c> number)
    /// at <paramref name="at"/> for a caster of <paramref name="level"/>. An area
    /// that lingers becomes a field (<see cref="Fields"/>), acting on those in it
    /// as they enter; any other acts at once on everyone in its area
    /// (<see cref="AffectedBy"/>). Damage from a party member turns a neutral
    /// creature hostile. Fire and dispelling clear fields they reach. A spell the
    /// caster had is used up (not one cast from an item, <paramref name="ownSpell"/>
    /// false); its turn ends. <paramref name="end"/> is a wall's other end.
    /// </summary>
    public bool Cast(Combatant caster, int effect, int level, Cell at, Cell? end = null, bool ownSpell = true)
    {
        if (Spells.Effect(effect) is not { } e || caster.Has(Condition.NoSpells) && effect < Spellcasting.FirstPower || ownSpell && caster.WasHit)
        {
            return false;
        }
        if (ownSpell)
        {
            UseCast(caster, effect);
        }
        Note(new Cast(caster, effect, at));
        if (e.Damaging || e.Condition != Condition.None && !OnSide(caster, at))
        {
            UseCharge(caster, Condition.Invisible);
            UseCharge(caster, Condition.InvisibleToUndead);
        }
        if (e.Summons)
        {
            Summon(caster, e, at);
        }
        else if (Spells.TemplateOf(e) is { Lingers: true } template && !e.SingleTarget)
        {
            MakeField(caster, e, template, level, at, end);
        }
        else
        {
            foreach (Combatant t in AffectedBy(caster, e, level, at, end))
            {
                Apply(caster, t, e, level);
                if (e.Damaging && caster.PartyMember && !t.PartyMember && (t.Side & 3) == 0)
                {
                    t.Side = 2;
                    Note(new SideChanged(t, t.Side));
                }
            }
        }
        ClearFields(caster, e, level, at, end);
        EndTurn(caster);
        return true;
    }

    private bool OnSide(Combatant caster, Cell at) => At(at) is { } t && !caster.IsEnemyOf(t);

    /// <summary>
    /// Whether the player may cast <paramref name="e"/> at <paramref name="at"/>
    /// (<c>8BEB:0584</c>, the spell pointer). Self effects always. One-creature
    /// effects (<c>612E:3283</c>) need a creature there, within range of its
    /// nearest cell, then in sight; others need the cell in sight, then within
    /// range, a cone's range being its size (<c>8BEB:086F</c>). Not traced: the
    /// allegiance mask (<c>8BEB:0B43</c>, −2) and a wall's other end (−3).
    /// </summary>
    public SpellAim Aim(Combatant caster, SpellEffect e, int level, Cell at)
    {
        if (e.Target is 7 or 8)
        {
            return SpellAim.Ok;
        }
        if (e.SingleTarget)
        {
            return At(at) is not { } t ? SpellAim.NoTarget
                : Distance(caster, t) > e.RangeCells(level) ? SpellAim.OutOfRange
                : !InSight(caster, t) ? SpellAim.NoLineOfSight
                : SpellAim.Ok;
        }
        int range = e.Target == 6 ? Math.Max(1, Spells.AreaOf(e, level) / 16) : e.RangeCells(level);
        return !InSight(caster.Cell, at) ? SpellAim.NoLineOfSight
            : Distance(caster.Cell, at) > range ? SpellAim.OutOfRange
            : SpellAim.Ok;
    }

    /// <summary>
    /// Who an effect cast at <paramref name="at"/> reaches (<c>8BEB:0000</c>):
    /// the caster for self spells; one creature for single-target ones;
    /// otherwise everyone its template's circle, cone or line covers and the
    /// shape's centre can see (<see cref="ShapeOf"/>).
    /// </summary>
    public IReadOnlyList<Combatant> AffectedBy(Combatant caster, SpellEffect e, int level, Cell at, Cell? end = null)
    {
        if (e.Target is 7 or 8)
        {
            return [caster];
        }
        if (Spells.TemplateOf(e) is not { } template || e.SingleTarget)
        {
            return At(at) is { } one && Reaches(caster, e, one) ? [one] : [];
        }
        Shape shape = ShapeOf(caster, e, template, level, at, end);
        return [.. _all.Where(c => c.Alive && Covers(shape, c) && Reaches(caster, e, c))];
    }

    /// <summary>
    /// Whom some effects take at all (<c>80F5:1CF7</c>, after <c>8BEB:0000</c>;
    /// <see cref="SpellRules.TargetFilters"/>). "No enemy beside" is within 28
    /// sixteenths (<c>80F5:249D</c>), measured, by a slip, from (x, x) of the
    /// target's position (from where it stands with
    /// <see cref="CombatFixes.BlessFilterFromItsCell"/>).
    /// </summary>
    public bool Reaches(Combatant caster, SpellEffect e, Combatant t) =>
        Spells.TargetFilters.Where(f => f.Effects.Contains(e.Number)).All(f =>
            (f.Races is null || f.Races.Contains(t.Race))
            && (!f.Undead || t.IsUndead)
            && f.Side switch { TargetSide.Caster => t.Side == caster.Side, TargetSide.Other => t.Side != caster.Side, _ => true }
            && (!f.NoEnemyBeside || !EnemyBeside(t)));

    private bool EnemyBeside(Combatant t)
    {
        var (x, y) = Centre(t.Cell);
        y = Rules.Fixes.BlessFilterFromItsCell ? y : x;
        return _all.Any(o => o.Alive && t.IsEnemyOf(o) && Distance16(x, y, Centre(o.Cell).X, Centre(o.Cell).Y) <= 28);
    }

    /// <summary>
    /// An effect reaching <paramref name="t"/> (<c>80F5:0079</c>): immunities
    /// (<see cref="Immune"/>); against a spell of small area, a mirror image,
    /// Gaze Reflection or Spell Turning (<c>80F5:1999</c>); against an undead's
    /// negative energy, Negative Plane Protection; Stoneskin and Ironskin;
    /// resistances (0 % stops it); magic resistance for spells; the save. On a
    /// failed save the condition, then the damage (halved on a save, or none) and
    /// the effect's own handling (<see cref="Special"/>). Against a hostile
    /// effect Armor spends a charge and a Cloak of Fear frightens the caster
    /// (<c>80F5:1999</c>); a psionic attack meets the target's psionic defence
    /// (<see cref="PsionicDefence"/>). A caster's effects on its own side skip
    /// the charges, turning, the psionic defence, magic resistance and saves. <paramref name="field"/>
    /// is the field it comes from, if any.
    /// </summary>
    public void Apply(Combatant caster, Combatant t, SpellEffect e, int level, AreaField? field = null)
    {
        if (!t.Alive && e.Number != RaiseDead)
        {
            return;
        }
        if (Immune(t, e))
        {
            Note(new Avoided(caster, t, "immune"));
            return;
        }
        bool friendly = caster == t || !caster.IsEnemyOf(t);
        if (!friendly)
        {
            UseCharge(t, Condition.ImprovedAc);
            if (t.Has(Condition.CloakOfFear) && e.Number != CauseFear)
            {
                UseCharge(t, Condition.CloakOfFear);
                if (Spells.Effect(CauseFear) is { } fear)
                {
                    Apply(t, caster, fear, CloakOfFearLevel);
                }
            }
            bool small = Spells.AreaOf(e) <= 8 && (e.Number < Spellcasting.FirstPower || e.Number > Spellcasting.LastPower);
            if (small && Turned(caster, t, e, level))
            {
                return;
            }
            if (!small && NegativePlaneProtection(caster, t, e.Elements))
            {
                return;
            }
        }
        if (e.Elements != DamageElement.None && Skin(caster, t, e.Elements))
        {
            return;
        }
        int percent = DamagePercent(t, e.Elements);
        if (percent == 0 || (e.Elements & DamageElement.Fire) != 0 && t.LevelIn(FireCleric) >= 5)
        {
            Note(new Avoided(caster, t, "immune"));
            return;
        }
        if (!friendly && e.Elements == DamageElement.Psionic && PsionicDefence(t))
        {
            Note(new Avoided(caster, t, "mind defended"));
            return;
        }
        if (!friendly && e.Number < Spellcasting.FirstPower)
        {
            int mr = MagicResistance(t, e, level), d100 = Roll(1, 100);
            if (mr > 0)
            {
                Note(new ResistanceRolled(t, e.Number, mr, d100, d100 < mr));
            }
            if (d100 < mr)
            {
                Note(new Avoided(caster, t, "resisted"));
                return;
            }
        }
        bool saved = !friendly && Saved(t, e, caster, level);
        if (saved)
        {
            Note(new Avoided(caster, t, "saved"));
        }
        else if (e.Condition != Condition.None)
        {
            AddCondition(caster, t, e, level, field: field);
        }
        if (e.Damaging && !Spells.IsCure(e.Number))
        {
            var (dice, flat) = e.DamageAt(level);
            int damage = RollDice(dice, e.Sides) + flat;
            Note(new DamageRolled(caster, t, dice, e.Sides, flat, 1, damage, e.Number));
            damage = saved ? (e.SaveCancels ? 0 : damage / 2) : damage;
            damage = damage * percent / 100;
            if (damage > 0 && Spells.AreaOf(e) > 8 && Creatures.HasAbility(t.Type, CreatureAbilityBit.DiesToRangedSpells))
            {
                damage += 1000; // 80F5:0E61: ability bit 23
            }
            Hurt(caster, t, damage, e.Elements);
        }
        Special(caster, t, e, level, saved);
    }

    /// <summary>Class code 3, a cleric of fire: at level 5 fire can't touch it (<c>80F5:161F</c>).</summary>
    private const int FireCleric = 3;

    /// <summary>
    /// A spell of small area aimed at <paramref name="t"/> (<c>80F5:1999</c>): a
    /// mirror image takes it 75 times in 100; Gaze Reflection turns a gaze back on
    /// its caster; Spell Turning turns back a spell of level 1–3 with no area and
    /// spends a charge.
    /// </summary>
    private bool Turned(Combatant caster, Combatant t, SpellEffect e, int level)
    {
        if (t.Has(Condition.MirrorImages) && Roll(1, 100) <= Rules.MirrorImageChance)
        {
            UseCharge(t, Condition.MirrorImages);
            Note(new Avoided(caster, t, "image"));
            return true;
        }
        if (t.Has(Condition.GazeReflection) && (e.Flags & SpellFlags.Gaze) != 0)
        {
            Note(new Avoided(caster, t, "reflected"));
            Apply(t, caster, e, level);
            return true;
        }
        if (t.Has(Condition.SpellTurning) && LowLevelSpell(e) && e.Template < 0)
        {
            UseCharge(t, Condition.SpellTurning);
            Note(new Avoided(caster, t, "turned"));
            Apply(t, caster, e, level);
            return true;
        }
        return false;
    }

    /// <summary>A wizard or priest spell of level 1–3 (<c>80F5:31FC</c>): what Minor Globe and Minor Spell Turning stop.</summary>
    private static bool LowLevelSpell(SpellEffect e) =>
        e.Number < Spellcasting.FirstPower && Spellcasting.Get(e.Number) is { Level: >= 1 and <= 3 };

    /// <summary>
    /// Negative Plane Protection (<c>80F5:1999</c>): an undead's negative-energy
    /// attack spends a charge; if the target then saves (as against Vampiric
    /// Touch, whose record allows none, so never; with
    /// <see cref="CombatFixes.NegativePlaneProtectionSaves"/> it gets that save),
    /// the attack is stopped and the undead takes 2d6.
    /// </summary>
    private bool NegativePlaneProtection(Combatant a, Combatant t, DamageElement elements)
    {
        if (!t.Has(Condition.NegativePlaneProtection) || (elements & DamageElement.Negative) == 0 || !a.IsUndead)
        {
            return false;
        }
        UseCharge(t, Condition.NegativePlaneProtection);
        if (Spells.Effect(VampiricTouch) is not { } touch
            || !Saved(t, Rules.Fixes.NegativePlaneProtectionSaves ? touch with { SaveAllowed = 1 } : touch, a, a.Level))
        {
            return false;
        }
        Note(new Avoided(a, t, "negative plane protection"));
        Hurt(t, a, RollDice(2, 6));
        return true;
    }

    public const int VampiricTouch = 40, RaiseDead = 136;

    /// <summary>
    /// What makes <paramref name="t"/> untouched by <paramref name="e"/>
    /// (<c>80F5:14E5</c>, <c>80F5:161F</c>): Minor Globe against spells of level
    /// 1–3 and Disintegrate; Protection from Missiles against effects saved as
    /// missiles (category 4); Shield against Magic Missile; Free Action and
    /// Protection from Paralysis against what paralyses or slows; Wisdom 19+
    /// against mind effects and Intelligence 19+ against illusions of a low
    /// enough level (level ≤ score − 18; the monsters' effects count as level 3);
    /// the undead against mind effects, negative energy and poison; ability
    /// bit 5 against charm and hold, bit 21 against Wall of Fog; bits 0 and 1
    /// (burrowing, flying) against lingering areas, bit 9 against those on the
    /// ground; type 12 against Web; a grafted weapon against Graft Weapon; Blink
    /// against small effects once it has acted this round (initiative −1,
    /// <c>37BD:00D9</c>); the carrier of item 30021 against effect 196.
    /// </summary>
    public bool Immune(Combatant t, SpellEffect e)
    {
        if (Blinked(t) && Spells.AreaOf(e) < 9 || e.Number == DetonateShrapnel && t.Items.Any(i => i.Rdff == ShrapnelGuard))
        {
            return true;
        }
        if (t.Has(Condition.Englobed) && (LowLevelSpell(e) || e.Number == Disintegrate)
            || t.Has(Condition.ProtectionFromMissiles) && e.SaveCategory == 4
            || t.Has(Condition.Shielded) && e.Number == MagicMissile
            || (t.Has(Condition.FreeAction) || t.Has(Condition.ProtectionFromParalysis)) && e.Condition is Condition.Paralyzed or Condition.Slowed)
        {
            return true;
        }
        bool mind = (e.Flags & SpellFlags.MindAffecting) != 0;
        int spellLevel = e.Number > Spellcasting.LastPower ? 3 : Spellcasting.Get(e.Number) is { Kind: not SpellKind.Psionic } s ? s.Level : 0;
        if (spellLevel > 0 && (mind && spellLevel <= t.Stats[4] - 18 || (e.Flags & SpellFlags.Illusion) != 0 && spellLevel <= t.Stats[3] - 18))
        {
            return true;
        }
        if (t.IsUndead && (mind || (e.Elements & (DamageElement.Negative | DamageElement.Poison)) != 0))
        {
            return true;
        }
        if (Creatures.HasAbility(t.Type, CreatureAbilityBit.NoCharmOrHold) && (e.Flags & SpellFlags.CharmOrHold) != 0
            || Creatures.HasAbility(t.Type, CreatureAbilityBit.ParalysingCone) && e.Number == 12)
        {
            return true;
        }
        if (Spells.AreaOf(e) > 8 && Spells.TemplateOf(e) is { Lingers: true } template)
        {
            if (Creatures.HasAbility(t.Type, CreatureAbilityBit.Burrows) || Creatures.HasAbility(t.Type, CreatureAbilityBit.Flies)
                || template.Kind == AreaTemplate.Ground && Creatures.HasAbility(t.Type, CreatureAbilityBit.NotStuck))
            {
                return true;
            }
        }
        return e.Number == 25 && t.Type == 12 || e.Number == GraftWeapon && t.Has(Condition.GraftWeapon);
    }

    public const int MagicMissile = 9, Disintegrate = 140, GraftWeapon = 156;

    /// <summary>The item whose carrier effect 196 can't touch (<c>80F5:161F</c>: 0x7545).</summary>
    public const int ShrapnelGuard = 30021;

    /// <summary>Out of reach by Blink (condition 5): it has acted this round (<c>80F5:14E5</c>).</summary>
    public static bool Blinked(Combatant t) => t.Has(Condition.Blink) && t.Initiative == -1;

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
    /// original does) + the effect's modifier + <see cref="SaveBonus"/> must reach it.
    /// </summary>
    public bool Saved(Combatant t, SpellEffect e, Combatant? caster = null, int level = 1)
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
            Note(new SaveRolled(t, e.Number, roll, roll, score, roll == 20));
            return roll == 20;
        }
        int natural = roll;
        if ((e.Elements & (DamageElement.Fire | DamageElement.Cold | DamageElement.Electricity)) != 0)
        {
            roll *= 2;
        }
        int total = (byte)(roll + e.SaveModifier + SaveBonus(t, e, caster, level, save));
        Note(new SaveRolled(t, e.Number, natural, total, score, total >= score));
        return total >= score;
    }

    public const int Dismissal = 61;

    /// <summary>
    /// What adds to <paramref name="t"/>'s save (<c>80F5:119C</c>): −1 with the
    /// save penalty; Spirit Armor +3 (not on the paralysation save), Barkskin +1,
    /// Bless +1; Protection from Evil +2 against an evil caster; Protection from
    /// Lightning +4, from Cold and from Fire +3 against their element; Prayer +1
    /// when its priest is on its side, −1 when not; +4 against a small spell from
    /// a caster who can't see it; Brave +4 against flag 0x100 (spends a charge);
    /// a druid +2 against fire and electricity; a psionicist +2 against mind and
    /// charm effects; Dexterity's adjustment against flag 0x40 and Wisdom's
    /// magical defence against flags 0x1E; on the paralysation save Constitution's
    /// poison adjustment, and a dwarf's or halfling's Con × 2 ÷ 7. No save
    /// against Chaos without a warrior's levels, nor against Cloudkill below
    /// level 7, and Scare can't miss below level 6 and can't land from 6
    /// (<see cref="SpellRules.SaveRules"/>); Dismissal adds the target's level
    /// less the caster's.
    /// </summary>
    public int SaveBonus(Combatant t, SpellEffect e, Combatant? caster, int level, int save)
    {
        foreach (SaveRule r in Spells.SaveRules.Where(r => r.Effect == e.Number))
        {
            SaveOutcome outcome = r.UnlessClasses is { } classes && !classes.Any(code => t.LevelIn(code) > 0) ? SaveOutcome.Fails
                : t.Level < r.Level ? r.Below : r.From;
            if (outcome != SaveOutcome.Rolled)
            {
                return outcome == SaveOutcome.Fails ? -100 : 100;
            }
        }
        int b = ModifiersOf(t).Where(m => m.NotOnSave != save && (m.SaveAgainst == DamageElement.None || (e.Elements & m.SaveAgainst) != 0))
            .Sum(m => m.Save);
        if (t.Has(Condition.ProtectionFromEvil) && caster is { Alignment: 3 or 6 or 9 })
        {
            b += 2;
        }
        if (t.Effects.FirstOrDefault(x => x.Condition == Condition.Prayer) is { Caster: { } priest })
        {
            b += priest.Side == t.Side ? 1 : -1;
        }
        if (caster is not null && Spells.AreaOf(e) < 9 && CantSee(caster, t))
        {
            b += 4;
        }
        if (((e.Flags & SpellFlags.BraveSave) != 0 || Rules.Fixes.BraveAgainstFear && (e.Flags & SpellFlags.Fear) != 0) && t.Has(Condition.Brave))
        {
            b += 4;
            UseCharge(t, Condition.Brave);
        }
        b += Rules.ClassSaveBonuses
            .Where(k => ((e.Elements & k.Elements) != 0 || (e.Flags & k.Flags) != 0) && k.Classes.Any(code => t.LevelIn(code) > 0))
            .Sum(k => k.Bonus);
        if ((e.Flags & SpellFlags.DexteritySave) != 0 || Rules.Fixes.DexterityAgainstAreas && !e.SingleTarget && Spells.TemplateOf(e) is not null)
        {
            b -= Abilities.DexDefenceOf(t.Stats[1]);
        }
        if ((e.Flags & SpellFlags.WisdomSave) != 0)
        {
            b += Abilities.WisDefenceOf(t.Stats[4]);
        }
        if (save == 1)
        {
            b += Abilities.ConPoisonOf(t.Stats[2]) + (t.Race is 2 or 6 ? t.Stats[2] * 2 / 7 : 0);
        }
        if (e.Number == Dismissal)
        {
            b += t.Level - level;
        }
        return b;
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
    /// by what prevents it (Free Action and Protection from Paralysis against
    /// Slowed, and against Paralyzed unless it comes with poison; Free Action and
    /// ability bit 9 against Stuck) or cancelling its opposite instead (Bless and
    /// Curse, Haste and Slow, Brave and Afraid). A timed condition replaces the
    /// same one; charges (unit −1) are counted, not timed. One from a field with
    /// no time of its own lasts while its holder stays in it. Paralyzed ends the
    /// holder's turn, Paralyzed and Stuck stop movement, Can't Attack stops
    /// attacks; Charmed takes it to the caster's side.
    /// </summary>
    public bool AddCondition(Combatant? caster, Combatant t, SpellEffect e, int level, int value = 0, AreaField? field = null) =>
        AddCondition(caster, t, e.Condition, e.Number, level, Duration(e, level), e.UsesCharges, value, field);

    private bool AddCondition(Combatant? caster, Combatant t, Condition c, int number, int level, int duration, bool charges = false,
        int value = 0, AreaField? field = null)
    {
        bool freed = t.Has(Condition.FreeAction) || t.Has(Condition.ProtectionFromParalysis);
        if (c == Condition.Paralyzed && !t.Has(Condition.Poisoned) && freed || c == Condition.Slowed && freed
            || c == Condition.Stuck && (t.Has(Condition.FreeAction) || Creatures.HasAbility(t.Type, CreatureAbilityBit.NotStuck)))
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
        if (duration == 0 && field is null)
        {
            return false;
        }
        if (!charges)
        {
            EndCondition(t, c);
        }
        var effect = new ActiveEffect(number, c, level, caster) { Value = value, Field = field };
        if (duration < 0 && duration != SpellEffect.Permanent)
        {
            effect.Charges = -duration;
        }
        else if (duration > 0)
        {
            effect.EndsAt = Clock + duration;
        }
        t.Effects.Add(effect);
        switch (c)
        {
            case Condition.Paralyzed:
                t.Flags = TurnFlags.None;
                t.Guarding = false;
                break;
            case Condition.Stuck:
                t.Flags &= ~TurnFlags.MayMove;
                break;
            case Condition.CantAttack:
                t.Flags &= ~TurnFlags.MayAttack;
                t.Guarding = false;
                break;
            case Condition.Charmed:
                t.Side = caster?.Side ?? 1;
                t.Target = null;
                Note(new SideChanged(t, t.Side));
                break;
            case Condition.ExtraHitPoints:
                t.Vitals.MaxHp += value;
                t.Vitals.Hp += value;
                break;
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

    /// <summary>
    /// An effect ending (<c>8053:066F</c>): a charm's end takes its holder back to
    /// its own side; Lent Strength's end ends the Strength it lent; extra hit
    /// points go; a conjured weapon goes. Berserk's and Confused's random side
    /// stays, as in the original, unless <see cref="CombatFixes.BerserkSideEnds"/>.
    /// </summary>
    private void EndEffect(Combatant t, ActiveEffect e)
    {
        if (!t.Effects.Remove(e))
        {
            return;
        }
        Note(new ConditionEnded(t, e.Condition));
        switch (e.Condition)
        {
            case Condition.Charmed or Condition.Berserk or Condition.Confused
                when (e.Condition == Condition.Charmed || Rules.Fixes.BerserkSideEnds)
                    && !t.Has(Condition.Charmed) && !t.Has(Condition.Berserk) && !t.Has(Condition.Confused) && t.Side != t.HomeSide:
                t.Side = t.HomeSide;
                t.Target = null;
                Note(new SideChanged(t, t.Side));
                break;
            case Condition.StrengthLent:
                foreach (Combatant other in _all)
                {
                    if (other.Effects.FirstOrDefault(b => b.Condition == Condition.StrengthBorrowed && b.Caster == t) is { } borrowed)
                    {
                        EndEffect(other, borrowed);
                    }
                }
                break;
            case Condition.ExtraHitPoints:
                t.Vitals.MaxHp = Math.Max(1, t.Vitals.MaxHp - e.Value);
                t.Vitals.Hp = Math.Min(t.Vitals.Hp, t.Vitals.MaxHp);
                break;
            case Condition.FlameBlade or Condition.SpiritualHammer or Condition.Shillelagh:
                if (t.Items.FirstOrDefault(i => i.Rdff == ConjuredItem(e.Effect)) is { } conjured)
                {
                    t.Items.Remove(conjured);
                    if (t.Weapon?.Item == conjured)
                    {
                        t.Weapon = null;
                    }
                }
                break;
        }
    }

    /// <summary>
    /// The item Shillelagh, Flame Blade and Spiritual Hammer put in the caster's
    /// hand (<c>80F5:0E2E</c>, <c>7372:0D93</c>): item <c>RDFF</c> 30024 (a 2d4
    /// club), 30025 (1d4 + 4, fire, +1), 30026 (+1, 1d4 + 1, two attacks).
    /// </summary>
    public static int ConjuredItem(int effect) => effect switch { 80 => 30024, 86 => 30025, 90 => 30026, _ => 0 };

    /// <summary>
    /// The creatures (<c>OJFF</c>) a summoning brings, one picked at random
    /// (<c>80F5:2687</c>: a switch on the record index, <c>80F5:2853</c>, rolling 1d3 or 1d4 where
    /// there are several): Monster Summoning I, II and III three each, Conjure Elemental the four
    /// lesser elementals (air, water, fire, earth), the others one.
    /// </summary>
    public static IReadOnlyList<int> SummonedBy(int effect) => effect switch
    {
        36 => [269, 150, 279],
        43 => [286], // Evard's Black Tentacles
        50 => [152, 167, 166],
        60 => [176, 175, 173, 164],
        66 => [53, 156, 160],
        67 => [153], // Summon Shadow
        84 => [239], // Dust Devil
        91 => [176], // Conjure Lesser Air Elemental … Water
        92 => [173],
        93 => [164],
        94 => [175],
        107 => [170], // Summon Insects
        124 => [177], // Conjure Air Elemental … Water
        125 => [172],
        126 => [165],
        127 => [174],
        _ => [],
    };

    /// <summary>
    /// Puts a summoned creature (<c>OJFF</c>) on a free cell and makes its combatant;
    /// without it, or when it gives none, the summoning goes unanswered.
    /// </summary>
    public Func<int, Cell, Combatant?>? MakeCreature { get; init; }

    /// <summary>
    /// A summoning (<c>80F5:2687</c>): the creature appears on the free cell nearest the point
    /// aimed at (<c>7372:0909</c>) and fights at once. DSUN.EXE puts it in object slot 4 on the
    /// party's side (allegiance 1) whoever casts; here it takes its caster's side. Nothing in that
    /// code ends it: it stays until it falls.
    /// </summary>
    private void Summon(Combatant caster, SpellEffect e, Cell at)
    {
        IReadOnlyList<int> kinds = SummonedBy(e.Number);
        if (kinds.Count == 0 || NearestFree(at) is not { } cell || MakeCreature?.Invoke(kinds[Roll(0, kinds.Count - 1)], cell) is not { } creature)
        {
            Note(new Avoided(caster, caster, "Your summoning goes unanswered"));
            return;
        }
        creature.Side = creature.HomeSide = caster.Side & 3;
        creature.ComputerControlled = true;
        Add(creature);
        Note(new Summoned(caster, creature));
    }

    /// <summary>The free cell nearest <paramref name="at"/>, within 8 cells (<c>7372:0909</c>'s search isn't traced).</summary>
    private Cell? NearestFree(Cell at)
    {
        for (int r = 0; r <= 8; r++)
        {
            Cell? best = null;
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    var c = new Cell(at.X + dx, at.Y + dy);
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == r && Free(c) && (best is null || Distance(c, at) < Distance(best.Value, at)))
                    {
                        best = c;
                    }
                }
            }
            if (best is not null)
            {
                return best;
            }
        }
        return null;
    }

    /// <summary>
    /// Makes an item from its <c>RDFF</c> number (<c>GameWorld.NewItem</c>), for
    /// the conjured weapons; without it they give only their condition.
    /// </summary>
    public Func<int, Item?>? NewItem { get; init; }

    /// <summary>
    /// A combatant's effects each round (<c>8053:0163</c>): regeneration (ability
    /// bit 17, up to its most hit points), a Poisoned charge (1000 damage when the
    /// last goes), an Acid charge with 2d4 damage, and Protection from Evil 10'
    /// Radius giving Protection from Evil (effect 122) to its side within 40
    /// sixteenths.
    /// </summary>
    private void RoundEffects(Combatant c)
    {
        if (Creatures.HasAbility(c.Type, CreatureAbilityBit.Regenerates) && c.Vitals.Hp < c.Vitals.MaxHp)
        {
            Heal(c, Rules.Regeneration);
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
        if (c.Alive && c.Has(Condition.ProtectionFromEvil10) && Spells.Effect(ProtectionFromEvil10) is { } aura)
        {
            var (x, y) = Centre(c.Cell);
            foreach (Combatant ally in _all.Where(a => a.Alive && a != c && (a.Side & c.Side & 7) != 0
                && Distance16(x, y, Centre(a.Cell).X, Centre(a.Cell).Y) <= 40).ToList())
            {
                AddCondition(c, ally, Condition.ProtectionFromEvil, aura.Number, c.Level, SpellEffect.Round);
            }
        }
    }

    public const int ProtectionFromEvil10 = 122;

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
            c.Target = null;
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

    /// <summary>Hit points back, up to its most (<c>80F5:307D</c>).</summary>
    public void Heal(Combatant t, int amount)
    {
        amount = Math.Min(amount, t.Vitals.MaxHp - t.Vitals.Hp);
        if (amount > 0 && t.Alive)
        {
            t.Vitals.Hp += amount;
            Note(new Healed(t, amount));
        }
    }

    // -- effects' own handling (80F5:05E6) ----------------------------------------------------------------

    /// <summary>
    /// What a record alone doesn't say (<c>80F5:05E6</c>, by effect): the cures;
    /// Dispel Magic (each effect, 50 in 100 + 5 a level the caster has over its
    /// caster); effect 195; Haste and Slow at once on this round's movement;
    /// Strength and Adrenalin Control (+1d6 Strength), Share Strength, Remove
    /// Curse (Strength back), the shadows' touch, Chill Touch and effect 184
    /// (Weakened); Aid (1d8 extra hit points); Vampiric Touch and Life Draining
    /// (hit points drained to the caster); Cell Adjustment, Lend Health, Blood
    /// Flow; Absorb Disease; Free Action, Remove Paralysis, Protection from
    /// Paralysis; Glitterdust (ends invisibility); Insect Plague (no spells for a
    /// round); Psionic Blast (to a fifth of its hit points); Slay Living and
    /// Disintegrate; Dismissal and Abjure; Turn Undead; Raise Dead; Detonate and
    /// the corroding touches.
    /// </summary>
    private void Special(Combatant caster, Combatant t, SpellEffect e, int level, bool saved)
    {
        int duration = Duration(e, level);
        switch (e.Number)
        {
            case var n when Spells.Cures.FirstOrDefault(c => c.Effect == n) is { } cure:
                Heal(t, RollDice(cure.Dice, cure.Sides) + cure.Bonus);
                break;
            case var n when Spells.Dispels.Contains(n):
                foreach (ActiveEffect a in t.Effects.Where(a => !Spells.Undispellable.Contains(a.Condition)).ToList())
                {
                    if (Roll(1, 100) <= Rules.DispelChance + 5 * level - 5 * a.Level)
                    {
                        EndEffect(t, a);
                    }
                }
                break;
            case var n when Spells.AfflictionCures.Contains(n):
                foreach (ActiveEffect a in t.Effects.Where(a => Spells.Afflictions.Contains(a.Condition)).ToList())
                {
                    EndEffect(t, a);
                }
                break;
            case 30 or 38 when t.Has(e.Condition):
                t.MovementPoints = e.Condition == Condition.Hasted
                    ? Math.Min(t.MovementPoints * 2, t.Move * Rules.MovementPerMove * 2)
                    : t.MovementPoints / 2;
                break;
            case 133:
                AddCondition(caster, t, Condition.NoSpells, e.Number, level, SpellEffect.Round);
                break;
            case 21 or 106 or 118:
                EndCondition(t, Condition.Slowed);
                if (!t.Has(Condition.Poisoned))
                {
                    EndCondition(t, Condition.Paralyzed);
                }
                break;
            case 17:
                EndCondition(t, Condition.Invisible);
                break;
            case 148:
                // Absorb Disease takes the disease, then (falling through, as the
                // original does) gives Strength's +1d6 as well.
                if (t.Has(Condition.Diseased))
                {
                    AddCondition(t, caster, Condition.Diseased, e.Number, level, duration);
                    EndCondition(t, Condition.Diseased);
                }
                if (!Rules.Fixes.NoStrengthFromAbsorbDisease)
                {
                    AddCondition(caster, t, Condition.Strength, e.Number, level, duration, value: RollDice(1, 6));
                }
                break;
            case 24 or 149:
                AddCondition(caster, t, e.Number == 149 ? Condition.AdrenalinControl : Condition.Strength, e.Number, level, duration,
                    value: RollDice(1, 6));
                break;
            case 158:
                int lent = Math.Min(24 - t.Strength, (caster.Strength - 3) / 2);
                if (lent > 0)
                {
                    AddCondition(caster, t, Condition.StrengthBorrowed, e.Number, level, duration, value: lent);
                    AddCondition(caster, caster, Condition.StrengthLent, e.Number, level, duration, value: -2 * lent);
                }
                break;
            case 104:
                EndCondition(t, Condition.Weakened);
                break;
            case 173:
                AddCondition(caster, t, Condition.Weakened, e.Number, level, duration, value: -1);
                break;
            case 157:
                int lend = Math.Min(t.Vitals.MaxHp - t.Vitals.Hp, caster.Vitals.Hp - 5);
                if (lend > 0)
                {
                    Heal(t, lend);
                    caster.Vitals.Hp -= lend;
                }
                break;
            case 109:
                if (t.Side == caster.Side)
                {
                    Heal(t, RollDice(2, 6));
                }
                else if (!saved)
                {
                    AddCondition(caster, t, Condition.Paralyzed, e.Number, level, duration);
                }
                break;
            case 81:
                AddCondition(caster, t, Condition.ExtraHitPoints, e.Number, level, 600, value: RollDice(1, 8));
                break;
            case VampiricTouch or 147:
                int drained = Math.Min(t.Vitals.Hp, e.Number == VampiricTouch ? RollDice(Math.Max(1, level / 2), 6) : RollDice(1, 6));
                Hurt(caster, t, drained);
                AddCondition(caster, caster, Condition.ExtraHitPoints, e.Number, level, 600, value: drained);
                break;
            case 170 when !saved:
                Hurt(caster, t, t.Vitals.Hp - t.Vitals.MaxHp * 2 / 10);
                break;
            case 80 or 86 or 90:
                Conjure(caster, e, level, duration);
                break;
            case Dismissal or 108 when t.Race == 14 && Roll(1, 20) >= 11 - level + t.Level:
                Hurt(caster, t, 1000);
                break;
            case TurnUndead when t.IsUndead && Roll(1, 20) >= TurnNeeded(t.Level, level):
                // A failed turn does nothing: its call (1A0A:0251) is an empty function.
                Hurt(caster, t, 1000);
                break;
            case RaiseDead when t.Vitals.Status == Vitals.Dead:
                if (t.Stats[2] > 0)
                {
                    t.Stats[2]--;
                    t.Vitals.Status = Vitals.Well;
                    t.Vitals.Hp = 1;
                    Note(new Healed(t, 1));
                }
                else
                {
                    t.Vitals.Status = Vitals.Destroyed;
                }
                break;
            case 139:
                if (Spells.Effect(DetonateShrapnel) is { } shrapnel)
                {
                    Apply(caster, t, shrapnel, level);
                }
                BreakItem(t);
                break;
            case 179 or 187:
                CorrodeArmor(t);
                break;
            case 188:
                CorrodeWeapon(t);
                break;
        }
        if (saved || !t.Alive)
        {
            return;
        }
        switch (e.Number)
        {
            case 4:
                AddCondition(caster, t, Condition.Weakened, e.Number, level, duration, value: -1);
                break;
            case 184:
                AddCondition(caster, t, Condition.Weakened, e.Number, level, duration, value: -5);
                break;
            case 137:
                Hurt(caster, t, 1000);
                break;
            case Disintegrate:
                Hurt(caster, t, 10000);
                break;
        }
    }

    public const int TurnUndead = 191, DetonateShrapnel = 196;

    /// <summary>
    /// Shillelagh, Flame Blade, Spiritual Hammer (<c>80F5:05E6</c>, <c>80F5:0E2E</c>):
    /// with the hand free, the weapon (<see cref="ConjuredItem"/>) goes into it
    /// and the condition marks it; it goes when the condition ends. A hand
    /// holding something: "Failed: weapon in hand".
    /// </summary>
    private void Conjure(Combatant caster, SpellEffect e, int level, int duration)
    {
        if (caster.Weapon is not null)
        {
            Note(new Avoided(caster, caster, "Failed: weapon in hand"));
            return;
        }
        if (NewItem?.Invoke(ConjuredItem(e.Number)) is { } item && Catalog?.TypeOf(item.Record) is { IsWeapon: true } type)
        {
            item.Slot = 3;
            caster.Items.Add(item);
            caster.Weapon = new Weapon(type, item.Record.Bonus, item);
        }
        AddCondition(caster, caster, e.Number switch { 80 => Condition.Shillelagh, 86 => Condition.FlameBlade, _ => Condition.SpiritualHammer },
            e.Number, level, duration);
    }

    /// <summary>
    /// What a d20 must reach for Turn Undead to destroy an undead
    /// (<c>80F5:2217</c>): (its level, +1 above 4, at most 13, less the priest's,
    /// at most 14, −1 above 10, −2 above 12) × 3 + 10, 22 counting as 20.
    /// </summary>
    public static int TurnNeeded(int undeadLevel, int priestLevel)
    {
        int u = undeadLevel > 4 ? Math.Min(undeadLevel + 1, 13) : undeadLevel;
        int p = Math.Min(priestLevel, 14);
        p = p > 12 ? p - 2 : p > 10 ? p - 1 : p;
        int needed = (u - p) * 3 + 10;
        return needed == 22 ? 20 : needed;
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
