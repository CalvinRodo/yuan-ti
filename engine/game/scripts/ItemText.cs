using DarkSun.Data.Items;
using DarkSun.Game.World;

namespace DarkSun.Engine;

/// <summary>How the screens name and describe items.</summary>
public static class ItemText
{
    public static string Name(GameWorld world, Item item)
    {
        string name = world.Items.NameOf(item.Record);
        return name.Length > 0 ? name : $"item {item.Rdff}";
    }

    /// <summary>
    /// <paramref name="item"/> in a few lines: worth, material, damage, where it
    /// is worn, the spell it casts and its charges, and who may use it
    /// (<paramref name="holder"/> named if its class can't).
    /// </summary>
    public static string Describe(GameWorld world, Item item, WorldObject? holder, string holderName, Func<int, string?> spellName)
    {
        ItemType? type = world.Items.TypeOf(item.Record);
        var lines = new List<string> { Name(world, item) + (item.Quantity > 1 ? $" ×{item.Quantity}" : "") };
        var parts = new List<string> { $"worth {item.Record.Value}" };
        if (type?.MadeOf is { } material && type.Placement != ItemPlacement.None)
        {
            parts.Add(material.ToString().ToLowerInvariant());
        }
        if (type is { IsWeapon: true })
        {
            parts.Add($"damage {type.Damage}" + ((type.Kind & ItemTypeFlags.TwoHanded) != 0 ? ", two-handed" : ""));
        }
        if (type is { ArmorClass: > 0 })
        {
            parts.Add($"armour {type.ArmorClass + Math.Max(0, (int)item.Record.Bonus)}");
        }
        if (type is { Placement: not ItemPlacement.None })
        {
            parts.Add(item.Equipped ? $"worn: {Equipment.Names[item.Slot]}" : $"worn on: {type.Placement}");
        }
        if (item.Record.PowerFor(type) is > 0 and int power)
        {
            parts.Add($"casts {spellName(power) ?? $"spell {power}"}" + (item.Record.Charges > 0 && type is { Stacks: false } ? $" ({item.Record.Charges} charges)" : ""));
        }
        if (!item.Record.Breakable)
        {
            parts.Add("unbreakable");
        }
        lines.Add(string.Join(", ", parts));
        if (type is not null && type.Classes != ItemClasses.All)
        {
            lines.Add($"For: {ClassNames(type.Classes)}"
                + (holder is not null && !world.CanUse(holder, item) ? $" (not {holderName})" : ""));
        }
        return string.Join("\n", lines);
    }

    /// <summary>The classes in <paramref name="classes"/>, clerics by sphere unless all four.</summary>
    public static string ClassNames(ItemClasses classes)
    {
        var names = new List<string>();
        if ((classes & ItemClasses.Clerics) == ItemClasses.Clerics)
        {
            names.Add("clerics");
            classes &= ~ItemClasses.Clerics;
        }
        foreach (ItemClasses c in Enum.GetValues<ItemClasses>().Where(c => c is not (ItemClasses.None or ItemClasses.Clerics or ItemClasses.All) && (classes & c) != 0))
        {
            names.Add(c switch
            {
                ItemClasses.AirCleric => "air clerics",
                ItemClasses.EarthCleric => "earth clerics",
                ItemClasses.FireCleric => "fire clerics",
                ItemClasses.WaterCleric => "water clerics",
                ItemClasses.Thief => "thieves",
                _ => c.ToString().ToLowerInvariant() + "s",
            });
        }
        return names.Count > 0 ? string.Join(", ", names) : "no one";
    }
}
