using DarkSun.Data.Gff;
using DarkSun.Data.Items;

namespace DarkSun.Data.Regions;

/// <summary>
/// Everything a creature's <c>RDFF</c> (<c>SEGOBJEX.GFF</c>) holds: its combat
/// record, its character record, and the items it carries. DSUN.EXE's loader
/// (<c>7155:0000</c>) reads them in that order: the combat record (load action
/// 1), the character record (3, type 4), then each item as an item record
/// (2 for the first, 4 for the others, which it chains through item record +4)
/// followed by references to its <c>IT1R</c> type and <c>NAME</c> (3, types 5
/// and 3), which set the record's <see cref="ItemRecord.Type"/> and
/// <see cref="ItemRecord.NameIndex"/>. An item's record +0x11 says whether it
/// is worn (its slot) or in the pack (255); when the creature dies, its body
/// takes them all.
/// </summary>
public sealed record CreatureDefinition(CombatRecord Combat, CharacterRecord? Character, IReadOnlyList<ItemRecord> Carried)
{
    /// <summary>Field numbers the item references set (<c>0826:012A</c>, an item object's fields).</summary>
    public const int TypeField = 4, NameField = 2;

    /// <summary>Hit points before the difficulty: the character record's, which DSUN.EXE uses, else the combat record's.</summary>
    public int HitPoints => Character?.HitPoints ?? Combat.Hp;

    /// <summary>The creature in <c>RDFF</c> data <paramref name="rdff"/>, or null if it isn't one.</summary>
    public static CreatureDefinition? FromRdff(ReadOnlyMemory<byte> rdff)
    {
        IReadOnlyList<RdffEntry> entries = Rdff.Entries(rdff);
        if (entries is not [{ LoadAction: RdffEntry.Inline, Type: RdffEntry.CombatType }, ..]
            || CombatRecord.Parse(rdff.Span) is not { } combat)
        {
            return null;
        }
        CharacterRecord? character = entries.Skip(1)
            .FirstOrDefault(e => e is { LoadAction: RdffEntry.Reference, Type: CharacterRecord.Type } && e.Body.Length >= CharacterRecord.Length)
            is { } c ? CharacterRecord.Parse(c.Body.Span) : null;
        return new CreatureDefinition(combat, character, CarriedIn(entries));
    }

    /// <summary>
    /// The items <c>RDFF</c> entries <paramref name="entries"/> link to their
    /// object (after its own record): a creature's carried items, or a
    /// container's contents (an item record with linked items, such as a chest).
    /// </summary>
    public static IReadOnlyList<ItemRecord> CarriedIn(IReadOnlyList<RdffEntry> entries)
    {
        var carried = new List<ItemRecord>();
        foreach (RdffEntry e in entries.Skip(1))
        {
            switch (e)
            {
                case { LoadAction: RdffEntry.NewObject or RdffEntry.NextObject, Type: RdffEntry.ItemType } when e.Body.Length >= ItemRecord.Size:
                    carried.Add(ItemRecord.Parse(e.Body.Span));
                    break;
                case { LoadAction: RdffEntry.Reference, Type: RdffEntry.ItemTypeReference, From: TypeField } when carried.Count > 0:
                    carried[^1] = carried[^1] with { Type = (short)e.Index };
                    break;
                case { LoadAction: RdffEntry.Reference, Type: RdffEntry.NameReference, From: NameField } when carried.Count > 0:
                    carried[^1] = carried[^1] with { NameIndex = (ushort)e.Index };
                    break;
            }
        }
        return carried;
    }
}
