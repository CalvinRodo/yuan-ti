using DarkSun.Data.Animation;
using DarkSun.Data.Gff;
using DarkSun.Data.Images;
using DarkSun.Data.Items;
using DarkSun.Data.Regions;
using DarkSun.Game.Animation;
using DarkSun.Game.Combat;
using DarkSun.Game.Movement;
using DarkSun.Game.World;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// How a fight looks and sounds, as DSUN.EXE shows it: attack animations
/// (<c>896E:0000</c>: the attacker's action 4, or 5 to shoot, with its attack
/// sound, and the missile its weapon shoots flying to the target), spells
/// (<c>7D80:003B</c>: the cast sound, a graphic over the caster, the projectile
/// flown to the target point, then on each one reached without hurt its hit
/// graphic and sound, <c>80F5:0079</c>), the hurt sound of a blow that wounds
/// (the target's, else 4; 5 when a hit does nothing), damage as a number on a
/// splash whose look says what hit (<c>BMP</c> 5014, <c>896E:0A0E</c>), and
/// the lingering fields' graphic on each of their cells.
/// </summary>
public partial class RegionView
{
    /// <summary>Pixels a projectile flies a second (<c>896E:0245</c> moves it 4 a tick at 96 ticks a second).</summary>
    private const float ProjectileSpeed = 4 * ScmdAnimator.TicksPerSecond;

    /// <summary>The longest a one-off graphic plays (some loop).</summary>
    private const double EffectSeconds = 1.2;

    /// <summary><c>BMP</c> 5014: the damage splashes (<c>896E:0A0E</c>).</summary>
    private const int DamageSplash = 5014;

    /// <summary>A blow that hurts without a hurt sound of the target's, and one that does nothing (<c>80F5:0079</c>).</summary>
    private const int DefaultHurtSound = 4, NoHarmSound = 5;

    private GffSet _objectData = null!;
    private readonly List<CombatGraphic> _graphics = [];
    private readonly Dictionary<AreaField, List<CombatGraphic>> _fieldGraphics = [];
    private IReadOnlyList<ImageFrame>? _splashFrames;

    /// <summary>The spell being played back: its look, and who has shown its hit graphic.</summary>
    private (Combatant Caster, SpellLook Look, int Template, HashSet<Combatant> Shown)? _castLook;

    /// <summary>The attack being played back.</summary>
    private AttackRolled? _lastAttack;

    /// <summary>An object drawn for a moment, or while a field lasts: its frames, its animation, where it is and where it flies.</summary>
    private sealed class CombatGraphic(Sprite2D sprite, RegionObject obj, ScmdAnimator? animator, Vector2 at)
    {
        public Sprite2D Sprite { get; } = sprite;
        public RegionObject Object { get; } = obj;
        public ScmdAnimator? Animator { get; } = animator;
        public Vector2 At { get; set; } = at;
        public Vector2? FliesTo { get; init; }
        public double Left { get; set; } = EffectSeconds;
        public bool Lasts { get; init; }
        public bool Blocks { get; init; }
    }

    /// <summary>
    /// <c>OJFF</c> <paramref name="ojff"/> standing at pixel <paramref name="feet"/>
    /// (as a dropped object would), playing its <c>SCMD</c> from the start.
    /// </summary>
    private CombatGraphic? ShowGraphic(int ojff, Vector2 feet, Vector2? fliesTo = null, bool lasts = false, bool blocks = false)
    {
        if (ojff <= 0 || _checking
            || Region.MakeObject(_objectData, ojff, (int)feet.X, (int)feet.Y, d => Session.Assets.Frames(d)) is not { } obj
            || AddSprite(obj.Frame, feet, new Vector2(obj.X, obj.Y), obj.Mirrored) is not { } sprite)
        {
            return null;
        }
        ScmdAnimator? animator = obj.Scmd > 0 && _objectData.TryRead("SCMD", obj.Scmd, out var data)
            ? new ScmdAnimator(Scmd.Parse(data.Span), obj.Frames.Count, 0, obj.Mirrored, obj.AnchorX, obj.AnchorY)
            : null;
        sprite.ZIndex = 10;
        var g = new CombatGraphic(sprite, obj, animator, feet) { FliesTo = fliesTo, Lasts = lasts, Blocks = blocks };
        _graphics.Add(g);
        return g;
    }

