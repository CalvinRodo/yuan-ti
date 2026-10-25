using DarkSun.Data.Items;
using DarkSun.Game.Characters;
using DarkSun.Game.Magic;
using DarkSun.Game.World;
using Godot;

namespace DarkSun.Engine;

/// <summary>One of a character's classes on the sheet: its name, its level and the colour both are written in.</summary>
public sealed record ClassLevel(string Name, int Level, Color Colour);

/// <summary>What View Character writes about the selected character (null: an empty box).</summary>
public sealed record CharacterDetailsView(
    string Name, IReadOnlyList<int> Abilities, string Looks, string Alignment, string Classes, string Levels,
    int Experience, int? NextLevel, int Hp, int MaxHp, int Psp, int MaxPsp, int ArmorClass, string Damage,
    IReadOnlyList<ClassLevel> Parts);

/// <summary>
/// The text DSUN.EXE's View Character draws into <c>WIND</c> 11500
/// (<c>8F23:1E30</c>), at its coordinates in the window (a label's top: the
/// letters start 2 pixels lower), in the colours a recording of the original
/// shows:
/// <list type="bullet">
/// <item>under each box (columns 54 and 105, rows 30 and 90): hit points
/// "%d/%d" centred on column + 13 at row + 33, yellow (47) for the selected
/// box and grey (52) for the others; psionic points blue (39) at row + 40;
/// the status word (<see cref="PartyFrames.StatusNames"/>, <c>78A9:1FBB</c>)
/// centred in 50 pixels from column − 13 at row + 47, as the hit points;</item>
/// <item>the name in the name bar (<c>EBOX</c> 4003), upper case, or INACTIVE
/// CHARACTER;</item>
/// <item>STR: … CHR: at (149, 40 + 7i), their values at (173, …); gender and
/// race at (149, 86), alignment (149, 93); the classes (149, 106,
/// <c>78A9:2DC1</c>: "Cleric", "Psionic" …) and levels (149, 113, <c>6CCA:11C7</c>)
/// split by "/", each in its colour (<see cref="ClassColour"/>); EXP:n (next) at
/// (149, 120); HP: and PSI: at (149, 127) and (149, 134), their "%d/%d" centred
/// on 189; AC: at (149, 141) and the damage (<c>6CCA:1B69</c>) at (199, 141);
/// all in yellow (47, <c>DS:326E</c>).</item>
/// <item>the three readied weapons' slots (<c>APFM</c> 11215–11217, <c>9106:0B64</c>),
/// <c>BMP</c> 13007's stone, the item's picture centred over it.</item>
/// </list>
/// </summary>
public sealed class ViewCharacterText
{
    public static readonly Color Yellow = Color.Color8(228, 232, 24), Grey = Color.Color8(212, 212, 224), Blue = Color.Color8(40, 124, 196);

    /// <summary>The elements' colours in palette 1001: air 39 (the blue), earth 64, fire 77, water 56 (<c>78A9:2F59</c>).</summary>
    private static readonly Color Earth = Color.Color8(156, 108, 0), Fire = Color.Color8(212, 64, 40), Water = Color.Color8(104, 164, 112);

    /// <summary>The equipment slots the readied boxes show, as the recording does: the missile weapon, the weapon hand, the other hand.</summary>
    public static readonly int[] ReadiedSlots = [2, 3, 10];

    private const int FirstReadied = 11215;

    private static readonly string[] AbilityNames = ["STR:", "DEX:", "CON:", "INT:", "WIS:", "CHR:"];
    private const int Left = 149, Values = 173, Centre = 189, DamageX = 199;

    private readonly Label _name;
    private readonly Label[] _abilityNames = new Label[6], _abilities = new Label[6];
    private readonly Label _looks, _alignment, _experience, _hpName, _hp, _pspName, _psp, _armour, _damage;
    private readonly HBoxContainer _classes, _levels;
    private readonly Label[] _boxHp = new Label[4], _boxPsp = new Label[4], _boxStatus = new Label[4];
    private readonly TextureRect[] _readied = new TextureRect[ReadiedSlots.Length];
    private readonly Control _root = new() { MouseFilter = Control.MouseFilterEnum.Ignore };

