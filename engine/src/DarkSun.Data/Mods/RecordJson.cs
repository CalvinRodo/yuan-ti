using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DarkSun.Data.Gff;
using DarkSun.Data.Items;

namespace DarkSun.Data.Mods;

/// <summary>A field of a fixed-layout record: where it is and how it's stored.</summary>
/// <param name="Kind">u8, s8, u16, s16, s32, or text (Latin-1, NUL-padded, <paramref name="Length"/> bytes).</param>
public sealed record RecordField(string Name, int Offset, string Kind, Type? Enum = null, int Length = 0);

/// <summary>
/// The structured resources mods can edit as JSON, field by field, instead
/// of as raw bytes: a <c>.json</c> file lists only the fields it changes, and
/// is applied to the original resource, so undecoded bytes and the fields it
/// doesn't name stay as they are. Keys starting with <c>_</c> are ignored
/// (the exporter writes names there as hints). Every section also takes
/// <c>"bytes": {"0x1c": 5}</c> for bytes no field covers yet.
/// <list type="bullet">
/// <item><c>IT1R/1.json</c> (<c>GPLDATA.GFF</c>): item types by number,
/// <c>{"12": {"damageSides": 10}}</c>; a number past the end adds a type.</item>
/// <item><c>RDFF/&lt;n&gt;.json</c>: an object's records; <c>"item"</c> (the
/// item record), <c>"combat"</c> (a creature's combat record, including its
/// name) and <c>"character"</c> (its character record).</item>
/// <item><c>RDAT/&lt;n&gt;.json</c> (<c>RESOURCE.GFF</c>): region <i>n</i>'s music
/// and colour cycling.</item>
/// <item><c>NAME/1.json</c> (<c>GPLDATA.GFF</c>): item names by index,
/// <c>{"45": "Bone Sword"}</c> (at most 24 characters).</item>
/// <item><c>WIND/&lt;n&gt;.json</c> (<c>RESOURCE.GFF</c>): a window's layout, its
/// size and where each control is, <c>{"items": {"BUTN 2048": {"x": 100, "y": 70}}}</c>.</item>
/// <item><c>BUTN</c>, <c>APFM</c>, <c>EBOX</c> <c>/&lt;n&gt;.json</c>: a control's size,
/// alignment, icon and text (<see cref="Ui.UiControl"/>).</item>
/// </list>
/// Enumerations take names (<c>"placement": "Hand"</c>, <c>"weapon": "Melee, Thrown"</c>) or numbers.
/// </summary>
public static class RecordJson
{
    public static readonly IReadOnlyList<RecordField> ItemTypeFields =
    [
        new("weapon", 0, "u8", typeof(WeaponKind)), new("unknown1", 1, "u8"), new("damageType", 2, "u16"),
        new("weight", 4, "u8"), new("unknown5", 5, "u16"), new("baseHp", 7, "u8"), new("material", 8, "u8"),
        new("placement", 9, "u8", typeof(ItemPlacement)), new("range", 10, "u8"), new("attacks", 11, "u8"),
        new("damageSides", 12, "u8"), new("damageDice", 13, "u8"), new("damageBonus", 14, "s8"),
        new("flags", 15, "u8"), new("legalClasses", 16, "u16"), new("armorClass", 18, "s8"), new("unknown19", 19, "u8"),
    ];

    public static readonly IReadOnlyList<RecordField> ItemFields =
    [
        new("id", 0, "s16"), new("quantity", 2, "u16"), new("next", 4, "s16"), new("value", 6, "u16"),
        new("packIndex", 8, "s16"), new("type", 10, "s16"), new("icon", 12, "s16"), new("charges", 14, "u8"),
        new("flags", 15, "u8"), new("special", 16, "u8"), new("slot", 17, "u8"), new("nameIndex", 18, "u16"),
        new("bonus", 20, "s8"),
    ];

    /// <summary>The 58-byte combat record (<c>CombatRecord</c>); names after libgff's <c>ds1_combat_t</c>.</summary>
    public static readonly IReadOnlyList<RecordField> CombatFields =
    [
        new("hp", 0, "s16"), new("psp", 2, "s16"), new("charIndex", 4, "s16"), new("id", 6, "s16"),
        new("readyItem", 8, "s16"), new("weaponIndex", 10, "s16"), new("packIndex", 12, "s16"),
        new("specialAttack", 22, "u8"), new("specialDefense", 23, "u8"), new("icon", 24, "s16"),
        new("ac", 26, "s8"), new("move", 27, "u8"), new("status", 28, "u8"), new("allegiance", 29, "u8"),
        new("mind", 30, "u8"), new("thac0", 31, "s8"), new("priority", 32, "u8"), new("flags", 33, "u8"),
        new("str", 34, "u8"), new("dex", 35, "u8"), new("con", 36, "u8"), new("int", 37, "u8"),
        new("wis", 38, "u8"), new("cha", 39, "u8"), new("name", 40, "text", Length: 18),
    ];