    /// <summary>Each frame: graphics animate and fly; done ones go.</summary>
    private void UpdateGraphics(double delta)
    {
        long now = Now;
        foreach (CombatGraphic g in _graphics.ToList())
        {
            if (g.Animator is { } a && a.Update(now))
            {
                ImageFrame frame = g.Object.Frames[Math.Clamp(a.Frame, 0, g.Object.Frames.Count - 1)];
                g.Sprite.Texture = TextureFor(frame);
                g.Sprite.FlipH = a.Mirrored;
                g.Sprite.Offset = -new Vector2(a.HotX, a.HotY + g.Object.YAdjust);
            }
            bool done;
            if (g.FliesTo is { } to)
            {
                g.At = g.At.MoveToward(to, ProjectileSpeed * (float)delta);
                done = g.At.DistanceTo(to) < 1;
            }
            else
            {
                g.Left -= delta;
                done = !g.Lasts && (g.Left <= 0 || g.Animator is { Animating: false } && g.Left < EffectSeconds - 0.2);
            }
            g.Sprite.Position = g.At + (g.Animator is { } an ? new Vector2(an.OffsetX, an.OffsetY) : Vector2.Zero);
            if (done)
            {
                g.Sprite.QueueFree();
                _graphics.Remove(g);
            }
        }
    }

    /// <summary>A graphic the playback waits for: a missile or spell in flight.</summary>
    private bool GraphicsBlock => _graphics.Any(g => g.Blocks);

    private void ClearGraphics()
    {
        foreach (CombatGraphic g in _graphics)
        {
            g.Sprite.QueueFree();
        }
        _graphics.Clear();
        _fieldGraphics.Clear();
        _castLook = null;
        _lastAttack = null;
        _shownAsh.Clear();
        _floor.ShowAsh([], []);
    }

    /// <summary>Where a combatant's feet are, in map pixels.</summary>
    private static Vector2 Feet(Cell cell) => new(cell.X * Region.TileSize + Region.TileSize / 2f, (cell.Y + 1) * Region.TileSize - 1);

    /// <summary>About the middle of a creature standing on the cell: where missiles leave and land.</summary>
    private static Vector2 Middle(Cell cell) => Feet(cell) - new Vector2(0, 12);

    private void Play(int sound)
    {
        if (sound > 0 && !_checking)
        {
            Sounds.Instance?.Play(sound);
        }
    }

    // -- what each event looks like ----------------------------------------------------------------------

    /// <summary>
    /// An attack roll (<c>896E:0000</c>): the attacker turns and plays its
    /// attack (or shooting) animation; a blow sounds, a missile flies.
    /// </summary>
    private void LookOfAttack(AttackRolled a)
    {
        bool missile = Battle.Distance(a.Actor, a.Target) > 1;
        if (a.Actor.Source?.Walker is { } walker)
        {
            Direction facing = Walker.DirectionOf(a.Target.Cell.X - a.Actor.Cell.X, a.Target.Cell.Y - a.Actor.Cell.Y);
            walker.PlayAction(missile ? Scmd.MissileAction : Scmd.AttackAction, facing);
        }
        if (!missile)
        {
            Play(a.Actor.AttackSound);
        }
        else if (_battle is { } battle)
        {
            // What flies goes by the missile weapon (896E:0000): a chatkcha's own, a sling's stone, else an arrow.
            int item = a.Actor.MissileWeapon?.Item?.Rdff ?? 0;
            ShowGraphic(battle.Spells.MissileOf(item), Middle(a.Actor.Cell), Middle(a.Target.Cell), blocks: true);
        }
        if (!a.Hit)
        {
            Play(NoHarmSound);
        }
        _lastAttack = a;
        _castLook = null;
    }

    /// <summary>
    /// A cast (<c>7D80:003B</c>): its sound, its graphic over the caster, its
    /// projectile to the point, a burst's animation where it lands. The seconds
    /// the playback should give it.
    /// </summary>
    private double LookOfCast(Cast c)
    {
        _lastAttack = null;
        if (_battle is not { } battle || battle.Spells.LookOf(c.Effect) is not { } look)
        {
            _castLook = null;
            return 0;
        }
        int template = battle.Spells.Effect(c.Effect) is { } e && battle.Spells.TemplateOf(e) is { } t ? t.Graphic : -1;
        _castLook = (c.Actor, look, template, []);
        Play(look.CastSound);
        ShowGraphic(look.CastGraphic, Feet(c.Actor.Cell));
        if (battle.Spells.ProjectileOf(look) is int projectile && ShowGraphic(projectile, Middle(c.Actor.Cell), Middle(c.At), blocks: true) is { } flying)
        {
            // The burst plays where the projectile lands (7D80:003B, then 7D80:05C7).
            Play(look.FlightSound);
            double flight = Middle(c.Actor.Cell).DistanceTo(Middle(c.At)) / ProjectileSpeed;
            GetTree().CreateTimer(flight).Timeout += () =>
            {
                if (InCombat)
                {
                    Burst(c.Actor, c.Effect, c.At);
                }
            };
            return flight + BurstSeconds;
        }
        return Burst(c.Actor, c.Effect, c.At) ? BurstSeconds : 0;
    }

