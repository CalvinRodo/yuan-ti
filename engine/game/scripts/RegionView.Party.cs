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

        foreach (var (action, key, dx, dy) in new[]
        {
            (Controls.MoveRight, Key.Right, 1, 0), (Controls.MoveDown, Key.Down, 0, 1),
            (Controls.MoveLeft, Key.Left, -1, 0), (Controls.MoveUp, Key.Up, 0, -1),
        })
        {
            Expect(InputMap.ActionHasEvent(action, new InputEventKey { PhysicalKeycode = key }), $"{key} is bound to movement");
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
            const int Spell = 32; // one creature, 15 cells
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
            player.Spells.Add(Spell);
            Cell from = player.Cell;
            ChooseCast(Spell);
            ClickCell(target.Cell);
            Expect(!player.Spells.Contains(Spell) && player.Cell == from && battle.Current != player,
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
