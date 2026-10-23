using DarkSun.Game.Combat;
using DarkSun.Game.Movement;
using DarkSun.Game.World;
using Godot;

namespace DarkSun.Engine;

/// <summary>The map's floor effects (<see cref="FloorEffects"/>): shadows, dust and the enemies' rings.</summary>
public partial class RegionView
{
    /// <summary>
    /// The floor's shadows and dust follow the living figures as drawn this
    /// frame, and in a fight the enemies' rings (<see cref="FloorEffects"/>).
    /// </summary>
    private void UpdateFloor(double delta)
    {
        (_floor.Shadows, _floor.Dust) = (Settings.Shadows, Settings.Dust);
        _floor.Follow(delta, FloorFigures());
        _floor.ShowRings(EnemyRings());
    }

    /// <summary>The sprites of the figures shown and standing: the party's, the creatures' (walking or not) and the clones'.</summary>
    private HashSet<Sprite2D> FloorFigures()
    {
        var figures = new HashSet<Sprite2D>();
        foreach (PartyMember member in _party.Where(m => m.Node.Visible && Living(_world[m.Walker.Id])))
        {
            figures.Add(member.Node);
        }
        foreach (var (_, node) in _creatureNodes.Concat(_cloneSprites).Where(p => p.Value.Visible && Living(p.Key)))
        {
            figures.Add(node);
        }
        foreach (var (obj, sprite) in _objectSprites)
        {
            if (sprite.Visible && _worldObjects.TryGetValue(obj, out WorldObject? o) && o.Kind == ObjectKind.Creature && Living(o))
            {
                figures.Add(sprite);
            }
        }
        return figures;
    }

    /// <summary>Shown and standing: in a fight as the battle has it, else by its hit points.</summary>
    private bool Living(WorldObject? o) =>
        o is { Hidden: false, Collapsed: false }
        && (_battle?.Combatants.FirstOrDefault(c => c.Source == o) is { } c ? c.Alive : o.Vitals.Alive);

