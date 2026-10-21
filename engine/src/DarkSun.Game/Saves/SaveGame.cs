using System.Text.Json;
using System.Text.Json.Serialization;
using DarkSun.Data.Animation;
using DarkSun.Game.Scripting;
using DarkSun.Game.World;

namespace DarkSun.Game.Saves;

/// <summary>A party member as saved: the sprite (its <c>OJFF</c> number), cell, facing, and the character made on Create Characters, if any.</summary>
public sealed record PartyMemberSave(int Sprite, int X, int Y, Direction Facing, Characters.Character? Character = null,
    bool Collapsed = false);

/// <summary>A creature's line-of-sight script (<see cref="GplCreatureScripts.Sight"/>).</summary>
public sealed record SightScriptSave(int Object, int Kind, GplEntry Entry, int Range);

/// <summary>
/// A saved game, written as JSON (readable, and editable by hand): where the
/// party is, the script globals and engine variables, the trigger table as
/// DSUN.EXE keeps it (200 × 13 bytes, local triggers of the current region
/// included), the creatures' scripts, the random generator, and every region
/// the party has been to as it left it (the current one as it is now).
/// Loading puts all of that back and enters the region without running its
/// script again: its triggers are in the save.
/// </summary>
public sealed record SaveGame
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public int Version { get; init; } = CurrentVersion;

    /// <summary>Shown in the load list ("Quick save", "Autosave", …).</summary>
    public string Name { get; init; } = "";

    public DateTimeOffset Saved { get; init; }

    public int Region { get; init; }

    /// <summary>The selected party member (index into <see cref="Party"/>).</summary>
    public int Selected { get; init; }

    public int? CollapsedLeader { get; init; }

    public List<PartyMemberSave> Party { get; init; } = [];

    /// <summary>What each party slot carries, its hit points and status (empty in saves from before items).</summary>
    public List<PartyMemberSaveState> PartyStates { get; init; } = [];

    /// <summary>The global variable areas (flags, numbers, big numbers, strings), as stored.</summary>
    public Dictionary<GplArea, byte[]> Globals { get; init; } = [];

    /// <summary>The engine variables <c>gname[0–14]</c>.</summary>
    public int[] Engine { get; init; } = [];

    public byte[] Triggers { get; init; } = [];

    public Dictionary<GplTriggerList, int> TriggerHeads { get; init; } = [];

    public int TriggerFree { get; init; }

    public Dictionary<int, GplEntry> NoOrders { get; init; } = [];

    public List<SightScriptSave> Sight { get; init; } = [];

    public uint Random { get; init; }

    public List<RegionState> Regions { get; init; } = [];

    /// <summary>The party's journal: conversations, events, regions visited (null in saves from before it).</summary>
    public History.JournalState? Journal { get; init; }

    /// <summary>The mods that were on (shown when loading; a save works without them).</summary>
    public List<string> Mods { get; init; } = [];

    private static readonly GplArea[] GlobalAreas = [GplArea.GlobalFlags, GplArea.GlobalNums, GplArea.GlobalBigNums, GplArea.GlobalStrings];

    /// <summary>The game as it is now. <paramref name="world"/> is the current region (saved as it is); <paramref name="memory"/> the others.</summary>
    public static SaveGame Capture(string name, GplMachine machine, int[] engine, GameWorld world, RegionMemory memory,
        IEnumerable<PartyMemberSave> party, int selected, IEnumerable<string> mods, IEnumerable<PartyMemberState>? states = null,
        History.Journal? journal = null)
    {
        var (table, heads, free) = machine.Triggers.Save();
        int current = world.Region.Number;
        return new SaveGame
        {
            Name = name,
            Saved = DateTimeOffset.Now,
            Region = current,
            Selected = selected,
            CollapsedLeader = world.CollapsedLeader,
            Party = [.. party.Select((member, slot) => member with { Collapsed = world[slot]?.Collapsed == true })],
            PartyStates = [.. (states ?? []).Select(s => s.Save())],
            Journal = journal?.Save(),
            Globals = GlobalAreas.ToDictionary(a => a, a => machine.Memory.Area(a).ToArray()),
            Engine = [.. engine],
            Triggers = table,
            TriggerHeads = new Dictionary<GplTriggerList, int>(heads),
            TriggerFree = free,
            NoOrders = machine.Creatures.NoOrders.ToDictionary(),
            Sight = [.. machine.Creatures.Sight.Select(s => new SightScriptSave(s.Key, s.Value.Kind, s.Value.Entry, s.Value.Range))],
            Random = machine.Random.State,
            Regions = [.. memory.States.Where(r => r.Region != current), world.Snapshot()],
            Mods = [.. mods],
        };
    }

    /// <summary>
    /// Give the party slots back what they carried and how they were;
    /// <paramref name="newItem"/> makes an item from its saved state (null if
    /// its <c>RDFF</c> is gone).
    /// </summary>
    public void RestoreParty(IReadOnlyList<PartyMemberState> members, Func<ItemState, Item?> newItem)
    {
        for (int slot = 0; slot < members.Count && slot < PartyStates.Count; slot++)
        {
            members[slot].Vitals.Load(PartyStates[slot].Vitals);
            members[slot].Items.Clear();
            members[slot].Items.AddRange(PartyStates[slot].Items.Select(newItem).OfType<Item>());
        }
    }

    /// <summary>Put the scripts' state and the regions back (the party and the current region are the view's to place).</summary>
    public void Restore(GplMachine machine, int[] engine, RegionMemory memory)
    {
        if (Version != CurrentVersion)
        {
            throw new InvalidDataException($"saved by version {Version} of the engine; this one reads version {CurrentVersion}");
        }
        foreach (var (area, bytes) in Globals)
        {
            byte[] into = machine.Memory.Area(area);
            if (bytes.Length != into.Length)
            {
                throw new InvalidDataException($"{area}: {bytes.Length} bytes, expected {into.Length}");
            }
            bytes.CopyTo(into, 0);
        }
        Array.Copy(Engine, engine, Math.Min(Engine.Length, engine.Length));
        machine.Triggers.Load(Triggers, TriggerHeads, TriggerFree);
        machine.Creatures.Load(NoOrders, Sight.Select(s => KeyValuePair.Create(s.Object, (s.Kind, s.Entry, s.Range))));
        machine.Random.State = Random;
        memory.Load(Regions);
    }

    public void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        // Write a new file, then replace: a crash mid-save doesn't lose the old one.
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>The save in <paramref name="path"/>; throws <see cref="InvalidDataException"/> saying why it can't be read.</summary>
    public static SaveGame Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<SaveGame>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("empty file");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException(e.Message, e);
        }
    }
}
