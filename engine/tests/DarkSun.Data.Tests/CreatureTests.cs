using System.Text;
using DarkSun.Data.Gff;
using DarkSun.Data.Items;
using DarkSun.Data.Mods;
using DarkSun.Data.Regions;

namespace DarkSun.Data.Tests;

public class CreatureTests
{
    /// <summary>
    /// A creature <c>RDFF</c> laid out as <c>SEGOBJEX.GFF</c>'s are (Father
    /// Garyn's, 112 bytes after the character record): combat record, character
    /// record, two carried items with their references, and the end header.
    /// </summary>
    internal static byte[] Rdff()
    {
        var b = new Bytes();
        b.Byte(1, 0).Short(2).Short(0).Short(0).Short(CombatRecord.Size);
        byte[] combat = new byte[CombatRecord.Size];
        (combat[0], combat[0x16], combat[0x1D], combat[0x1E], combat[0x21]) = (15, 31, 2, 1, 0x20);
        Encoding.Latin1.GetBytes("Father Garyn").CopyTo(combat, 40);
        b.Byte(combat);
        b.Byte(3, 0).Short(4).Short(0).Short(0).Short(CharacterRecord.Length);
        byte[] character = new byte[CharacterRecord.Length];
        BitConverter.GetBytes(1000).CopyTo(character, 0);
        BitConverter.GetBytes(420).CopyTo(character, 4);
        BitConverter.GetBytes((short)30).CopyTo(character, 8);
        (character[0x18], character[0x21], character[0x24], character[0x27], character[0x29], character[0x2A]) = (1, 9, 6, 10, 30, 2);
        (character[0x2B], character[0x2E], character[0x31], character[0x34]) = (2, 1, 8, 1);
        (character[0x37], character[0x3E], character[0x46]) = (13, 2, 4);
        b.Byte(character);
        // The first item: load action 2, stored in field 0x4F of block 0 (the creature); a mace (IT1R 12, NAME 82).
        b.Byte(2, 0).Short(1).Short(9).Short(0x4F).Short(ItemRecord.Size).Byte(Item(1030, slot: 255));
        b.Byte(3, 2).Short(5).Short(12).Short(4).Short(0);
        b.Byte(3, 2).Short(3).Short(82).Short(2).Short(0);
        // The next: load action 4, after block 2; worn (slot 3), a shield (IT1R 3, NAME 4).
        b.Byte(4, 2).Short(1).Short(0x17).Short(1).Short(ItemRecord.Size).Byte(Item(1020, slot: 3));
        b.Byte(3, 5).Short(5).Short(3).Short(4).Short(0);
        b.Byte(3, 5).Short(3).Short(4).Short(2).Short(0);
        b.Byte(0xFF, 5, 1, 0, 0x0C, 0, 1, 0, 0x15, 0);
        return b.ToArray();
    }

    private static byte[] Item(int rdff, byte slot)
    {
        byte[] r = new byte[ItemRecord.Size];
        BitConverter.GetBytes((short)-rdff).CopyTo(r, 0);
        r[17] = slot;
        return r;
    }

    [Fact]
    public void ReadsEveryLoadAction()
    {
        IReadOnlyList<RdffEntry> entries = Gff.Rdff.Entries(Rdff());

        Assert.Equal([1, 3, 2, 3, 3, 4, 3, 3], entries.Select(e => e.LoadAction));
        Assert.Equal((5, 2, 12, 4), (entries[3].Type, entries[3].BlockNumber, entries[3].Index, entries[3].From));
    }

    [Fact]
    public void ACreatureIsItsRecordsAndWhatItCarries()
    {
        CreatureDefinition c = CreatureDefinition.FromRdff(Rdff())!;

        Assert.Equal(("Father Garyn", 15, 30), (c.Combat.Name, (int)c.Combat.Hp, c.HitPoints));
        Assert.Equal((31, 1, true, true), (c.Combat.SpecialAttack, c.Combat.Mind, c.Combat.ComputerControlled, c.Combat.IsEnemy));
        Assert.Equal([(1030, 12, 82, 255), (1020, 3, 4, 3)],
            c.Carried.Select(i => (-i.Id, (int)i.Type, (int)i.NameIndex, (int)i.Slot)));

        CharacterRecord ch = c.Character!;
        Assert.Equal((1000, 420, 30), (ch.Experience, ch.ExperienceValue, ch.HitPoints));
        Assert.Equal((1, 9, 6, 10, 2), (ch.Race, ch.ClassCodes[0], ch.Levels[0], (int)ch.BaseArmorClass, (int)ch.AttackHalves));
        Assert.Equal(new NaturalAttack(2, 1, 8, 1), ch.NaturalAttack(0));
        Assert.False(ch.NaturalAttack(1).Exists);
        Assert.Equal((13, 2, 4, 30), (ch.Saves[0], ch.SpellSet, ch.PsionicSet, (int)ch.MagicResistance));
    }

    [Fact]
    public void AnItemIsNotACreature()
    {
        Assert.Null(CreatureDefinition.FromRdff(ItemTests.ItemRdff(1233, 67, 44, 0)));
    }

    [Fact]
    public void ModsEditCharacterFieldsAndLeaveTheCarriedItemsAlone()
    {
        byte[] original = Rdff();

        string json = RecordJson.Export("RDFF", original, _ => null);
        byte[] edited = RecordJson.Apply("RDFF", original,
            """{ "combat": { "mind": 8 }, "character": { "experienceValue": 100000, "psionicSet": 9, "magicResistance": 70 } }""");

        Assert.Contains("\"experienceValue\": 420", json);
        Assert.Equal(original, RecordJson.Apply("RDFF", original, json));
        CreatureDefinition c = CreatureDefinition.FromRdff(edited)!;
        Assert.Equal((8, 100000, 9, 70, 2), (c.Combat.Mind, c.Character!.ExperienceValue, c.Character.PsionicSet, (int)c.Character.MagicResistance, c.Carried.Count));
    }
}
