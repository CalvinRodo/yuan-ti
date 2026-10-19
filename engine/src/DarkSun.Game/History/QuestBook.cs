using System.Text.Json;
using System.Text.Json.Serialization;

namespace DarkSun.Game.History;

/// <summary>A step of a quest: shown once <see cref="When"/> holds.</summary>
public sealed record QuestStage(string When, string Text);

/// <summary>
/// A quest the journal shows: once any stage's condition holds, with every
/// stage reached so far; finished when <see cref="Done"/> holds.
/// </summary>
public sealed record Quest(string Title, int Region, IReadOnlyList<QuestStage> Stages, string? Done = null);

/// <summary>Where a quest stands now.</summary>
public sealed record QuestStatus(Quest Quest, IReadOnlyList<QuestStage> Reached, bool Finished);

/// <summary>
/// The quest log's quests (new; DSUN.EXE keeps no quest log), read from JSON
/// (<c>journal/quests.json</c>: the engine's, or a mod's instead) and
/// judged by the scripts' variables, as the scripts set them:
/// <code>
/// { "quests": [ { "title": "…", "region": 42,
///     "stages": [ { "when": "gflag[28] == 1", "text": "…" } ],
///     "done": "gflag[26] == 1" } ] }
/// </code>
/// A condition is comparisons of a variable (<c>gflag[n]</c>, <c>gnum[n]</c>,
/// <c>gbignum[n]</c>, <c>gname[n]</c>) with a number (<c>== != &lt; &gt; &lt;= &gt;=</c>; a
/// variable alone means "not 0"), joined by <c>&amp;&amp;</c> or <c>||</c> (no brackets;
/// <c>&amp;&amp;</c> binds first). Locals (<c>lflag</c>, …) belong to a running script
/// and aren't offered.
/// </summary>
public sealed class QuestBook
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public QuestBook(IReadOnlyList<Quest> quests) => Quests = quests;

    public static QuestBook Empty { get; } = new([]);

    public IReadOnlyList<Quest> Quests { get; }

    private sealed record File(List<Quest> Quests);

    /// <exception cref="InvalidDataException">Not a quest file, or a condition that doesn't parse (it says which).</exception>
    public static QuestBook Parse(string json)
    {
        File file;
        try
        {
            file = JsonSerializer.Deserialize<File>(json, Json) ?? throw new InvalidDataException("empty quest file");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException(e.Message, e);
        }
        foreach (Quest q in file.Quests ?? [])
        {
            foreach (string condition in q.Stages.Select(s => s.When).Append(q.Done).OfType<string>())
            {
                if (Condition.Check(condition) is { } error)
                {
                    throw new InvalidDataException($"{q.Title}: \"{condition}\": {error}");
                }
            }
        }
        return new QuestBook(file.Quests ?? []);
    }

    /// <summary>The quests begun, with where each stands; <paramref name="read"/> gives a variable's value (null: none such).</summary>
    public IReadOnlyList<QuestStatus> Status(Func<string, int?> read) =>
        [.. Quests.Select(q => new QuestStatus(q, [.. q.Stages.Where(s => Condition.Holds(s.When, read))],
                q.Done is { } done && Condition.Holds(done, read)))
            .Where(s => s.Reached.Count > 0 || s.Finished)];
}

/// <summary>The quest conditions: <c>gflag[26] == 1 &amp;&amp; gnum[16] &gt; 0 || gflag[7]</c>.</summary>
public static class Condition
{
    private static readonly string[] Operators = ["==", "!=", "<=", ">=", "<", ">"];

    public static bool Holds(string condition, Func<string, int?> read) =>
        condition.Split("||").Any(any => any.Split("&&").All(all => Compare(all, read)));

    /// <summary>Null if <paramref name="condition"/> parses, else why not.</summary>
    public static string? Check(string condition)
    {
        foreach (string part in condition.Split("||").SelectMany(p => p.Split("&&")))
        {
            var (name, op, value) = Split(part);
            if (!IsVariable(name))
            {
                return $"\"{name}\" isn't a global variable (gflag[n], gnum[n], gbignum[n], gname[n])";
            }
            if (op is not null && value is null)
            {
                return $"\"{part.Trim()}\": compare with a number";
            }
        }
        return null;
    }

    private static bool Compare(string part, Func<string, int?> read)
    {
        var (name, op, value) = Split(part);
        if (read(name) is not int v)
        {
            return false;
        }
        int n = value ?? 0;
        return op switch
        {
            null => v != 0,
            "==" => v == n,
            "!=" => v != n,
            "<=" => v <= n,
            ">=" => v >= n,
            "<" => v < n,
            _ => v > n,
        };
    }

    private static (string Name, string? Op, int? Value) Split(string part)
    {
        foreach (string op in Operators)
        {
            int at = part.IndexOf(op, StringComparison.Ordinal);
            if (at >= 0)
            {
                string right = part[(at + op.Length)..].Trim();
                return (part[..at].Trim(), op, int.TryParse(right, out int v) ? v : null);
            }
        }
        return (part.Trim(), null, null);
    }

    private static bool IsVariable(string name)
    {
        int open = name.IndexOf('[');
        return open > 0 && name.EndsWith(']') && name[..open] is "gflag" or "gnum" or "gbignum" or "gname"
            && int.TryParse(name[(open + 1)..^1], out int i) && i >= 0;
    }
}
