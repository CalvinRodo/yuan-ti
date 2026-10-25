using DarkSun.Data.Gff;
using DarkSun.Data.Regions;
using DarkSun.Game.Combat;
using DarkSun.Game.Magic;
using DarkSun.Game.Movement;
using DarkSun.Game.World;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// The mouse pointer (<see cref="Pointers"/>) and, while a spell is aimed, its
/// targets flashing black and an area's outline drawn in dots (<c>BMP</c> 5015,
/// <c>8B7D:00AB</c>).
/// </summary>
public partial class RegionView
{
    /// <summary>Aimed-at targets are black for <see cref="FlashOn"/> of every <see cref="FlashPeriod"/> seconds.</summary>
    private const double FlashPeriod = 0.5, FlashOn = 0.15;

    private const int OutlineDot = 5015;

    private static readonly IReadOnlySet<Cell> NoCells = new HashSet<Cell>();

    private readonly HashSet<Sprite2D> _flashing = [];
    private Node2D? _aimOutline;
    private (int Spell, Cell At, Combatant Caster)? _aimed;
    private IReadOnlyList<Combatant> _aimTargets = [];

    /// <summary>Other screens have the system's pointer, and a repeating sound and the colour cycling stop with the map.</summary>
    public override void _ExitTree()
    {
        Session.ScriptHost.LevelGained -= LevelGained;
        Pointers.Reset();
        Sounds.Instance?.StopRepeating();
        ColourCycling.Instance?.Stop();
    }

    /// <summary>The pointer for where the mouse is, and the aimed spell's targets and outline.</summary>
    private void UpdatePointer()
    {
        bool overWindow = GetViewport().GuiGetHoveredControl() is not null || _character.IsOpen || _dialogueWindow.Visible;
        int icon = overWindow ? Pointers.Move : PointerIcon(GetGlobalMousePosition());
        if (_character.IsOpen && _character.Picked is { } picked && _character.HeldIcon?.GetImage() is { } carried)
        {
            // An item picked up in the inventory rides on the pointer until it is put down.
            Pointers.ShowHeld(GetWindow(), Pointers.HeldItem(picked.Rdff), carried);
        }
        else if (icon == Pointers.Held && _useWithItem is { } heldItem && ItemIcon(heldItem.Rdff)?.GetImage() is { } itemImage)
        {
            Pointers.ShowHeld(GetWindow(), Pointers.HeldItem(heldItem.Rdff), itemImage);
        }
        else if (icon == Pointers.Held && _useWith is { Source.Frames: [var picture, ..] } held && TextureFor(picture)?.GetImage() is { } image)
        {
            Pointers.ShowHeld(GetWindow(), Pointers.HeldObject(held.Index), image);
        }
        else
        {
            Pointers.Show(GetWindow(), icon == Pointers.Held ? Pointers.Move : icon);
        }
        UpdateAim(overWindow ? null : MouseCell(), icon);
    }

