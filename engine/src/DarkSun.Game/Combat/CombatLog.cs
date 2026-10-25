using DarkSun.Game.Magic;
using DarkSun.Game.World;

namespace DarkSun.Game.Combat;

/// <summary>What a line of the combat log is, for its colour.</summary>
public enum LogKind { Round, Turn, Hit, Miss, Damage, Save, Fell, Detail, Other }

/// <summary>
/// How much the combat log says, as Obsidian Edition's three levels: only what
/// came of each roll ("Slig hits Cermak for 4"); the dice too ("Slig attacks
/// Cermak: 11 vs 11+ HIT, 4 damage (1d4)"); every term ("THAC0 18 - STR 1 - AC 6 = 11").
/// </summary>
public enum LogLevel { Least, Short, Detail }

/// <summary>The lines a filter keeps (Obsidian Edition's All, Party, Damage, Saves).</summary>
public enum LogFilter { All, Party, Damage, Saves }

/// <summary>A piece of a line: a name is <paramref name="Name"/>'s, drawn in their colour.</summary>
public sealed record LogRun(string Text, Combatant? Name = null);

/// <summary>A line of the log: its kind, whose it is (the pip: filled for the party, hollow for the rest), and its pieces.</summary>
public sealed record LogLine(LogKind Kind, Combatant? Whose, IReadOnlyList<LogRun> Runs, int Depth = 0)
{
    public string Text => string.Concat(Runs.Select(r => r.Text));
}

/// <summary>
/// A fight's combat log, as Obsidian Edition's (github.com/daaki85/darksun-obsidian-edition)
/// in-game log shows a fight: each <see cref="CombatEvent"/> becomes lines at
/// every <see cref="LogLevel"/>, so a change of level shows the fight so far
/// that way. Events are written when they happen (<see cref="Add"/>: hit
/// points as they were then) and shown when the fight plays them back
/// (<see cref="Reveal"/>). The last <see cref="History"/> are kept.
/// </summary>
public sealed class CombatLog(Func<int, string> effectName, Func<Item, string> itemName)
{
    public const int History = 200;

    private sealed record Entry(LogLine[] Least, LogLine[] Short, LogLine[] Detail, Combatant? JoinsAttackOf = null)
    {
        /// <summary>The event the lines are of (none for a <see cref="Note"/>).</summary>
        public CombatEvent? Event { get; init; }

        public LogLine[] At(LogLevel level) => level switch { LogLevel.Least => Least, LogLevel.Short => Short, _ => Detail };
    }

    private readonly List<Entry> _shown = [];
    private readonly Queue<(CombatEvent Event, Entry? Entry)> _pending = new();

    /// <summary>Counts changes to what is shown.</summary>
    public int Version { get; private set; }

    public bool IsEmpty => _shown.Count == 0;

    /// <summary>The events whose lines are shown and still kept (the oldest go past <see cref="History"/>).</summary>
    public IEnumerable<CombatEvent> Kept => _shown.Select(s => s.Event).OfType<CombatEvent>();

    /// <summary>The round shown last (0 before the first).</summary>
    public int Round { get; private set; }

    /// <summary>Whose turn was shown last.</summary>
    public Combatant? Acting { get; private set; }

    /// <summary>Forget it all.</summary>
    public void Clear()
    {
        _shown.Clear();
        _pending.Clear();
        (Round, Acting) = (0, null);
        Version++;
    }

    /// <summary>A line of words shown at once, at every level ("Combat! 2 enemies").</summary>
    public void Note(string text, LogKind kind = LogKind.Other)
    {
        LogLine[] line = [new(kind, null, [new LogRun(text)])];
        Show(new Entry(line, line, line));
    }

    /// <summary>An event as it happens, to show when <see cref="Reveal"/> reaches it.</summary>
    public void Add(CombatEvent e) => _pending.Enqueue((e, Format(e)));

    /// <summary>Show what happened up to <paramref name="e"/> (the event the fight plays back now); the new lines' words, at <see cref="LogLevel.Short"/>.</summary>
    public IReadOnlyList<string> Reveal(CombatEvent e)
    {
        var words = new List<string>();
        if (!_pending.Any(p => ReferenceEquals(p.Event, e)))
        {
            return words;
        }
        while (_pending.TryDequeue(out var p))
        {
            if (p.Event is RoundStarted r)
            {
                (Round, Acting) = (r.Round, null);
            }
            else if (p.Event is TurnStarted t)
            {
                Acting = t.Actor;
            }
            if (p.Entry is { } entry)
            {
                Show(entry with { Event = p.Event });
                words.AddRange(entry.Short.Select(l => l.Text));
            }
            if (ReferenceEquals(p.Event, e))
            {
                break;
            }
        }
        return words;
    }

    private void Show(Entry entry)
    {
        _shown.Add(entry);
        if (_shown.Count > History)
        {
            _shown.RemoveRange(0, _shown.Count - History);
        }
        Version++;
    }

