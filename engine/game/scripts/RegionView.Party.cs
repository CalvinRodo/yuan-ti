using DarkSun.Data.Regions;
using DarkSun.Game.Animation;
using DarkSun.Game.Combat;
using DarkSun.Game.Movement;
using DarkSun.Game.Saves;
using DarkSun.Game.World;
using Godot;

namespace DarkSun.Engine;

public partial class RegionView
{
    private void CheckPartyMovement()
    {
        var problems = new List<string>();
        void Expect(bool ok, string what)
        {
            if (!ok)
            {
                problems.Add(what);
            }
        }
        void Step(int ticks)
        {
            for (int tick = 0; tick < ticks; tick++)
            {
                _Process(1.0 / ScmdAnimator.TicksPerSecond);
            }
        }
        Session.Scripts.Abandon();
        Session.ScriptHost.Dialogue.Clear();
        Session.ScriptHost.WatchedFight = null;
        _watchedFightPending = false;
        _whenFree.Clear();
        Session.Scripts.Triggers.Reset();
        Session.Scripts.Creatures.Clear();
        _world.PendingScripts.Clear();
        _dialogueWindow.Refresh();
        _world.Teleport(GameWorld.WholeParty, _region.Number, 30, 30);
        _gameMenu.Open();
        _gameMenu.Press(10313);
        Expect(!_gameMenu.IsOpen, "collapse closes the menu");
        Expect(_party.Skip(1).Any(p => p.Walker.IsWalking), "followers walk toward the leader before disappearing");
        Step(4000);
        Expect(_world.Party.Count(p => p.Collapsed) == _party.Count - 1, "all followers enter the leader");
        Expect(_party.Count(p => p.Node.Visible) == 1, "only the leader sprite remains");
        Expect(!_world.Occupancy.Cells.Any(p => p.Value is >= 0 and < PartySize && p.Value != _selected),
            "collapsed followers no longer block the map");

        // The keys bound to walking (by default W, A, S, D; the player's own if changed).
        foreach (var (action, dx, dy) in new[]
        {
            (Controls.MoveRight, 1, 0), (Controls.MoveDown, 0, 1), (Controls.MoveLeft, -1, 0), (Controls.MoveUp, 0, -1),
        })
        {
            Key key = InputMap.ActionGetEvents(action).OfType<InputEventKey>().Select(k => k.PhysicalKeycode).FirstOrDefault();
            Expect(key != Key.None, $"{action} has a key");
            Cell before = Selected!.Walker.Cell;
            Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Pressed = true });
            Input.FlushBufferedEvents();
            Step(1);
            Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Pressed = false });
            Input.FlushBufferedEvents();
            Step(300);
            Expect(Selected.Walker.Cell == new Cell(before.X + dx, before.Y + dy), $"{key} moves one cell, not the camera");
            Expect(_world.Party.All(p => p.Cell == Selected.Walker.Cell), $"{key} carries every follower");
            Expect(_party.Count(p => p.Node.Visible) == 1, $"{key} leaves followers invisible");
        }

        Cell paused = Selected!.Walker.Cell;
        _gameMenu.Open();
        Input.ActionPress(Controls.MoveRight);
        Step(100);
        Input.ActionRelease(Controls.MoveRight);
        Expect(Selected.Walker.Cell == paused, "arrows do not move the party behind the menu");
        _gameMenu.Press(10308);
        Session.ScriptHost.Dialogue.Append("Movement check", 98);
        _dialogueWindow.Refresh();
        Input.ActionPress(Controls.MoveRight);
        Step(100);
        Input.ActionRelease(Controls.MoveRight);
        Expect(Selected.Walker.Cell == paused, "arrows do not move the party behind dialogue");
        Session.ScriptHost.Dialogue.Clear();
        _dialogueWindow.Refresh();

        SaveGame save = SaveGame.Capture("Party movement check", Session.Scripts, Session.ScriptHost.EngineVariables,
            _world, Session.Regions, _party.Select(m => new PartyMemberSave(m.Sprite.Ojff, m.Walker.Cell.X,
                m.Walker.Cell.Y, m.Walker.Facing)), _selected, [], Session.ScriptHost.PartyMembers);
        Expect(LoadSaved(save), "the collapsed game loads");
        Expect(_party.Count(p => p.Node.Visible) == 1 && _world.Party.All(p => p.Cell == Selected!.Walker.Cell),
            "loading restores the single visible leader and all followers' positions");
        WalkTo(new Cell(30, 34), wholeParty: true);
        Expect(_world.CollapsedLeader is null && _party.All(p => p.Node.Visible), "whole-party movement spreads everyone out");
        Expect(_world.Party.Select(p => p.Cell).Distinct().Count() == _party.Count, "spread-out members occupy different cells");
        Step(4000);
        CheckOriginalMovement();
        CheckMessages();
        CheckBoxSelect();

        // The original's message box (5F6D:05DE): a walk with no way there, a verb on something out of sight.
        void CheckMessages()
        {
            Cell from = Selected!.Walker.Cell;
            var reached = new HashSet<Cell> { from };
            var queue = new Queue<Cell>([from]);
            while (queue.Count > 0)
            {
                Cell c = queue.Dequeue();
                foreach (Cell n in new[] { new Cell(c.X + 1, c.Y), new Cell(c.X - 1, c.Y), new Cell(c.X, c.Y + 1), new Cell(c.X, c.Y - 1) })
                {
                    if (n.X >= 0 && n.Y >= 0 && n.X < _region.Width && n.Y < _region.Height && !_region.IsBlocked(n.X, n.Y) && reached.Add(n))
                    {
                        queue.Enqueue(n);
                    }
                }
            }
            Vector2 Middle(Cell c) => new((c.X + 0.5f) * Region.TileSize, (c.Y + 0.5f) * Region.TileSize);
            Cell? cutOff = Enumerable.Range(0, _region.Width * _region.Height).Select(i => new Cell(i % _region.Width, i / _region.Width))
                .Where(c => !_region.IsBlocked(c.X, c.Y) && !reached.Contains(c) && ObjectAt(Middle(c)) is null)
                .Select(c => (Cell?)c).FirstOrDefault(c => !Selected.Walker.CanReach(c!.Value, _region.IsBlocked));
            if (cutOff is { } far)
            {
                ClickToWalk(Middle(far), wholeParty: false);
                Expect(_message.Shown == NoPath, $"a click on {far}, cut off from {from}, says NO PATH FROM HERE ({_message.Shown})");
                Expect(!Selected.Walker.IsWalking, "and nobody walks");
                Clock();
                Input.ParseInputEvent(new InputEventKey { Keycode = Key.Space, PhysicalKeycode = Key.Space, Pressed = true });
                Input.FlushBufferedEvents();
                Step(2);
                Expect(!_message.IsOpen, "a key puts the message away");
            }
            else
            {
                problems.Add($"messages: no free cell cut off from {from} in region {_region.Number}");
            }
            if (_world.Objects.FirstOrDefault(o => o.Kind == ObjectKind.Thing && !o.Hidden && !o.IsDoor && !Sees(o)) is { } hidden)
            {
                StartVerb(hidden, Verb.Use, null);
                Expect(_message.Shown == NoLineOfSight && _pendingUse is null, $"USE on {Describe(hidden)}, out of sight, says NO LINE OF SIGHT ({_message.Shown})");
                _message.Close();
            }
            else
            {
                problems.Add($"messages: nothing out of sight in region {_region.Number}");
            }
            void Clock()
            {
                int clock = _world.Clock;
                Step(4 * ScmdAnimator.TicksPerSecond);
                Expect(_world.Clock == clock, "the map waits while the message shows");
            }
        }

        // As in the original: COLLAPSE PARTY again spreads the party out; the leader walks
        // (a click or the arrows) and the others follow, unless Settings.FollowLeader is off.
        void CheckOriginalMovement()
        {
            bool follow = Settings.FollowLeader;
            _gameMenu.ResetCursor();
            _gameMenu.Open();
            Expect(_gameMenu.CollapseShows == "party", $"COLLAPSE PARTY shows the party while it is spread out ({_gameMenu.CollapseShows})");
            _gameMenu.Press(10313);
            Step(4000);
            Expect(_world.CollapsedLeader == _selected, "collapse gathers the party");
            _gameMenu.Open();
            Expect(_gameMenu.CollapseShows == "one", $"and one figure while it is gathered ({_gameMenu.CollapseShows})");
            _gameMenu.Press(10313);
            Expect(_world.CollapsedLeader is null && _party.All(p => p.Node.Visible), "COLLAPSE PARTY again spreads the party out");
            Expect(_world.Party.Select(p => p.Cell).Distinct().Count() == _party.Count, "spread out on different cells");

            int Gap(PartyMember p) => Math.Max(Math.Abs(p.Walker.Cell.X - Selected!.Walker.Cell.X), Math.Abs(p.Walker.Cell.Y - Selected.Walker.Cell.Y));
            Cell? Away(int by) => new[] { (by, 0), (-by, 0), (0, by), (0, -by) }
                .Select(d => new Cell(Selected!.Walker.Cell.X + d.Item1, Selected.Walker.Cell.Y + d.Item2))
                .Where(c => c.X > 0 && c.Y > 0 && c.X < _region.Width && c.Y < _region.Height && Selected!.Walker.CanStand(c, _region.IsBlocked))
                .Select(c => (Cell?)c).FirstOrDefault();

            Settings.FollowLeader = true;
            if (Away(8) is not { } goal)
            {
                problems.Add("follow: no open cell 8 away from the leader");
                return;
            }
            WalkTo(goal, wholeParty: false);
            Step(4000);
            Expect(Selected!.Walker.Cell == goal, $"follow: the leader walks to the click ({Selected.Walker.Cell}, not {goal})");
            Expect(_party.All(p => Gap(p) <= 2), $"follow: the others follow the leader ({string.Join(", ", _party.Select(Gap))} cells off)");

            Cell before = Selected.Walker.Cell;
            Input.ActionPress(Controls.MoveRight);
            Step(200);
            Input.ActionRelease(Controls.MoveRight);
            Step(1500);
            Expect(Selected.Walker.Cell != before, "follow: the arrows walk the leader");
            Expect(_party.All(p => Gap(p) <= 2), $"follow: the others follow the arrows ({string.Join(", ", _party.Select(Gap))} cells off)");

            Settings.FollowLeader = false;
            List<Cell> stay = [.. _party.Where(p => p != Selected).Select(p => p.Walker.Cell)];
            if (Away(6) is { } alone)
            {
                WalkTo(alone, wholeParty: false);
                Step(3000);
                Expect(_party.Where(p => p != Selected).Select(p => p.Walker.Cell).SequenceEqual(stay), "follow off: only the leader walks");
            }
            Settings.FollowLeader = follow;
        }

        // Box selection (Settings.BoxSelect): a drag picks the figures it touches, a click walks only them.
        void CheckBoxSelect()
        {
            bool was = Settings.BoxSelect;
            _gameMenu.ResetCursor();
            // Apart, four cells between each, so a box can take some and not others.
            Cell start = Selected!.Walker.Cell;
            List<Cell> spots = [.. Enumerable.Range(-12, 25).Where(dx => dx % 4 == 0).Select(dx => new Cell(start.X + dx, start.Y))
                .Where(c => c.X > 0 && c.X < _region.Width && !_region.IsBlocked(c.X, c.Y))];
            for (int i = 0; i < _party.Count && i < spots.Count; i++)
            {
                _party[i].Walker.Teleport(spots[i], (_, _) => false);
                DrawMember(_party[i]);
            }
            Step(10);
            Rect2 FigureOf(PartyMember p) => p.Node.GetGlobalTransform() * p.Node.GetRect();
            Vector2 Screen(Vector2 map) => GetCanvasTransform() * map;
            void Drag(Rect2 box)
            {
                Vector2 from = Screen(box.Position), to = Screen(box.End);
                _UnhandledInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = from });
                _UnhandledInput(new InputEventMouseMotion { ButtonMask = MouseButtonMask.Left, Position = to });
                _UnhandledInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = to });
            }
            // Two members whose figures a box can cover without touching anyone else's, the second not the leader.
            (PartyMember A, PartyMember B, Rect2 Both)? pair = _party.SelectMany(a => _party.Where(b => b != a && _party.IndexOf(b) != _selected)
                    .Select(b => (A: a, B: b, Both: FigureOf(a).Merge(FigureOf(b)))))
                .Where(t => _party.Where(o => o != t.A && o != t.B).All(o => !FigureOf(o).Intersects(t.Both, includeBorders: true)))
                .Select(t => ((PartyMember, PartyMember, Rect2)?)t).FirstOrDefault();
            if (pair is not var (a, b, both))
            {
                problems.Add("box select: no two members stand apart from the rest");
                return;
            }
            Settings.BoxSelect = true;
            Drag(FigureOf(b).Grow(-2));
            Expect(_selected == _party.IndexOf(b) && Picked.SequenceEqual([b]), "box select: a box round one member makes it leader and picks it");
            Drag(both.Grow(1));
            Expect(Picked.Count == 2 && Picked.Contains(a) && Picked.Contains(b), $"box select: a box round two picks both ({Picked.Count})");
            List<PartyMember> others = [.. _party.Where(p => p != a && p != b)];
            List<Cell> stay = [.. others.Select(o => o.Walker.Cell)];
            Cell goal = new[] { (6, 0), (-6, 0), (0, 6), (0, -6) }.Select(d => new Cell(b.Walker.Cell.X + d.Item1, b.Walker.Cell.Y + d.Item2))
                .FirstOrDefault(c => c.X > 0 && c.Y > 0 && c.X < _region.Width && c.Y < _region.Height && !_region.IsBlocked(c.X, c.Y), b.Walker.Cell);
            Vector2 click = Screen(new Vector2((goal.X + 0.5f) * Region.TileSize, (goal.Y + 0.5f) * Region.TileSize));
            _UnhandledInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = click });
            _UnhandledInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = click });
            Expect(a.Walker.IsWalking && b.Walker.IsWalking, "box select: a click walks the picked members");
            Step(4000);
            Expect(others.Select(o => o.Walker.Cell).SequenceEqual(stay), "box select: the others stay where they were");
            Expect(Battle.Distance(a.Walker.Cell, goal) <= 2 && Battle.Distance(b.Walker.Cell, goal) <= 2,
                $"box select: the picked reach the click ({a.Walker.Cell}, {b.Walker.Cell} for {goal})");
            Drag(new Rect2(Selected!.Node.GlobalPosition + new Vector2(200, 200), new Vector2(10, 10)));
            Expect(_picked.Count == 0, "box select: a box round no one drops the picks");
            Settings.BoxSelect = false;
            Drag(FigureOf(a).Merge(FigureOf(b)));
            Expect(_picked.Count == 0, "box select: off, a drag picks no one");
            Settings.BoxSelect = was;
        }

        // Out of a fight a right-click on the map turns the pointer too (1587:01C7), and a held item joins the turn.
        var turn = new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true };
        _gameMenu.ResetCursor();
        var seen = new List<GameMenu.Cursor>();
        for (int i = 0; i < 3; i++)
        {
            _UnhandledInput(turn);
            seen.Add(_cursor);
        }
        Expect(seen.SequenceEqual([GameMenu.Cursor.Attack, GameMenu.Cursor.Look, GameMenu.Cursor.Move]),
            $"right-clicks on the map: ATTACK, LOOK, MOVE ({string.Join(", ", seen)})");
        foreach (GameMenu.Cursor held in new[] { GameMenu.Cursor.Attack, GameMenu.Cursor.Look })
        {
            _gameMenu.SetCursor(held);
            int clock = _world.Clock;
            long ticks = Now;
            Step(4 * ScmdAnimator.TicksPerSecond);
            Expect(_world.Clock == clock && Now == ticks, $"the {held} pointer holds the map and its clock still");
        }
        _gameMenu.ResetCursor();
        int moving = _world.Clock;
        Step(4 * ScmdAnimator.TicksPerSecond);
        Expect(_world.Clock > moving, "the MOVE pointer runs the clock again");
        if (_world.Objects.FirstOrDefault(o => o.Kind == ObjectKind.Thing && !o.Hidden && o.Source is not null) is { } thing)
        {
            SetUseWith(thing);
            Expect(_cursor == GameMenu.Cursor.Item && PointerIcon(Vector2.Zero) == Pointers.Held, "Use with… holds the item as the pointer");
            _UnhandledInput(turn);
            Expect(_cursor == GameMenu.Cursor.Move && _useWith == thing, "a right-click turns to MOVE and keeps the item");
            for (int i = 0; i < 3; i++)
            {
                _UnhandledInput(turn);
            }
            Expect(_cursor == GameMenu.Cursor.Item, "and the turn comes back to it after LOOK");
            SetUseWith(null);
            Expect(_cursor == GameMenu.Cursor.Move, "putting it away goes back to MOVE");
        }
        else
        {
            problems.Add("no item on the map for the held-item pointer check");
        }

        _world.CollapseParty(_selected);
        Step(4000);
        Expect(StartTestFight(), "a fight can start with the party collapsed");
        Expect(_world.CollapsedLeader is null && _party.All(p => p.Node.Visible), "combat reveals every party member");
        if (_battle is { } battle)
        {
            foreach (Combatant member in battle.Combatants.Where(c => c.PartyMember))
            {
                member.ComputerControlled = false;
            }
            for (int tick = 0; tick < 10_000 && InCombat && PlayerTurn is null; tick++)
            {
                Step(1);
            }
            if (PlayerTurn is { } player)
            {
                var step = new[]
                {
                    (Controls.MoveRight, new Cell(player.Cell.X + 1, player.Cell.Y)),
                    (Controls.MoveLeft, new Cell(player.Cell.X - 1, player.Cell.Y)),
                    (Controls.MoveDown, new Cell(player.Cell.X, player.Cell.Y + 1)),
                    (Controls.MoveUp, new Cell(player.Cell.X, player.Cell.Y - 1)),
                }.FirstOrDefault(s => battle.Free(s.Item2), ("", player.Cell));
                Expect(step.Item1 != "", "the active combatant has a free adjacent cell");
                if (step.Item1 != "")
                {
                    int points = player.MovementPoints;
                    Input.ActionPress(step.Item1);
                    CombatInput(new InputEventAction { Action = step.Item1, Pressed = true });
                    Input.ActionRelease(step.Item1);
                    Expect(player.Cell == step.Item2 && player.MovementPoints < points,
                        "combat arrows move the active member and spend movement points");
                }
            }
            else
            {
                problems.Add("no player turn for the combat movement check");
            }
            CheckRangedCast(battle);
        }

        // CAST, then a click on an enemy in range and sight: the spell is cast from where the member stands.
        void CheckRangedCast(Battle battle)
        {
            const int Spell = 9; // one creature, any creature, 210 cells
            for (int tick = 0; tick < 10_000 && InCombat && PlayerTurn is null; tick++)
            {
                Step(1);
            }
            SpellEffect e = battle.Spells.Effect(Spell)!;
            if (PlayerTurn is not { } player)
            {
                problems.Add("no player turn for the cast check");
                return;
            }
            if (battle.Combatants.FirstOrDefault(t => player.IsEnemyOf(t) && t.Alive && battle.Aim(player, e, player.Level, t.Cell) == SpellAim.Ok) is not { } target)
            {
                problems.Add("no enemy in range and sight for the cast check");
                return;
            }
            Vector2 Middle(Cell cell) => new((cell.X + 0.5f) * Region.TileSize, (cell.Y + 0.5f) * Region.TileSize);
            var right = new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true };

            // The pointers (1587:29FA) and the right-click's cycle (1587:01C7).
            Expect(PointerIcon(Middle(target.Cell)) is Pointers.Attack or Pointers.Ranged, "MOVE over an enemy: the sword or the missile");
            // Anywhere on its picture aims at it, not only its own cell (the beetle's body is drawn above its feet).
            if (SpriteOf(target) is { Texture: not null } picture && SpriteRect(picture) is var shape
                && Enumerable.Range(0, (int)shape.Size.Y).SelectMany(y => Enumerable.Range(0, (int)shape.Size.X).Select(x => shape.Position + new Vector2(x + 0.5f, y + 0.5f)))
                    .FirstOrDefault(p => DrawnAt(picture, p)) is var head && head != default
                && new Cell((int)Math.Floor(head.X / Region.TileSize), (int)Math.Floor(head.Y / Region.TileSize)) is var above
                && above != target.Cell && battle.At(above) is null)
            {
                Expect(AimCell(head) == target.Cell && PointerIcon(head) is Pointers.Attack or Pointers.Ranged,
                    $"the pointer on {Who(target)}'s head ({above}, it stands on {target.Cell}) aims at it");
            }
            else
            {
                problems.Add($"{Who(target)}'s picture doesn't reach past its cell; the aim-by-picture check didn't run");
            }
            _clock = 0;
            UpdateAim(target.Cell, PointerIcon(Middle(target.Cell)));
            Expect(SpriteOf(target)?.SelfModulate == Colors.Black, "the enemy under the sword or the missile flashes");
            UpdateAim(player.Cell, PointerIcon(Middle(player.Cell)));
            Expect(SpriteOf(target)?.SelfModulate == Colors.White, "and stops when the pointer leaves it");
            CombatInput(right);
            Expect(_cursor == GameMenu.Cursor.Attack && PointerIcon(Middle(player.Cell)) == Pointers.CantAttack,
                "a right-click turns to ATTACK, crossed out over oneself");
            CombatInput(right);
            Expect(_cursor == GameMenu.Cursor.Look && PointerIcon(Middle(target.Cell)) == Pointers.Look, "another to LOOK, the eye over the enemy");
            CombatInput(right);
            Expect(_cursor == GameMenu.Cursor.Move, "and another back to MOVE");

            // An area spell: its outline and everyone in it flashing; a right-click puts it away.
            player.Spells.Add(8);
            ChooseCast(8);
            _clock = 0;
            UpdateAim(target.Cell, PointerIcon(Middle(target.Cell)));
            Expect(_aimOutline?.GetChildCount() > 0 && _aimTargets.Contains(target) && SpriteOf(target)?.SelfModulate == Colors.Black,
                $"an aimed area is outlined ({_aimOutline?.GetChildCount()} dots) and its targets flash black");
            CombatInput(right);
            UpdateAim(player.Cell, PointerIcon(Middle(player.Cell)));
            Expect(_castChoice is null && _cursor == GameMenu.Cursor.Move && _aimOutline?.GetChildCount() == 0 && SpriteOf(target)?.SelfModulate == Colors.White,
                "a right-click puts the spell away and the flashing stops");
            player.Spells.Remove(8);

            player.Spells.Add(Spell);
            int ready = player.Spells.Count(n => n == Spell); // a mage may have it already
            Cell from = player.Cell;
            ChooseCast(Spell);
            UpdateAim(target.Cell, PointerIcon(Middle(target.Cell)));
            Expect(PointerIcon(Middle(target.Cell)) == DarkSun.Game.Magic.Spellcasting.Get(Spell)!.Icon
                && PointerIcon(Middle(new Cell(player.Cell.X + 40, player.Cell.Y))) == Pointers.CantCast
                && _aimTargets.SequenceEqual([target]), $"one target: the spell's icon over it ({PointerIcon(Middle(target.Cell))}), 6009 elsewhere ({PointerIcon(Middle(new Cell(player.Cell.X + 40, player.Cell.Y)))}), and it alone flashes ({string.Join(", ", _aimTargets.Select(t => t.Name))})");
            ClickCell(target.Cell);
            Expect(player.Spells.Count(n => n == Spell) == ready - 1 && player.Cell == from && battle.Current != player,
                $"a ranged spell clicked on an enemy {Battle.Distance(player, target)} cells away is cast from where the member stands");
        }

        foreach (string problem in problems)
        {
            GD.Print($"  PROBLEM: {problem}");
        }
        GD.Print(problems.Count == 0 ? "party movement: OK" : $"party movement: {problems.Count} problems");
        GetTree().Quit(problems.Count == 0 ? 0 : 1);
    }
}
