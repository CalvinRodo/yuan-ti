namespace DarkSun.Game.History;

/// <summary>A line of a conversation: what was said, or the player's reply.</summary>
public sealed record JournalLine(string Text, bool Reply = false);

/// <summary>One conversation: where, with whom (their name and <c>PORT</c> portrait, if any), and what was said.</summary>
public sealed class Conversation
{
    public int Region { get; init; }

    public string Speaker { get; set; } = "";

    public int? Portrait { get; set; }

    public List<JournalLine> Lines { get; init; } = [];
}

/// <summary>Something that happened: an item gained, experience, money, a new region.</summary>
public sealed record JournalEvent(int Region, string Text);

/// <summary>A journal as saved.</summary>
public sealed record JournalState(List<Conversation> Conversations, List<JournalEvent> Events, List<int> Visited);

/// <summary>
/// The party's journal (new; DSUN.EXE has none): every conversation as the
/// dialogue window showed it, with the player's replies, and the events
/// worth remembering. The script host feeds it (<c>DialogueGplHost</c>,
/// <c>WorldGplHost</c>); quests come from <see cref="QuestBook"/>. Kept with
/// the game and in saves. The oldest go once there are more than
/// <see cref="Limit"/>.
/// </summary>
public sealed class Journal
{
    public const int Limit = 1000;

    private readonly List<Conversation> _conversations = [];
    private readonly List<JournalEvent> _events = [];
    private readonly HashSet<int> _visited = [];
    private Conversation? _open;
    private int _clears = -1;

    /// <summary>Oldest first.</summary>
    public IReadOnlyList<Conversation> Conversations => _conversations;

    /// <summary>Oldest first.</summary>
    public IReadOnlyList<JournalEvent> Events => _events;

    /// <summary>Changes whenever the journal does, so a view knows when to redraw.</summary>
    public int Version { get; private set; }

    /// <summary>
    /// Text the dialogue window shows. It joins the conversation in progress
    /// unless the window was cleared since (<paramref name="clears"/>, the
    /// dialogue's count), the region changed, or someone else speaks.
    /// </summary>
    public void Said(int region, string speaker, int? portrait, string text, int clears)
    {
        if (text.Length == 0)
        {
            return;
        }
        bool other = speaker.Length > 0 && _open is { Speaker.Length: > 0 } open && open.Speaker != speaker;
        if (_open is null || clears != _clears || _open.Region != region || other)
        {
            _open = new Conversation { Region = region, Speaker = speaker, Portrait = portrait };
            Add(_conversations, _open);
        }
        _clears = clears;
        if (_open.Speaker.Length == 0 && speaker.Length > 0)
        {
            _open.Speaker = speaker;
        }
        _open.Portrait ??= portrait;
        // Text comes in pieces (PRINT_STRING, PRINT_NUMBER): a piece without a break joins the line before.
        if (_open.Lines.Count > 0 && !_open.Lines[^1].Reply && !_open.Lines[^1].Text.EndsWith('\n'))
        {
            _open.Lines[^1] = _open.Lines[^1] with { Text = _open.Lines[^1].Text + text };
        }
        else
        {
            _open.Lines.Add(new JournalLine(text));
        }
        Version++;
    }

    /// <summary>The player's answer to a choice, in the conversation in progress.</summary>
    public void Replied(string text)
    {
        if (_open is null || text.Length == 0)
        {
            return;
        }
        _open.Lines.Add(new JournalLine(text.Trim(), Reply: true));
        Version++;
    }

    public void Event(int region, string text)
    {
        Add(_events, new JournalEvent(region, text));
        Version++;
    }

    /// <summary>The party is in <paramref name="region"/>: the first time is an event.</summary>
    public void Visit(int region)
    {
        if (_visited.Add(region))
        {
            Event(region, $"Arrived in region {region} for the first time.");
        }
        _open = null;
    }

    private static void Add<T>(List<T> list, T item)
    {
        list.Add(item);
        if (list.Count > Limit)
        {
            list.RemoveRange(0, list.Count - Limit);
        }
    }

    public JournalState Save() => new([.. _conversations], [.. _events], [.. _visited]);

    public void Load(JournalState? state)
    {
        _conversations.Clear();
        _events.Clear();
        _visited.Clear();
        if (state is not null)
        {
            _conversations.AddRange(state.Conversations);
            _events.AddRange(state.Events);
            _visited.UnionWith(state.Visited);
        }
        _open = null;
        Version++;
    }
}
