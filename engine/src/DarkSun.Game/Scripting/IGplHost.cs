namespace DarkSun.Game.Scripting;

/// <summary>A world command a script ran, with its evaluated operands.</summary>
/// <param name="Values">Operand values, in order (strings evaluate to 0).</param>
/// <param name="Texts">For each operand, its text if it was a string, else null.</param>
/// <param name="Locations">For each operand, the last variable or field it read (DSUN.EXE's lvalue pointer), else null.</param>
public sealed record GplCommand(
    GplInstruction Instruction, GplEntry Where, IReadOnlyList<int> Values, IReadOnlyList<string?> Texts,
    IReadOnlyList<GplLocation?> Locations)
{
    public byte Op => Instruction.Op;

    public string Mnemonic => Instruction.Mnemonic;

    public override string ToString()
    {
        IEnumerable<string> args = Values.Select((v, i) => Texts[i] is { } t ? $"\"{t}\"" : v.ToString());
        return Values.Count == 0 ? Mnemonic : $"{Mnemonic}({string.Join(", ", args)})";
    }
}

/// <summary>A MENU option as the script offered it.</summary>
public sealed record GplMenuOption(string Text, int Address, bool Enabled);

/// <summary>
/// What the game world provides to scripts. Control flow, variables, the
/// accumulator, RAND and the triggers are handled by <see cref="GplMachine"/>;
/// everything that touches the world comes here.
/// </summary>
public interface IGplHost
{
    /// <summary>
    /// Run a world command (dialogue, movement, items, combat, …). Return a
    /// value to put in the accumulator, or null to leave it.
    /// </summary>
    int? Execute(GplMachine machine, GplCommand command);

    /// <summary>Engine variable <c>gname</c> (raw index 0x20–0x2E; 0x29–0x2A are 32-bit).</summary>
    int ReadEngineVariable(GplMachine machine, int index);

    void WriteEngineVariable(GplMachine machine, int index, int value);

    /// <summary>An object field (<c>obj(id).f.g</c>, or a special object when the id has its top bit set).</summary>
    int ReadField(GplMachine machine, GplObjectRef field);

    void WriteField(GplMachine machine, GplObjectRef field, int value);

    /// <summary>The name shown for a speaker-name string (kind 1).</summary>
    string SpeakerName(GplMachine machine);

    /// <summary>MENU: the chosen option's index, or −1 for none.</summary>
    int ChooseMenu(GplMachine machine, string title, IReadOnlyList<GplMenuOption> options);

    /// <summary>The id object triggers match for an object (DSUN.EXE: object field 0x48).</summary>
    int TriggerId(GplMachine machine, int obj) => obj;
}

/// <summary>Where scripts come from: <c>GPL </c> and <c>MAS </c> resources by number.</summary>
public interface IGplScripts
{
    byte[]? Load(int file, GplFileKind kind);
}
