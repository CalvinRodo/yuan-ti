namespace DarkSun.Game.Scripting;

/// <summary>What the dialogue window is waiting for.</summary>
public abstract record GplPrompt;

/// <summary>CONTINUE: "Press continue" with a Continue button.</summary>
public sealed record GplContinuePrompt : GplPrompt;

/// <summary>ASK_YES_NO: "Answer Yes or No"; the answer is 1 for Yes, 0 for No.</summary>
public sealed record GplYesNoPrompt : GplPrompt;

/// <summary>MENU: the options in script order; disabled ones are not offered. The answer is an option's index.</summary>
public sealed record GplMenuPrompt(string Title, IReadOnlyList<GplMenuOption> Options) : GplPrompt;

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

    /// <summary>CLEAR_PIC (window 1): the portrait and the text go.</summary>
    public void Clear()
    {
        Portrait = null;
        _text.Clear();
        Version++;
    }

    public void Ask(GplPrompt prompt)
    {
        Prompt = prompt;
        Version++;
    }

    public void Answered()
    {
        Prompt = null;
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
    /// <summary>TELEPORT's "the party" value.</summary>
    public const int WholeParty = 0x7FFE;

    public GplDialogue Dialogue { get; } = new();

    public IGplWorld? World { get; set; }

    /// <summary>SOUND requests, for the audio layer to play.</summary>
    public Queue<int> Sounds { get; } = new();

    public override int? Execute(GplMachine machine, GplCommand command)
    {
        switch (command.Mnemonic)
        {
            case "PRINT_STRING":
                Dialogue.Append(command.Texts[1] ?? "", command.Values[0] & 0xFF);
                return null;
            case "PRINT_NUMBER":
                Dialogue.Append(command.Values[1].ToString(), command.Values[0] & 0xFF);
                return null;
            case "PRINT_NEWLINE":
                Dialogue.Append("\n", 0);
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

    public override int ChooseMenu(GplMachine machine, string title, IReadOnlyList<GplMenuOption> options) =>
        Wait(machine, new GplMenuPrompt(title, options), out int choice) ? choice : -1;

    /// <summary>
    /// The player's answer if the script is being resumed; otherwise show the
    /// prompt and pause the script.
    /// </summary>
    private bool Wait(GplMachine machine, GplPrompt prompt, out int answer)
    {
        if (machine.TryTakeAnswer(out answer))
        {
            Dialogue.Answered();
            return true;
        }
        Dialogue.Ask(prompt);
        machine.Suspend();
        return false;
    }
}
