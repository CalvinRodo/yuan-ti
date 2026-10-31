using DarkSun.Data.Items;
using DarkSun.Data.Regions;
using DarkSun.Game.Characters;
using DarkSun.Game.Movement;
using DarkSun.Game.Rules;
using DarkSun.Game.World;

namespace DarkSun.Game.Combat;

/// <summary>A weapon in use: its type and the item's bonus (record +0x14).</summary>
public sealed record Weapon(ItemType Type, int Bonus = 0, Item? Item = null)
{
    public bool IsMissile => Type.Range > 1;

    /// <summary>Magic: a bonus, or a named magic item (<c>IT1R</c> +8 &amp; 0x80).</summary>
    public bool Magic => Bonus > 0 || (Type.Material & 0x80) != 0;

    /// <summary>
    /// What the AI's weapon score counts as magic (<c>612E:0350</c>): a bonus,
    /// the item's +0x0F, or <c>IT1R</c> +8 &amp; 4, which every metal (4) and
    /// leather (5) item has, as the original reads it.
    /// </summary>
    public bool ScoresAsMagic => Bonus > 0 || Item is { Record.Flags: not 0 } || (Type.Material & 0x04) != 0;

    /// <summary>What a hit is made of: the type's damage bits and the magic bits for its bonus (<c>612E:1359</c>).</summary>
    public DamageElement Elements => (DamageElement)Type.DamageType | Bonus switch
    {
        <= 0 => DamageElement.None,
        1 => DamageElement.Plus1,
        2 => DamageElement.Plus1 | DamageElement.Plus2,
        _ => DamageElement.Plus1 | DamageElement.Plus2 | DamageElement.Plus3,
    };

    /// <summary>
    /// The to-hit penalty of a weapon not made of metal, when it has no bonus and
    /// isn't magic (<c>6868:0411</c>): wooden −3, bone −1, stone and obsidian −2.
    /// </summary>
    public int MaterialToHit => Magic ? 0 : Type.MadeOf switch
    {
        ItemMaterial.Wooden => -3,
        ItemMaterial.Bone => -1,
        ItemMaterial.Stone or ItemMaterial.Obsidian => -2,
        _ => 0,
    };

    /// <summary>The damage penalty for the material (<c>6868:0489</c>): wooden −2, bone, stone, obsidian −1.</summary>
    public int MaterialDamage => Type.MadeOf switch
    {
        ItemMaterial.Wooden => -2,
        ItemMaterial.Bone or ItemMaterial.Stone or ItemMaterial.Obsidian => -1,
        _ => 0,
    };
}

/// <summary>
/// One side of a fight: what DSUN.EXE reads from a combatant's combat and
/// character records (<c>dsun-exe-reference.md</c> §8) and its state for the
/// fight. <see cref="FromCreature"/> builds one from a region creature; a
/// party member's values come from its character sheet. <see cref="Vitals"/>
/// is shared with the world object, so damage done here is the creature's.
/// </summary>
public sealed class Combatant
{
    public required string Name { get; init; }

    /// <summary>Allegiance (+0x1D): 1 the party's side, 2 its enemies; &amp; 3 fights.</summary>
    public int Side { get; set; }

    /// <summary>
    /// The allegiance it goes back to when a charm ends (character record +0x3C,
    /// <c>8053:066F</c>); 0 takes <see cref="Side"/> as the battle starts.
    /// </summary>
    public int HomeSide { get; set; }

    public Vitals Vitals { get; init; } = new() { Hp = 1, MaxHp = 1 };

    public Cell Cell { get; set; }

    /// <summary>Facing 0–7 clockwise from north.</summary>
    public int Facing { get; set; }

    /// <summary>A party member (indexes 0–3): the AI uses no psionics for them, they give no experience.</summary>
    public bool PartyMember { get; init; }

    /// <summary>Computer control (+0x21 &amp; 0x20); conditions such as Charmed also hand it over (<see cref="UnderAi"/>).</summary>
    public bool ComputerControlled { get; set; }