    /// <summary>A spell reaching someone without hurting them (<c>80F5:0079</c>): its hit graphic and sound, once each.</summary>
    private void LookOfSpellOn(Combatant target)
    {
        if (_castLook is not { } cast || !cast.Shown.Add(target))
        {
            return;
        }
        int graphic = cast.Look.HitGraphic >= 0 ? cast.Look.HitGraphic : cast.Template;
        Play(cast.Look.HitSound);
        ShowGraphic(graphic, Feet(target.Cell));
    }

    /// <summary>Damage: a blow's hurt sound, and the number on its splash.</summary>
    private void LookOfDamage(Damaged d)
    {
        if (_lastAttack is { Hit: true } a && a.Target == d.Target && a.Actor == d.Actor)
        {
            Play(d.Target.HurtSound > 0 ? d.Target.HurtSound : DefaultHurtSound);
        }
        else if (_castLook is { } cast)
        {
            cast.Shown.Add(d.Target);
        }
        Splash(d.Target, d.Amount, d.Elements);
    }

    /// <summary>
    /// The damage splash (<c>896E:0A0E</c>): <c>BMP</c> 5014's frame by what hit,
    /// 4 fire, cold or electricity, 2 poison or acid, 3 magic, else 0 under 15
    /// and 1 from 15, centred on the figure as drawn (its position + half its
    /// size), the number centred across it 6, 11, 8, 11 or 12 pixels from its top
    /// by frame (2 to the right on frame 4); both stay 40 ticks, then go.
    /// </summary>
    private void Splash(Combatant c, int amount, DamageElement elements)
    {
        if (_checking)
        {
            return;
        }
        // DSUN.EXE puts a member's splash on its portrait whenever a window covers the map, at the
        // character screen's places; here only when that screen shows the portrait, else on the figure.
        if (MapCovered && c.PartyMember && c.Source is { Kind: ObjectKind.Party } member && _character.PortraitRect(member.Index) is not null)
        {
            _character.ShowVitals();
            PortraitSplash(member.Index);
            return;
        }
        int frame = ((int)elements & 0x86) != 0 ? 4 : ((int)elements & 0x41) != 0 ? 2 : ((int)elements & 0x200) != 0 ? 3 : amount < 15 ? 0 : 1;
        _splashFrames ??= Session.Open("RESOURCE.GFF").TryRead("BMP ", DamageSplash, out var data) ? Session.Assets.Frames(data) : [];
        Vector2 centre = SpriteOf(c) is { Texture: not null } sprite ? SpriteRect(sprite).GetCenter() : Middle(c.Cell);
        var holder = new Node2D { Position = centre.Floor(), ZIndex = 100 };
        float top = -6;
        if (frame < _splashFrames.Count && _splashFrames[frame] is { Width: > 1 } image)
        {
            holder.AddChild(new Sprite2D { Texture = TextureFor(image), Centered = true });
            top = -(image.Height / 2);
        }
        int down = frame switch { 0 => 6, 2 => 8, 4 => 12, _ => 11 }, right = frame == 4 ? 2 : 0;
        var label = new Label
        {
            Text = amount.ToString(),
            HorizontalAlignment = HorizontalAlignment.Center,
            Position = new Vector2(-20 + right, top + down - 3),
            Size = new Vector2(40, 12),
        };
        label.AddThemeFontSizeOverride("font_size", 8);
        label.AddThemeColorOverride("font_color", Colors.White);
        label.AddThemeColorOverride("font_outline_color", Colors.Black);
        label.AddThemeConstantOverride("outline_size", 3);
        holder.AddChild(label);
        _floaters.AddChild(holder);
        GetTree().CreateTimer(SplashTicks / (double)ScmdAnimator.TicksPerSecond).Timeout += holder.QueueFree;
    }

    /// <summary>How long a damage splash stays (<c>896E:0A0E</c> waits 0x28 ticks).</summary>
    private const int SplashTicks = 40;

    /// <summary>
    /// A window covers the map (DSUN.EXE's display mode <c>DS:0BC9</c> = 2, set by
    /// <c>1587:22BA</c> for the character screens, messages and popups).
    /// </summary>
    private bool MapCovered => _character.IsOpen || _dialogueWindow.Visible || _gameMenu.IsOpen || _saves.IsOpen || _combatMenu is { Visible: true };