    /// <summary>The character record's known fields (<c>CharacterRecord</c>).</summary>
    public static readonly IReadOnlyList<RecordField> CharacterFields =
    [
        new("experience", 0, "s32"), new("experienceValue", 4, "s32"), new("hpBeforeDifficulty", 8, "u16"),
        new("classBits", 0x12, "u16", typeof(ItemClasses)), new("race", 0x18, "u8"),
        new("class1", 0x21, "u8"), new("class2", 0x22, "u8"), new("class3", 0x23, "u8"),
        new("level1", 0x24, "u8"), new("level2", 0x25, "u8"), new("level3", 0x26, "u8"),
        new("baseAc", 0x27, "s8"), new("magicResistance", 0x29, "s8"), new("attackHalves", 0x2A, "u8"),
        new("natural1Halves", 0x2B, "s8"), new("natural2Halves", 0x2C, "s8"), new("natural3Halves", 0x2D, "s8"),
        new("natural1Dice", 0x2E, "s8"), new("natural2Dice", 0x2F, "s8"), new("natural3Dice", 0x30, "s8"),
        new("natural1Sides", 0x31, "s8"), new("natural2Sides", 0x32, "s8"), new("natural3Sides", 0x33, "s8"),
        new("natural1Bonus", 0x34, "s8"), new("natural2Bonus", 0x35, "s8"), new("natural3Bonus", 0x36, "s8"),
        new("saveParalysis", 0x37, "u8"), new("saveRod", 0x38, "u8"), new("savePetrification", 0x39, "u8"),
        new("saveBreath", 0x3A, "u8"), new("saveSpell", 0x3B, "u8"),
        new("size", 0x3D, "u8"), new("spellSet", 0x3E, "u8"), new("psionicSet", 0x46, "u8"),
    ];

    /// <summary><c>RDAT</c> (<c>RegionInfo</c>): colour cycles as first colour, count, and speed × 10.</summary>
    public static readonly IReadOnlyList<RecordField> RegionFields =
    [
        new("cycle1First", 0, "u8"), new("cycle1Count", 1, "u8"), new("cycle1Speed", 2, "u8"),
        new("cycle2First", 3, "u8"), new("cycle2Count", 4, "u8"), new("cycle2Speed", 5, "u8"),
        new("music", 0x2F, "u8"),
    ];

    // Plain characters (×, ', é) in what modders read and edit, not \u escapes.
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Whether <paramref name="tag"/> can be edited as JSON.</summary>
    public static bool Handles(string tag) =>
        GffFile.NormalizeTag(tag.Trim().ToUpperInvariant()) is "IT1R" or "RDFF" or "RDAT" or "NAME" or "WIND" or "BUTN" or "APFM" or "EBOX";

    /// <summary>A <c>BUTN</c>'s or <c>APFM</c>'s fields (<see cref="Ui.UiControl"/>); the text runs to the end of the resource.</summary>
    public static readonly IReadOnlyList<RecordField> ControlFields =
    [
        new("width", 0x28, "s16"), new("height", 0x2A, "s16"), new("align", 0x58, "u16"), new("icon", 0x64, "u16"),
    ];

    public static readonly IReadOnlyList<RecordField> TextBoxFields =
    [
        new("width", 0x22, "s16"), new("height", 0x24, "s16"), new("background", 0x7E, "s16"), new("textColour", 0x80, "s16"),
    ];

    private static IReadOnlyList<RecordField> ControlFieldsOf(string tag, int length) => tag switch
    {
        "EBOX" => TextBoxFields,
        _ when length > 0x6E => [.. ControlFields, new("text", 0x6E, "text", Length: length - 0x6E)],
        _ => ControlFields,
    };

