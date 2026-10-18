using DarkSun.Data.Gff;

namespace DarkSun.Game.Scripting;

/// <summary>Scripts from <c>GPLDATA.GFF</c> (or any resource source): <c>GPL </c> and <c>MAS </c> resources.</summary>
public sealed class GffScripts(IResourceSource source) : IGplScripts
{
    public byte[]? Load(int file, GplFileKind kind) =>
        source.TryRead(kind == GplFileKind.Mas ? "MAS " : "GPL ", file, out var data) ? data.ToArray() : null;
}

/// <summary>
/// A host with no world: it records every world command, keeps engine
/// variables and object fields in dictionaries, reads 0 for anything unset,
/// walks <see cref="Party"/> for GETPARTY, and answers menus with
/// <see cref="MenuChoice"/>. Enough to run every script end to end, and a
/// base for the real game host.
/// </summary>
public class LoggingGplHost : IGplHost
{
    /// <summary>GETPARTY's "no more members" value.</summary>
    public const int EndOfParty = 9999;

    /// <summary>RANGE's "nothing found" distance; scripts test for it (<c>RANGE(…) != 999</c>).</summary>
    public const int NothingInRange = 999;

    private readonly Dictionary<int, int> _engine = [];
    private readonly Dictionary<string, int> _fields = [];
    private int _menus;

    /// <summary>World commands in the order scripts ran them.</summary>
    public List<(GplEntry Where, GplCommand Command)> Log { get; } = [];

    /// <summary>Party members, as GETPARTY returns them.</summary>
    public List<int> Party { get; } = [];

    /// <summary>
    /// Which option to pick in a MENU (−1: none). By default the enabled
    /// options in turn, one more on each menu, so that dialogue loops
    /// eventually take their way out.
    /// </summary>
    public Func<string, IReadOnlyList<GplMenuOption>, int> MenuChoice { get; set; }

    public LoggingGplHost() => MenuChoice = RotateOptions;

    public string Speaker { get; set; } = "Speaker";

    public virtual int? Execute(GplMachine machine, GplCommand command)
    {
        Log.Add((command.Where, command));
        if (command.Mnemonic == "GETPARTY")
        {
            // The member after the one given; the first after EndOfParty.
            int at = command.Values[0] == EndOfParty ? -1 : Party.IndexOf(command.Values[0]);
            return at + 1 < Party.Count ? Party[at + 1] : EndOfParty;
        }
        return command.Mnemonic == "RANGE" ? NothingInRange : null;
    }

    private int RotateOptions(string title, IReadOnlyList<GplMenuOption> options)
    {
        var enabled = options.Select((o, i) => (o, i)).Where(x => x.o.Enabled).Select(x => x.i).ToList();
        return enabled.Count == 0 ? -1 : enabled[_menus++ % enabled.Count];
    }

    public virtual int ReadEngineVariable(GplMachine machine, int index) => _engine.GetValueOrDefault(index);

    public virtual void WriteEngineVariable(GplMachine machine, int index, int value) => _engine[index] = value;

    public virtual int ReadField(GplMachine machine, GplObjectRef field) => _fields.GetValueOrDefault(Key(field));

    public virtual void WriteField(GplMachine machine, GplObjectRef field, int value) => _fields[Key(field)] = value;

    public virtual string SpeakerName(GplMachine machine) => Speaker;

    public virtual int ChooseMenu(GplMachine machine, string title, IReadOnlyList<GplMenuOption> options) =>
        MenuChoice(title, options);

    private static string Key(GplObjectRef field) => $"{field.Id}:{string.Join('.', field.Path)}";
}