    /// <param name="slotArt">The readied slots' stone (<c>BMP</c> 13007).</param>
    /// <param name="boxText">Write the hit points, psionic points and status under the boxes (the game's screen has <see cref="PartyFrames"/> do it).</param>
    public ViewCharacterText(UiWindowView window, Texture2D? slotArt, bool boxText = true)
    {
        window.AddChild(_root);
        for (int i = 0; i < ReadiedSlots.Length; i++)
        {
            Rect2 at = window.RectOf("APFM", FirstReadied + i);
            var stone = new TextureRect { Texture = slotArt, Position = at.Position, MouseFilter = Control.MouseFilterEnum.Ignore };
            _root.AddChild(stone);
            _readied[i] = new TextureRect { Position = at.Position, Size = at.Size, StretchMode = TextureRect.StretchModeEnum.KeepCentered, MouseFilter = Control.MouseFilterEnum.Ignore };
            _root.AddChild(_readied[i]);
        }
        Rect2 nameBox = window.RectOf("EBOX", 4003);
        _name = Add(_root, nameBox.Position, nameBox.Size.X, Yellow);
        for (int i = 0; i < 6; i++)
        {
            _abilityNames[i] = Add(_root, new Vector2(Left, 40 + 7 * i), Values - Left, Yellow);
            _abilityNames[i].Text = AbilityNames[i];
            _abilities[i] = Add(_root, new Vector2(Values, 40 + 7 * i), 30, Yellow);
        }
        _looks = Add(_root, new Vector2(Left, 86), 134, Yellow);
        _alignment = Add(_root, new Vector2(Left, 93), 134, Yellow);
        _classes = Parts(_root, new Vector2(Left, 106));
        _levels = Parts(_root, new Vector2(Left, 113));
        _experience = Add(_root, new Vector2(Left, 120), 134, Yellow);
        _hpName = Add(_root, new Vector2(Left, 127), 30, Yellow);
        _hp = Add(_root, new Vector2(Centre - 40, 127), 80, Yellow, HorizontalAlignment.Center);
        _pspName = Add(_root, new Vector2(Left, 134), 30, Yellow);
        _psp = Add(_root, new Vector2(Centre - 40, 134), 80, Yellow, HorizontalAlignment.Center);
        _armour = Add(_root, new Vector2(Left, 141), DamageX - Left, Yellow);
        _damage = Add(_root, new Vector2(DamageX, 141), 84, Yellow);
        for (int slot = 0; slot < 4 && boxText; slot++)
        {
            int column = slot % 2 == 0 ? 54 : 105, row = slot < 2 ? 30 : 90;
            _boxHp[slot] = Add(_root, new Vector2(column + 13 - 25, row + 33), 50, Grey, HorizontalAlignment.Center);
            _boxPsp[slot] = Add(_root, new Vector2(column + 13 - 25, row + 40), 50, Blue, HorizontalAlignment.Center);
            _boxStatus[slot] = Add(_root, new Vector2(column - 13, row + 47), 50, Grey, HorizontalAlignment.Center);
        }
    }

    /// <summary>
    /// What View Character and Character Generation write about
    /// <paramref name="c"/> out of the game, carrying <paramref name="items"/>
    /// (none on the creation screen, where the gear isn't given yet): armour
    /// class 10 with Dexterity's adjustment and the worn armour's, and the
    /// damage of the weapon in hand (slot 3), else of bare hands (or a
    /// thri-kreen's first natural attack), with Strength's bonus.
    /// </summary>
    public static CharacterDetailsView Of(Character c, IReadOnlyList<ItemState>? items = null) =>
        items is { Count: > 0 } ? Of(c, Carried(items, Session.ObjectData), ItemCatalog.Load(Session.ObjectData)) : Of(c, [], null);