    /// <summary>Every field of <paramref name="data"/> as JSON, the starting point for an edit.</summary>
    public static string Export(string tag, ReadOnlyMemory<byte> data, Func<int, string?>? itemName = null)
    {
        var root = new JsonObject();
        switch (GffFile.NormalizeTag(tag.Trim().ToUpperInvariant()))
        {
            case "IT1R":
                for (int i = 0; i * ItemType.Size + ItemType.Size <= data.Length; i++)
                {
                    root[i.ToString(CultureInfo.InvariantCulture)] = Read(ItemTypeFields, data.Span.Slice(i * ItemType.Size, ItemType.Size));
                }
                break;
            case "NAME":
                var names = Text.NameTable.Parse(data.Span);
                for (int i = 0; i < names.Count; i++)
                {
                    root[i.ToString(CultureInfo.InvariantCulture)] = names[i];
                }
                break;
            case "RDAT":
                root = Read(RegionFields, data.Span);
                break;
            case "RDFF":
                foreach (RdffEntry e in Rdff.Entries(data))
                {
                    if (Section(e) is not var (name, fields) || root.ContainsKey(name))
                    {
                        continue;
                    }
                    JsonObject section = Read(fields, e.Body.Span);
                    if (name == "item" && itemName is not null && ItemRecord.Parse(e.Body.Span) is var item && itemName(item.NameIndex) is { Length: > 0 } n)
                    {
                        section.Insert(0, "_name", n);
                    }
                    root[name] = section;
                }
                break;
            case "WIND":
                var window = Ui.UiWindow.Parse(data.Span);
                root["_about"] = "Move a control by its x and y (in the window, 320 × 200 screen pixels); delete the ones you don't move.";
                root["width"] = window.Width;
                root["height"] = window.Height;
                var items = new JsonObject();
                foreach (Ui.WindowItem item in window.Items)
                {
                    items.TryAdd($"{item.Tag.Trim()} {item.Number}", new JsonObject { ["x"] = item.X, ["y"] = item.Y });
                }
                root["items"] = items;
                break;
            case "BUTN" or "APFM" or "EBOX":
                root = Read(ControlFieldsOf(GffFile.NormalizeTag(tag.Trim().ToUpperInvariant()).Trim(), data.Length), data.Span);
                root.Insert(0, "_about", "align: icon across << 8 | down << 10, text across << 4 | down << 6 (0 start, 1 inset, 2 centre, 3 end)");
                break;
            default:
                throw new ArgumentException($"{tag.Trim()} has no JSON form");
        }
        return root.ToJsonString(Indented) + "\n";
    }

    /// <summary><paramref name="original"/> with the edits in <paramref name="json"/>; throws <see cref="InvalidDataException"/> saying what's wrong.</summary>
    public static byte[] Apply(string tag, ReadOnlySpan<byte> original, string json)
    {
        JsonObject edits;
        try
        {
            edits = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                ?? throw new InvalidDataException("expected a JSON object");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException(e.Message, e);
        }
        byte[] data = original.ToArray();
        switch (GffFile.NormalizeTag(tag.Trim().ToUpperInvariant()))
        {
            case "IT1R":
                return Table(data, edits, ItemType.Size, (record, value, key) => Write(ItemTypeFields, record, Object(value, key), key));
            case "NAME":
                return Table(data, edits, Text.NameTable.RecordSize, (record, value, key) =>
                {
                    string name = value?.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : throw new InvalidDataException($"{key}: expected a name in quotes");
                    WriteText(record, 0, Text.NameTable.RecordSize - 1, name, key);
                });
            case "RDAT":
                if (data.Length == 0)
                {
                    data = new byte[Regions.RegionInfo.Size];
                }
                Write(RegionFields, data, edits, "");
                return data;
            case "RDFF":
                // Entries follow each other from the start: (entry, where its body is).
                var entries = new List<(RdffEntry Entry, int Body)>();
                int at = 0;
                foreach (RdffEntry e in Rdff.Entries(data))
                {
                    entries.Add((e, at + RdffEntry.HeaderSize));
                    at += RdffEntry.HeaderSize + e.Length;
                }
                foreach (var (key, value) in edits)
                {
                    if (key.StartsWith('_'))
                    {
                        continue;
                    }
                    var (entry, body) = entries.FirstOrDefault(e => Section(e.Entry)?.Name == key);
                    if (entry is null)
                    {
                        var has = entries.Select(e => Section(e.Entry)?.Name).OfType<string>().Distinct().ToList();
                        throw new InvalidDataException(has.Count > 0
                            ? $"this object has no {key} record (it has: {string.Join(", ", has)})"
                            : "this object has no records JSON can edit");
                    }
                    Write(Section(entry)!.Value.Fields, data.AsSpan(body, entry.Length), Object(value, key), key);
                }
                return data;
            case "WIND":
                return ApplyWindow(data, edits);
            case "BUTN" or "APFM" or "EBOX":
                string t = GffFile.NormalizeTag(tag.Trim().ToUpperInvariant()).Trim();
                Write(ControlFieldsOf(t, data.Length), data, edits, "");
                return data;
            default:
                throw new InvalidDataException($"{tag.Trim()} has no JSON form; use .bin");
        }
    }

