using DarkSun.Game.Characters;
using DarkSun.Game.Combat;
using DarkSun.Game.Magic;
using DarkSun.Game.Movement;
using static DarkSun.Game.Tests.CombatTests;

namespace DarkSun.Game.Tests;

/// <summary>The bundled mods' new spells and powers (issues #137–#141), and the mods' spell files read together.</summary>
[Collection(SpellCatalogCollection.Name)]
public class EffectSpellsTests
{
    private const int CeruleanHail = 198, DimensionDoor = 199, MolecularAgitation = 200, GustOfWind = 201, SpikeStones = 202;
    private const int StinkingCloud = 23;

    private static readonly string[] Mods = ["SweetWater", "CeruleanHail", "DimensionDoor", "MolecularAgitation", "GustOfWind", "SpikeStones"];

    private static string SpellsOf(string mod) => SpellcastingTests.BundledModFile(mod, Path.Combine("rules", "spells.json"));

    private static SpellRules Rules(string mod) => SpellRules.Read(SpellsOf(mod));

    private static Battle Started(ICombatDice dice, SpellRules spells, params Combatant[] all)
    {
        var b = new Battle(all, new OpenField(30, 30), dice, null, spells);
        b.StartRound();
        return b;
    }

    // -- the mods' files together ----------------------------------------------------------------------------

    [Fact]
    public void EveryModsSpellsFileAddsToTheOthers()
    {
        SpellRules all = SpellRules.Read([.. Mods.Select(SpellsOf)]);

        Assert.Equal(202, all.Effects.Count);
        Assert.Equal(SpellRules.Default.Effects, all.Effects.Take(196));
        Assert.Equal([197, 198, 199, 200, 201, 202], all.Spells.Select(s => s.Number));
        Assert.Equal(47, all.Templates.Count);
        Assert.Equal(SpellRules.Default.Templates, all.Templates.Take(45));
        Assert.Equal((AreaTemplate.Burst, AreaTemplate.Line, 201), (all.Templates[45].Kind, all.Templates[45].Shape, all.Templates[45].Effect));
        Assert.Equal((AreaTemplate.Ground, AreaTemplate.Circle, 202), (all.Templates[46].Kind, all.Templates[46].Shape, all.Templates[46].Effect));
        Assert.Equal([199, 200], all.Powers.Skip(34).Select(p => p.Number));
        Assert.Single(all.AshRestorers);
    }

    [Fact]
    public void AModsTemplatePastTheEndLeavesUnusedOnesBetween()
    {
        SpellRules spikes = Rules("SpikeStones");

        Assert.Equal(47, spikes.Templates.Count);
        Assert.Equal(new AreaTemplate(AreaTemplate.Burst, AreaTemplate.Line), spikes.Templates[45]);
        Assert.Equal(spikes.Templates[46], spikes.TemplateOf(spikes.Effect(SpikeStones)!));
    }