    /// <summary>
    /// Damage to a party member while a window covers the map
    /// (<c>896E:0ED3</c>): <c>BMP</c> 5014's frame 4, no number, for 40 ticks,
    /// centred over the member's portrait on the character screen (DSUN.EXE: the
    /// inventory column at x 37, y 48 × member + 30, else the 2 × 2 boxes'
    /// places, (78, 54), (129, 54), (78, 114), (129, 114) of the 320 × 200
    /// screen, less 10 in x, even under another window).
    /// </summary>
    private void PortraitSplash(int slot)
    {
        _splashFrames ??= Session.Open("RESOURCE.GFF").TryRead("BMP ", DamageSplash, out var data) ? Session.Assets.Frames(data) : [];
        if (_character.PortraitRect(slot) is not { } portrait || _splashFrames.Count <= PortraitFrame
            || _splashFrames[PortraitFrame] is not { Width: > 1 } image)
        {
            return;
        }
        float scale = Math.Max(1f, MathF.Floor(Math.Min(GetViewportRect().Size.X / RetroScreen.Width, GetViewportRect().Size.Y / RetroScreen.Height)));
        Vector2 centre = portrait.GetCenter();
        var splash = new TextureRect
        {
            Texture = TextureFor(image),
            Scale = new Vector2(scale, scale),
            Position = (centre - new Vector2(image.Width, image.Height) * scale / 2).Floor(),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ZIndex = 100,
        };
        _combatLayer.AddChild(splash);
        GetTree().CreateTimer(PortraitSplashTicks / (double)ScmdAnimator.TicksPerSecond).Timeout += splash.QueueFree;
    }

    /// <summary>The portrait splash's frame and how long it stays (<c>896E:0ED3</c>: frame 4, 0x28 ticks).</summary>
    private const int PortraitFrame = 4, PortraitSplashTicks = 40;

    /// <summary>
    /// A burst's animation (<c>7D80:200D</c>): its template's graphic on every
    /// point of the area, then a shrinking share of them (6/8 … 1/8 of the
    /// points, a step at a time), then none. Played as the spell is cast,
    /// before its effects.
    /// </summary>
    private bool Burst(Combatant caster, int effect, Cell at)
    {
        if (_checking || _battle is not { } battle || battle.Spells.Effect(effect) is not { } e
            || battle.Spells.TemplateOf(e) is not { Kind: AreaTemplate.Burst, Graphic: > 0 } template)
        {
            return false;
        }
        List<CombatGraphic> shown = [.. battle.AreaCells(caster, e, caster.Level, at).OrderBy(c => Battle.Distance(c, at))
            .Select(cell => ShowGraphic(template.Graphic, Feet(cell), lasts: true, blocks: true)).OfType<CombatGraphic>()];
        if (shown.Count == 0)
        {
            return false;
        }
        Tween tween = CreateTween();
        tween.TweenInterval(BurstStep * 2);
        for (int step = 6; step >= 0; step--)
        {
            int keep = shown.Count * step / 8;
            tween.TweenCallback(Callable.From(() =>
            {
                foreach (CombatGraphic g in shown.Skip(keep).Where(_graphics.Contains).ToList())
                {
                    g.Sprite.QueueFree();
                    _graphics.Remove(g);
                }
            }));
            tween.TweenInterval(BurstStep);
        }
        return true;
    }

    /// <summary>How long a burst plays: two steps whole, then seven shrinking.</summary>
    private const double BurstSeconds = BurstStep * 9;

    /// <summary>A step of a burst's animation (DSUN.EXE: a frame each, <c>0980:5814</c>).</summary>
    private const double BurstStep = 0.07;

    /// <summary>The ash shown so far: the battle's, as far as the playback has come.</summary>
    private readonly HashSet<Vector2I> _shownAsh = [];

    /// <summary>
    /// A defiler's spell drained the ground (<see cref="DefilingRules"/>): its
    /// graphic and sound at the caster's feet, then the ash on the floor.
    /// </summary>
    private void LookOfDefiling(Defiled d)
    {
        if (_battle is { } battle)
        {
            Play(battle.Rules.Defiling.Sound);
            ShowGraphic(battle.Rules.Defiling.Graphic, Feet(d.Actor.Cell));
        }
        _shownAsh.UnionWith(d.Cells.Select(c => new Vector2I(c.X, c.Y)));
        _floor.ShowAsh(_shownAsh, Colours(_palette));
    }

    /// <summary>Ash brought back (<see cref="SpellRules.AshRestorers"/>): the floor shows through again.</summary>
    private void LookOfRestoring(AshRestored r)
    {
        _shownAsh.ExceptWith(r.Cells.Select(c => new Vector2I(c.X, c.Y)));
        _floor.ShowAsh(_shownAsh, Colours(_palette));
    }

    /// <summary>A field laid (<c>7D80:2782</c>): its template's graphic on each of its cells while it lasts.</summary>
    private void LookOfField(AreaField f)
    {
        if (f.Template.Graphic <= 0)
        {
            return;
        }
        _fieldGraphics[f] = [.. f.Cells.Select(cell => ShowGraphic(f.Template.Graphic, Feet(cell), lasts: true)).OfType<CombatGraphic>()];
    }

    private void FieldGone(AreaField f)
    {
        if (_fieldGraphics.Remove(f, out var shown))
        {
            foreach (CombatGraphic g in shown)
            {
                g.Sprite.QueueFree();
                _graphics.Remove(g);
            }
        }
    }
}