    /// <summary>
    /// <c>--check-floor</c>: in a new game every living figure casts a shadow; in
    /// the arena the first member walks east across the sand, raising dust, which is
    /// gone a second after they stop; then in a fight Tab chooses the nearest
    /// enemy (its ring drawn) and Enter attacks it. Played in real frames: the
    /// dust's puffs age with the frames' time.
    /// </summary>
    private async void CheckFloor()
    {
        _checking = true;
        var problems = new List<string>();
        async Task Frames(int count)
        {
            for (int i = 0; i < count; i++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
        }
        async Task Seconds(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
        void DropScripts()
        {
            // The announcer's opening, or a move trigger's script, would hold the fight below: put away, as the movement check does.
            Session.Scripts.Abandon();
            Session.ScriptHost.Dialogue.Clear();
            Session.ScriptHost.WatchedFight = null;
            _watchedFightPending = false;
            _whenFree.Clear();
            Session.Scripts.Triggers.Reset();
            Session.Scripts.Creatures.Clear();
            _world.PendingScripts.Clear();
            _dialogueWindow.Refresh();
        }
        DropScripts();
        await Frames(3);

        int figures = FloorFigures().Count;
        if (!Settings.Shadows)
        {
            GD.Print("floor: shadows are off in the options; not checked");
        }
        else if (_floor.ShadowCount != figures || figures < _party.Count)
        {
            problems.Add($"{_floor.ShadowCount} shadows for {figures} living figures");
        }

        Walker walker = _party[0].Walker;
        // Onto open sand in the arena, south of the starting pen, with room to walk east.
        Cell sand = Enumerable.Range(14, 10).SelectMany(y => Enumerable.Range(18, 12).Select(x => new Cell(x, y)))
            .FirstOrDefault(c => Enumerable.Range(0, 11).All(dx => walker.CanStand(new Cell(c.X + dx, c.Y), _region.IsBlocked)), walker.Cell);
        walker.Teleport(sand, _region.IsBlocked);
        _follow = true;
        await Frames(10);
        // East across the sand (dust is raised behind the feet: a walk south would hide it behind the walker).
        List<Cell> path = [.. Enumerable.Range(1, 10).Select(dx => new Cell(walker.Cell.X + dx, walker.Cell.Y)).TakeWhile(c => !_region.IsBlocked(c.X, c.Y))];
        walker.Follow(path);
        int most = 0;
        ulong started = Time.GetTicksMsec();
        int frames = 0;
        for (int frame = 0; frame < 600 && (walker.IsWalking || frame < 10); frame++)
        {
            await Frames(1);
            most = Math.Max(most, _floor.PuffCount);
            frames++;
        }
        double walkSeconds = (Time.GetTicksMsec() - started) / 1000.0;
        await Seconds(1.2);
        if (!Settings.Dust)
        {
            GD.Print("floor: dust is off in the options; not checked");
        }
        else if (most == 0)
        {
            problems.Add($"no dust walking {path.Count} cells to {walker.Cell}");
        }
        else if (_floor.PuffCount > 0)
        {
            problems.Add($"{_floor.PuffCount} puffs of dust still there a second after the walk");
        }
        GD.Print($"floor: {figures} figures cast shadows; walking {path.Count} cells raised up to {most} puffs of dust ({walkSeconds:F1} s, {frames} frames)");

        DropScripts();
        CheckChooseEnemy(problems);
        if (_chosenEnemy is { } chosen && PlayerTurn is { } attacker)
        {
            int before = _battle!.Log.Count(e => e is AttackRolled a && a.Actor == attacker || e is Moved m && m.Actor == attacker);
            CombatInput(new InputEventKey { PhysicalKeycode = Key.Enter, Keycode = Key.Enter, Pressed = true });
            if (_battle?.Log.Count(e => e is AttackRolled a && a.Actor == attacker && a.Target == chosen || e is Moved m && m.Actor == attacker) <= before)
            {
                problems.Add($"Enter didn't attack {Who(chosen)}");
            }
        }

        foreach (string p in problems)
        {
            GD.Print($"  PROBLEM: {p}");
        }
        GD.Print(problems.Count == 0 ? "floor: OK" : $"floor: {problems.Count} problems");
        GetTree().Quit(problems.Count == 0 ? 0 : 1);
    }

    /// <summary>The Tab step of <c>--check-floor</c>: on a member's turn Tab chooses the nearest enemy and its ring is drawn.</summary>
    private void CheckChooseEnemy(List<string> problems)
    {
        if (!Settings.ChooseEnemyKeys)
        {
            GD.Print("floor: Tab and Enter are off in the options; not checked");
            return;
        }
        if (!StartTestFight())
        {
            problems.Add("no creature to fight in the start region");
            return;
        }
        for (int frame = 0; frame < 20_000 && PlayerTurn is null && InCombat; frame++)
        {
            CombatProcess(1.0 / 60);
        }
        if (PlayerTurn is not { } c)
        {
            problems.Add($"no member's turn came (in combat: {InCombat}; current: {(_battle?.Current is { } now ? Who(now) : "no one")}, "
                + $"party fights: {PartyFights}, watched fight pending: {_watchedFightPending})");
            return;
        }
        Combatant? nearest = _battle!.Combatants.Where(e => e.Alive && c.IsEnemyOf(e)).MinBy(e => Battle.Distance(c, e));
        CombatInput(new InputEventKey { PhysicalKeycode = Key.Tab, Keycode = Key.Tab, Pressed = true });
        if (_chosenEnemy != nearest || nearest is null)
        {
            problems.Add($"Tab chose {(_chosenEnemy is null ? "no one" : Who(_chosenEnemy))}, not the nearest enemy {(nearest is null ? "(none)" : Who(nearest))}");
            return;
        }
        UpdateFloor(0);
        if (Settings.EnemyRings > 0 && _floor.RingCount is not { Count: > 0, Chosen: true })
        {
            problems.Add($"no ring under the chosen {Who(nearest)}");
        }
        GD.Print($"floor: Tab chose {Who(nearest)}, {Battle.Distance(c, nearest)} cells from {Who(c)}; {_floor.RingCount.Count} rings");
    }
}