    /// <summary>Armour class before effects: the character record's +0x27 for a creature, the sheet's for a member.</summary>
    public int ArmorClass { get; set; } = 10;

    /// <summary>Of <see cref="ArmorClass"/>, what shields give (they don't count from behind).</summary>
    public int ShieldClass { get; init; }

    public int Thac0 { get; init; } = 20;

    /// <summary>Move (+0x1B): movement points are this × 10 a round.</summary>
    public int Move { get; init; } = 12;

    /// <summary>Its own Str, Dex, Con, Int, Wis, Cha (character record +0x1B); Raise Dead takes a point of Con. What counts is <see cref="Score"/>.</summary>
    public int[] Stats { get; init; } = [10, 10, 10, 10, 10, 10];

    /// <summary>Ability <paramref name="ability"/> (0 Str … 5 Cha) with its effects and items (combat record +0x22, <see cref="AbilityScores"/>).</summary>
    public int Score(int ability) => AbilityScores.Of(Stats, Effects, Items)[ability];

    /// <summary>Strength with its effects and items (<see cref="Score"/>).</summary>
    public int Strength => Score(0);

    /// <summary>The creature type (+0x16): its abilities and resistances (<see cref="DarkSun.Game.Rules.CreatureRules"/>).</summary>
    public int Type { get; init; }

    /// <summary>What the AI does when no condition overrides it (+0x1E; 1 flees, 8 stands by).</summary>
    public int Mind { get; init; } = 2;

    /// <summary>The level its spells and abilities are cast at.</summary>
    public int Level { get; init; } = 1;

    /// <summary>Character record +0x18: 9 is undead, 8 thri-kreen, 14 a summoned or planar creature (Dismissal, Dispelling Evil).</summary>
    public int Race { get; init; }

    /// <summary>Character record +0x1A, 1–9 (3, 6, 9 evil).</summary>
    public int Alignment { get; init; }

    /// <summary>Its classes (character record +0x21 codes, +0x24 levels): what its spells per level and some saves go by.</summary>
    public IReadOnlyList<(int Code, int Level)> Classes { get; init; } = [];

    /// <summary>Its level in class <paramref name="code"/> (<c>6698:0C54</c>), 0 none.</summary>
    public int LevelIn(int code) => Classes.Where(c => c.Code == code).Select(c => c.Level).DefaultIfEmpty(0).Max();

    /// <summary>The sound when a blow hurts it (character record +0x42), 0 the default (4).</summary>
    public int HurtSound { get; init; }

    /// <summary>The sound of its blows (character record +0x44), 0 none.</summary>
    public int AttackSound { get; init; }

    /// <summary>Its footprint (character record +0x3D, <c>1A0A:2AB4</c>): x ± (size &amp; 7), y − size / 8 … y.</summary>
    public int Size { get; init; }

    /// <summary>The cells it stands on.</summary>
    public IEnumerable<Cell> Footprint
    {
        get
        {
            int w = Size & 7, h = Size >> 3;
            for (int dy = -h; dy <= 0; dy++)
            {
                for (int dx = -w; dx <= w; dx++)
                {
                    yield return new Cell(Cell.X + dx, Cell.Y + dy);
                }
            }
        }
    }

    public int MagicResistance { get; init; }

    /// <summary>The five saving throws (paralysation, rod, petrification, breath, spell).</summary>
    public IReadOnlyList<int> Saves { get; init; } = [16, 16, 16, 16, 16];

    /// <summary>Attacks a round in halves (+0x2A; 3 alternates 1 and 2).</summary>
    public int AttackHalves { get; init; } = 2;

    /// <summary>Attacks without a weapon (+0x2B …).</summary>
    public IReadOnlyList<NaturalAttack> NaturalAttacks { get; init; } = [new(2, 1, 2, 0)];

    /// <summary>The weapon in hand (slot 3), if any.</summary>
    public Weapon? Weapon { get; set; }

    /// <summary>A missile weapon (slot 2) and how many shots its ammunition (slot 1) has.</summary>
    public Weapon? MissileWeapon { get; set; }

    public int Ammunition { get; set; }

