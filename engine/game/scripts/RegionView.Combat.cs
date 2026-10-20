using DarkSun.Data.Images;
using DarkSun.Data.Items;
using DarkSun.Data.Regions;
using DarkSun.Game.Combat;
using DarkSun.Game.Movement;
using DarkSun.Game.Rules;
using DarkSun.Game.World;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// Combat on the region map. FIGHT (or attacking a creature that has no attack
/// trigger) starts a <see cref="Battle"/> from the party, as their character
/// sheets make them, and the creatures that fight near them
/// (<see cref="Encounter"/>). The battle runs headless; what it reports
/// (<see cref="Battle.Happened"/>) plays back here one event at a time: steps
/// walked, attack rolls, damage and healing as numbers rising over the target,
/// effects, and a combat log. On a party member's turn the player clicks an
/// enemy to attack it (hovering shows the chance to hit), the ground to move,
/// or uses the buttons (cast, guard, wait, end turn, auto).
/// </summary>
public partial class RegionView
{
    /// <summary>Seconds a hit, a cast or a miss stays on screen before the next event.</summary>
    private const double AttackPause = 0.35, CastPause = 0.5, NotePause = 0.12;

    /// <summary>The longest a step's walk is waited for.</summary>
    private const double StepTimeout = 0.6;

    /// <summary>Combat log lines kept on screen.</summary>
    private const int LogLines = 12;

    private Battle? _battle;
    private readonly Queue<CombatEvent> _combatEvents = new();
    private double _combatWait;
    private (Walker Walker, double Left)? _combatWalk;
    private readonly List<string> _combatLines = [];
    private PanelContainer _combatPanel = null!;
    private Label _combatTurn = null!;
    private Label _combatTip = null!;
    private RichTextLabel _combatLog = null!;
    private PanelContainer _combatLogPanel = null!;
    private PopupMenu _castMenu = null!;
    private Node2D _floaters = null!;
    private Button _castButton = null!, _autoButton = null!;

    /// <summary>A spell or power chosen with CAST, waiting for its target.</summary>
    private int? _castChoice;

    /// <summary>Headless checks: the party fights by itself.</summary>
    private bool _autoParty;

    private bool InCombat => _battle is not null;

    /// <summary>The party member whose turn it is, waiting for the player (null while the battle plays or the AI acts).</summary>
    private Combatant? PlayerTurn =>
        _battle is { Current: { } c } && _combatEvents.Count == 0 && _combatWalk is null && _combatWait <= 0 && !c.UnderAi && c.Fights ? c : null;

    private void BuildCombatHud(CanvasLayer layer)
    {
        _floaters = new Node2D { ZIndex = 100 };
        AddChild(_floaters);

        _combatPanel = new PanelContainer { Visible = false };
        _combatPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom);
        _combatPanel.GrowHorizontal = Control.GrowDirection.Both;
        _combatPanel.GrowVertical = Control.GrowDirection.Begin;
        _combatPanel.OffsetBottom = -4;
        var box = new VBoxContainer();
        _combatTurn = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _combatTurn.AddThemeFontSizeOverride("font_size", 10);
        _combatTip = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _combatTip.AddThemeFontSizeOverride("font_size", 9);
        _combatTip.AddThemeColorOverride("font_color", new Color(1f, 0.9f, 0.6f));
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        Button Add(string text, string action, Action pressed)
        {
            var b = new Button { Text = text, TooltipText = KeyHelp.Keys(action), FocusMode = Control.FocusModeEnum.None };
            b.AddThemeFontSizeOverride("font_size", 9);
            b.Pressed += pressed;
            buttons.AddChild(b);
            return b;
        }
        _castButton = Add("CAST", Controls.CombatCast, OpenCastMenu);
        Add("GUARD", Controls.CombatGuard, () => PlayerAct(c => _battle!.Guard(c)));
        Add("WAIT", Controls.CombatWait, () => PlayerAct(c => _battle!.Wait(c)));
        Add("END TURN", Controls.CombatEndTurn, () => PlayerAct(c => _battle!.EndTurn(c)));
        _autoButton = Add("AUTO", Controls.CombatAuto, ToggleAuto);
        box.AddChild(_combatTurn);
        box.AddChild(_combatTip);
        box.AddChild(buttons);
        _combatPanel.AddChild(box);
        layer.AddChild(_combatPanel);

