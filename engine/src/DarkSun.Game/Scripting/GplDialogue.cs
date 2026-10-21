namespace DarkSun.Game.Scripting;

/// <summary>What the dialogue window is waiting for.</summary>
public abstract record GplPrompt;

/// <summary>CONTINUE: "Press continue" with a Continue button.</summary>
public sealed record GplContinuePrompt : GplPrompt;

/// <summary>ASK_YES_NO: "Answer Yes or No"; the answer is 1 for Yes, 0 for No.</summary>
public sealed record GplYesNoPrompt : GplPrompt;

/// <summary>MENU: the options in script order; disabled ones are not offered. The answer is an option's index.</summary>
public sealed record GplMenuPrompt(string Title, IReadOnlyList<GplMenuOption> Options) : GplPrompt;

/// <summary>INPUT_STRING (<c>043D:19AA</c>, <c>7372:11FD</c>): the player types up to <paramref name="MaxLength"/> characters.</summary>
public sealed record GplTextPrompt(int MaxLength) : GplPrompt;

/// <summary>SHOP (<c>7372:08D2</c>): the shop of <paramref name="Merchant"/> (an object index) is open; the script goes on when it closes.</summary>
public sealed record GplShopPrompt(int Merchant) : GplPrompt;

/// <summary>A piece of text from PRINT_STRING / PRINT_NUMBER / PRINT_NEWLINE, with its style byte (98 or 115 in the scripts).</summary>
public readonly record struct GplText(string Text, int Style);

/// <summary>
/// The dialogue window's content, as DSUN.EXE's text windows hold it
/// (<c>84C9:0253</c>): window 1 the portrait (<c>PORT</c> resource, SHOW_PIC),
/// window 2 the text (PRINT_*), window 0 the prompt buttons.
/// </summary>
public sealed class GplDialogue
{
    private readonly List<GplText> _text = [];

    /// <summary>The <c>PORT</c> number shown, or null.</summary>
    public int? Portrait { get; private set; }

    public IReadOnlyList<GplText> Text => _text;

    public GplPrompt? Prompt { get; private set; }

    /// <summary>Changes whenever the content does, so a view knows when to redraw.</summary>
    public int Version { get; private set; }

    public bool IsEmpty => Portrait is null && _text.Count == 0 && Prompt is null;

    public void Append(string text, int style)
    {
        _text.Add(new GplText(text, style));
        Version++;
    }

    public void ShowPortrait(int number)
    {
        Portrait = number;
        Version++;
    }

    /// <summary>How many times the window was cleared (CLEAR_PIC, CLOSE): the journal starts a new conversation.</summary>
    public int Clears { get; private set; }

    /// <summary>How many times the text went away (<see cref="Clear"/>, or an answer), so a view starts its text again.</summary>
    public int Pages { get; private set; }

    /// <summary>CLEAR_PIC (window 1): the portrait and the text go.</summary>
    public void Clear()
    {
        Portrait = null;
        _text.Clear();
        Clears++;
        Pages++;
        Version++;
    }

    public void Ask(GplPrompt prompt)
    {
        Prompt = prompt;
        Version++;
    }

    /// <summary>
    /// The player answered. DSUN.EXE empties the text once a button is pressed
    /// (<c>84C9:0CD9</c> → <c>09F0</c>: CONTINUE, ASK_YES_NO, a menu), so what is
    /// said next starts a new page; the portrait stays. <paramref name="clearText"/>
    /// false: the engine's own wait after END, which keeps it.
    /// </summary>
    public void Answered(bool clearText = true)
    {
        Prompt = null;
        if (clearText && _text.Count > 0)
        {
            _text.Clear();
            Pages++;
        }
        Version++;
    }
}

/// <summary>The parts of the game world scripts change, as far as the engine models them.</summary>
public interface IGplWorld
{
    /// <summary>
    /// TELEPORT the party (who 32766) to region <paramref name="region"/>,
    /// cell (<paramref name="x"/>, <paramref name="y"/>): in the current region
    /// each member goes to the nearest free cell; another region is entered.
    /// </summary>
    void TeleportParty(int region, int x, int y);

    /// <summary>TELEPORT the objects named −<paramref name="number"/> (an <c>OJFF</c> number); region 255 takes them off the map.</summary>
    void TeleportObjects(int number, int region, int x, int y);

    /// <summary>An object field the world knows (e.g. field 95, facing 0–7 clockwise from north), or null.</summary>
    int? ReadField(GplObjectRef field) => null;
}

/// <summary>
/// A host that carries out the text and dialogue commands into a
/// <see cref="GplDialogue"/>, pausing the script for CONTINUE, ASK_YES_NO
/// and MENU until the player answers (<see cref="GplMachine.Resume"/>), and
/// the world commands an <see cref="World"/> supports. Other world commands
/// are logged, as by <see cref="LoggingGplHost"/>.
/// </summary>
public class DialogueGplHost : LoggingGplHost
{
    /// <summary>
    /// What <c>MAS 99</c> puts in <c>gstr[2]</c> and <c>gstr[3]</c>, which the
    /// scripts print after a speech (about 1 450 and 1 250 times): not text.
    /// The original shows neither; END waits for the player to read the
    /// speech, CLOSE puts the window away (inferred from their use: END
    /// before a CLEAR_PIC and a new page, the arena's bout after its speech).
    /// </summary>
    public const string EndMark = "END", CloseMark = "CLOSE";