    /// <summary>A <c>WIND</c>'s size (<c>+0xBE</c>, <c>+0xC0</c>) and its items' positions (<c>+0x0C</c>, <c>+0x0E</c> of each).</summary>
    private static byte[] ApplyWindow(byte[] data, JsonObject edits)
    {
        Ui.UiWindow window = Ui.UiWindow.Parse(data);
        foreach (var (key, value) in edits)
        {
            switch (key)
            {
                case var _ when key.StartsWith('_'):
                    break;
                case "width":
                    BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(0xBE), (short)Number(value, 0, 640, key, null));
                    break;
                case "height":
                    BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(0xC0), (short)Number(value, 0, 400, key, null));
                    break;
                case "items":
                    foreach (var (name, place) in Object(value, key))
                    {
                        int i = window.Items.ToList().FindIndex(it => $"{it.Tag.Trim()} {it.Number}".Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
                        if (i < 0)
                        {
                            throw new InvalidDataException($"items.{name}: not in this window (it has: {string.Join(", ", window.Items.Select(it => $"{it.Tag.Trim()} {it.Number}"))})");
                        }
                        int at = Ui.UiWindow.HeaderSize + i * Ui.UiWindow.ItemSize;
                        foreach (var (axis, n) in Object(place, $"items.{name}"))
                        {
                            int offset = axis switch
                            {
                                "x" => 0x0C,
                                "y" => 0x0E,
                                _ => throw new InvalidDataException($"items.{name}.{axis}: a control has x and y"),
                            };
                            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(at + offset), (short)Number(n, -640, 640, $"items.{name}.{axis}", null));
                        }
                    }
                    break;
                default:
                    throw new InvalidDataException($"{key}: a window has width, height and items");
            }
        }
        return data;
    }

    private static (string Name, IReadOnlyList<RecordField> Fields)? Section(RdffEntry e) => e switch
    {
        { LoadAction: RdffEntry.Inline, Type: RdffEntry.ItemType, Length: >= ItemRecord.Size } => ("item", ItemFields),
        { Type: RdffEntry.CombatType, Length: >= Regions.CombatRecord.Size } => ("combat", CombatFields),
        { Type: RdffEntry.CharacterType, Length: >= Regions.CharacterRecord.Length } => ("character", CharacterFields),
        _ => null,
    };

    private static JsonObject Object(JsonNode? node, string key) =>
        node as JsonObject ?? throw new InvalidDataException($"{key}: expected {{ fields }}");

    /// <summary>A table of fixed-size records keyed by index; indexes past the end add zeroed records.</summary>
    private static byte[] Table(byte[] data, JsonObject edits, int size, Action<Span<byte>, JsonNode?, string> edit)
    {
        var list = new List<byte>(data);
        foreach (var (key, value) in edits)
        {
            if (key.StartsWith('_'))
            {
                continue;
            }
            if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index > 10000)
            {
                throw new InvalidDataException($"\"{key}\": expected a record number");
            }
            while (list.Count < (index + 1) * size)
            {
                list.Add(0);
            }
        }
        byte[] result = [.. list];
        foreach (var (key, value) in edits)
        {
            if (!key.StartsWith('_'))
            {
                edit(result.AsSpan(int.Parse(key, CultureInfo.InvariantCulture) * size, size), value, key);
            }
        }
        return result;
    }

    private static JsonObject Read(IReadOnlyList<RecordField> fields, ReadOnlySpan<byte> r)
    {
        var o = new JsonObject();
        foreach (RecordField f in fields)
        {
            if (f.Offset + Width(f) > r.Length)
            {
                continue;
            }
            if (f.Kind == "text")
            {
                ReadOnlySpan<byte> s = r.Slice(f.Offset, f.Length);
                int end = s.IndexOf((byte)0);
                o[f.Name] = Encoding.Latin1.GetString(end < 0 ? s : s[..end]);
                continue;
            }
            int value = f.Kind switch
            {
                "u8" => r[f.Offset],
                "s8" => (sbyte)r[f.Offset],
                "u16" => BinaryPrimitives.ReadUInt16LittleEndian(r[f.Offset..]),
                "s32" => BinaryPrimitives.ReadInt32LittleEndian(r[f.Offset..]),
                _ => BinaryPrimitives.ReadInt16LittleEndian(r[f.Offset..]),
            };
            o[f.Name] = f.Enum is not null && System.Enum.ToObject(f.Enum, value).ToString() is { } named && !char.IsAsciiDigit(named[0]) && named[0] != '-'
                ? named : value;
        }
        return o;
    }

    private static void Write(IReadOnlyList<RecordField> fields, Span<byte> r, JsonObject edits, string section)
    {
        string where = section.Length > 0 ? section + "." : "";
        foreach (var (key, node) in edits)
        {
            if (key.StartsWith('_'))
            {
                continue;
            }
            if (key == "bytes")
            {
                foreach (var (offset, b) in Object(node, where + key))
                {
                    int at = offset.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                        ? int.Parse(offset.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                        : int.Parse(offset, CultureInfo.InvariantCulture);
                    if (at < 0 || at >= r.Length)
                    {
                        throw new InvalidDataException($"{where}bytes.{offset}: past the end of the record ({r.Length} bytes)");
                    }
                    r[at] = (byte)Number(b, 0, 255, $"{where}bytes.{offset}", null);
                }
                continue;
            }
            RecordField f = fields.FirstOrDefault(x => x.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"{where}{key}: no such field (fields: {string.Join(", ", fields.Select(x => x.Name))}, bytes)");
            if (f.Offset + Width(f) > r.Length)
            {
                throw new InvalidDataException($"{where}{key}: this record is too short to have it");
            }
            switch (f.Kind)
            {
                case "text":
                    string text = node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : throw new InvalidDataException($"{where}{key}: expected text in quotes");
                    WriteText(r, f.Offset, f.Length, text, where + key);
                    break;
                case "u8":
                    r[f.Offset] = (byte)Number(node, 0, 255, where + key, f.Enum);
                    break;
                case "s8":
                    r[f.Offset] = (byte)(sbyte)Number(node, -128, 127, where + key, f.Enum);
                    break;
                case "u16":
                    BinaryPrimitives.WriteUInt16LittleEndian(r[f.Offset..], (ushort)Number(node, 0, 65535, where + key, f.Enum));
                    break;
                case "s32":
                    BinaryPrimitives.WriteInt32LittleEndian(r[f.Offset..], (int)Number(node, int.MinValue, int.MaxValue, where + key, f.Enum));
                    break;
                default:
                    BinaryPrimitives.WriteInt16LittleEndian(r[f.Offset..], (short)Number(node, -32768, 32767, where + key, f.Enum));
                    break;
            }
        }
    }

    private static int Width(RecordField f) => f.Kind switch { "u8" or "s8" => 1, "s32" => 4, "text" => f.Length, _ => 2 };

    private static long Number(JsonNode? node, long min, long max, string key, Type? enumType)
    {
        long value;
        if (node?.GetValueKind() == JsonValueKind.Number && node.AsValue().TryGetValue(out long n))
        {
            value = n;
        }
        else if (enumType is not null && node?.GetValueKind() == JsonValueKind.String
            && System.Enum.TryParse(enumType, node.GetValue<string>(), ignoreCase: true, out object? e))
        {
            value = Convert.ToInt64(e, CultureInfo.InvariantCulture);
        }
        else
        {
            string names = enumType is null ? "" : $" or one of {string.Join(", ", System.Enum.GetNames(enumType))}";
            throw new InvalidDataException($"{key}: expected a whole number{names}, not {node?.ToJsonString() ?? "null"}");
        }
        return value >= min && value <= max ? value : throw new InvalidDataException($"{key}: {value} is out of range ({min} to {max})");
    }

    private static void WriteText(Span<byte> r, int offset, int length, string text, string key)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(text);
        if (bytes.Length >= length)
        {
            throw new InvalidDataException($"{key}: \"{text}\" is too long (at most {length - 1} characters)");
        }
        r.Slice(offset, length).Clear();
        bytes.CopyTo(r[offset..]);
    }
}