    /// <summary>The same for <paramref name="c"/> carrying <paramref name="carried"/> (the items' types in <paramref name="catalog"/>; none: no items).</summary>
    public static CharacterDetailsView Of(Character c, IReadOnlyList<Item> carried, ItemCatalog? catalog)
    {
        var cc = new CharacterCreation(Session.Rules, Session.Names, new Random());
        var rules = Session.Rules;
        int str = cc.Adjusted(c, Ability.Str), dex = cc.Adjusted(c, Ability.Dex);
        int halves = CharacterSheet.AttackHalves(cc.ActiveLevels(c), rules), strDamage = rules.Abilities.StrDamageOf(str);
        int baseArmorClass = CharacterSheet.BaseArmorClassOf(c.Race, rules);
        int armorClass = baseArmorClass + CharacterSheet.DexterityDefence(dex, rules);
        string? damage = null;
        if (catalog is not null && carried.Count > 0)
        {
            armorClass = CharacterSheet.ArmorClass(dex, carried, catalog, rules, baseArmorClass);
            if (carried.FirstOrDefault(i => i.Slot == 3) is { } held && catalog.TypeOf(held.Record) is { IsWeapon: true } weapon)
            {
                damage = CharacterSheet.DamageText(halves, weapon.DamageDice, weapon.DamageSides, weapon.DamageBonus + held.Record.Bonus + strDamage);
            }
        }
        damage ??= rules.Race(c.Race).NaturalAttacks is [var first, ..]
            ? CharacterSheet.DamageText(first.Halves, first.Dice, first.Sides, first.Bonus + strDamage)
            : CharacterSheet.DamageText(halves, rules.UnarmedDice, rules.UnarmedSides, strDamage);
        int psp = new Spellcasting(rules).MaxPsp(c);
        var levels = cc.Levels(c);
        List<ClassLevel> parts =
        [
            .. c.Former.Select(f => new ClassLevel(SheetName(f.Class), f.Level, ClassColour(f.Class, c.Sphere))),
            .. c.Classes.Select((k, n) => new ClassLevel(SheetName(k), levels[n], ClassColour(k, c.Sphere))),
        ];
        return new CharacterDetailsView(
            c.Name,
            [.. Enumerable.Range(0, 6).Select(i => cc.Adjusted(c, (Ability)i))],
            $"{c.Gender.ToString().ToUpperInvariant()} {rules.Race(c.Race).Name.ToUpperInvariant()}",
            string.Concat(c.Alignment.ToString().Select((ch, i) => i > 0 && char.IsUpper(ch) ? " " + ch : ch.ToString())).ToUpperInvariant(),
            string.Join("/", c.Former.Select(f => rules.Class(f.Class).Name).Concat(c.Classes.Select(k => rules.Class(k).Name))),
            string.Join("/", c.Former.Select(f => f.Level).Concat(cc.Levels(c))),
            c.Experience, cc.NextLevelExperience(c), c.Hp, c.Hp, Math.Max(0, psp - c.PspSpent), psp,
            armorClass, damage, parts);
    }

    /// <summary>A class's name as the sheet writes it (<c>DS:1259</c>: Cleric, Druid, Fighter, Gladiator, Preserver, Psionic, Ranger, Thief).</summary>
    public static string SheetName(CharacterClass k) => k == CharacterClass.Psionicist ? "Psionic" : Session.Rules.Class(k).Name;

    /// <summary>
    /// <c>78A9:2F59</c>: a cleric's, druid's or ranger's name and level in its element's
    /// colour (class codes 1–8 and 13–16), any other class's in the text's yellow.
    /// </summary>
    public static Color ClassColour(CharacterClass k, Sphere? element) =>
        k is CharacterClass.Cleric or CharacterClass.Druid or CharacterClass.Ranger && element is { } e
            ? e switch { Sphere.Air => Blue, Sphere.Earth => Earth, Sphere.Fire => Fire, _ => Water }
            : Yellow;

    /// <summary><paramref name="items"/> as items, their records read from <paramref name="data"/> (any that isn't an item left out).</summary>
    public static List<Item> Carried(IEnumerable<ItemState> items, DarkSun.Data.Gff.IResourceSource data) =>
        [.. items.Select(s => data.TryRead("RDFF", s.Rdff, out var rdff) && ItemRecord.FromRdff(rdff) is { } record
            ? new Item(s.Rdff, record, s.Quantity, s.Slot)
            : null).OfType<Item>()];