    /// <summary>
    /// The pointer by mode, as <c>1587:29FA</c> picks it: in a fight the
    /// hourglass while it isn't the player's turn, the spell's icon (or 6009)
    /// while one is aimed (<see cref="Battle.Aim"/>), and otherwise the cursor's,
    /// crossed out where a click does nothing. (Over the map's windows, the arrow.)
    /// </summary>
    /// <param name="at">Where the pointer is on the map.</param>
    private int PointerIcon(Vector2 at)
    {
        Cell cell = AimCell(at);
        if (_battle is { } battle)
        {
            if (PlayerTurn is not { } c)
            {
                return Pointers.Wait;
            }
            if (_castChoice is { Spell: var spell, Level: var level })
            {
                return battle.Spells.Effect(spell) is { } e && battle.Aim(c, e, level, cell) == SpellAim.Ok
                    && Spellcasting.Get(spell) is { } known ? known.Icon : Pointers.CantCast;
            }
            Combatant? there = battle.At(cell);
            return _cursor switch
            {
                GameMenu.Cursor.Look => there is not null && battle.InSight(c.Cell, cell) ? Pointers.Look : Pointers.CantLook,
                GameMenu.Cursor.Attack => AttackPointer(battle, c, there, cell),
                // MOVE: a click on an enemy still attacks it (Click), walking up for a blow.
                _ when there is not null && there != c && c.IsEnemyOf(there) =>
                    (c.Flags & TurnFlags.MayAttack) == 0 ? Pointers.CantAttack : MissileClick(c, there) ? Pointers.Ranged : Pointers.Attack,
                _ => there == c || (c.Flags & TurnFlags.MayMove) != 0 && battle.Free(cell) ? Pointers.Move : Pointers.CantMove,
            };
        }
        WorldObject? hit = ObjectAt(at);
        return _cursor switch
        {
            GameMenu.Cursor.Look => hit is not null && Sees(hit) ? Pointers.Look : Pointers.CantLook,
            GameMenu.Cursor.Attack => hit is { Kind: ObjectKind.Creature } ? Pointers.Attack : Pointers.CantAttack,
            GameMenu.Cursor.Item when _useWith is not null => Pointers.Held,
            _ => hit is null && _region.IsBlocked(cell.X, cell.Y) ? Pointers.CantMove : Pointers.Move,
        };
    }

    /// <summary>
    /// The ATTACK pointer (<c>1587:29FA</c>, mode 4): crossed out on itself; with
    /// no one there or no sight of them, the sword crossed out within a cell, the
    /// missile beyond; the sword next to them; farther, the missile if it can shoot
    /// them (<c>1587:293E</c>), crossed out if not.
    /// </summary>
    private static int AttackPointer(Battle battle, Combatant c, Combatant? there, Cell cell)
    {
        if (there == c)
        {
            return Pointers.CantAttack;
        }
        if (there is null || !battle.InSight(c.Cell, cell))
        {
            return Battle.Distance(c.Cell, cell) < 2 ? Pointers.CantAttack : Pointers.CantRanged;
        }
        if (Battle.Distance(c, there) < 2)
        {
            return Pointers.Attack;
        }
        return battle.CanAttack(c, there, missile: true) ? Pointers.Ranged : Pointers.CantRanged;
    }

    /// <summary>
    /// The cell a pointer at <paramref name="at"/> aims at: out of a fight, or for
    /// an area spell, the one under it; otherwise that of the combatant whose
    /// picture is under it (the front-most; DSUN.EXE finds objects by the screen
    /// point, <c>1DF3:2822</c>), so a tall creature is aimed at anywhere on
    /// its body, not only at its feet.
    /// </summary>
    private Cell AimCell(Vector2 at)
    {
        var cell = new Cell((int)Math.Floor(at.X / Region.TileSize), (int)Math.Floor(at.Y / Region.TileSize));
        if (_battle is not { } battle
            || _castChoice is { Spell: var spell } && battle.Spells.Effect(spell) is { SingleTarget: false })
        {
            return cell;
        }
        Combatant? drawn = battle.Combatants
            .Where(c => c.Alive && SpriteOf(c) is { } s && DrawnAt(s, at))
            .MaxBy(c => SpriteOf(c)!.Position.Y);
        return drawn is not null && battle.At(cell) is null ? drawn.Cell : cell;
    }

    private static Rect2 SpriteRect(Sprite2D s) => new(s.Position + s.GetRect().Position, s.GetRect().Size);

    /// <summary>Would a click on <paramref name="there"/> shoot rather than walk up (<see cref="Click"/>)?</summary>
    private static bool MissileClick(Combatant c, Combatant there) =>
        Battle.Distance(c, there) > Battle.Reach(c) && c.CanShoot;