        _combatLogPanel = new PanelContainer { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _combatLogPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight);
        _combatLogPanel.GrowHorizontal = Control.GrowDirection.Begin;
        _combatLogPanel.OffsetTop = 4;
        _combatLogPanel.OffsetRight = -4;
        _combatLog = new RichTextLabel
        {
            BbcodeEnabled = true, FitContent = true, ScrollActive = false, CustomMinimumSize = new Vector2(230, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _combatLog.AddThemeFontSizeOverride("normal_font_size", 8);
        _combatLogPanel.AddChild(_combatLog);
        layer.AddChild(_combatLogPanel);

        _castMenu = new PopupMenu();
        _castMenu.AddThemeFontSizeOverride("font_size", 9);
        _castMenu.IdPressed += id => ChooseCast((int)id);
        layer.AddChild(_castMenu);
    }

    // -- starting and ending ---------------------------------------------------------------------------

    /// <summary>
    /// A fight begins (<see cref="Encounter.Start"/>): everyone stops where they
    /// stand, and the battle's first round starts. Nothing to fight: says so.
    /// </summary>
    private bool StartCombat(WorldObject? attacked, string why)
    {
        if (InCombat)
        {
            return true;
        }
        var dice = new BorlandDice(new BorlandRandom((uint)System.Environment.TickCount));
        Battle? battle = Encounter.Start(_world, slot => slot < Session.Party.Count ? Session.Party[slot] : null, dice, Session.Rules,
            Session.CombatRules, Session.SpellRules, Session.CreatureRules, attacked);
        if (battle is null)
        {
            AddScriptLine($"> {why}: no one to fight");
            return false;
        }
        _pendingUse = null;
        _verbMenu.Close();
        SetUseWith(null);
        foreach (PartyMember m in _party)
        {
            m.Walker.Stop();
        }
        foreach (WorldObject c in _world.Objects.Where(o => o.Walker is not null && o.Kind != ObjectKind.Party))
        {
            c.Walker!.Stop();
        }
        if (_autoParty)
        {
            foreach (Combatant c in battle.Combatants.Where(c => c.PartyMember))
            {
                c.ComputerControlled = true;
            }
        }
        _battle = battle;
        _combatLines.Clear();
        _combatEvents.Clear();
        battle.Happened += e => _combatEvents.Enqueue(e);
        int enemies = battle.Combatants.Count(c => !c.PartyMember && battle.Combatants.Any(p => p.PartyMember && p.IsEnemyOf(c)));
        Log($"[color=#ffd080]Combat! {enemies} {(enemies == 1 ? "enemy" : "enemies")}[/color]");
        AddScriptLine($"> {why}: combat, {battle.Combatants.Count} combatants");
        _combatPanel.Visible = !_checking;
        _combatLogPanel.Visible = !_checking;
        return true;
    }

    /// <summary>The fight is dropped as it stands (the region is left, or a check only wanted to see it start).</summary>
    private void AbandonCombat()
    {
        if (_battle is null)
        {
            return;
        }
        _battle = null;
        _combatEvents.Clear();
        (_combatWait, _combatWalk, _castChoice) = (0, null, null);
        _combatPanel.Visible = _combatLogPanel.Visible = false;
    }

    private void EndCombat()
    {
        if (_battle is not { } battle)
        {
            return;
        }
        Encounter.End(battle);
        bool won = battle.Combatants.Any(c => c.PartyMember && c.Alive);
        Log(won ? "[color=#a0ffa0]The fight is over.[/color]" : "[color=#ff8080]The party has fallen.[/color]");
        AddScriptLine($"> combat over after {battle.Round} rounds: {(won ? "won" : "lost")}");
        foreach (Combatant c in battle.Combatants.Where(c => c.PartyMember && c.Source is not null))
        {
            if (_party.FirstOrDefault(m => m.Walker == c.Source!.Walker) is { } member)
            {
                member.Node.Modulate = c.Alive ? Colors.White : new Color(0.5f, 0.3f, 0.3f);
            }
        }
        _battle = null;
        _castChoice = null;
        _combatPanel.Visible = false;
        _marker.Follow(Selected?.Walker);
        UpdateHud();
        if (!_checking)
        {
            Notice(won ? "The fight is over." : "The party has fallen.");
            GetTree().CreateTimer(6).Timeout += () => _combatLogPanel.Visible = InCombat;
        }
    }

    // -- each frame -----------------------------------------------------------------------------------

    /// <summary>
    /// Moves the fight on: the next event plays when the last has had its time
    /// (and a walk its steps), then the battle goes on: a new turn, a new round,
    /// the AI's turn, or the player's, which waits for input.
    /// </summary>
    private void CombatProcess(double delta)
    {
        if (_battle is not { } battle)
        {
            return;
        }
        for (int guard = 0; guard < 64; guard++)
        {
            if (_combatWait > 0)
            {
                _combatWait -= delta;
                return;
            }
            if (_combatWalk is { } walk)
            {
                if (walk.Walker.IsWalking && walk.Left > 0)
                {
                    _combatWalk = (walk.Walker, walk.Left - delta);
                    return;
                }
                _combatWalk = null;
            }
            if (_combatEvents.TryDequeue(out CombatEvent? e))
            {
                PlayEvent(e);
                delta = 0;
                continue;
            }
            if (battle.Over)
            {
                EndCombat();
                return;
            }
            if (battle.Current is not { } current)
            {
                if (battle.NextTurn() is null)
                {
                    battle.StartRound();
                }
                continue;
            }
            if (current.UnderAi)
            {
                battle.RunAi(current);
                continue;
            }
            UpdateCombatHud();
            return;
        }
    }

    /// <summary>Walkers and animations while fighting: no orders, scripts or move triggers.</summary>
    private void AnimateCombat()
    {
        long now = Now;
        foreach (AnimatedObject animated in Settings.Animations || _checking ? _animated : [])
        {
            if (animated.Animator.Update(now))
            {
                ImageFrame frame = animated.Object.Frames[animated.Animator.Frame];
                animated.Sprite.Texture = TextureFor(frame);
                animated.Sprite.FlipH = animated.Animator.Mirrored;
            }
        }
        foreach (PartyMember member in _party)
        {
            if (member.Walker.Update(now, _region.IsBlocked))
            {
                DrawMember(member);
            }
        }
        foreach (WorldObject creature in _creatureNodes.Keys.ToList())
        {
            if (creature.Walker is { } walker && walker.Update(now, _region.IsBlocked))
            {
                DrawCreature(creature);
            }
        }
        if (_battle?.Current?.Source?.Walker is { } current)
        {
            _marker.Follow(current);
        }
        if (_overlay.Visible)
        {
            _overlay.QueueRedraw();
        }
    }

    // -- playing events back ---------------------------------------------------------------------------

    private void PlayEvent(CombatEvent e)
    {
        bool fast = _checking;
        switch (e)
        {
            case RoundStarted r:
                Log($"[color=#c0c0c0]— Round {r.Round} —[/color]");
                break;
            case TurnStarted t:
                if (t.Actor.PartyMember && t.Actor.Source is { } member && member.Index < _party.Count)
                {
                    _selected = member.Index;
                    _follow = true;
                }
                break;
            case Moved m:
                MoveSprite(m.Actor, m.From, m.To);
                return;
            case AttackRolled a:
                Log($"{Who(a.Actor)} attacks {Who(a.Target)}: rolls {a.Roll}, needs {Math.Max(2, a.Needed)} — {(a.Hit ? "[color=#ffd080]hit[/color]" : "miss")}");
                if (!a.Hit)
                {
                    Float(a.Target, "miss", new Color(0.8f, 0.8f, 0.8f));
                }
                _combatWait = fast ? 0 : AttackPause;
                return;
            case Damaged d:
                Log($"{Who(d.Target)} takes [color=#ff8080]{d.Amount}[/color] ({Math.Max(0, d.Target.Vitals.Hp)} left)");
                Float(d.Target, $"-{d.Amount}", new Color(1f, 0.35f, 0.3f));
                _combatWait = fast ? 0 : NotePause;
                return;
            case Healed h:
                Log($"{Who(h.Target)} heals [color=#a0ffa0]{h.Amount}[/color]");
                Float(h.Target, $"+{h.Amount}", new Color(0.5f, 1f, 0.5f));
                _combatWait = fast ? 0 : NotePause;
                return;
            case Avoided v:
                Log($"{Who(v.Target)}: {v.Why}");
                Float(v.Target, v.Why, new Color(0.7f, 0.8f, 1f));
                _combatWait = fast ? 0 : NotePause;
                return;
            case Cast c:
                Log($"{Who(c.Actor)} {(c.Effect is >= DarkSun.Game.Magic.Spellcasting.FirstPower and <= DarkSun.Game.Magic.Spellcasting.LastPower ? "uses" : "casts")} [color=#c0a0ff]{EffectName(c.Effect)}[/color]");
                Float(c.Actor, EffectName(c.Effect), new Color(0.8f, 0.65f, 1f));
                _combatWait = fast ? 0 : CastPause;
                return;
            case ConditionGained g:
                Log($"{Who(g.Target)} is {Words(g.Condition)}");
                Float(g.Target, Words(g.Condition), new Color(1f, 0.85f, 0.4f));
                break;
            case ConditionEnded x:
                Log($"[color=#a0a0a0]{Who(x.Target)} is no longer {Words(x.Condition)}[/color]");
                break;
            case Guarded gd:
                Log($"{Who(gd.Actor)} guards");
                Float(gd.Actor, "guard", new Color(0.8f, 0.8f, 1f));
                break;
            case Waited w:
                Log($"{Who(w.Actor)} waits");
                break;
            case Fell f:
                Log($"[color=#ff8080]{Who(f.Actor)} {(f.Status switch { Vitals.Down => "falls", Vitals.Dead => "dies", _ => "is destroyed" })}[/color]");
                if (f.Actor.PartyMember && _party.FirstOrDefault(p => p.Walker == f.Actor.Source?.Walker) is { } fallen)
                {
                    fallen.Node.Modulate = new Color(0.5f, 0.3f, 0.3f);
                }
                break;
            case ItemDestroyed i:
                Log($"{Who(i.Owner)}: {_world.Items.NameOf(i.Item.Record)} {i.Message}");
                break;
            case FieldMade fm:
                Log($"{EffectName(fm.Field.Effect.Number)} covers {fm.Field.Cells.Count} cells");
                break;
            case FieldEnded fe:
                Log($"[color=#a0a0a0]{EffectName(fe.Field.Effect.Number)} fades[/color]");
                break;
            case SideChanged s:
                Log($"{Who(s.Actor)} now fights for {(s.Side == 1 ? "the party" : "the enemy")}");
                break;
        }
        _combatWait = fast ? 0 : Math.Max(_combatWait, 0);
    }

    /// <summary>A step: a walker walks it (and is waited for), a creature that stands is redrawn on its cell.</summary>
    private void MoveSprite(Combatant c, Cell from, Cell to)
    {
        if (c.Source is not { } obj)
        {
            return;
        }
        if (obj.Walker is { } walker)
        {
            if (_checking)
            {
                walker.Teleport(to, (_, _) => false);
                return;
            }
            if (walker.Cell != from)
            {
                walker.Teleport(from, (_, _) => false);
            }
            walker.Follow([to]);
            _combatWalk = (walker, StepTimeout);
            return;
        }
        obj.FixedCell = to;
        OnWorldChanged(obj, null);
    }

    /// <summary>A number or word rising over a combatant and fading.</summary>
    private void Float(Combatant c, string text, Color color)
    {
        if (_checking)
        {
            return;
        }
        var label = new Label { Text = text, HorizontalAlignment = HorizontalAlignment.Center, ZIndex = 100 };
        label.AddThemeFontSizeOverride("font_size", 8);
        label.AddThemeColorOverride("font_color", color);
        label.AddThemeColorOverride("font_outline_color", Colors.Black);
        label.AddThemeConstantOverride("outline_size", 3);
        Vector2 head = Head(c) + new Vector2(-30, 0);
        int stacked = _floaters.GetChildren().OfType<Label>().Count(l => Math.Abs(l.Position.X - head.X) < 1 && head.Y - l.Position.Y < 24);
        label.Position = head + new Vector2(0, -10 * stacked);
        label.Size = new Vector2(60, 12);
        _floaters.AddChild(label);
        Tween tween = label.CreateTween();
        tween.TweenProperty(label, "position", label.Position + new Vector2(0, -18), 1.0);
        tween.Parallel().TweenProperty(label, "modulate:a", 0f, 1.0).SetDelay(0.4);
        tween.TweenCallback(Callable.From(label.QueueFree));
    }

    /// <summary>Above a combatant's head, in map pixels.</summary>
    private static Vector2 Head(Combatant c) =>
        new(c.Cell.X * Region.TileSize + Region.TileSize / 2f, (c.Cell.Y + 1) * Region.TileSize - 40);

    private static string Who(Combatant c) => c.Name.Length > 0 ? c.Name : "someone";

    private static string EffectName(int effect) =>
        Session.SpellName(effect) is { Length: > 0 } name ? name : $"effect {effect}";

    /// <summary>A condition as words ("CantAttack" → "can't attack").</summary>
    private static string Words(Condition c) => c switch
    {
        Condition.CantAttack => "unable to attack",
        Condition.ImprovedAc => "armored",
        _ => string.Concat(c.ToString().Select((ch, i) => i > 0 && char.IsUpper(ch) ? " " + char.ToLowerInvariant(ch) : char.ToLowerInvariant(ch).ToString())),
    };

    private void Log(string line)
    {
        _combatLines.Add(line);
        if (_combatLines.Count > 200)
        {
            _combatLines.RemoveRange(0, 100);
        }
        _combatLog.Text = string.Join('\n', _combatLines.TakeLast(LogLines));
        _combatLogPanel.Visible = !_checking;
        AddScriptLine("    " + System.Text.RegularExpressions.Regex.Replace(line, @"\[/?color[^\]]*\]", ""));
    }

    // -- the player's turn --------------------------------------------------------------------------------

    private void UpdateCombatHud()
    {
        if (_battle is not { } battle)
        {
            return;
        }
        Combatant? c = PlayerTurn;
        _combatPanel.Visible = !_checking;
        _castButton.Disabled = c is null || c.Spells.Count == 0 && (c.Powers.Count == 0 || c.Psp <= 0);
        _autoButton.Disabled = c is null;
        if (c is null)
        {
            _combatTurn.Text = battle.Current is { } other ? $"{Who(other)}'s turn" : $"Round {battle.Round}";
            _combatTip.Text = "";
            return;
        }
        int steps = c.MovementPoints / Math.Max(1, battle.Rules.StepCost);
        _combatTurn.Text = $"{Who(c)}: HP {c.Vitals.Hp}/{c.Vitals.MaxHp} · moves {((c.Flags & TurnFlags.MayMove) != 0 ? steps : 0)}"
            + ((c.Flags & TurnFlags.MayAttack) != 0 ? " · may attack" : "") + (c.Psp > 0 ? $" · PSP {c.Psp}" : "");
        _combatTip.Text = TipFor(c, MouseCell());
    }

    private Cell MouseCell()
    {
        Vector2 at = GetGlobalMousePosition();
        return new Cell((int)Math.Floor(at.X / Region.TileSize), (int)Math.Floor(at.Y / Region.TileSize));
    }

    /// <summary>What a click on <paramref name="cell"/> would do, with the chance to hit an enemy there.</summary>
    private string TipFor(Combatant c, Cell cell)
    {
        Battle battle = _battle!;
        Combatant? there = battle.At(cell);
        if (_castChoice is int spell)
        {
            if (battle.Spells.Effect(spell) is not { } e)
            {
                return "";
            }
            int range = e.RangeCells(c.Level);
            if (Battle.Distance(c.Cell, cell) > range)
            {
                return $"{EffectName(spell)}: out of range ({range} cells)";
            }
            IReadOnlyList<Combatant> hit = battle.AffectedBy(c, e, c.Level, cell);
            return $"{EffectName(spell)}: {(hit.Count == 0 ? "no one" : string.Join(", ", hit.Select(h => Who(h))))} · right-click cancels";
        }
        if (there is null || there == c)
        {
            return (c.Flags & TurnFlags.MayMove) != 0 && battle.Free(cell) ? "Click: move here" : "";
        }
        string who = $"{Who(there)} {there.Vitals.Hp}/{there.Vitals.MaxHp}";
        if (!c.IsEnemyOf(there))
        {
            return who;
        }
        bool missile = Battle.Distance(c, there) > Battle.Reach(c) && c.MissileWeapon is not null && c.Ammunition > 0;
        Weapon? w = missile ? c.MissileWeapon : c.Weapon;
        int needed = battle.ToHitNeeded(c, there, w);
        int chance = Math.Clamp(21 - needed, 1, 19) * 5;
        string reach = battle.CanAttack(c, there, missile) ? "" : (c.Flags & TurnFlags.MayAttack) == 0 ? " · already attacked" : " · walks up first";
        return $"{who} · {chance}% to hit (needs {Math.Clamp(needed, 2, 20)}){reach}";
    }

    /// <summary>A player's action, then: a turn with nothing left to do ends.</summary>
    private void PlayerAct(Action<Combatant> act)
    {
        if (PlayerTurn is not { } c)
        {
            return;
        }
        _castChoice = null;
        act(c);
        if (_battle!.Current == c && (c.Flags & (TurnFlags.MayMove | TurnFlags.MayAttack)) == 0)
        {
            _battle.EndTurn(c);
        }
        UpdateCombatHud();
    }

    /// <summary>
    /// Input in a fight: a click attacks an enemy (walking up to it first), moves
    /// to the ground, or casts the chosen spell; the combat keys; the map's own
    /// keys (scroll, zoom, menus) still work. True: handled.
    /// </summary>
    private bool CombatInput(InputEvent e)
    {
        if (!InCombat)
        {
            return false;
        }
        if (e.IsActionPressed(Controls.CombatEndTurn))
        {
            PlayerAct(c => _battle!.EndTurn(c));
            return true;
        }
        if (e.IsActionPressed(Controls.CombatGuard))
        {
            PlayerAct(c => _battle!.Guard(c));
            return true;
        }
        if (e.IsActionPressed(Controls.CombatWait))
        {
            PlayerAct(c => _battle!.Wait(c));
            return true;
        }
        if (e.IsActionPressed(Controls.CombatCast))
        {
            OpenCastMenu();
            return true;
        }
        if (e.IsActionPressed(Controls.CombatAuto))
        {
            ToggleAuto();
            return true;
        }
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } && _castChoice is not null)
        {
            _castChoice = null;
            UpdateCombatHud();
            return true;
        }
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
        {
            Cell cell = MouseCell();
            PlayerAct(c => Click(c, cell));
            return true;
        }
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left or MouseButton.Right })
        {
            return true; // no walking or verbs in a fight
        }
        if (e is InputEventMouseMotion)
        {
            UpdateCombatHud();
        }
        return e.IsActionPressed(Controls.NextCharacter) || e.IsActionPressed(Controls.QuickSave) || e.IsActionPressed(Controls.QuickLoad)
            || e.IsActionPressed(Controls.NextRegion) || e.IsActionPressed(Controls.PreviousRegion)
            || e.IsActionPressed(Controls.AddCharacter) || e.IsActionPressed(Controls.ChangeSprite);
    }

    private void Click(Combatant c, Cell cell)
    {
        Battle battle = _battle!;
        if (_castChoice is int spell)
        {
            _castChoice = null;
            CastAt(c, spell, cell);
            return;
        }
        if (battle.At(cell) is { } there && there != c && c.IsEnemyOf(there))
        {
            bool missile = Battle.Distance(c, there) > Battle.Reach(c) && c.MissileWeapon is not null && c.Ammunition > 0;
            if (!battle.CanAttack(c, there, missile) && (c.Flags & TurnFlags.MayMove) != 0 && battle.FreeCellNextTo(there, c) is { } next)
            {
                battle.MoveToward(c, next);
            }
            if (battle.Current == c && !battle.Attack(c, there, missile))
            {
                Notice(Battle.Distance(c, there) > Battle.Reach(c) ? $"{Who(there)} is out of reach." : $"{Who(c)} can't attack now.");
            }
            return;
        }
        if ((c.Flags & TurnFlags.MayMove) != 0 && battle.MoveToward(c, cell) == 0)
        {
            Notice("Can't move there.");
        }
    }

    private void CastAt(Combatant c, int spell, Cell cell)
    {
        Battle battle = _battle!;
        if (battle.Spells.Effect(spell) is not { } e)
        {
            return;
        }
        if (Battle.Distance(c.Cell, cell) > e.RangeCells(c.Level) && e.Target is not (7 or 8))
        {
            Notice($"{EffectName(spell)}: out of range.");
            return;
        }
        Cell at = e.Target is 7 or 8 ? c.Cell : cell;
        bool power = spell is >= DarkSun.Game.Magic.Spellcasting.FirstPower and <= DarkSun.Game.Magic.Spellcasting.LastPower;
        if (power ? !battle.UsePower(c, spell, at) : !battle.Cast(c, spell, c.Level, at))
        {
            Notice(power ? "Not enough psionic strength." : $"{Who(c)} can't cast now.");
        }
    }

    /// <summary>CAST: the spells this member still has ready and, with points, its powers.</summary>
    private void OpenCastMenu()
    {
        if (PlayerTurn is not { } c)
        {
            return;
        }
        _castMenu.Clear();
        foreach (var g in c.Spells.GroupBy(n => n).OrderBy(g => g.Key))
        {
            _castMenu.AddItem($"{EffectName(g.Key)}{(g.Count() > 1 ? $" ×{g.Count()}" : "")}", g.Key);
        }
        foreach (int p in c.Powers.Distinct().Order())
        {
            int cost = _battle!.Spells.Power(p)?.Cost ?? 0;
            _castMenu.AddItem($"{EffectName(p)} ({cost} PSP)", p);
            _castMenu.SetItemDisabled(_castMenu.ItemCount - 1, cost > c.Psp);
        }
        if (_castMenu.ItemCount == 0)
        {
            Notice($"{Who(c)} has nothing to cast.");
            return;
        }
        _castMenu.Position = (Vector2I)GetViewport().GetMousePosition();
        _castMenu.Popup();
    }

    private void ChooseCast(int spell)
    {
        if (PlayerTurn is not { } c || _battle!.Spells.Effect(spell) is not { } e)
        {
            return;
        }
        if (e.Target is 7 or 8)
        {
            PlayerAct(m => CastAt(m, spell, m.Cell));
            return;
        }
        _castChoice = spell;
        UpdateCombatHud();
    }

    /// <summary>AUTO: the computer fights for the member whose turn it is, for the rest of the fight.</summary>
    private void ToggleAuto()
    {
        if (PlayerTurn is not { } c)
        {
            return;
        }
        c.ComputerControlled = true;
        Log($"{Who(c)} fights on by itself");
        UpdateCombatHud();
    }

    // -- development and the headless check ---------------------------------------------------------------

    /// <summary>
    /// <c>--fight</c> (and the check): the nearest creature that fights is
    /// brought a few cells from the party and attacked.
    /// </summary>
    private bool StartTestFight()
    {
        WorldObject? foe = _world.Objects.Where(o => o.Kind == ObjectKind.Creature && !o.Hidden && o.Vitals.Alive
                && o.Record is { Mind: not (CombatRecord.MindIdle or CombatRecord.MindFlees) })
            .OrderBy(o => Battle.Distance(o.Cell, _party[0].Walker.Cell)).FirstOrDefault();
        Cell leader = _party[0].Walker.Cell;
        Cell? near = foe is null ? null : Enumerable.Range(3, 6).SelectMany(r => new[] { new Cell(leader.X + r, leader.Y), new Cell(leader.X - r, leader.Y),
            new Cell(leader.X, leader.Y + r), new Cell(leader.X, leader.Y - r) }).FirstOrDefault(c => !_region.IsBlocked(c.X, c.Y)
            && _world.Party.All(p => p.Cell != c), new Cell(-1, -1));
        if (foe is null || near is not { X: >= 0 } spot)
        {
            return false;
        }
        if (foe.Walker is { } w)
        {
            w.Teleport(spot, (_, _) => false);
            DrawCreature(foe);
        }
        else
        {
            foe.FixedCell = spot;
            OnWorldChanged(foe, null);
        }
        foe.Allegiance = 0;
        return StartCombat(foe, $"attack {Describe(foe)}");
    }

    // -- headless check ------------------------------------------------------------------------------

    /// <summary>
    /// <c>--check-combat</c>: a new game; the nearest creature turns hostile and
    /// the party attacks it, fighting by itself; the fight is played to its end
    /// (or 40 rounds) and summed up.
    /// </summary>
    private async void CheckCombat()
    {
        _checking = true;
        _autoParty = true;
        var problems = new List<string>();
        if (!StartTestFight() || _battle is not { } battle)
        {
            GD.Print("combat: no creature to fight in the start region");
            GetTree().Quit(1);
            return;
        }
        int frames = 0;
        while (InCombat && battle.Round <= 40 && frames++ < 200_000)
        {
            CombatProcess(1.0 / 60);
            if (frames % 500 == 0)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
        }
        if (InCombat)
        {
            problems.Add($"still fighting after {battle.Round} rounds");
        }
        int attacks = battle.Log.OfType<AttackRolled>().Count(), damage = battle.Log.OfType<Damaged>().Sum(d => d.Amount);
        if (attacks == 0)
        {
            problems.Add("no one attacked");
        }
        if (_combatLines.Count == 0)
        {
            problems.Add("nothing was logged");
        }
        GD.Print($"combat: {battle.Combatants.Count} combatants, {battle.Round} rounds, {attacks} attacks, {damage} damage, "
            + $"{battle.Log.OfType<Fell>().Count()} fell; the log has {_combatLines.Count} lines");
        foreach (string line in _combatLines.Take(12))
        {
            GD.Print("  " + System.Text.RegularExpressions.Regex.Replace(line, @"\[/?color[^\]]*\]", ""));
        }
        foreach (string p in problems)
        {
            GD.Print($"  PROBLEM: {p}");
        }
        GD.Print(problems.Count == 0 ? "combat: OK" : $"combat: {problems.Count} problems");
        GetTree().Quit(problems.Count == 0 ? 0 : 1);
    }
}