    [Fact]
    public void TheFirstModsEntryWins()
    {
        string path = Path.Combine(Path.GetTempPath(), $"darksun-spells-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "Effects": [{ "Number": 198, "Sides": 6 }], "Templates": [{ "Index": 3, "Graphic": 1 }] }""");
            SpellRules first = SpellRules.Read(path, SpellsOf("CeruleanHail"));
            Assert.Equal(Rules("CeruleanHail").Effect(CeruleanHail)! with { Sides = 6 }, first.Effect(CeruleanHail));
            Assert.Equal(SpellRules.Default.Templates[3] with { Graphic = 1 }, first.Templates[3]);

            Assert.Equal(4, SpellRules.Read(SpellsOf("CeruleanHail"), path).Effect(CeruleanHail)!.Sides);

            File.WriteAllText(path, """{ "Templates": [{ "Index": 3, "Graphic": 1 }, { "Kind": 1, "Shape": 0 }] }""");
            Assert.Throws<InvalidDataException>(() => SpellRules.Read(path)); // some with an index, some without
            File.WriteAllText(path, """{ "Effects": [{ "Number": 9, "Template": 99 }] }""");
            Assert.Throws<InvalidDataException>(() => SpellRules.Read(path)); // no such template
        }
        finally
        {
            File.Delete(path);
        }
    }

    // -- Cerulean Hail (#137) ------------------------------------------------------------------------------------

    [Fact]
    public void CeruleanHailHurtsByLevelAndSlowsThoseWhoFail()
    {
        (Battle, Combatant) HailOn(int d20)
        {
            Combatant mage = Fighter("mage", 2, 10, 10), t = Fighter("t", 1, 16, 10, hp: 500), near = Fighter("near", 1, 17, 10, hp: 500);
            // Every d4 a 4; the d20 doubled for cold against 16.
            Battle b = Started(new ScriptedDice((l, h) => h == 20 ? d20 : h), Rules("CeruleanHail"), mage, t, near);
            Assert.True(b.Cast(mage, CeruleanHail, 6, t.Cell));
            Assert.Equal(t.Vitals.Hp, near.Vitals.Hp); // a cell away, in the circle too
            return (b, t);
        }

        var (_, saved) = HailOn(10);
        Assert.Equal(500 - 12, saved.Vitals.Hp); // half
        Assert.False(saved.Has(Condition.Slowed));

        var (b, failed) = HailOn(2);
        Assert.Equal(500 - 24, failed.Vitals.Hp); // 6d4 at level 6
        Assert.True(failed.Has(Condition.Slowed));

        b.StartRound();
        Assert.True(failed.Has(Condition.Slowed)); // through the next round
        Assert.Equal(failed.Move * b.Rules.MovementPerMove / 2, failed.MovementPoints);
        b.StartRound();
        Assert.False(failed.Has(Condition.Slowed));
    }

    // -- Dimension Door (#138) ------------------------------------------------------------------------------------

    [Fact]
    public void DimensionDoorTakesThePsionicistToAFreeCellWithoutAGuardsBlow()
    {
        Combatant psion = Fighter("psion", 1, 5, 10, party: true), guard = Fighter("guard", 2, 6, 10);
        psion.Psp = 50;
        Battle b = Started(new ScriptedDice(), Rules("DimensionDoor"), psion, guard);
        b.Guard(guard);
        SpellEffect door = b.Spells.Effect(DimensionDoor)!;

        Assert.Equal(SpellAim.Ok, b.Aim(psion, door, 5, new Cell(12, 10)));
        Assert.Equal(SpellAim.NoTarget, b.Aim(psion, door, 5, guard.Cell));
        Assert.Equal(SpellAim.OutOfRange, b.Aim(psion, door, 5, new Cell(20, 10)));

        Assert.True(b.UsePower(psion, DimensionDoor, new Cell(12, 10)));
        Assert.Equal(new Cell(12, 10), psion.Cell);
        Assert.Equal(new Teleported(psion, new Cell(5, 10), new Cell(12, 10)), Assert.Single(b.Log.OfType<Teleported>()));
        Assert.Equal(46, psion.Psp);
        Assert.Empty(b.Log.OfType<AttackRolled>());
        Assert.Empty(psion.Maintained);
    }

    // -- Molecular Agitation (#139) --------------------------------------------------------------------------------

    [Fact]
    public void MolecularAgitationBurnsEachRoundItIsKeptUpAndMoreThroughMetal()
    {
        Combatant psion = Fighter("psion", 1, 5, 10, party: true), armed = Fighter("armed", 2, 10, 10, hp: 100);
        armed.Weapon = Sword();
        psion.Psp = 9;
        // The power check's d20 a 1, every d6 a 6.
        Battle b = Started(new ScriptedDice((l, h) => h == 20 ? 1 : h), Rules("MolecularAgitation"), psion, armed);

        Assert.True(b.UsePower(psion, MolecularAgitation, armed.Cell));
        Assert.Equal(100 - 7, armed.Vitals.Hp); // 1d6, +1 for the metal sword
        Assert.True(armed.Has(Condition.Heated));
        Assert.Equal(3, psion.Psp);
        Assert.Contains(MolecularAgitation, psion.Maintained);

        b.StartRound(); // kept up: 2 points
        Assert.Equal(100 - 14, armed.Vitals.Hp);
        Assert.Equal(1, psion.Psp);

        b.StartRound(); // 1 point left, not enough: the heat ends
        Assert.False(armed.Has(Condition.Heated));
        Assert.Equal(100 - 14, armed.Vitals.Hp);
        Assert.Empty(psion.Maintained);
    }

    [Fact]
    public void MolecularAgitationEndsWithItsCaster()
    {
        Combatant psion = Fighter("psion", 1, 5, 10, party: true), t = Fighter("t", 2, 10, 10, hp: 100);
        psion.Psp = 50;
        Battle b = Started(new ScriptedDice((l, h) => h == 20 ? 1 : h), Rules("MolecularAgitation"), psion, t);
        b.UsePower(psion, MolecularAgitation, t.Cell);
        Assert.Equal(100 - 6, t.Vitals.Hp); // no metal

        b.Hurt(null, psion, 1000);
        b.StartRound();
        Assert.False(t.Has(Condition.Heated));
        Assert.Equal(100 - 6, t.Vitals.Hp);
    }

    // -- Gust of Wind (#140) ----------------------------------------------------------------------------------------

    [Fact]
    public void GustOfWindLeavesTheStrongAndCantBlowThroughThem()
    {
        Combatant priest = Fighter("priest", 1, 10, 10), weak = Fighter("weak", 2, 13, 10), strong = Fighter("strong", 2, 14, 10, str: 18);
        // Every d20 a 15: over Strength 10, not over 18.
        Battle b = Started(new ScriptedDice((l, h) => h == 20 ? 15 : l), Rules("GustOfWind"), priest, weak, strong);

        Assert.True(b.Cast(priest, GustOfWind, 6, new Cell(13, 10)));

        Assert.Contains(b.Log, e => e is Avoided { Why: "stands firm" } a && a.Target == strong);
        Assert.Contains(b.Log, e => e is Avoided { Why: "nowhere to go" } a && a.Target == weak);
        Assert.Equal((new Cell(10, 10), new Cell(13, 10), new Cell(14, 10)), (priest.Cell, weak.Cell, strong.Cell));
        Assert.Equal(-1, weak.Initiative); // buffeted all the same: the rest of its round is lost
        Assert.NotEqual(-1, strong.Initiative);
    }

    [Fact]
    public void GustOfWindPushesTwoCellsAndClearsTheCloudsItReaches()
    {
        Combatant priest = Fighter("priest", 1, 10, 10), mage = Fighter("mage", 1, 2, 2), foe = Fighter("foe", 2, 13, 10);
        Battle b = Started(new ScriptedDice((l, h) => h == 20 ? 15 : l), Rules("GustOfWind"), priest, mage, foe);
        b.Cast(mage, StinkingCloud, 6, new Cell(13, 12));
        Assert.Single(b.Fields);

        Assert.True(b.Cast(priest, GustOfWind, 6, new Cell(13, 10)));

        Assert.Equal(new Cell(15, 10), foe.Cell);
        Assert.Equal(new Pushed(foe, new Cell(13, 10), new Cell(15, 10)), Assert.Single(b.Log.OfType<Pushed>()));
        Assert.Equal(-1, foe.Initiative);
        Assert.Empty(b.Fields);
    }

    // -- Spike Stones (#141) ---------------------------------------------------------------------------------------

    [Fact]
    public void SpikeStonesCutEachCellWalked()
    {
        Combatant priest = Fighter("priest", 1, 2, 2), standing = Fighter("standing", 2, 10, 10), walker = Fighter("walker", 2, 8, 11);
        Battle b = Started(new ScriptedDice((l, h) => h), Rules("SpikeStones"), priest, standing, walker);

        Assert.True(b.Cast(priest, SpikeStones, 6, new Cell(10, 10)));
        AreaField field = Assert.Single(b.Fields);
        Assert.Equal(9, field.Cells.Count);
        Assert.Equal(30 - 4, standing.Vitals.Hp); // as they rise
        Assert.Equal(30, walker.Vitals.Hp);

        Assert.True(b.Step(walker, new Cell(9, 11)));
        Assert.True(b.Step(walker, new Cell(10, 11)));
        Assert.Equal(30 - 8, walker.Vitals.Hp); // 1d4 for each
    }

    // -- the catalog --------------------------------------------------------------------------------------------------

    [Fact]
    public void TheModsPowersAreStudiedAndUsedLikeTheOriginals()
    {
        SpellRules all = SpellRules.Read([.. Mods.Select(SpellsOf)]);
        Spellcasting.UseAdded(all.Spells.Select(s => s.ToSpell()));
        try
        {
            Assert.True(Spellcasting.IsPower(DimensionDoor) && Spellcasting.IsPower(MolecularAgitation));
            Assert.False(Spellcasting.IsPower(GustOfWind));
            Assert.Equal((Discipline.Psychokinesis, 3014), (Spellcasting.Get(DimensionDoor)!.Discipline, Spellcasting.Get(DimensionDoor)!.Icon));

            Character psionicist = new() { Classes = [CharacterClass.Psionicist], Rolled = [12, 12, 12, 12, 12, 12], Experience = 3000 };
            Assert.Equal([DimensionDoor, MolecularAgitation], Spellcasting.PowersToStudy(psionicist, Discipline.Psychokinesis).TakeLast(2));
            Spellcasting.StudyPower(psionicist, DimensionDoor);
            Assert.True(new Spellcasting(CharacterRules.Default).CanUse(psionicist, DimensionDoor));

            var magic = new Spellcasting(CharacterRules.Default);
            Character air = new() { Classes = [CharacterClass.Cleric], Sphere = Sphere.Air, Rolled = [12, 12, 12, 12, 12, 12], Experience = 250000 };
            Assert.Contains(Spellcasting.Get(GustOfWind)!, magic.Known(air, SpellKind.Priest, 3));
            Assert.DoesNotContain(Spellcasting.Get(SpikeStones)!, magic.Known(air, SpellKind.Priest, 4));
            Assert.Contains(Spellcasting.Get(SpikeStones)!, magic.Known(air with { Sphere = Sphere.Earth }, SpellKind.Priest, 4));
        }
        finally
        {
            Spellcasting.UseAdded([]);
        }
    }
}
