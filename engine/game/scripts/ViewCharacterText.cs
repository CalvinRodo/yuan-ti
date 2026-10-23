using DarkSun.Game.Characters;
using DarkSun.Game.Magic;
using Godot;

namespace DarkSun.Engine;

/// <summary>What View Character writes about the selected character (null: an empty box).</summary>
public sealed record CharacterDetailsView(
    string Name, IReadOnlyList<int> Abilities, string Looks, string Alignment, string Classes, string Levels,
    int Experience, int? NextLevel, int Hp, int MaxHp, int Psp, int MaxPsp, int ArmorClass, string Damage);

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
/// race at (149, 86), alignment (149, 93); the classes (149, 106) and levels
/// (149, 113) in blue; EXP:n (next) at (149, 120); HP: and PSI: at (149, 127)
/// and (149, 134), their "%d/%d" centred on 189; AC: at (149, 141) and the
/// damage (<c>6CCA:1B69</c>) at (199, 141); all but the classes yellow.</item>
/// </list>
/// </summary>
public sealed class ViewCharacterText
{
    public static readonly Color Yellow = Color.Color8(228, 232, 24), Grey = Color.Color8(212, 212, 224), Blue = Color.Color8(40, 124, 196);

    private static readonly string[] AbilityNames = ["STR:", "DEX:", "CON:", "INT:", "WIS:", "CHR:"];
    private const int Left = 149, Values = 173, Centre = 189, DamageX = 199;

    private readonly Label _name;
    private readonly Label[] _abilityNames = new Label[6], _abilities = new Label[6];
    private readonly Label _looks, _alignment, _classes, _levels, _experience, _hpName, _hp, _pspName, _psp, _armour, _damage;
    private readonly Label[] _boxHp = new Label[4], _boxPsp = new Label[4], _boxStatus = new Label[4];

    public ViewCharacterText(UiWindowView window)
    {
        Rect2 nameBox = window.RectOf("EBOX", 4003);
        _name = Add(window, nameBox.Position, nameBox.Size.X, Yellow);
        for (int i = 0; i < 6; i++)
        {
            _abilityNames[i] = Add(window, new Vector2(Left, 40 + 7 * i), Values - Left, Yellow);
            _abilityNames[i].Text = AbilityNames[i];
            _abilities[i] = Add(window, new Vector2(Values, 40 + 7 * i), 30, Yellow);
        }
        _looks = Add(window, new Vector2(Left, 86), 134, Yellow);
        _alignment = Add(window, new Vector2(Left, 93), 134, Yellow);
        _classes = Add(window, new Vector2(Left, 106), 134, Blue);
        _levels = Add(window, new Vector2(Left, 113), 134, Blue);
        _experience = Add(window, new Vector2(Left, 120), 134, Yellow);
        _hpName = Add(window, new Vector2(Left, 127), 30, Yellow);
        _hp = Add(window, new Vector2(Centre - 40, 127), 80, Yellow, HorizontalAlignment.Center);
        _pspName = Add(window, new Vector2(Left, 134), 30, Yellow);
        _psp = Add(window, new Vector2(Centre - 40, 134), 80, Yellow, HorizontalAlignment.Center);
        _armour = Add(window, new Vector2(Left, 141), DamageX - Left, Yellow);
        _damage = Add(window, new Vector2(DamageX, 141), 84, Yellow);
        for (int slot = 0; slot < 4; slot++)
        {
            int column = slot % 2 == 0 ? 54 : 105, row = slot < 2 ? 30 : 90;
            _boxHp[slot] = Add(window, new Vector2(column + 13 - 25, row + 33), 50, Grey, HorizontalAlignment.Center);
            _boxPsp[slot] = Add(window, new Vector2(column + 13 - 25, row + 40), 50, Blue, HorizontalAlignment.Center);
            _boxStatus[slot] = Add(window, new Vector2(column - 13, row + 47), 50, Grey, HorizontalAlignment.Center);
        }
    }

    /// <summary>
    /// What View Character and Character Generation write about
    /// <paramref name="c"/> out of the game: wearing nothing, so armour class
    /// 10 with Dexterity's adjustment and the damage of bare hands (or a
    /// thri-kreen's first natural attack) with Strength's bonus.
    /// </summary>
    public static CharacterDetailsView Of(Character c)
    {
        var cc = new CharacterCreation(Session.Rules, Session.Names, new Random());
        var rules = Session.Rules;
        int str = cc.Adjusted(c, Ability.Str), dex = cc.Adjusted(c, Ability.Dex);
        string damage = rules.Race(c.Race).NaturalAttacks is [var first, ..]
            ? CharacterSheet.DamageText(first.Halves, first.Dice, first.Sides, first.Bonus + rules.Abilities.StrDamageOf(str))
            : CharacterSheet.DamageText(CharacterSheet.AttackHalves(cc.ActiveLevels(c), rules), rules.UnarmedDice, rules.UnarmedSides, rules.Abilities.StrDamageOf(str));
        int psp = new Spellcasting(rules).MaxPsp(c);
        return new CharacterDetailsView(
            c.Name,
            [.. Enumerable.Range(0, 6).Select(i => cc.Adjusted(c, (Ability)i))],
            $"{c.Gender.ToString().ToUpperInvariant()} {rules.Race(c.Race).Name.ToUpperInvariant()}",
            string.Concat(c.Alignment.ToString().Select((ch, i) => i > 0 && char.IsUpper(ch) ? " " + ch : ch.ToString())).ToUpperInvariant(),
            string.Join("/", c.Former.Select(f => rules.Class(f.Class).Name).Concat(c.Classes.Select(k => rules.Class(k).Name))),
            string.Join("/", c.Former.Select(f => f.Level).Concat(cc.Levels(c))),
            c.Experience, cc.NextLevelExperience(c), c.Hp, c.Hp, Math.Max(0, psp - c.PspSpent), psp,
            CharacterSheet.BaseArmorClass + CharacterSheet.DexterityDefence(dex, rules), damage);
    }

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
        _classes.Text = c?.Classes ?? "";
        _levels.Text = c?.Levels ?? "";
        _experience.Text = c is null ? "" : $"EXP:{c.Experience}" + (c.NextLevel is int next ? $" ({next})" : "");
        _hp.Text = c is null ? "" : $"{c.Hp}/{c.MaxHp}";
        _psp.Text = c is null ? "" : $"{c.Psp}/{c.MaxPsp}";
        _armour.Text = c is null ? "" : $"AC:{c.ArmorClass,3}";
        _damage.Text = c is null ? "" : $"DAM: {c.Damage}";
    }

    /// <summary>The text under box <paramref name="slot"/>: its member's points and status, the selected box's in yellow.</summary>
    public void ShowBox(int slot, PartyMemberView? m, bool selected)
    {
        _boxHp[slot].Text = m is null ? "" : $"{m.Hp}/{m.MaxHp}";
        _boxPsp[slot].Text = m is null ? "" : $"{m.Psp}/{m.MaxPsp}";
        _boxStatus[slot].Text = m is null ? "" : PartyFrames.StatusName(m.Status);
        Color colour = selected ? Yellow : Grey;
        _boxHp[slot].LabelSettings = GameFonts.Settings(colour);
        _boxStatus[slot].LabelSettings = GameFonts.Settings(colour);
    }

    private static Label Add(UiWindowView window, Vector2 at, float width, Color colour, HorizontalAlignment align = HorizontalAlignment.Left)
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
        window.AddChild(label);
        return label;
    }
}
