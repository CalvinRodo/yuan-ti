using System.Text;
using DarkSun.Game.History;
using DarkSun.Game.Scripting;
using Godot;

namespace DarkSun.Engine;

/// <summary>
/// The journal (J on the map; new, DSUN.EXE has none), in three tabs:
/// <list type="bullet">
/// <item>Quests: the quest log's quests begun (<see cref="Session.Quests"/>, judged
/// by the scripts' variables now), each with the stages reached;</item>
/// <item>Conversations: every conversation, newest first, with the speaker's
/// portrait and the player's replies;</item>
/// <item>Events: items gained and given up, experience, money, regions reached.</item>
/// </list>
/// J or Esc closes it.
/// </summary>
public partial class JournalWindow : PanelContainer
{
    /// <summary>The conversations shown (the newest); the journal keeps more.</summary>
    private const int ConversationsShown = 120;

    private static readonly Color Heading = new(0.95f, 0.8f, 0.55f);
    private static readonly Color Reply = new(0.7f, 0.85f, 1f);
    private static readonly Color Dim = new(0.7f, 0.65f, 0.6f);

    private TabContainer _tabs = null!;
    private RichTextLabel _quests = null!, _events = null!;
    private VBoxContainer _conversations = null!;

    /// <summary>The <c>PORT</c> portrait textures (the region view's).</summary>
    public Func<int, Texture2D?> Portraits { get; set; } = _ => null;

    public override void _Ready()
    {
        Visible = false;
        SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
        GrowHorizontal = GrowDirection.Both;
        GrowVertical = GrowDirection.Both;
        CustomMinimumSize = new Vector2(520, 320);
        AddThemeStyleboxOverride("panel", Panels.Panel());
        var column = new VBoxContainer();
        AddChild(column);
        var top = new HBoxContainer();
        column.AddChild(top);
        top.AddChild(Panels.Text("Journal", Heading));
        top.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        top.AddChild(Panels.Text("J or Esc closes", Dim));
        _tabs = Panels.Small(new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill });
        column.AddChild(_tabs);
        _quests = Rich("Quests");
        _tabs.AddChild(_quests);
        var scroll = new ScrollContainer { Name = "Conversations", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _conversations = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _conversations.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_conversations);
        _tabs.AddChild(scroll);
        _events = Rich("Events");
        _tabs.AddChild(_events);
    }

    private static RichTextLabel Rich(string name)
    {
        var label = new RichTextLabel { Name = name, BbcodeEnabled = true, SelectionEnabled = true, SizeFlagsVertical = SizeFlags.ExpandFill };
        label.AddThemeFontSizeOverride("normal_font_size", 10);
        label.AddThemeFontSizeOverride("bold_font_size", 10);
        label.AddThemeFontSizeOverride("italics_font_size", 10);
        return label;
    }

    /// <summary>Open on tab <paramref name="tab"/>: 0 quests, 1 conversations, 2 events.</summary>
    public void Open(int tab = 0)
    {
        Visible = true;
        _tabs.CurrentTab = Math.Clamp(tab, 0, _tabs.GetTabCount() - 1);
        Refresh();
    }

    public void Close() => Visible = false;

    public override void _UnhandledInput(InputEvent e)
    {
        if (Visible && (e.IsActionPressed(Controls.Back) || e.IsActionPressed(Controls.ToggleJournal)))
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }

    private static string Escape(string text) => text.Replace("[", "[lb]");

    private static string Hex(Color c) => c.ToHtml(false);

    private void Refresh()
    {
        Journal journal = Session.ScriptHost.Journal;
        GplMachine machine = Session.Scripts;
        int? Read(string name) => int.TryParse(GplVariables.Get(machine, name), out int v) ? v : null;

        // Quests, unfinished first.
        var text = new StringBuilder();
        var quests = Session.Quests.Status(Read).OrderBy(q => q.Finished).ToList();
        foreach (QuestStatus q in quests)
        {
            text.Append($"[b][color=#{Hex(Heading)}]{Escape(q.Quest.Title)}[/color][/b]")
                .Append(q.Finished ? $"  [color=#{Hex(Dim)}](done)[/color]" : "")
                .Append($"  [color=#{Hex(Dim)}]region {q.Quest.Region}[/color]\n");
            foreach (QuestStage s in q.Reached)
            {
                text.Append($"  • {Escape(s.Text)}\n");
            }
            text.Append('\n');
        }
        _quests.Text = quests.Count > 0 ? text.ToString()
            : $"[color=#{Hex(Dim)}]No quests yet. ({Session.Quests.Quests.Count} in the quest log's file; they show once begun.)[/color]";

        // Conversations, newest first.
        foreach (Node child in _conversations.GetChildren())
        {
            child.QueueFree();
        }
        foreach (Conversation c in journal.Conversations.Reverse().Take(ConversationsShown))
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            row.AddChild(new TextureRect
            {
                Texture = c.Portrait is int p ? Portraits(p) : null,
                CustomMinimumSize = new Vector2(32, 32),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsVertical = SizeFlags.ShrinkBegin,
            });
            var lines = new StringBuilder($"[color=#{Hex(Heading)}]{(c.Speaker.Length > 0 ? Escape(c.Speaker) : "Narration")}[/color]  [color=#{Hex(Dim)}]region {c.Region}[/color]\n");
            foreach (JournalLine l in c.Lines)
            {
                string t = Escape(l.Text.Trim());
                if (t.Length > 0)
                {
                    lines.Append(l.Reply ? $"[color=#{Hex(Reply)}]» {t}[/color]\n" : $"{t}\n");
                }
            }
            var label = Rich("Text");
            label.FitContent = true;
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            label.Text = lines.ToString().TrimEnd();
            row.AddChild(label);
            _conversations.AddChild(row);
        }
        if (journal.Conversations.Count == 0)
        {
            _conversations.AddChild(Panels.Text("No conversations yet.", Dim));
        }

        // Events, newest first.
        var events = new StringBuilder();
        foreach (JournalEvent e in journal.Events.Reverse())
        {
            events.Append($"[color=#{Hex(Dim)}]region {e.Region}[/color]  {Escape(e.Text)}\n");
        }
        _events.Text = events.Length > 0 ? events.ToString() : $"[color=#{Hex(Dim)}]Nothing yet.[/color]";
    }
}
