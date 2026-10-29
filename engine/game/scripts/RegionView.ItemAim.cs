using DarkSun.Data.Regions;
using DarkSun.Game.Combat;
using DarkSun.Game.Magic;
using DarkSun.Game.Scripting;
using DarkSun.Game.World;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// Items used from the inventory out of a fight: a quest item's use trigger,
/// and an item's spell aimed on the map. DSUN.EXE's USE (<c>91F7:03D0</c>)
/// puts the spell pointer on the map (<c>8B7D:04D1</c>) and casts where it is
/// clicked (<c>8B7D:0627</c> → <c>7D80:21C5</c>, level 6). Here a help is cast
/// on the party member clicked, as members' own spells are out of a fight
/// (<see cref="Encounter.CastOnMap"/>); anything else on a creature starts the
/// fight with it and is cast first. A right-click puts the spell away.
/// </summary>
public partial class RegionView
{
    /// <summary>The item spell being aimed on the map out of a fight, and the member using it.</summary>
    private (int Member, CastChoice Cast)? _mapCast;

    private string? CastOnMap(int caster, int target, int number, bool fromItem) =>
        Encounter.CastOnMap(_world, slot => slot < Session.Party.Count ? Session.Party[slot] : null,
            caster, target, number, new BorlandDice(new DarkSun.Game.Rules.BorlandRandom((uint)System.Environment.TickCount)), Session.Rules,
            Session.CombatRules, Session.SpellRules, Session.CreatureRules, fromItem);

    /// <summary>USE on a quest item: its use trigger, with the member as actor and the item as target (by its id, as for USE WITH).</summary>
    private bool UseQuestItem(int member, Item item)
    {
        GplMachine scripts = Session.Scripts;
        if (scripts.Triggers.MatchObject(GplTriggerList.Use, item.Id) is null)
        {
            return false;
        }
        string what = $"{_world[member]?.Name ?? "someone"} uses {ItemText.Name(_world, item)}";
        if (scripts.OnObject(GplTriggerList.Use, member, item.Id) is { } run)
        {
            ReportScript(what, run);
        }
        else
        {
            AddScriptLine($"> {what}: a script is busy");
        }
        return true;
    }

    /// <summary>USE on an item whose spell is aimed, out of a fight: the spell pointer goes up on the map.</summary>
    private string? AimItemOnMap(int member, Item item, WorldObject holder, ItemUse use)
    {
        if (_world[member] is not { Kind: ObjectKind.Party })
        {
            return Spellcasting.NothingHappens;
        }
        SetUseWith(null);
        _mapCast = (member, new CastChoice(use.Spell, ItemMagic.CasterLevel, item, holder));
        AddScriptLine($"> {_world[member]!.Name} aims {EffectName(use.Spell)} from {ItemText.Name(_world, item)}");
        return null;
    }

    /// <summary>
    /// Whom a click at <paramref name="at"/> casts the aimed spell on, if anyone: a help, a party
    /// member; anything else, a creature within the spell's range of the caster and in its sight.
    /// </summary>
    private WorldObject? MapCastTarget(Vector2 at)
    {
        if (_mapCast is not { Member: var member, Cast.Spell: var spell } || Session.SpellRules.Effect(spell) is not { } e
            || _world[member] is not { } caster)
        {
            return null;
        }
        if (e.Purpose == SpellEffect.Helps)
        {
            return MemberAt(at);
        }
        return ObjectAt(at) is { Kind: ObjectKind.Creature, Hidden: false } creature && creature.Vitals.Alive
            && _world.CellsOf(creature).Any(c => Battle.Distance(caster.Cell, c) <= e.RangeCells(ItemMagic.CasterLevel)
                && GplGeometry.LineOfSight(_region, caster.Cell.X, caster.Cell.Y, c.X, c.Y))
            ? creature : null;
    }

