using DarkSun.Game.History;
using DarkSun.Game.Rules;
using DarkSun.Game.Scripting;

namespace DarkSun.Game.Tests;

public class JournalTests
{
    [Fact]
    public void ConversationsGatherTextAndReplies()
    {
        var j = new Journal();
        j.Said(42, "Semyon", 12, "Uunh... ", clears: 0);
        j.Said(42, "Semyon", 12, "help!", clears: 0);
        j.Said(42, "", null, "\n", clears: 0);
        j.Said(42, "Semyon", 12, "Water...", clears: 0);
        j.Replied("  Where can I get water? ");

        Conversation c = Assert.Single(j.Conversations);
        Assert.Equal(("Semyon", 12, 42), (c.Speaker, c.Portrait, c.Region));
        Assert.Equal([new JournalLine("Uunh... help!\n"), new JournalLine("Water..."), new JournalLine("Where can I get water?", Reply: true)], c.Lines);

        j.Said(42, "Semyon", 12, "Thank you!", clears: 1); // the window was cleared: a new conversation
        j.Said(42, "Tristram", 30, "Who are you?", clears: 1); // someone else
        Assert.Equal(3, j.Conversations.Count);
    }

    [Fact]
    public void RegionsAreAnEventTheFirstTime()
    {
        var j = new Journal();
        j.Visit(42);
        j.Visit(41);
        j.Visit(42);
        j.Event(42, "Rikus receives Pot of Water.");

        Assert.Equal(3, j.Events.Count);
        Assert.Equal("Rikus receives Pot of Water.", j.Events[^1].Text);

        var back = new Journal();
        back.Load(j.Save());
        back.Visit(41);
        Assert.Equal(3, back.Events.Count); // 41 was visited already
    }

    [Fact]
    public void QuestsFollowTheVariables()
    {
        QuestBook book = QuestBook.Parse("""
            { "quests": [ { "title": "Semyon", "region": 42,
                "stages": [ { "when": "gflag[28] == 1", "text": "He begs for water." },
                            { "when": "gflag[26]", "text": "You gave him water." } ],
                "done": "gflag[26] == 1 && gnum[16] >= 750 || gflag[7] == 1" } ] }
            """);
        var vars = new Dictionary<string, int>();
        int? Read(string name) => vars.GetValueOrDefault(name);

        Assert.Empty(book.Status(Read));
        vars["gflag[28]"] = 1;
        QuestStatus begun = Assert.Single(book.Status(Read));
        Assert.Equal(["He begs for water."], begun.Reached.Select(s => s.Text));
        Assert.False(begun.Finished);
        vars["gflag[26]"] = 1;
        vars["gnum[16]"] = 750;
        QuestStatus done = Assert.Single(book.Status(Read));
        Assert.Equal(2, done.Reached.Count);
        Assert.True(done.Finished);

        Assert.Throws<InvalidDataException>(() => QuestBook.Parse("""{ "quests": [ { "title": "x", "region": 1, "stages": [ { "when": "lflag[2] == 1", "text": "" } ] } ] }"""));
        Assert.Throws<InvalidDataException>(() => QuestBook.Parse("""{ "quests": [ { "title": "x", "region": 1, "stages": [ { "when": "gnum[2] > lots", "text": "" } ] } ] }"""));
        Assert.Throws<InvalidDataException>(() => QuestBook.Parse("not json"));
    }

    [Fact]
    public void TheHostWritesConversationsAndReplies()
    {
        var scripts = new Scripts();
        scripts.Files[(1, GplFileKind.Gpl)] = GplAssembler.Assemble("""
              PRINT_STRING(115, "Water...")
              MENU
                TITLE("What do you say?")
                ENTRY(text="Here you are.", func=@L9, cond=1)
              MENU_END
              EXIT_GPL
            L9:
              LOCAL_RET
            """);
        var host = new DialogueGplHost();
        var m = new GplMachine(scripts, host, [], new BorlandRandom(1));

        Assert.True(m.Run(new GplEntry(1, 0)).Suspended);
        Assert.True(m.Resume(0).Ok);

        Conversation c = Assert.Single(host.Journal.Conversations);
        Assert.Equal([new JournalLine("Water..."), new JournalLine("Here you are.", Reply: true)], c.Lines);
    }
}