    /// <summary>
    /// Does the missile weapon shoot ammunition (<c>IT1R</c> +0 &amp; 8, a bow)?
    /// Others (a chatkcha, a sling) need none: <c>612E:1FCF</c> counts the
    /// ammunition slot only for these, 999 shots otherwise, and only their
    /// ammunition is used up (<c>612E:20A1</c>).
    /// </summary>
    public bool ShootsAmmunition => MissileWeapon is { } w && (w.Type.Weapon & WeaponKind.UsesAmmunition) != 0;

    /// <summary>Has it a missile weapon it can shoot now (ammunition left, if it takes any)?</summary>
    public bool CanShoot => MissileWeapon is not null && (!ShootsAmmunition || Ammunition > 0);

    /// <summary>A thief's level, for backstabs (0 for others).</summary>
    public int ThiefLevel { get; init; }

    /// <summary>
    /// Spells it can still cast this fight (<c>SPIN</c> numbers, one entry a
    /// cast). A creature's come from <see cref="SpellSet"/> the first time it
    /// thinks of casting (<see cref="Battle.CastsFor"/>), unless set here.
    /// </summary>
    public List<int> Spells { get; init; } = [];

    /// <summary>A party member's psionic powers (a creature's come from <see cref="PsionicSet"/>).</summary>
    public IReadOnlyList<int> Powers { get; init; } = [];

    /// <summary>A party member's character sheet, which the fight's end updates (<see cref="Battle.SyncSheets"/>).</summary>
    public Character? Sheet { get; init; }

    /// <summary>
    /// A party member's spells by level, as DSUN.EXE counts them: the level of
    /// each spell in <see cref="Spells"/> (a key of <see cref="CastsLeft"/>), whose
    /// casts all its spells share. Null for a creature: then <see cref="Spells"/>
    /// has one entry a cast.
    /// </summary>
    public IReadOnlyDictionary<int, int>? SpellLevels { get; init; }

    /// <summary>A party member's casts left at each level of <see cref="SpellLevels"/>.</summary>
    public Dictionary<int, int>? CastsLeft { get; init; }

    /// <summary>How many more times it can cast <paramref name="spell"/> this fight.</summary>
    public int CastsOf(int spell) =>
        CastsLeft is { } left && SpellLevels!.TryGetValue(spell, out int level) && Spells.Contains(spell)
            ? left.GetValueOrDefault(level) : Spells.Count(n => n == spell);

    /// <summary>The spells it cast of its own this fight (for its character sheet).</summary>
    internal List<int> SpellsCast { get; } = [];

    /// <summary>Its psionic points when the fight began.</summary>
    internal int PspAtStart { get; set; }

    /// <summary>Its spell set (character record +0x3E), 0 none.</summary>
    public int SpellSet { get; init; }

    /// <summary>Its casts have been counted (<c>6798:0000</c> keeps them per creature).</summary>
    internal bool SpellsCounted { get; set; }

    public int Psp { get; set; }

    /// <summary>Its psionic set (+0x46), 0 none.</summary>
    public int PsionicSet { get; init; }

    /// <summary>
    /// Powers a party member keeps up (<c>6698:04C7</c>): each round it pays
    /// their maintenance, or they end. Defence modes kept up shield it from
    /// psionic attacks (<see cref="Battle.PsionicDefence"/>).
    /// </summary>
    public HashSet<int> Maintained { get; } = [];

    /// <summary>
    /// A party member's rank in each power (<c>DS:0077 + 0x22·member + power</c>,
    /// bits 3–7; <c>6698:0137</c>): 1 when learned, +1 each time it is learned
    /// again from an item (<c>91F7:03D0</c>, <c>6698:0685</c>), at most 30. It adds
    /// to the power check and to a defence mode. Missing powers, and monsters, count 1.
    /// </summary>
    public Dictionary<int, int> PowerRanks { get; init; } = [];

    /// <summary>A party member's ranks from its character (<see cref="Character.PowerRanks"/>), for <see cref="PowerRanks"/>.</summary>
    public static Dictionary<int, int> PowerRanksOf(Character c) => c.Powers.Distinct().ToDictionary(n => n, c.PowerRank);

