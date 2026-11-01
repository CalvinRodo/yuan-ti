using DarkSun.Game.Characters;
using DarkSun.Game.Combat;
using DarkSun.Game.Magic;
using DarkSun.Game.Movement;
using DarkSun.Game.World;
using static DarkSun.Game.Tests.CombatTests;

namespace DarkSun.Game.Tests;

/// <summary>Tests that change the spell catalog (<see cref="Spellcasting.UseAdded"/>) run alone.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SpellCatalogCollection
{
    public const string Name = "Spell catalog";
}

/// <summary>The bundled Defiling and Sweet Water mods: ash on the battlefield, and the spell that brings it back.</summary>
[Collection(SpellCatalogCollection.Name)]
public class DefilingTests
{
    private const int Defiler = 18, Preserver = 11, WaterCleric = 4;
    private const int MagicMissile = 9, LightningBolt = 33, IceStorm = 46, CureLightWounds = 72, Barkskin = 82, SweetWater = 197;

    private static readonly CombatRules Defiling = CombatRules.Default with { Defiling = new DefilingRules { On = true } };

    /// <summary>The Sweet Water mod's spells: the original's with spell 197 added.</summary>
    private static SpellRules SweetWaterSpells => SpellRules.Read(SpellcastingTests.BundledModFile("SweetWater", Path.Combine("rules", "spells.json")));

    private static Combatant Caster(string name, int side, int code, int x, int y) => new()
    {
        Name = name,
        Side = side,
        PartyMember = side == 1,
        Cell = new Cell(x, y),
        Vitals = new Vitals { Hp = 30, MaxHp = 30 },
        Stats = [10, 10, 10, 10, 10, 10],
        ArmorClass = 5,
        Mind = 2,
        Thac0 = 20,
        Level = 6,
        Classes = [(code, 6)],
        Facing = side == 1 ? 2 : 6,
        ComputerControlled = side != 1,
    };

    private static Battle Started(ICombatDice dice, CombatRules? rules, SpellRules? spells, params Combatant[] all)
    {
        var b = new Battle(all, new OpenField(30, 30), dice, rules, spells);
        b.StartRound();
        return b;
    }

    // -- defiling ----------------------------------------------------------------------------------------------

    [Fact]
    public void ADefilersWizardSpellTurnsTheGroundAroundItToAsh()
    {
        Combatant defiler = Caster("defiler", 2, Defiler, 10, 10), foe = Fighter("foe", 1, 15, 10, hp: 500);
        Battle b = Started(new ScriptedDice(), Defiling, null, defiler, foe);

        Assert.True(b.Cast(defiler, MagicMissile, 6, foe.Cell));
        Assert.Equal(9, b.Ash.Count); // a 1st-level spell: the cells around the caster
        Assert.Contains(new Cell(9, 9), b.Ash);
        Assert.Equal(9, Assert.Single(b.Log.OfType<Defiled>()).Cells.Count);

        Assert.True(b.Cast(defiler, IceStorm, 6, foe.Cell));
        Assert.Equal(25, b.Ash.Count); // a 4th-level spell: two cells around
        Assert.Equal(16, b.Log.OfType<Defiled>().Last().Cells.Count); // only the new ash
    }

    [Fact]
    public void PreserversItemsPriestSpellsAndTheOriginalLeaveNoAsh()
    {
        Combatant defiler = Caster("defiler", 2, Defiler, 10, 10), preserver = Caster("preserver", 2, Preserver, 20, 20);
        Combatant foe = Fighter("foe", 1, 15, 10, hp: 500);

        Battle original = Started(new ScriptedDice(), null, null, defiler, foe);
        original.Cast(defiler, MagicMissile, 6, foe.Cell);
        Assert.Empty(original.Ash);

        Battle b = Started(new ScriptedDice(), Defiling, null, defiler, preserver, foe);
        b.Cast(preserver, MagicMissile, 6, foe.Cell);
        b.Cast(defiler, MagicMissile, 6, foe.Cell, ownSpell: false); // from an item
        b.Cast(defiler, CureLightWounds, 6, defiler.Cell);
        Assert.Empty(b.Ash);
        Assert.Empty(b.Log.OfType<Defiled>());
    }