    /// <summary>The log's lines at <paramref name="level"/>, an attack's damage on its line (but in detail).</summary>
    public List<LogLine> At(LogLevel level, LogFilter filter = LogFilter.All)
    {
        var lines = new List<LogLine>();
        foreach (Entry entry in _shown)
        {
            foreach (LogLine line in entry.At(level))
            {
                if (entry.JoinsAttackOf is { } attacker && level != LogLevel.Detail
                    && lines.Count > 0 && lines[^1] is { Kind: LogKind.Hit } hit && hit.Whose == attacker)
                {
                    lines[^1] = hit with { Runs = [.. hit.Runs, .. line.Runs] };
                    continue;
                }
                lines.Add(line);
            }
        }
        return Filtered(lines, filter);
    }

    /// <summary>The lines <paramref name="filter"/> keeps: a party member's, the hits and damage (and falls), the saves and magic resistance; the rounds with them.</summary>
    public static List<LogLine> Filtered(List<LogLine> lines, LogFilter filter) => filter switch
    {
        LogFilter.Party => [.. lines.Where(l => l.Kind == LogKind.Round || l.Whose is { PartyMember: true })],
        LogFilter.Damage => [.. lines.Where(l => l.Kind is LogKind.Round or LogKind.Hit or LogKind.Damage or LogKind.Fell)],
        LogFilter.Saves => [.. lines.Where(l => l.Kind is LogKind.Round or LogKind.Save)],
        _ => lines,
    };

    /// <summary>
    /// The panel's top line: "Round 2: Gerakis, then Cermak, Cilla": the round
    /// and whose turn as shown so far, then who else still has a turn in
    /// <paramref name="battle"/> (none out of a fight).
    /// </summary>
    public string Header(Battle? battle)
    {
        if (battle is null || Round == 0)
        {
            return "";
        }
        string now = Acting?.Name ?? "";
        string next = string.Join(", ", battle.StillToAct().Where(c => c != Acting).Select(c => c.Name));
        string who = now.Length > 0 && next.Length > 0 ? $"{now}, then {next}" : now.Length > 0 ? now : next.Length > 0 ? $"then {next}" : "";
        return who.Length > 0 ? $"Round {Round}: {who}" : $"Round {Round}";
    }

    // -- the lines of each event ---------------------------------------------------------------------

    private static LogRun N(Combatant c) => new(c.Name.Length > 0 ? c.Name : "someone", c);

    private static LogRun T(string text) => new(text);

    private static LogLine L(LogKind kind, Combatant? whose, params LogRun[] runs) => new(kind, whose, runs);

    private static LogLine Sub(LogKind kind, params LogRun[] runs) => new(kind, null, runs, 1);

    private static Entry Same(params LogLine[] lines) => new(lines, lines, lines);

    private static Entry NotLeast(params LogLine[] lines) => new([], lines, lines);

    private Entry? Format(CombatEvent e) => e switch
    {
        RoundStarted r => Same(L(LogKind.Round, null, T($"ROUND {r.Round}"))),
        TurnStarted t => NotLeast(L(LogKind.Turn, t.Actor, N(t.Actor), T("'s turn"))),
        AttackRolled a => Attack(a),
        DamageRolled { Effect: 0 } d => new Entry(
            [L(LogKind.Hit, d.Actor, T($" for {d.Total}"))],
            [L(LogKind.Hit, d.Actor, T($", {d.Total} damage ({Dice(d)})"))],
            [Sub(LogKind.Damage, T($"damage {Dice(d)} = {d.Total}"))],
            d.Actor),
        DamageRolled d => NotLeast(L(LogKind.Damage, d.Actor, T($"{effectName(d.Effect)} damage: {Dice(d)} = {d.Total}"))),
        Damaged d => NotLeast(L(LogKind.Damage, d.Target, N(d.Target),
            T($" now {Math.Max(0, d.Target.Vitals.Hp - d.Amount)}/{d.Target.Vitals.MaxHp} HP (-{d.Amount})"))),
        Healed h => Same(L(LogKind.Other, h.Target, N(h.Target), T($" heals {h.Amount}"))),
        // A save or magic resistance has its own line, with its roll.
        Avoided { Why: "saved" or "resisted" } => null,
        Avoided v => Same(L(LogKind.Miss, v.Target, N(v.Target), T($": {v.Why}"))),
        SaveRolled s => Save(s),
        ResistanceRolled m => Resistance(m),
        Cast c => Same(L(LogKind.Other, c.Actor, N(c.Actor),
            T($" {(c.Effect is >= Spellcasting.FirstPower and <= Spellcasting.LastPower ? "uses" : "casts")} {effectName(c.Effect)}"))),
        ConditionGained g => Same(L(LogKind.Other, g.Target, N(g.Target), T($" is {Words(g.Condition)}"))),
        ConditionEnded x => NotLeast(L(LogKind.Detail, x.Target, N(x.Target), T($" is no longer {Words(x.Condition)}"))),
        Guarded g => NotLeast(L(LogKind.Turn, g.Actor, N(g.Actor), T(" guards"))),
        Waited w => NotLeast(L(LogKind.Turn, w.Actor, N(w.Actor), T(" waits"))),
        Fell f => Same(L(LogKind.Fell, f.Actor, N(f.Actor), T(f.Status switch { Vitals.Down => " falls", Vitals.Dead => " is killed", _ => " is destroyed" }))),
        ItemDestroyed i => Same(L(LogKind.Other, i.Owner, N(i.Owner), T($": {itemName(i.Item)} {i.Message}"))),
        FieldMade fm => NotLeast(L(LogKind.Other, null, T($"{effectName(fm.Field.Effect.Number)} covers {fm.Field.Cells.Count} cells"))),
        FieldEnded fe => NotLeast(L(LogKind.Detail, null, T($"{effectName(fe.Field.Effect.Number)} fades"))),
        SideChanged s => Same(L(LogKind.Other, s.Actor, N(s.Actor), T($" now fights for {(s.Side == 1 ? "the party" : "the enemy")}"))),
        Summoned s => Same(L(LogKind.Other, s.Actor, N(s.Actor), T(" summons "), N(s.Creature))),
        _ => null,
    };