    /// <summary>Its rank in power <paramref name="number"/> (<see cref="PowerRanks"/>).</summary>
    public int PowerRank(int number) => PartyMember ? PowerRanks.GetValueOrDefault(number, 1) : 1;

    /// <summary>What it carries; corrosion destroys items here.</summary>
    public List<Item> Items { get; init; } = [];

    /// <summary>The region object it is, if any.</summary>
    public WorldObject? Source { get; init; }

    public List<ActiveEffect> Effects { get; } = [];

    // -- the fight's state (612E:0754, 612E:0BAD) ------------------------------------------------

    public int Initiative { get; internal set; } = -1;

    internal int TieBreak { get; set; }

    public int MovementPoints { get; internal set; }

    public TurnFlags Flags { get; internal set; }

    /// <summary>Guarding (<c>3931:0231</c>): attacks the first enemy that moves next to it.</summary>
    public bool Guarding { get; internal set; }

    /// <summary>The AI's target (<c>3931:01DD</c>).</summary>
    public Combatant? Target { get; internal set; }

    /// <summary>The AI has tried its spells this round (<c>37BD:00AF</c>).</summary>
    internal bool TriedSpells { get; set; }

    /// <summary>
    /// It took damage this round: until the next round it can't cast a spell or use a
    /// power, whatever its class (the original's YOU WERE HIT, <c>8F23:1594</c>, for
    /// wizards, priests and psionicists alike; set by <c>612E:1A74</c>, cleared each round).
    /// </summary>
    public bool WasHit { get; set; }

    /// <summary>This turn's mind (<c>DS:4946</c>, <c>8053:000E</c>).</summary>
    public int CurrentMind { get; internal set; }

    public bool Alive => Vitals.Alive;

    /// <summary>Still in the fight: alive, on a side that fights.</summary>
    public bool Fights => Alive && (Side & 3) != 0;

    public bool Has(Condition c) => Effects.Any(e => e.Condition == c);

    /// <summary>Afraid, Charmed, Berserk and Confused hand a creature to the AI (combat record +0x21 |= 0x60, <c>8053:03C0</c>).</summary>
    public bool UnderAi => ComputerControlled || Has(Condition.Afraid) || Has(Condition.Charmed) || Has(Condition.Berserk) || Has(Condition.Confused);

    /// <summary>Is <paramref name="other"/> on a side this one fights (allegiance &amp; (3 − own))?</summary>
    public bool IsEnemyOf(Combatant other) => other.Fights && (other.Side & (3 - (Side & 3))) != 0;

    public bool IsUndead => Race == 9;

    public override string ToString() => Name;

    /// <summary>
    /// A region creature as a combatant: its combat record (THAC0, Move, the
    /// abilities, type, mind, flags), its character record (armour class,
    /// classes and levels, alignment, size, saves, magic resistance, attacks,
    /// spell and psionic sets) and the weapons it wears (<paramref name="catalog"/>
    /// gives their types). Its casts are counted when it first thinks of a spell
    /// (<see cref="Battle.CastsFor"/>).
    /// </summary>
    public static Combatant FromCreature(WorldObject obj, ItemCatalog? catalog = null) =>
        Build(obj.Record, obj.Character, obj.Items, obj.Vitals, obj.Name, obj.Allegiance, obj.Kind == ObjectKind.Party,
            catalog, obj, obj.Cell, obj.Facing);

    /// <summary>A creature straight from its <c>RDFF</c> (<see cref="CreatureDefinition"/>), carrying its items, with full hit points.</summary>
    public static Combatant FromDefinition(CreatureDefinition d, ItemCatalog? catalog = null, Cell cell = default)
    {
        List<Item> items = [.. d.Carried.Select(r => new Item(-r.Id, r, 1, r.Slot < Equipment.Slots ? r.Slot : Equipment.Pack))];
        var vitals = new Vitals { Hp = Math.Max(1, d.HitPoints), MaxHp = Math.Max(1, d.HitPoints) };
        return Build(d.Combat, d.Character, items, vitals, d.Combat.Name, d.Combat.Allegiance, false, catalog, null, cell, 0);
    }