    [Fact]
    public void CastingAgainFromDrainedAshGivesTheTargetsABetterSave()
    {
        // d20 5, doubled against electricity; Lightning Bolt is a 3rd-level spell, its line (± 5 cells) clear of the caster.
        Combatant defiler = Caster("defiler", 2, Defiler, 10, 10), foe = Fighter("foe", 1, 16, 10, hp: 500);
        Battle b = Started(new ScriptedDice((l, h) => h == 20 ? 5 : l), Defiling, null, defiler, foe);

        b.Cast(defiler, LightningBolt, 6, foe.Cell);
        b.Cast(defiler, LightningBolt, 6, foe.Cell);

        int[] totals = [.. b.Log.OfType<SaveRolled>().Where(s => s.Target == foe).Select(s => s.Total)];
        Assert.Equal(2, totals.Length);
        Assert.Equal(3, totals[1] - totals[0]);
    }

    [Fact]
    public void NothingGrowsOnAsh()
    {
        Combatant defiler = Caster("defiler", 2, Defiler, 10, 10), druid = Caster("druid", 1, 8, 20, 10);
        Combatant onAsh = Fighter("onAsh", 1, 11, 10), clear = Fighter("clear", 1, 21, 10);
        Battle b = Started(new ScriptedDice(), Defiling, null, defiler, druid, onAsh, clear);
        b.Cast(defiler, MagicMissile, 6, druid.Cell);

        b.Apply(druid, onAsh, SpellRules.Default.Effect(Barkskin)!, 6);
        b.Apply(druid, clear, SpellRules.Default.Effect(Barkskin)!, 6);

        Assert.False(onAsh.Has(Condition.Barkskin));
        Assert.Contains(b.Log.OfType<Avoided>(), a => a.Target == onAsh && a.Why == "nothing grows");
        Assert.True(clear.Has(Condition.Barkskin));
    }

    // -- Sweet Water ----------------------------------------------------------------------------------------------

    [Fact]
    public void SweetWaterHealsAndEndsPoisonAndDisease()
    {
        Combatant priest = Caster("priest", 1, WaterCleric, 10, 10), friend = Fighter("friend", 1, 11, 10, party: true);
        friend.Vitals.Hp = 10;
        friend.Effects.Add(new ActiveEffect(120, Condition.Poisoned, 5, null) { EndsAt = 9999 });
        friend.Effects.Add(new ActiveEffect(98, Condition.Diseased, 5, null) { EndsAt = 9999 });
        Battle b = Started(new ScriptedDice((_, h) => h), null, SweetWaterSpells, priest, friend);

        Assert.True(b.Cast(priest, SweetWater, 6, friend.Cell));

        Assert.Equal(10 + 4 + 3, friend.Vitals.Hp); // 1d4, and 1 per two of the priest's 6 levels
        Assert.False(friend.Has(Condition.Poisoned));
        Assert.False(friend.Has(Condition.Diseased));
    }

    [Fact]
    public void SweetWaterCastAtAshBringsTheGroundBack()
    {
        Combatant defiler = Caster("defiler", 2, Defiler, 10, 10), priest = Caster("priest", 1, WaterCleric, 12, 10);
        Combatant foe = Fighter("foe", 1, 20, 20, hp: 500);
        Battle b = Started(new ScriptedDice(), Defiling, SweetWaterSpells, defiler, priest, foe);
        b.Cast(defiler, MagicMissile, 6, foe.Cell); // not at the priest: a wound would spoil its casting this round
        SpellEffect sweetWater = b.Spells.Effect(SweetWater)!;

        Assert.Equal(SpellAim.Ok, b.Aim(priest, sweetWater, 6, new Cell(11, 10))); // ash, no one on it
        Assert.Equal(SpellAim.NoTarget, b.Aim(priest, sweetWater, 6, new Cell(13, 11))); // bare ground
        Assert.True(b.Cast(priest, SweetWater, 6, new Cell(11, 10)));

        // A cell around (11, 10): columns 10 and 11 of the ash come back, column 9 stays.
        Assert.Equal(6, Assert.Single(b.Log.OfType<AshRestored>()).Cells.Count);
        Assert.Equal([new Cell(9, 9), new Cell(9, 10), new Cell(9, 11)], b.Ash.OrderBy(c => c.Y));
    }

