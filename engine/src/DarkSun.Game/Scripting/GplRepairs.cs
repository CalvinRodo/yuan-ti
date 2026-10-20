namespace DarkSun.Game.Scripting;

/// <summary>
/// A repair to a shipped script: at <paramref name="Offset"/> in GPL
/// <paramref name="File"/>, the bytes <paramref name="Expect"/> become
/// <paramref name="Replace"/> (the same length). A script whose bytes differ
/// (another release, a mod) is left alone.
/// </summary>
public sealed record GplRepair(string Id, int File, int Offset, byte[] Expect, byte[] Replace, bool OnByDefault, string What);

/// <summary>
/// Script bugs in the shipped <c>GPLDATA.GFF</c> (GOG 1.10) and their repairs,
/// as the OpenDS project's darkfix found and published them
/// (<c>fix.ds1.deadtriggers</c>, <c>fix.ds1.script-repairs</c>): each re-points
/// one trigger or call to the handler it was meant to have. Checked here
/// against the shipped file (each lands on its instruction).
/// </summary>
public static class GplRepairs
{
    private static byte[] Hex(string s) => Convert.FromHexString(s);

    public static IReadOnlyList<GplRepair> All { get; } =
    [
        // Darkhold (regions 30, 31): story beats re-register these objects' triggers to GPL 200 @0x909, a lone EXIT_GPL.
        new("deadtriggers.wyvern-look", 41, 0x1D, Hex("66090900c8"), Hex("6600010029"), true,
            "the wyvern scene's creature (−2248) keeps its look text (GPL 41 entry 1)"),
        new("deadtriggers.portcullis-guard", 195, 0x2B, Hex("650909"), Hex("65033b"), true,
            "the portcullis guard (−255) still fights when attacked (GPL 200 @0x33B)"),
        new("deadtriggers.chamber-look-2263", 203, 0x3FE, Hex("66090900c8"), Hex("66038900cb"), true,
            "the queen's chamber creature −2263 keeps its look text (GPL 203 @0x389)"),
        new("deadtriggers.chamber-look-1209", 203, 0x406, Hex("66090900c8"), Hex("66038900cb"), true,
            "the queen's chamber creature −1209 keeps its look text (GPL 203 @0x389)"),
        // Lava Rifts: the ranger's re-arm points at the hermit's handler (endless hermit talk, a second Iron Necklace).
        new("script-repairs.ranger-trigger", 100, 0x684, Hex("1b00560064"), Hex("1b01bd0064"), true,
            "the ranger (−21) keeps his own sight trigger (GPL 100 @445), not the hermit's"),
        // Gedron: after the extract is delivered, Linara's talk dead-ends with no menu.
        new("script-repairs.linara-menu", 68, 0xAA, Hex("13134e"), Hex("1300d1"), true,
            "Linara's talk goes on to her menu (GPL 68 @209) after the extract, so Jasmine's spellbook can come up"),
        // The final battle: the stage gate (GPL 62 @4345) exits without re-arming inside its window. With the
        // game clock running the gate is polled again and waits its window out, so this is off by default.
        new("script-repairs.final-battle-gate", 62, 0x10F1, Hex("10f9"), Hex("00ef"), false,
            "the final battle's next stage starts at once (GPL 62 @239), without the pause between stages"),
    ];

    /// <summary><paramref name="code"/> (GPL <paramref name="file"/>) with the <paramref name="repairs"/> that fit it applied.</summary>
    public static byte[] Apply(int file, byte[] code, IEnumerable<GplRepair> repairs)
    {
        byte[]? patched = null;
        foreach (GplRepair r in repairs.Where(r => r.File == file))
        {
            if (r.Offset + r.Expect.Length <= code.Length && code.AsSpan(r.Offset, r.Expect.Length).SequenceEqual(r.Expect))
            {
                patched ??= (byte[])code.Clone();
                r.Replace.CopyTo(patched, r.Offset);
            }
        }
        return patched ?? code;
    }
}

/// <summary>Scripts with <see cref="GplRepairs"/> applied to the <c>GPL</c> files as they load.</summary>
public sealed class RepairedScripts(IGplScripts inner, IReadOnlyList<GplRepair> repairs) : IGplScripts
{
    public byte[]? Load(int file, GplFileKind kind) =>
        inner.Load(file, kind) is { } code ? (kind == GplFileKind.Gpl ? GplRepairs.Apply(file, code, repairs) : code) : null;
}