    private static Combatant Build(CombatRecord? r, CharacterRecord? ch, List<Item> carried, Vitals vitals, string name, int side,
        bool party, ItemCatalog? catalog, WorldObject? source, Cell cell, int facing)
    {
        Weapon? WornIn(int slot) =>
            catalog is null ? null
            : carried.FirstOrDefault(i => i.Slot == slot) is { } item && catalog.TypeOf(item.Record) is { IsWeapon: true } type
                ? new Weapon(type, item.Record.Bonus, item)
                : null;
        Item? ammo = carried.FirstOrDefault(i => i.Slot == 1);
        return new Combatant
        {
            Name = name.Length > 0 ? name : r?.Name ?? "",
            Side = side,
            Vitals = vitals,
            Cell = cell,
            Facing = facing,
            PartyMember = party,
            // Any creature but a party member is the AI's whatever its record says: loading a combat record into a
            // slot past the party's sets flag 0x20 (7155:0732). The arena's Legcrusher and Semyon, freed, have it clear.
            ComputerControlled = !party || (r?.ComputerControlled ?? false),
            ArmorClass = source?.SheetField(WorldObject.SheetBaseArmorClass) ?? ch?.BaseArmorClass ?? r?.Ac ?? 10,
            Thac0 = r?.Thac0 ?? 20,
            Move = r?.Move ?? 12,
            Stats = r is null ? [10, 10, 10, 10, 10, 10]
                : [.. r.Stats.Select((s, i) => source?.StoredField(WorldObject.FieldStr + i) ?? s)],
            HomeSide = ch is { HomeAllegiance: > 0 } ? ch.HomeAllegiance : side,
            Type = (source?.StoredField(WorldObject.FieldSpecialAttack) ?? r?.SpecialAttack ?? 0) & 0xFF, // GPL 81 takes Balkazar's away
            Mind = source?.Mind ?? r?.Mind ?? 2,
            Level = ch is null ? 1 : Math.Max(1, (int)ch.Levels.ToArray().Max()),
            Race = ch?.Race ?? 0,
            Alignment = ch?.Alignment ?? 0,
            Classes = ch is null ? [] : [.. Enumerable.Range(0, 3).Where(i => ch.ClassCodes[i] != 0).Select(i => ((int)ch.ClassCodes[i], (int)ch.Levels[i]))],
            Size = ch?.Size ?? 0,
            HurtSound = ch?.HurtSound ?? 0,
            AttackSound = ch?.AttackSound ?? 0,
            MagicResistance = Math.Max(0, (int)(ch?.MagicResistance ?? 0)),
            Saves = ch is null ? [16, 16, 16, 16, 16] : [.. ch.Saves.ToArray().Select(s => (int)s)],
            AttackHalves = ch is { AttackHalves: > 0 } ? ch.AttackHalves : 2,
            NaturalAttacks = ch is null ? [new(2, 1, 2, 0)]
                : [.. Enumerable.Range(0, CharacterRecord.NaturalAttackCount).Select(ch.NaturalAttack).Where(n => n.Exists)],
            Weapon = WornIn(3),
            MissileWeapon = WornIn(2),
            Ammunition = ammo?.Quantity ?? 0,
            SpellSet = ch?.SpellSet ?? 0,
            Psp = source?.StoredField(WorldObject.FieldPsp) ?? r?.Psp ?? 0,
            PsionicSet = ch?.PsionicSet ?? 0,
            Items = carried,
            Source = source,
        };
    }
}

/// <summary>A combatant's action flags (<c>37BD:01D5</c>), 0x3FF at the start of a round.</summary>
[Flags]
public enum TurnFlags
{
    None = 0,
    MayMove = 0x001,
    MayAttack = 0x002,
    Waited = 0x010,
    Reequip = 0x040,
    MayUsePsionics = 0x080,
    NewRound = 0x100,
    MayUseAbility = 0x200,
    All = 0x3FF,
}
