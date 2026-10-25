using System.Buffers.Binary;
using DarkSun.Data.Items;
using DarkSun.Game.Characters;
using DarkSun.Game.Combat;
using DarkSun.Game.Magic;
using DarkSun.Game.Movement;
using DarkSun.Game.Scripting;

namespace DarkSun.Game.World;

public sealed partial class WorldGplHost
{
    /// <summary>What object fields are in each record (<c>GPLX</c> 1); without it only the modelled fields read.</summary>
    public GplFieldMap Fields { get; set; } = GplFieldMap.Empty;

    /// <summary>The character rules: thief skills, ability rolls, the sheets' numbers.</summary>
    public CharacterRules Rules { get; set; } = CharacterRules.Default;

    /// <summary>
    /// The item SETTHING made current, which <c>obj_special(0x2b)</c> reads
    /// (<c>gname[11]</c>, <c>3781:035F</c>), and whose items it walks.
    /// </summary>
    private Item? _thing;
    private WorldObject? _thingOwner;
    private int _thingNext;

    /// <summary>The engine variable SETTHING's item lives in (raw 0x2B, <c>gname[11]</c>).</summary>
    private const int ThingVariable = 0x2B;

    private CharacterCreation Creation => new(Rules, CharacterNames.Empty, new Random(0));

    private Character? SheetOf(WorldObject o) => o.Kind == ObjectKind.Party ? PartyCharacter(o.Index) : null;

    // -- object fields (GPLX) ---------------------------------------------------------------------------

    /// <summary>
    /// The record and field a reference names (<c>obj(id).f.g</c>): from the
    /// object (SETTHING's item for <c>obj_special(0x2b)</c>), each field but the
    /// last leading to the next record (<see cref="GplRecord.Follow"/>: the
    /// combat record's sheet, an item's name or type, an object by id or index).
    /// </summary>
    private GplRecord? Record(GameWorld world, GplObjectRef field, out int last)
    {
        last = field.Path.Length > 0 ? field.Path[^1] : 0;
        if (field.Path.Length == 0)
        {
            return null;
        }
        GplRecord? record = field.Id == (0x8000 | ThingVariable) && _thing is { } thing
            ? new GplItemRecord(Fields, thing, world.Items)
            : world.Resolve(field.Id) is { } obj ? new GplObjectRecord(Fields, obj, world, this) : null;
        foreach (byte f in field.Path[..^1])
        {
            record = record?.Follow(f);
        }
        return record;
    }