    /// <summary>A click or key while an item's spell is aimed out of a fight; true when it was taken.</summary>
    private bool MapCastInput(InputEvent e)
    {
        if (_mapCast is not { } aimed || InCombat)
        {
            _mapCast = null;
            return false;
        }
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } || e.IsActionPressed(Controls.Back))
        {
            // 1587:01C7: a right-click puts the spell away.
            _mapCast = null;
            AddScriptLine($"> {EffectName(aimed.Cast.Spell)} put away");
            return true;
        }
        if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
        {
            return e is InputEventMouseButton;
        }
        if (MapCastTarget(GetGlobalMousePosition()) is not { } target)
        {
            Notice("No one there.");
            return true;
        }
        _mapCast = null;
        CastItemOnMap(aimed.Member, aimed.Cast, target);
        return true;
    }

    private void CastItemOnMap(int member, CastChoice cast, WorldObject target)
    {
        string name = ItemText.Name(_world, cast.Item!);
        string spell = EffectName(cast.Spell);
        if (target.Kind == ObjectKind.Party)
        {
            string result = CastOnMap(member, target.Index, cast.Spell, fromItem: true) ?? "ONLY IN COMBAT";
            if (result == CharacterScreen.TakesEffect && _world.UseItemCharge(cast.Holder!, cast.Item!))
            {
                result += $"; {name.ToUpperInvariant()} IS USED UP";
            }
            AddScriptLine($"> {_world[member]?.Name} casts {spell} from {name} on {target.Name}: {result.ToLowerInvariant()}");
            Notice($"{spell}: {result}");
            UpdateHud();
            return;
        }
        // A spell at a creature is an attack on it: the fight starts, and the spell is its first blow.
        if (!StartCombat(target, $"{spell} from {name} at {Describe(target)}"))
        {
            Notice($"{Called(target, start: true)} won't fight.");
            return;
        }
        if (_battle!.Combatants.FirstOrDefault(c => c.PartyMember && c.Source == _world[member]) is { } caster
            && _battle.Combatants.FirstOrDefault(c => c.Source == target) is { } aimedAt)
        {
            CastAt(caster, cast, aimedAt.Cell);
        }
    }

    /// <summary>
    /// <c>--check-map-cast</c>: a new game; the nearest creature that fights is brought near the
    /// party, the leader uses a Wand of Missiles from the inventory, the spell pointer goes up on
    /// the map, a right-click puts it away, and the next aim at the creature starts the fight with
    /// Magic Missile cast first, a charge used.
    /// </summary>
    private void CheckMapCast()
    {
        _checking = true;
        var problems = new List<string>();
        void Expect(bool ok, string what)
        {
            GD.Print($"map cast: {(ok ? "" : "PROBLEM ")}{what}");
            if (!ok)
            {
                problems.Add(what);
            }
        }
        const int Wand = 31007, MagicMissile = 9;
        WorldObject? foe = BringFoeNear();
        WorldObject me = _world[0]!;
        _world.Give(1, -Wand, 0, GameWorld.None);
        Item? wand = me.Items.LastOrDefault(i => i.Rdff == Wand);
        if (foe is null || wand is null)
        {
            GD.Print($"map cast: PROBLEM {(foe is null ? "no creature to aim at" : "no wand")}");
            GetTree().Quit(1);
            return;
        }
        // A point the creature's picture draws, where a click finds it (ObjectAt).
        Vector2 there = new((foe.Cell.X + 0.5f) * Region.TileSize, (foe.Cell.Y + 0.5f) * Region.TileSize);
        for (int frame = 0; frame < 10; frame++)
        {
            _Process(1.0 / 60);
        }
        if (SpriteOf(foe) is { } sprite)
        {
            Rect2 rect = SpriteRect(sprite);
            there = Enumerable.Range(0, (int)rect.Size.Y).SelectMany(y => Enumerable.Range(0, (int)rect.Size.X).Select(x => rect.Position + new Vector2(x + 0.5f, y + 0.5f)))
                .FirstOrDefault(v => ObjectAt(v) == foe, there);
        }
        void UseWand()
        {
            const int Pack = 1;
            _character.Open(_world, 0, CharacterScreen.Page.Items);
            _character.Click(Pack, Enumerable.Range(0, 12).FirstOrDefault(n => _character.ItemAt(Pack, n) == wand, -1), button: MouseButton.Right);
            _character.ItemWindow.PressUse();
        }
        UseWand();
        Expect(!_character.IsOpen && _mapCast is { Member: 0 } aimed && aimed.Cast.Item == wand && aimed.Cast.Spell == MagicMissile,
            $"USE on the wand out of a fight closes the screen and aims Magic Missile on the map ({_character.Status})");
        Expect(PointerIcon(there) != Pointers.CantCast, $"the spell pointer is good on {foe.Name} ({PointerIcon(there)})");
        MapCastInput(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true });
        Expect(_mapCast is null && !InCombat, "a right-click puts it away");
        UseWand();
        int charges = wand.Charges;
        if (_mapCast is { } cast && MapCastTarget(there) is { } target)
        {
            _mapCast = null;
            CastItemOnMap(cast.Member, cast.Cast, target);
        }
        Expect(InCombat && _battle!.Log.Any(e => e is Cast { Effect: MagicMissile } c && c.Actor.Source == me),
            $"aimed at {foe.Name}, the fight starts with Magic Missile cast ({(InCombat ? "fighting" : "no fight")})");
        Expect(wand.Charges == charges - 1, $"a charge is used ({charges} to {wand.Charges})");
        AbandonCombat();
        GD.Print(problems.Count == 0 ? "map cast: OK" : $"map cast: {problems.Count} PROBLEM(S)");
        GetTree().Quit(problems.Count == 0 ? 0 : 1);
    }
}
