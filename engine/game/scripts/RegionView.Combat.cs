using DarkSun.Data.Images;
using DarkSun.Data.Items;
using DarkSun.Data.Regions;
using DarkSun.Game.Animation;
using DarkSun.Game.Combat;
using DarkSun.Game.Movement;
using DarkSun.Game.Rules;
using DarkSun.Game.Scripting;
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

    private Battle? _battle;

    /// <summary>The enemy chosen with Tab (<see cref="ChooseEnemy"/>), whom Enter attacks; its ring is the red one.</summary>
    private Combatant? _chosenEnemy;
    private readonly Queue<CombatEvent> _combatEvents = new();
    private double _combatWait;
    private (Walker Walker, double Left)? _combatWalk;
    private readonly List<string> _combatLines = [];
    private PanelContainer _combatPanel = null!;
    private Label _combatTurn = null!;
    private Label _combatTip = null!;
    private CombatLogView _combatLog = null!;
    private PopupMenu _castMenu = null!;
    private Node2D _floaters = null!;
    private Button _castButton = null!, _autoButton = null!;

    /// <summary>A spell or power chosen with CAST (or an item's, USE), waiting for its target.</summary>
    private CastChoice? _castChoice;

    /// <summary>
    /// A spell to cast: a member's own, at their level, or an item's
    /// (<paramref name="Item"/>, carried by <paramref name="Holder"/>), at
    /// <see cref="ItemMagic.CasterLevel"/>, which uses one of its charges.
    /// </summary>
    private sealed record CastChoice(int Spell, int Level, Item? Item = null, WorldObject? Holder = null);

    /// <summary>Headless checks: the party fights by itself.</summary>
    private bool _autoParty;

    private bool InCombat => _battle is not null;

    /// <summary>The longest the playback waits for a missile or spell in flight.</summary>
    private const double GraphicsTimeout = 1.5;

    private double _graphicsWait;

    /// <summary>The party member whose turn it is, waiting for the player (null while the battle plays or the AI acts).</summary>
    private Combatant? PlayerTurn =>
        _battle is { Current: { } c } && _combatEvents.Count == 0 && _combatWalk is null && _combatWait <= 0 && !c.UnderAi && c.Fights ? c : null;

    private void BuildCombatHud(CanvasLayer layer)
    {
        _combatLayer = layer;
        _floaters = new Node2D { ZIndex = 100 };
        AddChild(_floaters);

        _combatPanel = new PanelContainer { Visible = false };
        // At the lower left: the combat log has the lower right, as in Obsidian Edition.
        _combatPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft);
        _combatPanel.GrowHorizontal = Control.GrowDirection.End;
        _combatPanel.GrowVertical = Control.GrowDirection.Begin;
        _combatPanel.OffsetBottom = -4;
        _combatPanel.OffsetLeft = 4;
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

        _combatLog = new CombatLogView
        {
            Log = NewCombatLog(),
            Battle = () => _battle,
            PartySlot = c => c.PartyMember && c.Source is { } o && o.Index < _party.Count ? o.Index : null,
            Covered = () => _checking || MapCovered || _journal.Visible || _options.Visible || _debugger.Visible,
        };
        layer.AddChild(_combatLog);

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
        _keyboardWalker = null;
        ClearPicked();
        Battle? battle = Encounter.Start(_world, slot => slot < Session.Party.Count ? Session.Party[slot] : null, dice, Session.Rules,
            Session.CombatRules, Session.SpellRules, Session.CreatureRules, attacked, fallsWhenShown: true);
        if (battle is null)
        {
            AddScriptLine($"> {why}: no one to fight");
            return false;
        }
        _pendingUse = null;
        _verbMenu.Close();
        _containerWindow.Close();
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
        // 612E:1359 → 6BB8:0EFA: a fight started with a creature takes the music level the region gives it.
        if (attacked is { Kind: not ObjectKind.Party } foe && RegionInfo.Read(Session.Open("RESOURCE.GFF"), _region.Number) is { } region)
        {
            Session.ScriptHost.FightMusic(region.FightLevel(-foe.Id));
        }
        _combatLines.Clear();
        _combatEvents.Clear();
        _combatLog.Log = NewCombatLog();
        _combatLog.FollowNewest();
        battle.Happened += e =>
        {
            _combatEvents.Enqueue(e);
            _combatLog.Log.Add(e);
        };
        int enemies = battle.Combatants.Count(c => !c.PartyMember && battle.Combatants.Any(p => p.PartyMember && p.IsEnemyOf(c)));
        Log(battle.Combatants.Any(c => c.PartyMember)
            ? $"Combat! {enemies} {(enemies == 1 ? "enemy" : "enemies")}"
            : $"The party watches: {string.Join(" against ", battle.Combatants.GroupBy(c => c.Side).Select(g => string.Join(", ", g.Select(c => c.Name))))}");
        AddScriptLine($"> {why}: combat, {battle.Combatants.Count} combatants");
        _combatPanel.Visible = !_checking && PartyFights;
        UpdateSilhouettes();
        return true;
    }

    /// <summary>A party member is in the fight (else the party only watches, and has no buttons).</summary>
    private bool PartyFights => _battle?.Combatants.Any(c => c.PartyMember) == true;

    /// <summary>REQUEST(14) staged a bout for the party to watch; it starts once the announcer's text is put away.</summary>
    private bool _watchedFightPending;

    private void StartWatchedFight()
    {
        if (!_watchedFightPending || _dialogueWindow.Visible || !Session.ScriptHost.Dialogue.IsEmpty || InCombat)
        {
            return;
        }
        _watchedFightPending = false;
        if (_checkingVerbs || !StartCombat(null, "the arena bout"))
        {
            ResumeAfterWatchedFight();
        }
    }

    /// <summary>The only script suspended is REQUEST 14's, waiting for the bout to end (nothing asks the player).</summary>
    private bool WaitingOnTheBout => Session.ScriptHost.WatchedFight is not null && Session.ScriptHost.Dialogue.Prompt is null;

    /// <summary>The script that staged the bout goes on (REQUEST 14 returns).</summary>
    private void ResumeAfterWatchedFight()
    {
        if (Session.ScriptHost.WatchedFight is null)
        {
            return;
        }
        Session.ScriptHost.WatchedFight = null;
        if (Session.Scripts.IsSuspended && Session.Scripts.PausedFor is null)
        {
            ReportScript("the bout is over", Session.Scripts.Resume());
        }
    }

    /// <summary>Creatures that fell in the fight but whose fall wasn't played back leave the map (<see cref="GameWorld.Fell"/> does nothing twice).</summary>
    private void LeaveFallen(Battle battle)
    {
        foreach (Combatant c in battle.Combatants.Where(c => !c.PartyMember && !c.Alive))
        {
            if (c.Source is { Kind: not ObjectKind.Party } o)
            {
                _world.Fell(o);
            }
        }
    }

    /// <summary>The fight is dropped as it stands (the region is left, or a check only wanted to see it start).</summary>
    private void AbandonCombat()
    {
        _watchedFightPending = false;
        Session.ScriptHost.WatchedFight = null;
        if (_battle is null)
        {
            return;
        }
        LeaveFallen(_battle);
        _battle = null;
        _combatEvents.Clear();
        (_combatWait, _combatWalk, _castChoice) = (0, null, null);
        _combatPanel.Visible = false;
        HideCombatMenu();
        ClearGraphics();
        UpdateSilhouettes();
    }

    private void EndCombat()
    {
        if (_battle is not { } battle)
        {
            return;
        }
        LeaveFallen(battle);
        bool fallen = Encounter.End(battle, _world);
        Session.ScriptHost.CheckLevels(_world); // 8E77:0A8F at the end of a fight
        Session.ScriptHost.FightMusicOver();
        bool won = !fallen;
        Log(won ? "The fight is over." : "The party has fallen.", won ? LogKind.Hit : LogKind.Fell);
        AddScriptLine($"> combat over after {battle.Round} rounds: {(won ? "won" : "lost")}");
        foreach (Combatant c in battle.Combatants.Where(c => c.PartyMember && c.Source is not null))
        {
            if (_party.FirstOrDefault(m => m.Walker == c.Source!.Walker) is { } member)
            {
                member.Node.Modulate = c.Alive ? Colors.White : new Color(0.5f, 0.3f, 0.3f);
                OnWorldChanged(c.Source!, null);
            }
        }
        _battle = null;
        _castChoice = null;
        _combatPanel.Visible = false;
        HideCombatMenu();
        ClearGraphics();
        _marker.Follow(Selected?.Walker);
        UpdateHud();
        if (!_checking)
        {
            Notice(won ? "The fight is over." : "The party has fallen.");
            if (fallen)
            {
                GameOver();
            }
        }
        _partyFell = fallen;
        if (won)
        {
            ShowLeaderAlone(); // 1587:1B43: the others gather into the leader again
        }
        UpdateSilhouettes();
        ResumeAfterWatchedFight();
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
        _combatPanel.Visible = !_checking && !_character.IsOpen && PartyFights; // the character screen covers the map, with its own controls
        for (int guard = 0; guard < 64; guard++)
        {
            if (_combatWait > 0)
            {
                _combatWait -= delta;
                return;
            }
            if (GraphicsBlock && _graphicsWait < GraphicsTimeout)
            {
                _graphicsWait += delta;
                return;
            }
            _graphicsWait = 0;
            if (Session.Scripts.IsSuspended && !WaitingOnTheBout || _transitioning || !InCombat)
            {
                return; // a move trigger's script is talking to the player, or the party is leaving
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
                    if (battle.Round > 0)
                    {
                        _world.PassTime(60); // 612E:0754: a minute a round, not for the first
                    }
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
    private void AnimateCombat(double delta)
    {
        UpdateGraphics(delta);
        long now = Now;
        foreach (AnimatedObject animated in Settings.Animations || _checking ? _animated : [])
        {
            (Sprite2D sprite, RegionObject obj, ScmdAnimator animator) = (animated.Sprite, animated.Object, animated.Animator);
            // On the clock it started on (Animate): a door a script opened just before FIGHT steps on the view's
            // running time; the fight's clock, which stood still while the map was held (a speech), lags behind it.
            if (!animator.Update(animated.OwnClock ? RunningNow : now))
            {
                continue;
            }
            ImageFrame frame = obj.Frames[animator.Frame];
            sprite.Texture = TextureFor(frame);
            sprite.Visible = frame.Width > 0 && frame.Height > 0 && !_hidden.Contains(obj);
            sprite.FlipH = animator.Mirrored;
            Vector2 at = _moved.TryGetValue(obj, out Vector2 m) ? m : new Vector2(obj.EtabX, obj.EtabY);
            sprite.Position = at + new Vector2(animator.OffsetX, animator.OffsetY);
            sprite.Offset = -new Vector2(animator.HotX, animator.HotY + obj.YAdjust);
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
        foreach (string line in _combatLog.Log.Reveal(e))
        {
            Remember(line);
        }
        switch (e)
        {
            case TurnStarted t:
                _menuShown = false;
                HideCombatMenu();
                if (t.Actor.PartyMember && t.Actor.Source is { } member && member.Index < _party.Count)
                {
                    _selected = member.Index;
                    _follow = true;
                }
                break;
            case Moved m:
                MoveSprite(m.Actor, m.From, m.To);
                MoveTriggers(m.Actor, m.To);
                return;
            case AttackRolled a:
                if (!a.Hit)
                {
                    Float(a.Target, "miss", new Color(0.8f, 0.8f, 0.8f));
                }
                LookOfAttack(a);
                _combatWait = fast ? 0 : AttackPause;
                return;
            case Damaged d:
                LookOfDamage(d);
                _combatWait = fast ? 0 : NotePause;
                return;
            case Healed h:
                Float(h.Target, $"+{h.Amount}", new Color(0.5f, 1f, 0.5f));
                LookOfSpellOn(h.Target);
                _combatWait = fast ? 0 : NotePause;
                return;
            case Avoided v:
                Float(v.Target, v.Why, new Color(0.7f, 0.8f, 1f));
                _combatWait = fast ? 0 : NotePause;
                return;
            case Cast c:
                Float(c.Actor, EffectName(c.Effect), new Color(0.8f, 0.65f, 1f));
                double look = LookOfCast(c);
                _combatWait = fast ? 0 : Math.Max(CastPause, look);
                return;
            case ConditionGained g:
                Float(g.Target, CombatLog.Words(g.Condition), new Color(1f, 0.85f, 0.4f));
                LookOfSpellOn(g.Target);
                break;
            case Guarded gd:
                Float(gd.Actor, "guard", new Color(0.8f, 0.8f, 1f));
                break;
            case Fell f:
                if (f.Actor.PartyMember && _party.FirstOrDefault(p => p.Walker == f.Actor.Source?.Walker) is { } fallen)
                {
                    fallen.Node.Modulate = new Color(0.5f, 0.3f, 0.3f);
                }
                else if (f.Actor.Source is { Kind: not ObjectKind.Party } gone)
                {
                    // Off the map now, as the fall is shown: the battle decided it a while ago.
                    _world.Fell(gone);
                }
                break;
            case FieldMade fm:
                LookOfField(fm.Field);
                break;
            case FieldEnded fe:
                FieldGone(fe.Field);
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

    /// <summary>A line of words in the combat log, at once.</summary>
    private void Log(string line, LogKind kind = LogKind.Other)
    {
        _combatLog.Log.Note(line, kind);
        Remember(line);
    }

    /// <summary>A line shown in the combat log, kept for the checks and the script log.</summary>
    private void Remember(string line)
    {
        _combatLines.Add(line);
        if (_combatLines.Count > 200)
        {
            _combatLines.RemoveRange(0, 100);
        }
        AddScriptLine("    " + line);
    }

    /// <summary>A combat log with the game's spell and item names.</summary>
    private CombatLog NewCombatLog() => new(EffectName, item => _world.Items.NameOf(item.Record));

    // -- the player's turn --------------------------------------------------------------------------------

    private void UpdateCombatHud()
    {
        if (_battle is not { } battle)
        {
            return;
        }
        Combatant? c = PlayerTurn;
        _combatPanel.Visible = !_checking && !_character.IsOpen;
        _castButton.Disabled = c is null || c.Spells.Count == 0 && (c.Powers.Count == 0 || c.Psp <= 0);
        _autoButton.Disabled = c is null;
        if (c is null)
        {
            _combatTurn.Text = battle.Current is { } other ? $"{Who(other)}'s turn" : $"Round {battle.Round}";
            _combatTip.Text = "";
            return;
        }
        if (!_menuShown && (c.Flags & TurnFlags.MayMove) == 0 && (c.Flags & ~TurnFlags.Waited) != TurnFlags.None)
        {
            ShowCombatMenu(c);
        }
        int steps = c.MovementPoints / Math.Max(1, battle.Rules.StepCost);
        _combatTurn.Text = $"{Who(c)}: HP {c.Vitals.Hp}/{c.Vitals.MaxHp} · moves {((c.Flags & TurnFlags.MayMove) != 0 ? steps : 0)}"
            + ((c.Flags & TurnFlags.MayAttack) != 0 ? " · may attack" : "") + (c.Psp > 0 ? $" · PSP {c.Psp}" : "");
        _combatTip.Text = TipFor(c, MouseCell());
    }

    /// <summary>The cell a click in a fight aims at: the mouse's, or that of the combatant drawn there (<see cref="AimCell"/>).</summary>
    private Cell MouseCell() => AimCell(GetGlobalMousePosition());

    /// <summary>What a click on <paramref name="cell"/> would do, with the chance to hit an enemy there.</summary>
    private string TipFor(Combatant c, Cell cell)
    {
        Battle battle = _battle!;
        Combatant? there = battle.At(cell);
        if (_castChoice is { Spell: var spell, Level: var level })
        {
            if (battle.Spells.Effect(spell) is not { } e)
            {
                return "";
            }
            if (battle.Aim(c, e, level, cell) is var aim and not SpellAim.Ok)
            {
                return $"{EffectName(spell)}: {AimText(aim).ToLowerInvariant()} · right-click cancels";
            }
            IReadOnlyList<Combatant> hit = battle.AffectedBy(c, e, level, cell);
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
        bool missile = MissileClick(c, there);
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
        HideCombatMenu();
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
        if (e.IsActionPressed(Controls.MoveLeft) || e.IsActionPressed(Controls.MoveRight)
            || e.IsActionPressed(Controls.MoveUp) || e.IsActionPressed(Controls.MoveDown))
        {
            if (KeyboardMovementLocked)
            {
                return true;
            }
            Vector2 direction = Input.GetVector(Controls.MoveLeft, Controls.MoveRight, Controls.MoveUp, Controls.MoveDown);
            PlayerAct(c =>
            {
                if ((c.Flags & TurnFlags.MayMove) != 0)
                {
                    _battle!.MoveToward(c, new Cell(c.Cell.X + Math.Sign(direction.X), c.Cell.Y + Math.Sign(direction.Y)));
                }
            });
            return true;
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
        if (Settings.ChooseEnemyKeys && e.IsActionPressed(Controls.CombatChooseEnemy))
        {
            ChooseEnemy(e is InputEventWithModifiers { ShiftPressed: true } ? -1 : 1);
            return true;
        }
        if (Settings.ChooseEnemyKeys && e.IsActionPressed(Controls.CombatAttackChosen) && _chosenEnemy is not null)
        {
            AttackChosen();
            return true;
        }
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true })
        {
            // 1587:01C7: a right-click puts an aimed spell away, else turns the pointer to the next cursor.
            if (_castChoice is not null)
            {
                _castChoice = null;
            }
            else
            {
                _gameMenu.CycleCursor();
            }
            UpdateCombatHud();
            return true;
        }
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
        {
            ClickCell(MouseCell());
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

    /// <summary>A left click on <paramref name="cell"/> in a fight.</summary>
    private void ClickCell(Cell cell)
    {
        CastChoice? cast = _castChoice; // PlayerAct clears it
        PlayerAct(c => Click(c, cell, cast));
    }

    /// <param name="cast">The spell chosen with CAST or USE: cast at the cell, or, if it can't be, still chosen.</param>
    private void Click(Combatant c, Cell cell, CastChoice? cast)
    {
        Battle battle = _battle!;
        if (cast is not null)
        {
            if (!CastAt(c, cast, cell))
            {
                _castChoice = cast;
            }
            return;
        }
        if (_cursor == GameMenu.Cursor.Look)
        {
            if (battle.At(cell) is { } seen && battle.InSight(c.Cell, cell))
            {
                Log($"{Who(seen)}: {seen.Vitals.Hp}/{seen.Vitals.MaxHp} hit points{(c.IsEnemyOf(seen) ? ", an enemy" : "")}", LogKind.Detail);
            }
            return;
        }
        if (_cursor == GameMenu.Cursor.Attack)
        {
            // The ATTACK cursor attacks only what it can reach from here (its pointer says which way: AttackPointer).
            if (battle.At(cell) is { } target && target != c && battle.InSight(c, target))
            {
                bool shoot = Battle.Distance(c, target) >= 2;
                if (!battle.Attack(c, target, shoot))
                {
                    Notice(shoot ? $"{Who(c)} can't shoot {Who(target)} from here." : $"{Who(c)} can't attack now.");
                }
            }
            return;
        }
        if (battle.At(cell) is { } there && there != c && c.IsEnemyOf(there))
        {
            AttackOrApproach(c, there);
            return;
        }
        if ((c.Flags & TurnFlags.MayMove) != 0 && battle.MoveToward(c, cell) == 0)
        {
            Notice("Can't move there.");
        }
    }

    /// <summary>Attack an enemy: shoot it if a click would (<see cref="MissileClick"/>), else walk up to it first if it is out of reach.</summary>
    private void AttackOrApproach(Combatant c, Combatant there)
    {
        Battle battle = _battle!;
        bool missile = MissileClick(c, there);
        if (!battle.CanAttack(c, there, missile) && (c.Flags & TurnFlags.MayMove) != 0 && battle.FreeCellNextTo(there, c) is { } next)
        {
            battle.MoveToward(c, next);
        }
        if (battle.Current == c && !battle.Attack(c, there, missile))
        {
            Notice(Battle.Distance(c, there) > Battle.Reach(c) ? $"{Who(there)} is out of reach." : $"{Who(c)} can't attack now.");
        }
    }

    /// <summary>
    /// Tab on a party member's turn: the next nearest living enemy is chosen
    /// (<paramref name="step"/> −1, Shift+Tab: the one before), named in the log,
    /// and the view goes to it if it is out of sight. After the Obsidian
    /// Edition's (its FEATURES.md, "Choosing an enemy: Tab, Enter and the rings").
    /// </summary>
    private void ChooseEnemy(int step)
    {
        if (PlayerTurn is not { } c)
        {
            return;
        }
        List<Combatant> enemies = [.. _battle!.Combatants.Where(e => e.Alive && c.IsEnemyOf(e))
            .OrderBy(e => Battle.Distance(c, e)).ThenBy(e => e.Cell.Y).ThenBy(e => e.Cell.X)];
        if (enemies.Count == 0)
        {
            _chosenEnemy = null;
            return;
        }
        int now = _chosenEnemy is null ? -1 : enemies.IndexOf(_chosenEnemy);
        int index = now < 0 ? (step > 0 ? 0 : enemies.Count - 1) : (now + step + enemies.Count) % enemies.Count;
        _chosenEnemy = enemies[index];
        Log($"Chosen: {TipFor(c, _chosenEnemy.Cell)}", LogKind.Detail);
        if (SpriteOf(_chosenEnemy) is { } sprite)
        {
            Vector2 size = GetViewportRect().Size / _camera.Zoom;
            if (!new Rect2(_camera.GetScreenCenterPosition() - size / 2, size).Grow(-16).HasPoint(sprite.Position))
            {
                _follow = false;
                _camera.Position = sprite.Position;
            }
        }
    }

    /// <summary>Enter: the current member attacks the enemy chosen with Tab, as a click on it would, whatever stands in front of it.</summary>
    private void AttackChosen()
    {
        if (_chosenEnemy is not { Alive: true } target || _battle?.Combatants.Contains(target) != true)
        {
            _chosenEnemy = null;
            return;
        }
        PlayerAct(c =>
        {
            if (c.IsEnemyOf(target))
            {
                AttackOrApproach(c, target);
            }
        });
    }

    /// <summary>
    /// The red rings under the enemies' feet, as the options say: none, the
    /// chosen one's, or every enemy's (the chosen one's thicker and redder).
    /// </summary>
    private List<(Vector2 Feet, float Radius, bool Chosen)> EnemyRings()
    {
        if (_battle is not { } battle)
        {
            _chosenEnemy = null;
            return [];
        }
        if (_chosenEnemy is { Alive: false })
        {
            _chosenEnemy = null;
        }
        int mode = Settings.EnemyRings;
        List<Combatant> party = [.. battle.Combatants.Where(c => c.PartyMember)];
        var rings = new List<(Vector2, float, bool)>();
        foreach (Combatant enemy in battle.Combatants.Where(e => e.Alive && !e.PartyMember && party.Any(p => p.IsEnemyOf(e))))
        {
            bool chosen = enemy == _chosenEnemy;
            if ((mode == 2 || mode == 1 && chosen) && SpriteOf(enemy) is { Visible: true } sprite)
            {
                int halfWidth = enemy.Source?.Walker?.Footprint.HalfWidth ?? 0;
                rings.Add((sprite.Position + new Vector2(0, 1), 6 + halfWidth * 16, chosen));
            }
        }
        return rings;
    }

    /// <summary>
    /// Cast <paramref name="cast"/> at <paramref name="cell"/> where the original's pointer allows it
    /// (<see cref="Battle.Aim"/>); false if it wasn't cast. An item's spell is cast (a power too, with no
    /// psionic strength spent) and uses one of the item's charges (<c>78A9:2324</c>).
    /// </summary>
    private bool CastAt(Combatant c, CastChoice cast, Cell cell)
    {
        Battle battle = _battle!;
        int spell = cast.Spell;
        if (battle.Spells.Effect(spell) is not { } e)
        {
            return false;
        }
        if (battle.Aim(c, e, cast.Level, cell) is var aim and not SpellAim.Ok)
        {
            Notice($"{EffectName(spell)}: {AimText(aim).ToLowerInvariant()}.");
            return false;
        }
        Cell at = e.Target is 7 or 8 ? c.Cell : cell;
        if (c.WasHit && cast.Item is null)
        {
            Notice("YOU WERE HIT"); // a wound this round spoils casting until the next
            return false;
        }
        if (cast is { Item: { } item, Holder: { } holder })
        {
            if (!battle.Cast(c, spell, cast.Level, at, ownSpell: false))
            {
                Notice($"{Who(c)} can't cast now.");
                return false;
            }
            string name = ItemText.Name(_world, item);
            AddScriptLine($"> {Who(c)} casts {EffectName(spell)} from {name}");
            if (_world.UseItemCharge(holder, item))
            {
                Log($"{name} is used up.");
            }
            return true;
        }
        bool power = spell is >= DarkSun.Game.Magic.Spellcasting.FirstPower and <= DarkSun.Game.Magic.Spellcasting.LastPower;
        if (power ? !battle.UsePower(c, spell, at) : !battle.Cast(c, spell, cast.Level, at))
        {
            Notice(power ? "Not enough psionic strength." : $"{Who(c)} can't cast now.");
            return false;
        }
        return true;
    }

    /// <summary>
    /// USE on an item in the inventory during a fight (<c>91F7:03D0</c>, <see cref="ItemMagic"/>),
    /// on member <paramref name="member"/>'s turn: a spell on oneself is cast at once, one to aim
    /// waits for its target as CAST's do, and a lemon's makes the member whole; each uses a charge
    /// and the turn. Null when done (the screen closes), else why not.
    /// </summary>
    private string? CastItemInFight(int member, Item item, WorldObject holder, ItemUse use)
    {
        if (PlayerTurn is not { } c || c.Source != _world[member])
        {
            return "NOT THIS CHARACTER'S TURN";
        }
        var cast = new CastChoice(use.Spell, ItemMagic.CasterLevel, item, holder);
        if (use.Kind == ItemUseKind.FullHeal)
        {
            PlayerAct(m =>
            {
                m.Vitals.Hp = m.Vitals.MaxHp;
                Log($"{Who(m)} is made whole by {ItemText.Name(_world, item)}.");
                _world.UseItemCharge(holder, item);
                _battle!.EndTurn(m);
            });
        }
        else if (use.Kind == ItemUseKind.OnSelf || _battle!.Spells.Effect(use.Spell) is { Target: 7 or 8 })
        {
            PlayerAct(m => CastAt(m, cast, m.Cell));
        }
        else
        {
            _castChoice = cast;
            UpdateCombatHud();
        }
        return null;
    }

    /// <summary>The original's messages: "TOO FAR AWAY" (<c>DS:0BEB</c>), "NO LINE OF SIGHT" (<c>DS:0BF8</c>).</summary>
    private static string AimText(SpellAim aim) => aim switch
    {
        SpellAim.NoTarget => "No one there",
        SpellAim.OutOfRange => "Too far away",
        _ => "No line of sight",
    };

    /// <summary>CAST: the spells this member can still cast (with its level's casts left) and, with points, its powers.</summary>
    private void OpenCastMenu()
    {
        if (PlayerTurn is not { } c)
        {
            return;
        }
        _castMenu.Clear();
        foreach (int spell in c.Spells.Distinct().Order())
        {
            int casts = c.CastsOf(spell);
            _castMenu.AddItem($"{EffectName(spell)}{(casts > 1 ? $" ×{casts}" : "")}", spell);
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
        if (c.Source is { } o && o.Index < Session.Party.Count)
        {
            Session.Party[o.Index].RememberCast(spell); // 78A9:2324: the quick-cast list
        }
        if (e.Target is 7 or 8)
        {
            PlayerAct(m => CastAt(m, new CastChoice(spell, m.Level), m.Cell));
            return;
        }
        _castChoice = new CastChoice(spell, c.Level);
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

    // -- the original's windows ------------------------------------------------------------------------

    private CanvasLayer _combatLayer = null!;
    private UiWindowView? _combatMenu;
    private bool _menuShown, _partyFell;

    /// <summary>
    /// <c>WIND</c> 14000 (<c>5F6D:0020</c>): its title bar (button 14004), the
    /// GUARD, WAIT and END TURN buttons, and a close button (10308).
    /// </summary>
    private const int CombatMenuWindow = 14000, MenuGuard = 14001, MenuWait = 14002, MenuEndTurn = 14003, MenuTitle = 14004, MenuClose = 10308;

    /// <summary>
    /// The original's combat menu (<c>1587:2EAF</c>, from <c>1587:1B43</c>):
    /// once a member can't move any more but may still act, "END name's MOVE"
    /// (or "END MOVE" for a long name) with GUARD, WAIT and END TURN, by the
    /// member. Clicking elsewhere (attacking, casting) just goes on.
    /// </summary>
    private void ShowCombatMenu(Combatant c)
    {
        _menuShown = true;
        if (_checking)
        {
            return;
        }
        if (_combatMenu is null)
        {
            _combatMenu = UiWindowView.Build(CombatMenuWindow, _palette);
            if (_combatMenu is null)
            {
                return;
            }
            _combatMenu.MouseFilter = Control.MouseFilterEnum.Stop;
            _combatLayer.AddChild(_combatMenu);
            (int Button, Action<Combatant> Act)[] buttons =
                [(MenuGuard, m => _battle!.Guard(m)), (MenuWait, m => _battle!.Wait(m)), (MenuEndTurn, m => _battle!.EndTurn(m))];
            foreach (var (number, act) in buttons)
            {
                if (_combatMenu.Button(number) is { } button)
                {
                    button.Pressed += () => PlayerAct(act);
                }
            }
            _combatMenu.SetButtonText(MenuGuard, "GUARD");
            _combatMenu.SetButtonText(MenuWait, "WAIT");
            _combatMenu.SetButtonText(MenuEndTurn, "END TURN");
            if (_combatMenu.Button(MenuClose) is { } close)
            {
                close.Pressed += HideCombatMenu;
            }
        }
        // DS:0C1D "END %Fs's MOVE", or DS:0C2C "END MOVE" when that is longer than 20.
        string title = $"END {Who(c).ToUpperInvariant()}'s MOVE";
        _combatMenu.SetButtonText(MenuTitle, title.Length > 20 ? "END MOVE" : title);
        float scale = Math.Max(1f, MathF.Floor(Math.Min(GetViewportRect().Size.X / RetroScreen.Width, GetViewportRect().Size.Y / RetroScreen.Height)));
        _combatMenu.Scale = new Vector2(scale, scale);
        Vector2 head = GetViewport().GetCanvasTransform() * Head(c);
        Vector2 size = _combatMenu.Size * scale, screen = GetViewportRect().Size;
        _combatMenu.Position = new Vector2(Math.Clamp(head.X - size.X / 2, 0, screen.X - size.X), Math.Clamp(head.Y - size.Y - 8, 0, screen.Y - size.Y)).Floor();
        _combatMenu.Visible = true;
    }

    private void HideCombatMenu()
    {
        if (_combatMenu is not null)
        {
            _combatMenu.Visible = false;
        }
    }

    /// <summary>
    /// The party has fallen (<c>85FE:02C9</c>): <c>WIND</c> 3004 over the screen
    /// with <c>BMP</c> 10004; its button, or any key, goes back to the main menu.
    /// </summary>
    private void GameOver()
    {
        if (UiWindowView.Build(GameOverWindow, _palette, backdrop: 0) is not { } window)
        {
            GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, MainMenu.ScenePath);
            return;
        }
        var layer = new CanvasLayer { Layer = 50 };
        var shade = new ColorRect { Color = Colors.Black, MouseFilter = Control.MouseFilterEnum.Stop };
        shade.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(shade);
        float scale = Math.Max(1f, MathF.Floor(Math.Min(GetViewportRect().Size.X / RetroScreen.Width, GetViewportRect().Size.Y / RetroScreen.Height)));
        window.Scale = new Vector2(scale, scale);
        window.Position = ((GetViewportRect().Size - window.Size * scale) / 2).Floor();
        window.MouseFilter = Control.MouseFilterEnum.Stop;
        // 85FE:02C9 draws BMP 10004 at (68, 24) of the screen.
        if (Session.Open("RESOURCE.GFF").TryRead("BMP ", GameOverPicture, out var picture) && Session.Assets.Frames(picture) is [{ Width: > 1 } frame, ..])
        {
            window.AddChild(new TextureRect { Texture = TextureFor(frame), Position = new Vector2(68, 24), MouseFilter = Control.MouseFilterEnum.Ignore });
        }
        layer.AddChild(window);
        AddChild(layer);
        void Leave() => GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, MainMenu.ScenePath);
        foreach (var (tag, _, control) in window.Items)
        {
            if (tag == "BUTN" && control is TextureButton b)
            {
                b.Pressed += Leave;
            }
        }
        shade.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true } or InputEventKey { Pressed: true })
            {
                Leave();
            }
        };
    }

    private const int GameOverWindow = 3004, GameOverPicture = 10004;

    /// <summary>
    /// A party member's step in a fight fires the move triggers of its new cell
    /// (<c>1A0A:0B0F</c> calls <c>trigger_on_move</c> for every step, fighting or
    /// not); the fight waits while the script talks to the player.
    /// </summary>
    private void MoveTriggers(Combatant c, Cell cell)
    {
        if (!c.PartyMember || c.Source is not { Kind: ObjectKind.Party } member || member.Index >= _party.Count || _checking)
        {
            return;
        }
        int i = member.Index;
        _lastCells[_party[i].Walker] = cell;
        if (Session.Scripts.OnMove(i, cell.X, cell.Y, i == _selected) is { } run)
        {
            ReportScript($"character {i + 1} reached ({cell.X},{cell.Y}) in a fight", run);
        }
    }

    // -- development and the headless check ---------------------------------------------------------------

    /// <summary>
    /// <c>--fight</c> (and the check): the nearest creature that fights is
    /// brought a few cells from the party and attacked.
    /// </summary>
    private bool StartTestFight() => BringFoeNear() is { } foe && StartCombat(foe, $"attack {Describe(foe)}");

    /// <summary>The nearest creature that fights, brought 3–8 cells from the party and made hostile; null if none.</summary>
    private WorldObject? BringFoeNear()
    {
        WorldObject? foe = _world.Objects.Where(o => o.Kind == ObjectKind.Creature && !o.Hidden && o.Vitals.Alive
                && o.Record is { Mind: not (CombatRecord.MindIdle or CombatRecord.MindFlees) })
            .OrderBy(o => o.Walker is null).ThenBy(o => Battle.Distance(o.Cell, _party[0].Walker.Cell)).FirstOrDefault();
        Cell leader = _party[0].Walker.Cell;
        Cell? near = foe is null ? null : Enumerable.Range(3, 6).SelectMany(r => new[] { new Cell(leader.X + r, leader.Y), new Cell(leader.X - r, leader.Y),
            new Cell(leader.X, leader.Y + r), new Cell(leader.X, leader.Y - r) }).FirstOrDefault(c => !_region.IsBlocked(c.X, c.Y)
            && _world.Party.All(p => p.Cell != c), new Cell(-1, -1));
        if (foe is null || near is not { X: >= 0 } spot)
        {
            return null;
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
        return foe;
    }

    // -- headless check ------------------------------------------------------------------------------

    /// <summary>
    /// <c>--check-gang</c>: the slave pens' gang (region 41: Merzol, <c>OJFF</c> 180,
    /// and the gladiators, 181) turned on the party as <c>GPL 142</c> @1002 does
    /// (REQUEST 18 to allegiance 2), fought to the end by the party itself: the
    /// fight ends once no enemy is near, and each that falls leaves a body with
    /// what it carried.
    /// </summary>
    private async void CheckGang()
    {
        _checking = true;
        _autoParty = true;
        var problems = new List<string>();
        int index = Session.Install!.RegionFiles.ToList().FindIndex(f => f.Equals("RGN29.GFF", StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            GD.Print("gang: no region 41");
            GetTree().Quit(1);
            return;
        }
        for (int i = 0; i < 10; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        ShowRegion(index);
        AnswerForThePlayer();
        for (int i = 0; i < 10; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            AnswerForThePlayer();
        }
        GD.Print($"gang: in region {_region.Number}");
        WorldObject? merzol = _world.First(-180);
        if (merzol is null)
        {
            GD.Print("gang: no Merzol");
            GetTree().Quit(1);
            return;
        }
        _world.Teleport(DialogueGplHost.WholeParty, _region.Number, merzol.Cell.X + 2, merzol.Cell.Y);
        // One gladiator has wandered out of sight (within sight range, behind a wall): he doesn't keep the fight going (612E:281F).
        Cell party = _world[0]!.Cell;
        WorldObject? away = _world.Objects.LastOrDefault(o => o.Id == -181);
        Cell? hidden = Enumerable.Range(8, 14).SelectMany(r => Enumerable.Range(-r, 2 * r + 1).SelectMany(d => new[]
            { new Cell(party.X + d, party.Y - r), new Cell(party.X + d, party.Y + r), new Cell(party.X - r, party.Y + d), new Cell(party.X + r, party.Y + d) }))
            .Where(c => c.X >= 0 && c.Y >= 0 && c.X < _region.Width && c.Y < _region.Height && !_region.IsBlocked(c.X, c.Y)
                && !GplGeometry.LineOfSight(_region, party.X, party.Y, c.X, c.Y))
            .Select(c => (Cell?)c).FirstOrDefault();
        if (away?.Walker is { } walker && hidden is { } spot)
        {
            walker.Teleport(spot, (_, _) => false);
            DrawCreature(away);
        }
        List<WorldObject> gang = [.. _world.Objects.Where(o => o.Id is -180 or -181)];
        GD.Print($"gang: {string.Join(", ", gang.Select(g => $"{g.Name} at {g.Cell.X},{g.Cell.Y} carrying {g.Items.Count}"))}; party at {_world[0]!.Cell.X},{_world[0]!.Cell.Y}");
        // "You're nothing and I'm going to kill you": GPL 142 @1002 turns the gang and sets them hunting; their line-of-sight script starts the fight.
        if (Session.Scripts.RunFor(new GplEntry(142, 0x1002), 0) is { } turned)
        {
            ReportScript("the gang turns", turned);
        }
        AnswerForThePlayer();
        Battle? battle = null;
        int fights = 0, frames = 0;
        while (frames++ < 60_000 && (battle is null || InCombat || frames < 600))
        {
            _Process(1.0 / 60);
            AnswerForThePlayer();
            if (_battle is { } b && b != battle)
            {
                battle = b;
                fights++;
                GD.Print($"gang: fight {fights} with {string.Join(", ", b.Combatants.Where(c => !c.PartyMember).Select(c => $"{c.Name} at {c.Cell.X},{c.Cell.Y}"))}");
            }
            if (battle is not null && battle.Round > 60)
            {
                break;
            }
            if (frames % 200 == 0)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
        }
        if (battle is null)
        {
            GD.Print($"gang: no fight started; hostile: {string.Join(", ", gang.Select(g => $"{g.Name} {g.Allegiance}"))}");
            GetTree().Quit(1);
            return;
        }

        string left = string.Join(", ", battle.Combatants.Where(c => !c.PartyMember).Select(c => $"{c.Name} {(c.Alive ? "alive" : "fell")} at {c.Cell.X},{c.Cell.Y}"));
        int bodies = _world.Objects.Count(o => o.Id == -GameWorld.DeadBody && !o.Hidden);
        int fell = battle.Combatants.Count(c => !c.PartyMember && c.Vitals.Status is >= Vitals.Down and < Vitals.Destroyed);
        GD.Print($"gang: {battle.Round} rounds, {(InCombat ? "still fighting" : "over")}; {left}; {bodies} bodies for {fell} fallen");
        if (InCombat)
        {
            problems.Add($"still fighting after {battle.Round} rounds");
        }
        if (bodies < fell)
        {
            problems.Add($"{fell - bodies} of the fallen left no body");
        }
        foreach (string p in problems)
        {
            GD.Print($"  PROBLEM: {p}");
        }
        GD.Print(problems.Count == 0 ? "gang: OK" : $"gang: {problems.Count} problems");
        GetTree().Quit(problems.Count == 0 ? 0 : 1);
    }

    /// <summary>The shipped opening bout, its cleanup, and the gate's next announcement.</summary>
    private void CheckArena()
    {
        _checking = true;
        var problems = new List<string>();
        bool called = false, announced = false;
        void Step()
        {
            _Process(1.0 / ScmdAnimator.TicksPerSecond);
            string text = string.Concat(Session.ScriptHost.Dialogue.Text.Select(t => t.Text));
            called |= text.Contains("Gladiators: Step forward");
            announced |= text.Contains("Citizens of Draj!");
            if (Session.ScriptHost.Dialogue.Prompt is GplContinuePrompt)
            {
                ReportScript("read announcement", Session.Scripts.Resume());
            }
            _dialogueWindow.Refresh();
        }
        for (int tick = 0; tick < 1000 && !InCombat; tick++)
        {
            Step();
        }
        if (_battle is not { } battle)
        {
            problems.Add("the opening bout did not start");
        }
        else
        {
            for (int tick = 0; tick < 300_000 && InCombat && battle.Round <= 100; tick++)
            {
                Step();
            }
            if (InCombat)
            {
                problems.Add($"the opening bout did not finish after {battle.Round} rounds");
            }
            GD.Print($"arena bout: {battle.Round} rounds; survivors: {string.Join(", ", battle.Combatants.Where(c => c.Alive).Select(c => c.Name))}");
        }
        for (int tick = 0; tick < 1000 && !called; tick++)
        {
            Step();
        }
        if (!called)
        {
            problems.Add("the party was not called into the arena");
        }
        if (_world.Objects.Any(o => o.Id is -296 or -171 or -269))
        {
            problems.Add("the opening combatants are still in the region");
        }
        if (!InCombat && !Session.Scripts.IsSuspended)
        {
            WalkTo(new Cell(30, 26), wholeParty: true);
            for (int tick = 0; tick < 10_000 && !announced; tick++)
            {
                Step();
            }
        }
        if (!announced)
        {
            problems.Add($"no next announcement; party at {string.Join(", ", _world.Party.Select(p => p.Cell))}; "
                + $"arena stage {Session.Scripts.Memory.Read(new GplVariable(GplVarType.GlobalNum, 3))}");
        }
        else
        {
            CheckReleasedMonsters(Step, problems);
            CheckDoorOpensInFight(Step, problems);
        }
        if (_scriptErrors > 0)
        {
            problems.Add($"{_scriptErrors} script errors");
        }
        foreach (string problem in problems)
        {
            GD.Print($"  PROBLEM: {problem}");
        }
        GD.Print(problems.Count == 0 ? "arena: OK" : $"arena: {problems.Count} problems");
        GetTree().Quit(problems.Count == 0 ? 0 : 1);
    }

    /// <summary>
    /// A door a script opens just before FIGHT (the slave pens' doors when Kurzak calls, the arena's side gates)
    /// is drawn opening in the fight, though the map was held before it (a speech: the view's time ran on, the
    /// game's clock didn't): its first step is taken on the map, the rest in the fight.
    /// </summary>
    private void CheckDoorOpensInFight(Action step, List<string> problems)
    {
        if (!InCombat || _world.First(-2057) is not { } gate || _animated.FirstOrDefault(a => a.Object == gate.Source) is not { } drawn)
        {
            problems.Add("no fight and side gate (2057) to open in it");
            return;
        }
        _running += 60;
        _world.SetDoor(gate, 0, force: true);
        Animate();
        for (int tick = 0; tick < 500 && InCombat && drawn.Animator.Animating; tick++)
        {
            step();
        }
        if (drawn.Animator.Animating)
        {
            problems.Add($"the side gate opened in the fight is still drawn at frame {drawn.Animator.Frame}, not open");
        }
        GD.Print($"side gate opened in the fight: frame {drawn.Animator.Frame}, {(drawn.Animator.Animating ? "still opening" : "open")}");
    }

    /// <summary>
    /// <c>--check-arena-release</c>: the gate script (GPL 2) as it stands once the opening bout is over
    /// and the gladiators are called (<c>gflag[9]</c>, <c>gflag[16]</c>, stage <c>gnum[3]</c> 1), the
    /// party south of the gate: the gate closes, the announcer speaks and the monsters come in.
    /// </summary>
    private void CheckArenaRelease()
    {
        _checking = true;
        var problems = new List<string>();
        GplMemory memory = Session.Scripts.Memory;
        memory.Assign(new GplVariable(GplVarType.GlobalFlag, 9), 1);
        memory.Assign(new GplVariable(GplVarType.GlobalFlag, 16), 1);
        memory.Assign(new GplVariable(GplVarType.GlobalNum, 3), 1);
        _world.Teleport(DialogueGplHost.WholeParty, _world.Region.Number, 30, 26);
        bool announced = false;
        void Step()
        {
            _Process(1.0 / ScmdAnimator.TicksPerSecond);
            announced |= string.Concat(Session.ScriptHost.Dialogue.Text.Select(t => t.Text)).Contains("Citizens of Draj!");
            if (Session.ScriptHost.Dialogue.Prompt is GplContinuePrompt)
            {
                ReportScript("read announcement", Session.Scripts.Resume());
            }
            _dialogueWindow.Refresh();
        }
        for (int tick = 0; tick < 10_000 && !announced; tick++)
        {
            Step();
        }
        if (!announced)
        {
            problems.Add($"no announcement; party at {string.Join(", ", _world.Party.Select(p => p.Cell))}");
        }
        else
        {
            CheckReleasedMonsters(Step, problems);
        }
        if (_scriptErrors > 0)
        {
            problems.Add($"{_scriptErrors} script errors");
        }
        foreach (string problem in problems)
        {
            GD.Print($"  PROBLEM: {problem}");
        }
        GD.Print(problems.Count == 0 ? "arena release: OK" : $"arena release: {problems.Count} problems");
        GetTree().Quit(problems.Count == 0 ? 0 : 1);
    }

    /// <summary>
    /// After "Citizens of Draj!" GPL 2 clones its monsters at one side of the arena (13, 32) or (51, 32),
    /// sets them hostile and HUNTs; they walk to the party and their line-of-sight script (GPL 74 @0059) FIGHTs.
    /// </summary>
    private void CheckReleasedMonsters(Action step, List<string> problems)
    {
        bool Hostile(WorldObject o) => o.Kind != ObjectKind.Party && o.Walker is not null && o.IsClone && o.Allegiance == 2;
        int tick = 0;
        for (; tick < 2000 && !_world.Objects.Any(Hostile); tick++)
        {
            step();
        }
        var monsters = _world.Objects.Where(Hostile).ToList();
        if (monsters.Count == 0)
        {
            problems.Add("no monsters were released");
            return;
        }
        Cell leader = _world.Party.First().Cell;
        var from = monsters.ToDictionary(m => m, m => m.Walker!.Cell);
        GD.Print($"released: {string.Join(", ", monsters.Select(m => $"{m.Name} at {m.Walker!.Cell}"))}; party at {leader}");
        for (tick = 0; tick < 20_000 && !InCombat; tick++)
        {
            step();
        }
        if (!InCombat)
        {
            problems.Add($"the monsters never attacked; now at {string.Join(", ", monsters.Select(m => m.Walker!.Cell))}");
            return;
        }
        GD.Print($"attacked after {tick} ticks; monsters at {string.Join(", ", monsters.Select(m => m.Walker!.Cell))}");
        if (!PartyFights)
        {
            problems.Add("the party does not take part in the fight");
        }
        static int Distance(Cell a, Cell b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        if (!monsters.Any(m => Distance(m.Walker!.Cell, leader) < Distance(from[m], leader)))
        {
            problems.Add("no monster walked towards the party");
        }
    }

    /// <summary>
    /// <c>--check-combat</c>: a new game; the nearest creature turns hostile and
    /// the party attacks it. On the first member's turn a Wand of Missiles is
    /// used from the inventory (USE) and aimed at an enemy; then the party
    /// fights by itself; the fight is played to its end (or 40 rounds) and summed up.
    /// </summary>
    private async void CheckCombat()
    {
        _checking = true;
        var problems = new List<string>();
        if (!StartTestFight() || _battle is not { } battle)
        {
            GD.Print("combat: no creature to fight in the start region");
            GetTree().Quit(1);
            return;
        }
        for (int frame = 0; frame < 20_000 && PlayerTurn is null && InCombat; frame++)
        {
            CombatProcess(1.0 / 60);
        }
        if (PlayerTurn is { Source: { } member } caster)
        {
            CheckWandInFight(caster, member, problems);
        }
        else
        {
            problems.Add("no member's turn came");
        }
        for (int frame = 0; frame < 20_000 && PlayerTurn is null && InCombat; frame++)
        {
            CombatProcess(1.0 / 60);
        }
        if (PlayerTurn is { } summoner)
        {
            CheckSummonInFight(summoner, problems);
        }
        for (int frame = 0; frame < 20_000 && PlayerTurn is null && InCombat; frame++)
        {
            CombatProcess(1.0 / 60);
        }
        if (PlayerTurn is { } priest)
        {
            CheckConjuredWeaponInFight(priest, problems);
        }
        foreach (Combatant c in battle.Combatants.Where(c => c.PartyMember))
        {
            c.ComputerControlled = true;
        }
        Dictionary<WorldObject, int> carried = battle.Combatants.Where(c => !c.PartyMember && c.Source is not null)
            .ToDictionary(c => c.Source!, c => c.Source!.Items.Count);
        // A thrown weapon (the thri-kreen's chatkcha) needs no ammunition (612E:1FCF): a click on a distant enemy throws it.
        foreach (Combatant thrower in battle.Combatants.Where(c => c.PartyMember && c.MissileWeapon is not null && !c.ShootsAmmunition))
        {
            Combatant? far = battle.Combatants.FirstOrDefault(e => thrower.IsEnemyOf(e) && Battle.Distance(thrower, e) > 1);
            GD.Print($"combat: {Who(thrower)} has a {_world.Items.NameOf(thrower.MissileWeapon!.Item!.Record)} to throw");
            if (!thrower.CanShoot || far is not null && !MissileClick(thrower, far))
            {
                problems.Add($"{Who(thrower)} can't throw its {_world.Items.NameOf(thrower.MissileWeapon!.Item!.Record)}");
            }
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
        CheckCombatLog(battle, problems);
        if (battle.Combatants.Any(c => !c.PartyMember && !c.Alive && c.Source is { } o && _world[o.Index] == o))
        {
            problems.Add("a creature that fell is still on the map");
        }
        int bodiesDue = battle.Combatants.Count(c => !c.PartyMember && c.Vitals.Status is >= Vitals.Down and < Vitals.Destroyed
            && c.Source is { } o && carried.GetValueOrDefault(o) > 0);
        int bodies = _world.Objects.Count(o => o.Id == -GameWorld.DeadBody && !o.Hidden);
        if (bodies < bodiesDue)
        {
            problems.Add($"{bodiesDue - bodies} creatures that fell carrying something left no body");
        }
        if (!_partyFell && battle.Combatants.Any(c => c.PartyMember && c.Vitals is not { Status: Vitals.Well, Hp: >= 1 } && c.Vitals.Status < Vitals.Dead))
        {
            problems.Add("a member who went down didn't get up");
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

    /// <summary>
    /// The combat log step of <c>--check-combat</c>: every attack, wound and
    /// save from the oldest the log still keeps (<see cref="CombatLog.Kept"/>: a long
    /// fight's first go past <see cref="CombatLog.History"/>) has its line (in short); the panel shows rows; its
    /// level chip, + and L work (the settings put back after).
    /// </summary>
    private void CheckCombatLog(Battle battle, List<string> problems)
    {
        List<LogLine> lines = _combatLog.Log.At(LogLevel.Short);
        int Count(Func<LogLine, bool> which) => lines.Count(which);
        CombatEvent? oldest = _combatLog.Log.Kept.FirstOrDefault();
        List<CombatEvent> kept = [.. battle.Log.SkipWhile(e => !ReferenceEquals(e, oldest))];
        void Expect(string what, int want, int got)
        {
            if (want != got)
            {
                problems.Add($"combat log: {got} {what} lines for {want}");
            }
        }
        Expect("attack", kept.OfType<AttackRolled>().Count(), Count(l => l.Kind is LogKind.Hit or LogKind.Miss && l.Text.Contains(" attacks ")));
        Expect("hit point", kept.OfType<Damaged>().Count(), Count(l => l.Kind == LogKind.Damage && l.Text.Contains(" HP (-")));
        Expect("save", kept.OfType<SaveRolled>().Count(), Count(l => l.Kind == LogKind.Save && l.Text.Contains("'s save vs ")));
        if (_combatLog.Log.At(LogLevel.Detail).Count(l => l.Text.StartsWith("THAC0 ")) != kept.OfType<AttackRolled>().Count())
        {
            problems.Add("combat log: an attack in detail without its terms");
        }

        var (open, level, size) = (Settings.CombatLogOpen, Settings.CombatLogLevel, Settings.CombatLogSize);
        _combatLog.Rebuild();
        if (_combatLog.ShownRows().Count == 0 && _combatLog.CurrentSize != CombatLogView.PanelSize.Collapsed)
        {
            problems.Add("combat log: the panel shows no rows");
        }
        LogLevel before = _combatLog.Level;
        if (!_combatLog.PressButton(new[] { "Least", "Short", "Detail" }[(int)before]) || _combatLog.Level == before)
        {
            problems.Add("combat log: the level chip doesn't change the level");
        }
        if (_combatLog.CurrentSize != CombatLogView.PanelSize.Expanded && (!_combatLog.PressButton("+") || _combatLog.CurrentSize == (CombatLogView.PanelSize)size))
        {
            problems.Add("combat log: + doesn't make it bigger");
        }
        bool shown = CombatLogView.Open;
        _combatLog.PressButton("L");
        if (CombatLogView.Open == shown)
        {
            problems.Add("combat log: its L button doesn't hide it");
        }
        foreach (bool down in new[] { true, false })
        {
            GetViewport().PushInput(new InputEventKey { Keycode = Key.L, PhysicalKeycode = Key.L, Pressed = down });
        }
        if (CombatLogView.Open != shown)
        {
            problems.Add("combat log: the L key doesn't show it again");
        }
        GD.Print($"combat log: {lines.Count} lines in short, {_combatLog.Log.At(LogLevel.Detail).Count} in detail");
        (Settings.CombatLogOpen, Settings.CombatLogLevel, Settings.CombatLogSize) = (open, level, size);
        Settings.Save();
    }

    /// <summary>
    /// The summoning step of <c>--check-combat</c>: Monster Summoning I (at level 5)
    /// aimed at an empty cell beside <paramref name="caster"/> brings a creature onto the map, on
    /// the party's side, with a sprite of its own (<c>80F5:2687</c>).
    /// </summary>
    private void CheckSummonInFight(Combatant caster, List<string> problems)
    {
        const int MonsterSummoning = 36;
        Battle battle = _battle!;
        int before = battle.Combatants.Count;
        Cell[] around = [new(0, -1), new(1, 0), new(0, 1), new(-1, 0), new(1, -1), new(1, 1), new(-1, 1), new(-1, -1)];
        if (around.Select(d => caster.Cell + d).Where(battle.Free).Cast<Cell?>().FirstOrDefault() is not { } empty)
        {
            GD.Print("combat: no empty cell beside the summoner");
            return;
        }
        if (!CastAt(caster, new CastChoice(MonsterSummoning, 5), empty))
        {
            problems.Add($"Monster Summoning I at the empty cell {empty} wasn't cast");
            return;
        }
        if (battle.Combatants.Count != before + 1 || battle.Combatants[^1] is not { Side: 1, Source: { Allegiance: 1 } summoned } creature
            || !_creatureNodes.ContainsKey(summoned))
        {
            problems.Add($"Monster Summoning I brought no creature on the party's side with a sprite ({battle.Combatants.Count - before} new)");
            return;
        }
        GD.Print($"combat: {Who(caster)} summons {Who(creature)} at {creature.Cell}");
    }

    /// <summary>
    /// The conjured weapon step of <c>--check-combat</c>: with its hand emptied, a member casts
    /// Spiritual Hammer (90) on itself; the hammer (<c>RDFF</c> 30026) is in its hand (slot 3), its
    /// weapon in the fight, and on the inventory's body.
    /// </summary>
    private void CheckConjuredWeaponInFight(Combatant caster, List<string> problems)
    {
        const int SpiritualHammer = 90, Hammer = 30026, Hand = 3;
        if (caster.Source is not { } member)
        {
            return;
        }
        foreach (Item held in member.Items.Where(i => i.Slot == Hand))
        {
            held.Slot = Equipment.Pack;
        }
        caster.Weapon = null;
        if (!CastAt(caster, new CastChoice(SpiritualHammer, 5), caster.Cell))
        {
            problems.Add("Spiritual Hammer wasn't cast");
            return;
        }
        Item? hammer = member.Items.FirstOrDefault(i => i.Rdff == Hammer);
        if (hammer is not { Slot: Hand } || caster.Weapon?.Item != hammer)
        {
            problems.Add($"Spiritual Hammer put no hammer in {Who(caster)}'s hand ({string.Join(", ", member.Items.Select(i => $"{i.Rdff}@{i.Slot}"))})");
            return;
        }
        _character.Open(_world, member.Index, CharacterScreen.Page.Items);
        bool shown = Enumerable.Range(0, 14).Any(n => _character.ItemAt(0, n) == hammer);
        _character.Close();
        if (!shown)
        {
            problems.Add("the conjured hammer isn't on the inventory's body");
            return;
        }
        GD.Print($"combat: {Who(caster)} casts Spiritual Hammer and holds the hammer");
    }

    /// <summary>
    /// The wand step of <c>--check-combat</c>: <paramref name="caster"/>
    /// (<paramref name="member"/> on the map) is given a Wand of Missiles (<c>RDFF</c>
    /// 31007: Magic Missile, 50 charges), right-clicks it in the inventory and
    /// presses USE: the screens close and Magic Missile waits for a target at level 6;
    /// cast at an enemy in range, it uses a charge and none of the member's own spells.
    /// </summary>
    private void CheckWandInFight(Combatant caster, WorldObject member, List<string> problems)
    {
        const int Wand = 31007, MagicMissile = 9, Pack = 1;
        if (_world.Give(1, -Wand, member.Index, GameWorld.None) != 1 || member.Items.LastOrDefault(i => i.Rdff == Wand) is not { } wand)
        {
            problems.Add("no wand to give");
            return;
        }
        int charges = wand.Charges, spells = caster.Spells.Count;
        _character.Open(_world, member.Index, CharacterScreen.Page.Items);
        int slot = Enumerable.Range(0, 12).FirstOrDefault(n => _character.ItemAt(Pack, n) == wand, -1);
        _character.Click(Pack, slot, button: MouseButton.Right);
        if (!_character.ItemWindow.UseOffered)
        {
            problems.Add("the wand's item window has no USE");
            _character.Close();
            return;
        }
        _character.ItemWindow.PressUse();
        if (_character.IsOpen || _castChoice is not { Spell: MagicMissile, Level: ItemMagic.CasterLevel } choice || choice.Item != wand)
        {
            problems.Add($"USE on the wand didn't aim its Magic Missile ({_castChoice}; the screen {(_character.IsOpen ? "is still open: " + _character.Status : "closed")})");
            return;
        }
        Combatant? target = _battle!.Combatants.FirstOrDefault(e => caster.IsEnemyOf(e) && e.Alive
            && _battle.Aim(caster, _battle.Spells.Effect(MagicMissile)!, ItemMagic.CasterLevel, e.Cell) == SpellAim.Ok);
        if (target is null)
        {
            GD.Print("combat: the wand is aimed, but no enemy is in range to cast at");
            _castChoice = null;
            return;
        }
        ClickCell(target.Cell);
        if (wand.Charges != charges - 1 || caster.Spells.Count != spells || !_battle.Log.OfType<Cast>().Any(c => c.Actor == caster && c.Effect == MagicMissile))
        {
            problems.Add($"the wand's Magic Missile at {Who(target)}: {wand.Charges} of {charges} charges left, {caster.Spells.Count} of {spells} spells");
            return;
        }
        GD.Print($"combat: {Who(caster)} casts Magic Missile from the Wand of Missiles at {Who(target)} ({wand.Charges} charges left)");
    }
}