    /// <summary>TELEPORT's "the party" value.</summary>
    public const int WholeParty = 0x7FFE;

    public GplDialogue Dialogue { get; } = new();

    /// <summary>The party's journal: what the dialogue window shows goes in it, with the player's replies.</summary>
    public History.Journal Journal { get; } = new();

    /// <summary>Who is speaking (for the journal): no one here; the world host names the creature talked to.</summary>
    protected virtual string SpeakerOf(GplMachine machine, int style) => "";

    /// <summary>The region the party is in (<c>gname[4]</c>).</summary>
    protected int Region(GplMachine machine) => ReadEngineVariable(machine, 0x24);

    private void Journalled(GplMachine machine, string text, int style) =>
        Journal.Said(Region(machine), SpeakerOf(machine, style), Dialogue.Portrait, text, Dialogue.Clears);

    public IGplWorld? World { get; set; }

    /// <summary>SOUND requests, for the audio layer to play.</summary>
    public Queue<int> Sounds { get; } = new();

    public override int? Execute(GplMachine machine, GplCommand command)
    {
        switch (command.Mnemonic)
        {
            case "PRINT_STRING" when command.Texts[1] == EndMark:
                // gstr[2] (MAS 99): the speech is over; the script goes on once the player has read it.
                Wait(machine, new GplContinuePrompt(), out _, clearsText: false);
                return null;
            case "PRINT_STRING" when command.Texts[1] == CloseMark:
                Dialogue.Clear(); // gstr[3]: put the window away
                return null;
            case "PRINT_STRING":
                Dialogue.Append(command.Texts[1] ?? "", command.Values[0] & 0xFF);
                Journalled(machine, command.Texts[1] ?? "", command.Values[0] & 0xFF);
                return null;
            case "PRINT_NUMBER":
                Dialogue.Append(command.Values[1].ToString(), command.Values[0] & 0xFF);
                Journalled(machine, command.Values[1].ToString(), command.Values[0] & 0xFF);
                return null;
            case "PRINT_NEWLINE":
                Dialogue.Append("\n", 0);
                Journalled(machine, "\n", 0);
                return null;
            case "CLEAR_PIC":
                Dialogue.Clear();
                return null;
            case "SHOW_PIC":
                Dialogue.ShowPortrait(command.Values[0]);
                return null;
            case "CONTINUE":
                Wait(machine, new GplContinuePrompt(), out _);
                return null;
            case "ASK_YES_NO":
                return Wait(machine, new GplYesNoPrompt(), out int yes) ? (yes == 1 ? 1 : 0) : 0;
            case "SOUND":
                Sounds.Enqueue(command.Values[0]);
                return base.Execute(machine, command);
            case "TELEPORT" when World is not null:
                // gpl_op_5e_TELEPORT (043D:1EAE): (who, region, x, y, flag).
                base.Execute(machine, command);
                if (command.Values[0] == WholeParty)
                {
                    World.TeleportParty(command.Values[1], command.Values[2], command.Values[3]);
                    return 1;
                }
                World.TeleportObjects(-command.Values[0], command.Values[1], command.Values[2], command.Values[3]);
                return null;
            default:
                return base.Execute(machine, command);
        }
    }

    /// <summary>Object fields: the world's value where it has one, else the stored one.</summary>
    public override int ReadField(GplMachine machine, GplObjectRef field) =>
        World?.ReadField(field) ?? base.ReadField(machine, field);

    public override string? InputText(GplMachine machine, int maxLength)
    {
        if (machine.TryTakeText(out string text))
        {
            Journal.Replied(text);
            Dialogue.Answered();
            Dialogue.Clear(); // 7372:1235: the windows close after typing (84C9:0A25)
            return text.Length > maxLength ? text[..maxLength] : text;
        }
        Dialogue.Ask(new GplTextPrompt(maxLength));
        machine.Suspend();
        return null;
    }

    public override int ChooseMenu(GplMachine machine, string title, IReadOnlyList<GplMenuOption> options) =>
        Wait(machine, new GplMenuPrompt(title, options), out int choice) ? choice : -1;

    /// <summary>
    /// The player's answer if the script is being resumed; otherwise show the
    /// prompt and pause the script.
    /// </summary>
    /// <param name="clearsText">Whether answering empties the text (<see cref="GplDialogue.Answered"/>).</param>
    protected bool Wait(GplMachine machine, GplPrompt prompt, out int answer, bool clearsText = true)
    {
        if (machine.TryTakeAnswer(out answer))
        {
            int a = answer;
            Journal.Replied(Dialogue.Prompt switch
            {
                GplYesNoPrompt => a == 1 ? "Yes" : "No",
                GplMenuPrompt menu when a >= 0 && a < menu.Options.Count => menu.Options[a].Text,
                _ => "",
            });
            Dialogue.Answered(clearsText);
            return true;
        }
        Dialogue.Ask(prompt);
        machine.Suspend();
        return false;
    }
}