    // -- the mod's spell ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheModsFileAddsSweetWaterAndKeepsTheOriginals()
    {
        SpellRules s = SweetWaterSpells;

        Assert.Equal(197, s.Effects.Count);
        Assert.Equal(SpellRules.Default.Effects, s.Effects.Take(196));
        Assert.Equal(SpellRules.Default.Looks.Count + 1, s.Looks.Count);
        Assert.Equal(SpellRules.Default.Cures, s.Cures.Take(4));
        Assert.Equal([Condition.Poisoned, Condition.Diseased], s.Cures[^1].Ends);
        Assert.Equal(new AshRestorer(SweetWater, 1), Assert.Single(s.AshRestorers));
        Assert.Equal(SpellRules.Default.Powers, s.Powers);
    }

    [Fact]
    public void ASpellsFileNeedOnlyHoldWhatItChanges()
    {
        string path = Path.Combine(Path.GetTempPath(), $"darksun-spells-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "Effects": [{ "Number": 9, "RangeBase": 99 }] }""");
            SpellRules changed = SpellRules.Read(path);
            Assert.Equal(SpellRules.Default.Effect(9)! with { RangeBase = 99 }, changed.Effect(9));
            Assert.Equal(SpellRules.Default.Effects.Skip(9), changed.Effects.Skip(9));
            Assert.Equal(SpellRules.Default.Templates, changed.Templates);

            File.WriteAllText(path, """{ "Cures": [{ "Effect": 72, "Dice": 1, "Sides": 8, "Bonus": 0 }, { "Effect": 72, "Dice": 2, "Sides": 8, "Bonus": 0 }] }""");
            Assert.Throws<InvalidDataException>(() => SpellRules.Read(path)); // listed twice
            File.WriteAllText(path, """{ "Spells": [{ "Number": 198, "Level": 1, "Kind": "Priest", "Casters": [4], "Icon": 21071 }] }""");
            Assert.Throws<InvalidDataException>(() => SpellRules.Read(path)); // no effect record
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnAddedSpellIsKnownAndCastLikeTheOriginals()
    {
        Spellcasting.UseAdded(SweetWaterSpells.Spells.Select(s => s.ToSpell()));
        try
        {
            Spell sweetWater = Spellcasting.Get(SweetWater)!;
            Assert.Equal((2, SpellKind.Priest, 21071), (sweetWater.Level, sweetWater.Kind, sweetWater.Icon)); // Cure Light Wounds' icon
            Assert.True(Spellcasting.IsSpell(SweetWater));
            Assert.Null(Spellcasting.Get(196)); // a creature's effect, still not a spell

            var magic = new Spellcasting(CharacterRules.Default);
            Character water = new() { Classes = [CharacterClass.Cleric], Sphere = Sphere.Water, Rolled = [12, 12, 12, 12, 12, 12], Experience = 3000 };
            Character fire = water with { Sphere = Sphere.Fire };
            Assert.Contains(sweetWater, magic.Known(water, SpellKind.Priest, 2));
            Assert.DoesNotContain(sweetWater, magic.Known(fire, SpellKind.Priest, 2));
            Assert.True(magic.CanCast(water, SweetWater));
        }
        finally
        {
            Spellcasting.UseAdded([]);
        }
        Assert.Null(Spellcasting.Get(SweetWater));
        Assert.Equal(Spellcasting.Original, Spellcasting.All);
    }
}