    /// <summary>The selected character's details, or an empty box's.</summary>
    public void Show(CharacterDetailsView? c)
    {
        _name.Text = c?.Name.ToUpperInvariant() ?? "INACTIVE CHARACTER";
        foreach (Label l in _abilityNames.Concat([_hpName, _pspName]))
        {
            l.Visible = c is not null;
        }
        _hpName.Text = "HP:";
        _pspName.Text = "PSI:";
        for (int i = 0; i < 6; i++)
        {
            _abilities[i].Text = c is null ? "" : $"{c.Abilities[i]}";
        }
        _looks.Text = c?.Looks ?? "";
        _alignment.Text = c?.Alignment ?? "";
        Fill(_classes, c?.Parts.Select(p => (p.Name, p.Colour)));
        Fill(_levels, c?.Parts.Select(p => (p.Level.ToString(), p.Colour)));
        _experience.Text = c is null ? "" : $"EXP:{c.Experience}" + (c.NextLevel is int next ? $" ({next})" : "");
        _hp.Text = c is null ? "" : $"{c.Hp}/{c.MaxHp}";
        _psp.Text = c is null ? "" : $"{c.Psp}/{c.MaxPsp}";
        _armour.Text = c is null ? "" : $"AC:{c.ArmorClass,3}";
        _damage.Text = c is null ? "" : $"DAM: {c.Damage}";
    }

    /// <summary>The readied weapons' pictures (null: an empty slot), in <see cref="ReadiedSlots"/>' order.</summary>
    public void ShowReadied(IReadOnlyList<Texture2D?> pictures)
    {
        for (int i = 0; i < _readied.Length; i++)
        {
            _readied[i].Texture = i < pictures.Count ? pictures[i] : null;
        }
    }

    /// <summary>What it says, line by line (for the checks).</summary>
    public string Text => string.Join("\n",
        new[] { _name.Text }
            .Concat(_abilityNames.Zip(_abilities, (n, v) => $"{n.Text} {v.Text}"))
            .Concat([_looks.Text, _alignment.Text, Line(_classes), Line(_levels), _experience.Text,
                $"{_hpName.Text} {_hp.Text}", $"{_pspName.Text} {_psp.Text}", $"{_armour.Text} {_damage.Text}"]));

    private static string Line(HBoxContainer line) => string.Concat(line.GetChildren().OfType<Label>().Select(l => l.Text));

    /// <summary>Show or hide all of it (the game's spell pages share the window: their slots take the readied slots' places).</summary>
    public bool Visible
    {
        get => _root.Visible;
        set => _root.Visible = value;
    }

    /// <summary>A line written in parts, each in its own colour, one after the other.</summary>
    private static HBoxContainer Parts(Control parent, Vector2 at)
    {
        var line = new HBoxContainer { Position = at, MouseFilter = Control.MouseFilterEnum.Ignore };
        line.AddThemeConstantOverride("separation", 0);
        parent.AddChild(line);
        return line;
    }

    /// <summary>Write <paramref name="parts"/> into <paramref name="line"/> with a "/" in the text's colour between them.</summary>
    private static void Fill(HBoxContainer line, IEnumerable<(string Text, Color Colour)>? parts)
    {
        foreach (Node child in line.GetChildren())
        {
            line.RemoveChild(child);
            child.QueueFree();
        }
        foreach (var ((text, colour), n) in (parts ?? []).Select((p, n) => (p, n)))
        {
            if (n > 0)
            {
                line.AddChild(Piece("/", Yellow));
            }
            line.AddChild(Piece(text, colour));
        }
    }

    private static Label Piece(string text, Color colour) => new()
    {
        Text = text,
        MouseFilter = Control.MouseFilterEnum.Ignore,
        LabelSettings = GameFonts.Settings(colour),
    };

    /// <summary>The text under box <paramref name="slot"/>: its member's points and status, the selected box's in yellow.</summary>
    public void ShowBox(int slot, PartyMemberView? m, bool selected)
    {
        if (_boxHp[slot] is null)
        {
            return;
        }
        _boxHp[slot].Text = m is null ? "" : $"{m.Hp}/{m.MaxHp}";
        _boxPsp[slot].Text = m is null ? "" : $"{m.Psp}/{m.MaxPsp}";
        _boxStatus[slot].Text = m is null ? "" : PartyFrames.StatusName(m.Status);
        Color colour = selected ? Yellow : Grey;
        _boxHp[slot].LabelSettings = GameFonts.Settings(colour);
        _boxStatus[slot].LabelSettings = GameFonts.Settings(colour);
    }

    private static Label Add(Control parent, Vector2 at, float width, Color colour, HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var label = new Label
        {
            Position = at,
            Size = new Vector2(width, GameFonts.Size),
            HorizontalAlignment = align,
            ClipText = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            LabelSettings = GameFonts.Settings(colour),
        };
        parent.AddChild(label);
        return label;
    }
}