    /// <summary>A party member's combat record (58 bytes) as DSUN.EXE would hold it, from the sheet.</summary>
    internal byte[] PartyCombatBytes(WorldObject o)
    {
        var b = new byte[58];
        BinaryPrimitives.WriteInt16LittleEndian(b, (short)o.Vitals.Hp);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(4), (short)o.Index);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(6), (short)o.Id);
        (b[0x1C], b[0x1D], b[0x1E]) = ((byte)o.Status, 1, (byte)o.Mind);
        if (SheetOf(o) is { } c)
        {
            CharacterCreation creation = Creation;
            BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(2), (short)new Spellcasting(Rules).Psp(c));
            b[0x1A] = 10;
            b[0x1B] = (byte)(Rules.Races.FirstOrDefault(r => r.Race == c.Race)?.Move ?? 12);
            b[0x1F] = (byte)CharacterSheet.Thac0(creation.ActiveLevels(c), Rules);
            b[0x21] = (byte)(c.ComputerControl ? 0x20 : 0);
            for (int i = 0; i < 6; i++)
            {
                b[0x22 + i] = (byte)creation.Adjusted(c, (Ability)i);
            }
        }
        System.Text.Encoding.Latin1.GetBytes(o.Name.Length > 17 ? o.Name[..17] : o.Name).CopyTo(b, 0x28);
        return b;
    }

    /// <summary>A party member's character record (71 bytes) from the sheet: experience, most hit points and psionic points, class bits, race, sex, alignment, abilities, classes and levels, saves.</summary>
    internal byte[] PartySheetBytes(WorldObject o)
    {
        var b = new byte[71];
        if (SheetOf(o) is not { } c)
        {
            return b;
        }
        CharacterCreation creation = Creation;
        var magic = new Spellcasting(Rules);
        var active = creation.ActiveLevels(c);
        BinaryPrimitives.WriteInt32LittleEndian(b, c.Experience);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(8), (short)o.Vitals.MaxHp);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(0x0A), (short)o.Vitals.MaxHp);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(0x0C), (short)magic.MaxPsp(c));
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(0x10), (short)o.Id);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0x12), (ushort)Creation.UsableItemClasses(c));
        (b[0x18], b[0x19], b[0x1A]) = ((byte)(c.Race + 1), (byte)(c.Gender + 1), (byte)(c.Alignment + 1));
        for (int i = 0; i < 6; i++)
        {
            b[0x1B + i] = (byte)creation.Adjusted(c, (Ability)i);
        }
        for (int i = 0; i < Math.Min(3, active.Count); i++)
        {
            b[0x21 + i] = (byte)Spellcasting.ClassCode(active[i].Class, c.Sphere);
            b[0x24 + i] = (byte)active[i].Level;
        }
        b[0x27] = 10;
        b[0x2A] = (byte)CharacterSheet.AttackHalves(active, Rules);
        int[] saves = CharacterSheet.Saves(active, c.Race, creation.Adjusted(c, Ability.Con), Rules);
        for (int i = 0; i < saves.Length; i++)
        {
            b[0x37 + i] = (byte)saves[i];
        }
        return b;
    }

    /// <summary>A script writes a party member's sheet: experience, most hit points and the abilities reach it; other fields are dropped.</summary>
    internal void WritePartySheet(WorldObject o, int field, int value)
    {
        if (SheetOf(o) is not { } c)
        {
            return;
        }
        switch (Fields.OffsetOf(GplFieldMap.Character, field))
        {
            case 0:
                c.Experience = value;
                break;
            case 8:
                o.Vitals.MaxHp = c.Hp = value;
                o.Vitals.Hp = Math.Min(o.Vitals.Hp, value);
                break;
            case >= 0x1B and <= 0x20 and var at:
                var a = (Ability)(at - 0x1B);
                c.Rolled[(int)a] += value - Creation.Adjusted(c, a);
                break;
        }
    }

    // -- rolls -----------------------------------------------------------------------------------------

    /// <summary>
    /// STAT_ROLL(who, difficulty, ability) (<c>043D:1C43</c>, <c>7372:067D</c>):
    /// a d20 (0–19; 19 always fails) under the ability (0 Str … 5 Cha, combat
    /// record +0x22) + <see cref="CharacterRules.StatRollModifiers"/>[difficulty].
    /// For the party (32766), member after member until one succeeds.
    /// </summary>
    private int StatRoll(GplMachine machine, GameWorld world, int who, int difficulty, int ability)
    {
        bool Rolls(WorldObject o)
        {
            int d20 = machine.Random.Next() * 20 / 0x8000;
            int mod = difficulty >= 0 && difficulty < Rules.StatRollModifiers.Count ? Rules.StatRollModifiers[difficulty] : 0;
            return d20 != 19 && d20 < AbilityOf(o, ability) + mod;
        }
        if (who == WholeParty)
        {
            return world.Party.Where(p => !p.Hidden).Any(Rolls) ? 1 : 0;
        }
        return (who >= 0 ? world[who] : world.First(who)) is { } one && Rolls(one) ? 1 : 0;
    }

    private int AbilityOf(WorldObject o, int ability)
    {
        if (ability is < 0 or > 5)
        {
            return 0;
        }
        return SheetOf(o) is { } c ? Creation.Adjusted(c, (Ability)ability) : o.Record?.Stats[ability] ?? 0;
    }

    /// <summary>What a skill check knows of <paramref name="o"/>: a party member's thief level, Dexterity and gear; no one keeps conditions outside a fight.</summary>
    private ThiefSkills.Who SkillUser(WorldObject o)
    {
        Character? c = SheetOf(o);
        return new ThiefSkills.Who(
            c?.Race ?? Race.Human,
            c is null ? 0 : new Spellcasting(Rules).Level(c, CharacterClass.Thief),
            AbilityOf(o, (int)Ability.Dex),
            o.Vitals.Status == Vitals.Well,
            [.. o.Items.Where(i => i.Equipped).Select(i => (i.Slot, i.Rdff))],
            new HashSet<Condition>());
    }

    /// <summary>
    /// REQUEST(3, who, skill, situation) (<c>87B3:028D</c>): a thief skill check
    /// (<see cref="ThiefSkills"/>), 1 on success. For the party (32766) the
    /// first slot rolls, as DSUN.EXE has it, or the member with the best chance
    /// (<see cref="CharacterRules.PartySkillCheckBest"/>).
    /// </summary>
    public int SkillCheck(GplMachine machine, GameWorld world, int who, int skill, int situation)
    {
        WorldObject? roller = who switch
        {
            < 0 => null,
            WholeParty when Rules.PartySkillCheckBest => world.Party.Where(p => !p.Hidden)
                .MaxBy(p => ThiefSkills.Target(Rules, SkillUser(p), skill, situation)),
            WholeParty => world[0],
            _ => world[who],
        };
        return roller is not null && ThiefSkills.Succeeds(Rules, SkillUser(roller), skill, situation, machine.Random.Next()) ? 1 : 0;
    }

    /// <summary>
    /// REQUEST(7, name(−trap), x, y) (<c>87B3:0AE1</c>): the trap item is made at
    /// cell (x, y). The party's find/remove traps check (situation 0) avoids
    /// it, and so does Detect Traps on the active member unless the trap's +0x0F
    /// has 0x80; both leave it lying there and return 1. Otherwise its +0x0F
    /// sets it off: bits 0–2 d6s of damage to everyone of the party's side
    /// within bits 3–4 cells in sight of it, and it goes. (Bits 5–6 pick the
    /// element, <c>DS:251C</c>: poison, fire, slashing, electricity; the party
    /// takes them all in full. No one keeps Detect Traps outside a fight here.)
    /// </summary>
    private int SetOffTrap(GplMachine machine, GameWorld world, int who, int x, int y)
    {
        if (world.Spawn(Math.Abs(who), new Cell(x, y)) is not { } trap)
        {
            return 0;
        }
        if (trap.Item is { } record)
        {
            if (SkillCheck(machine, world, WholeParty, ThiefSkills.FindTraps, 0) == 1)
            {
                return 1;
            }
            int dice = record.Flags & 7, radius = (record.Flags >> 3) & 3;
            foreach (WorldObject t in world.Objects.Where(o => o.Index < GameWorld.MaxCreatures && o.Kind != ObjectKind.Thing && !o.Hidden
                && o.Vitals.Alive && (o.Kind == ObjectKind.Party || (o.Allegiance & 1) != 0)
                && GplGeometry.Distance(x, y, o.Cell.X, o.Cell.Y) <= radius
                && GplGeometry.LineOfSight(world.Region, x, y, o.Cell.X, o.Cell.Y)).ToList())
            {
                int damage = Enumerable.Range(0, dice).Sum(_ => machine.Random.Next() * 6 / 0x8000 + 1);
                world.Hurt(t, damage);
                Journal.Event(world.Region.Number, $"A trap! {t.Name} takes {damage}.");
            }
        }
        world.Remove(trap);
        return 0;
    }

    /// <summary>
    /// SETTHING(who, n) (<c>043D:140A</c>, <c>1A0A:261E</c>): the next of
    /// <paramref name="who"/>'s items becomes the current thing (<c>obj_special(0x2b)</c>);
    /// n = 9999 (or another owner) starts again from the first. 1 while there is
    /// one, 0 after the last.
    /// </summary>
    private int SetThing(GameWorld world, int who, int n)
    {
        WorldObject? owner = who >= 0 ? world[who] : world.First(who);
        if (n >= GameWorld.None || owner != _thingOwner)
        {
            _thingNext = 0;
        }
        _thingOwner = owner;
        if (owner is null || _thingNext >= owner.Items.Count)
        {
            _thing = null;
            return 0;
        }
        _thing = owner.Items[_thingNext++];
        return 1;
    }
}