    /// <summary>
    /// Who the aimed spell would reach flashes black, and an area's outline is
    /// dotted; with no spell, so does the creature under the sword or the missile.
    /// </summary>
    /// <param name="pointed">The cell under the pointer, null over a window.</param>
    /// <param name="icon">The pointer shown there (<see cref="PointerIcon"/>).</param>
    private void UpdateAim(Cell? pointed, int icon)
    {
        (int, Cell, Combatant)? aimed = null;
        if (_battle is { } battle && PlayerTurn is { } c && _castChoice is { Spell: var spell, Level: var level } && battle.Spells.Effect(spell) is { } e
            && pointed is { } cell && battle.Aim(c, e, level, cell) == SpellAim.Ok)
        {
            aimed = (spell, cell, c);
            if (_aimed != aimed)
            {
                _aimTargets = battle.AffectedBy(c, e, level, cell);
                DrawOutline(e.SingleTarget ? NoCells : battle.AreaCells(c, e, level, cell));
            }
        }
        else if (_aimed is not null)
        {
            _aimTargets = [];
            DrawOutline(NoCells);
        }
        _aimed = aimed;
        IReadOnlyList<Combatant> flash = _aimTargets;
        if (aimed is null && _battle?.At(pointed ?? new Cell(-1, -1)) is { } attacked && icon is Pointers.Attack or Pointers.Ranged)
        {
            flash = [attacked];
        }
        bool dark = _clock % FlashPeriod < FlashOn;
        var now = flash.Select(SpriteOf).OfType<Sprite2D>().ToHashSet();
        foreach (Sprite2D sprite in _flashing.Where(s => !now.Contains(s) && IsInstanceValid(s)))
        {
            sprite.SelfModulate = Colors.White;
        }
        _flashing.Clear();
        foreach (Sprite2D sprite in now)
        {
            sprite.SelfModulate = dark ? Colors.Black : Colors.White;
            _flashing.Add(sprite);
        }
    }

    /// <summary>A dot (<c>BMP</c> 5015) on each cell at the edge of <paramref name="area"/>.</summary>
    private void DrawOutline(IReadOnlySet<Cell> area)
    {
        if (_aimOutline is null)
        {
            _aimOutline = new Node2D { ZIndex = 90 };
            AddChild(_aimOutline);
        }
        foreach (Node dot in _aimOutline.GetChildren())
        {
            _aimOutline.RemoveChild(dot);
            dot.QueueFree();
        }
        GffFile resources = Session.Open("RESOURCE.GFF");
        if (area.Count == 0 || !resources.TryRead("BMP ", OutlineDot, out var data) || Session.Assets.Frames(data) is not [var frame, ..])
        {
            return;
        }
        Texture2D texture = Textures.ToTexture(frame, Session.Assets.Palette(resources.Read("PAL ", 1001)));
        foreach (Cell cell in area.Where(a => !area.Contains(new Cell(a.X + 1, a.Y)) || !area.Contains(new Cell(a.X - 1, a.Y))
            || !area.Contains(new Cell(a.X, a.Y + 1)) || !area.Contains(new Cell(a.X, a.Y - 1))))
        {
            _aimOutline.AddChild(new Sprite2D
            {
                Texture = texture,
                Position = new Vector2((cell.X + 0.5f) * Region.TileSize, (cell.Y + 0.5f) * Region.TileSize),
            });
        }
    }

    /// <summary>The sprite <paramref name="c"/> is drawn with, if any.</summary>
    private Sprite2D? SpriteOf(Combatant c)
    {
        if (c.Source is not { } o)
        {
            return null;
        }
        if (c.PartyMember)
        {
            return _party.FirstOrDefault(m => m.Walker == o.Walker)?.Node;
        }
        return _creatureNodes.GetValueOrDefault(o) ?? _cloneSprites.GetValueOrDefault(o)
            ?? _objectSprites.FirstOrDefault(s => _worldObjects.GetValueOrDefault(s.Object) == o).Sprite;
    }
}
