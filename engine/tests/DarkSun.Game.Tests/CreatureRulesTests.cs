using DarkSun.Data.Items;
using DarkSun.Game.Rules;

namespace DarkSun.Game.Tests;

public class CreatureRulesTests
{
    private static readonly CreatureRules Original = CreatureRules.Default;

    [Fact]
    public void TheDefaultsAreDsunExesTables()
    {
        // 3E45:0000: 42 types (the resistance rows follow at 00A8); DS:05D0/05DB: 11 abilities, Fireball (28) first.
        Assert.Equal(42, Original.AbilityTypes.Count);
        Assert.Equal(42, Original.Resistances.Count);
        Assert.Equal([5, 7, 17], Original.AbilityTypes[1]);
        Assert.Equal(11, Original.Abilities.Count);
        Assert.Equal(new CreatureAbility(26, 28), Original.Abilities[0]);
        // 3FB9:0110: set 1 Magic Missile, Fireball, Acid Arrow, Ice Storm, Cone of Cold.
        Assert.Equal([9, 28, 19, 46, 59], Original.SpellsOf(1));
        // 37F0:001C: set 2 starts Ballistic Attack, Disintegrate, Psionic Blast.
        Assert.Equal([142, 140, 170], Original.PowersOf(2).Take(3));
        // 37E5:0000: Fireball 63, Magic Missile 40, Armor never.
        Assert.Equal((63, 40, 0), (Original.WeightOf(28), Original.WeightOf(9), Original.WeightOf(1)));
    }

    [Fact]
    public void LookupsOutsideTheTablesFindNothing()
    {
        Assert.Empty(Original.SpellsOf(0));
        Assert.Empty(Original.SpellsOf(10));
        Assert.Empty(Original.PowersOf(-1));
        Assert.False(Original.HasAbility(129, 26)); // Maris and Aposs have types past the table
        Assert.Empty(Original.AbilitiesOf(0));
    }

    [Fact]
    public void ResistancesAreTheHighestChanceThatMatches()
    {
        const DamageElement sword = DamageElement.Slashing, plus1 = DamageElement.Plus1;
        // Shadows (13): hit only by +1 weapons.
        Assert.Equal((0, 100), (Original.ChanceToAffect(13, sword), Original.ChanceToAffect(13, sword | plus1)));
        // Elementals (7) need +2; the rampager (12) takes half from a sword, all from Fireball.
        Assert.Equal((0, 100), (Original.ChanceToAffect(7, sword | plus1), Original.ChanceToAffect(7, sword | plus1 | DamageElement.Plus2)));
        Assert.Equal((50, 100), (Original.ChanceToAffect(12, sword), Original.ChanceToAffect(12, DamageElement.Fire | DamageElement.Magic)));
        // No row, or a type past the table: always.
        Assert.Equal((100, 100), (Original.ChanceToAffect(0, sword), Original.ChanceToAffect(129, sword)));
    }

    [Fact]
    public void AbilityBitsAreNamed()
    {
        Assert.True(Original.HasAbility(11, CreatureAbilityBit.Regenerates)); // mastyrials
        Assert.True(Original.HasAbility(26, CreatureAbilityBit.DeadlyPoison)); // wyverns
        Assert.Equal([CreatureAbilityBit.ParalysingChill, CreatureAbilityBit.NoCharmOrHold, CreatureAbilityBit.NotStuck, CreatureAbilityBit.Unused10],
            Original.AbilityTypes[13].Select(b => (CreatureAbilityBit)b)); // shadows
        Assert.All(Original.Abilities, a => Assert.True(Enum.IsDefined((CreatureAbilityBit)a.Bit)));
    }

    [Fact]
    public void AbilitiesComeInTheOrderTheAiTriesThem()
    {
        // Type 18 has bits 10, 26, 27, 28, 29: Fireball (26) before Fear (29); 10, 27, 28 aren't the AI's.
        Assert.Equal([28, 44], Original.AbilitiesOf(18).Select(a => a.Spell));
    }

    [Fact]
    public void RulesRoundTripAsJsonAndBadOnesAreRefused()
    {
        string path = Path.Combine(Path.GetTempPath(), $"darksun-creatures-{Guid.NewGuid():N}.json");
        try
        {
            string json = Original.ToJson();
            Assert.Contains("[5, 7, 17]", json);
            File.WriteAllText(path, json);
            CreatureRules back = CreatureRules.Read(path);
            Assert.Equal(Original.AbilityTypes.Count, back.AbilityTypes.Count);
            Assert.Equal(Original.SpellsOf(4), back.SpellsOf(4));
            Assert.Equal(Original.SpellWeights.Count, back.SpellWeights.Count);
            Assert.Equal(json, back.ToJson());
            Assert.Equal(Original.Resistances[27], back.Resistances[27], (a, b) => a.SequenceEqual(b));
            Assert.Contains("\"Elements\": \"Bludgeoning, Slashing, Piercing\"", json);

            File.WriteAllText(path, json.Replace("\"AbilityChance\": 4", "\"AbilityChance\": 0"));
            Assert.Throws<InvalidDataException>(() => CreatureRules.Read(path));
            File.WriteAllText(path, json.Replace("\"Percent\": 50", "\"Percent\": 150"));
            Assert.Throws<InvalidDataException>(() => CreatureRules.Read(path));
            File.WriteAllText(path, "{ \"AbilityTypes\": [] }");
            Assert.Throws<InvalidDataException>(() => CreatureRules.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