    private static Entry Attack(AttackRolled a)
    {
        LogKind kind = a.Hit ? LogKind.Hit : LogKind.Miss;
        string result = a.Hit ? "HIT" : "miss";
        LogLine least = L(kind, a.Actor, N(a.Actor), T(a.Hit ? " hits " : " misses "), N(a.Target));
        LogLine brief = L(kind, a.Actor, N(a.Actor), T(" attacks "), N(a.Target), T($": {a.Roll} vs {Math.Clamp(a.Needed, 2, 20)}+ {result}"));
        var detail = new List<LogLine>
        {
            L(kind, a.Actor, N(a.Actor), T(" attacks "), N(a.Target),
                T($": d20 = {a.Roll}, {(a.Needed > 20 ? "only a 20 hits" : $"needs {Math.Max(2, a.Needed)}+")} -> {result}")),
        };
        if (a.Terms is { Count: > 0 } terms)
        {
            detail.Add(Sub(LogKind.Detail, T(Terms(terms) + $" = {a.Needed}")));
        }
        return new Entry([least], [brief], [.. detail]);
    }

    /// <summary>"THAC0 18 - STR 1 - AC 6": the terms of a number needed (in the game font's characters: code page 437 has no minus sign).</summary>
    public static string Terms(IReadOnlyList<RollTerm> terms) =>
        string.Concat(terms.Select((t, i) => t.What == "AC" ? $" - AC {-t.Value}"
            : i == 0 ? $"{t.What} {t.Value}"
            : t.Value < 0 ? $" - {t.What} {-t.Value}" : $" + {t.What} {t.Value}"));

    /// <summary>"2d4+1", "1d8 x3" (a backstab), "4" (no dice).</summary>
    public static string Dice(DamageRolled d)
    {
        string dice = d.Dice > 0 && d.Sides > 0 ? $"{d.Dice}d{d.Sides}" : "";
        string bonus = d.Bonus == 0 ? "" : dice.Length == 0 ? $"{d.Bonus}" : d.Bonus > 0 ? $"+{d.Bonus}" : $"{d.Bonus}";
        string text = dice + bonus;
        return (text.Length > 0 ? text : "0") + (d.Times > 1 ? $" x{d.Times}" : "");
    }

    private Entry Save(SaveRolled s)
    {
        string what = effectName(s.Effect), result = s.Saved ? "saved" : "failed";
        return new Entry(
            [L(LogKind.Save, s.Target, N(s.Target), T(s.Saved ? $" saves vs {what}" : $" fails to save vs {what}"))],
            [L(LogKind.Save, s.Target, N(s.Target), T($"'s save vs {what}: {s.Total} vs {s.Needed}, {result}"))],
            [L(LogKind.Save, s.Target, N(s.Target), T($"'s saving throw vs {what}: d20 = {s.Natural}"
                + (s.Natural is 1 or 20 ? $" (natural {s.Natural})" : s.Total != s.Natural ? $", {s.Total} in all" : "")
                + $", needs {s.Needed} -> {result}"))]);
    }

    private Entry Resistance(ResistanceRolled m)
    {
        string what = effectName(m.Effect), result = m.Resisted ? "resisted" : "not resisted";
        LogLine brief = L(LogKind.Save, m.Target, N(m.Target), T($" MR {m.Percent}% vs {what}: {m.Roll}, {result}"));
        return new Entry(
            m.Resisted ? [L(LogKind.Save, m.Target, N(m.Target), T($" resists {what}"))] : [],
            [brief],
            [L(LogKind.Save, m.Target, N(m.Target), T($" magic resistance {m.Percent}% vs {what}: d100 = {m.Roll} -> {result}"))]);
    }

    /// <summary>A condition as words ("CantAttack" → "unable to attack").</summary>
    public static string Words(Condition c) => c switch
    {
        Condition.CantAttack => "unable to attack",
        Condition.ImprovedAc => "armored",
        _ => string.Concat(c.ToString().Select((ch, i) => i > 0 && char.IsUpper(ch) ? " " + char.ToLowerInvariant(ch) : char.ToLowerInvariant(ch).ToString())),
    };
}
