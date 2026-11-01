namespace DarkSun.Game.Scripting;

/// <summary>
/// A repair to a shipped script: at <paramref name="Offset"/> in GPL
/// <paramref name="File"/>, the bytes <paramref name="Expect"/> become
/// <paramref name="Replace"/>. A script whose bytes differ (another release, a
/// mod) is left alone. A longer <paramref name="Replace"/> is a detour: it
/// goes after the end of the file, followed by a JUMP back, and
/// <paramref name="Expect"/>'s place becomes a JUMP to it (the rest EXIT_GPL,
/// never reached), so no address in the file moves.
/// </summary>
public sealed record GplRepair(string Id, int File, int Offset, byte[] Expect, byte[] Replace, bool OnByDefault, string What)
{
    /// <summary>Where in a detour's <see cref="GplRepair.Replace"/> an address (a big-endian word) counts from the detour's start, as an IF's or ELSE's in it does.</summary>
    public int[] Relocate { get; init; } = [];
}

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
        // The slave pens' halfling (GPL 31) reads the race from the combat record, which has none (0): a halfling
        // in the party is never greeted as one, and the insult names no race. The race is the sheet's, .71.20.
        new("script-repairs.halfling-greeting", 31, 0x3B, Hex("18b180250114d78f06"), Hex("18b18025024714d78f06"), true,
            "the halfling greets a halfling in the party as one of his own (GPL 31 @3B: .71.20, the race)"),
        new("script-repairs.halfling-insult", 31, 0x254, Hex("17b180250114"), Hex("17b18025024714"), true,
            "the halfling's insult names the speaker's race (GPL 31 @254: .71.20)"),
        // Region 8's bodies: the ranger tracking roll (GPL 210 @5F8) can never succeed. The ranger test reads the
        // usable item classes from the combat record (.17, not .71.17); the level bonus tests class codes as bit
        // masks (a ranger's code is 13–16); and the roll is the wrong way round (Wisdom + 8 and more < RAND(20)).
        new("script-repairs.tracking-ranger", 210, 0x5FD, Hex("18e28c20b1802601110200e1"), Hex("18e28c20b180260247110200e1"), true,
            "a ranger can try to read the tracks by region 8's bodies (GPL 210 @5FD: .71.17, the classes)"),
        .. new[] { (0x60C, 0x23), (0x622, 0x24), (0x638, 0x25) }.Select((t, i) => new GplRepair($"script-repairs.tracking-level-{i + 1}", 210, t.Item1,
            Hex($"18e28c20b180260247{t.Item2:x2}0200e1"), Hex($"18e2b180260247{t.Item2:x2}d98f0ce1d5e2b180260247{t.Item2:x2}da8f11e1"), true,
            $"the tracking roll's ranger level bonus counts class {i + 1} if it is a ranger's, 13–16 (GPL 210 @{t.Item1:X}: .71.{t.Item2} > 12 && < 17)")),
        new("script-repairs.tracking-roll", 210, 0x6CC, Hex("188204dae28c528f14e1"), Hex("18e28c528f14e1da8204"), true,
            "the tracking roll succeeds under the ranger's score, not over it (GPL 210 @6CC: RAND(20) < lnum[4])"),
        // The slave pens' sewer grate (GPL 137): the search for a weapon in hand stops at once (lflag[3] == 1 where
        // it was just set to 0), so "hack the lock off" is never offered; and a hack that works never opens it.
        new("script-repairs.grate-hack-offered", 137, 0xA65, Hex("18e28c4989258200e1d5e28e03d78f01e1"), Hex("18e28c4989258200e1d5e28e03d78f00e1"), true,
            "the sewer grate offers \"hack the lock off\" to a member with a weapon in hand (GPL 137 @A65)"),
        new("script-repairs.grate-hack-his-her", 137, 0x9E0,
            Hex("4f0062920541cfae3c7979f3cdd766cf283461c7af9a0e9a32a0d9bf1eb41bf36641df4f4d083469e68018"),
            // " successfully hacks the lock off with "; IF .71.21 == 1 "his " ELSE "her " ENDIF
            Hex("4f0062920541cfae3c7979f3cdd766cf283461c7af9a0e9a32a0d9bf1eb41bf36641df4f4d080180"
                + "18b18025024715d78f013e003f4f00629205d1a79a00603f004c4f00629205d19792006067"), true,
            "a hack on the sewer grate says his or her as picking it does (GPL 137 @9E0)") { Relocate = [51, 64] },
        new("script-repairs.grate-hack-opens", 137, 0xA14, Hex("4f006292055c8018"), Hex("4f006292055c80184f00628100168f01cd01f9"), true,
            "a hack that takes the sewer grate's lock off opens it, as picking it does (GPL 137 @A14: \"The grate opens.\", gflag[505])"),
        // An attack on the grate tests .43 < 220, which the grate (an item, no hit points) reads as 0: the lock always
        // breaks. A Strength roll instead, as ripping it open has (@AA6: RAND(30) < Str).
        new("script-repairs.grate-attack-roll", 137, 0xB5C, Hex("18b18027012bda00dc"), Hex("18e28c528f1ee1dab180250117"), true,
            "an attack on the sewer grate breaks its lock on a Strength roll, as ripping it open does (GPL 137 @B5C: RAND(30) < Str)"),
        // The Rebel Mindhome (GPL 204): with the spiders gone, every talk to their leader runs the thanks again and
        // its 5000 XP award; nothing reads gflag[695], which the first one sets. The award only while it is 0.
        new("script-repairs.mindhome-reward-once", 204, 0x5C9, Hex("1613888710140087004a"),
            Hex("18cd02b7d78f003e0014" + "1613888710140087004a" + "67"), true,
            "the Rebel Mindhome's 5000 XP for the spiders is given once, not at every talk (GPL 204 @5C9: gflag[695] == 0)") { Relocate = [8] },
        // Region 9: MAS 9 registers the messenger's sight trigger (−1297, GPL 133) only if the elves have left
        // (gflag[245]) when the region loads; they leave during a stay (GPL 58), so without a second visit he never
        // comes. Where GPL 58 sets the flag, the trigger as MAS 9 registers it (while gflag[339] == 0).
        .. new[] { 0x1A, 0x99, 0xCC }.Select((at, i) => new GplRepair($"script-repairs.messenger-trigger-{i + 1}", 58, at, Hex("168f018df5"),
            Hex("168f018df5" + "18cd0153d78f00" + "3e0019" + "1b000100859105118f0f" + "67"), true,
            $"the messenger in region 9 comes once the elves have left, without leaving the region first (GPL 58 @{at:X}: MAS 9's sight trigger on −1297)") { Relocate = [13] }),
        // Region 38's exit to region 10 (GPL 60 @CD1) takes the following serf along only within 14 cells; one
        // further behind stays, without his orders, and talking to him again runs the whole deal and gives a second
        // obelisk gem (−1352), while MAS 10 brings a copy of him along anyway. Any distance; off by default.
        new("script-repairs.serf-comes-along", 60, 0xCD8, Hex("18e28c808925910103e1da8f0e"), Hex("18e28c808925910103e1da8f7f"), false,
            "the Draj serf comes along to region 10 however far behind, so he can't give a second obelisk gem (GPL 60 @CD8: range < 127)"),
        // The slave pens (region 41): the party can kill Legcrusher (−326), and Kurzak goes on calling for him,
        // greeting them as if he were about, and Dinos warns of him. Each line first asks if he is still on the map
        // (RANGE from the active member is 999 once he's dead or gone, as GPL 139 @275 asks it of −182).
        WithoutLegcrusher(1, 136, 0x130, ["Legcrusher! Get gladiators!"],
            ["Legcrusher! ... Legcrusher? Bah! Gladiators, get up here before I come down and drag you up!"],
            "Kurzak's call to the arena (GPL 136 @130)"),
        WithoutLegcrusher(2, 141, 0x6B4, ["Good thing you got here. I thought I was going to have to send out ", "Legcrusher. "],
            ["Good thing you got here. With Legcrusher gone, I'd have had to come down and fetch you myself. "],
            "Kurzak's greeting (GPL 141 @6B4)"),
        WithoutLegcrusher(3, 141, 0x70B, ["I had to send Legcrusher after you. It's good that you found me before ", "I found you. "],
            ["I sent Legcrusher after you, and he never came back. I don't suppose you'd know why. "],
            "Kurzak's greeting once he sent Legcrusher (GPL 141 @70B)"),
        WithoutLegcrusher(4, 141, 0x766, ["I'm glad you listened to Legcrusher. I hate it when we lose gladiators ", "that way. "],
            ["So Legcrusher found you. Then why didn't he come back with you? "],
            "Kurzak's greeting once Legcrusher found them (GPL 141 @766)"),
        WithoutLegcrusher(5, 141, 0xF29, ["Oh, that's the monster trainer. His name is Legcrusher. ", "He's not too bright, but he's good with monsters. "],
            ["That was Legcrusher, the monster trainer. Nobody has seen him lately. ", "He's not too bright, but he's good with monsters. "],
            "Kurzak's answer to \"Who is the half-giant?\" (GPL 141 @F29)"),
        WithoutLegcrusher(6, 139, 0x218, ["Kurzak's looking for you. You better go fight before he sends ", "out Legcrusher. "],
            ["Kurzak's looking for you. You better go fight before he comes down here himself. "],
            "Dinos's warning (GPL 139 @218)"),
        // The final battle: the stage gate (GPL 62 @4345) exits without re-arming inside its window. With the
        // game clock running the gate is polled again and waits its window out, so this is off by default.
        new("script-repairs.final-battle-gate", 62, 0x10F1, Hex("10f9"), Hex("00ef"), false,
            "the final battle's next stage starts at once (GPL 62 @239), without the pause between stages"),
    ];

    /// <summary>A detour written as GPL source: <paramref name="original"/>'s bytes become <paramref name="detour"/>'s, its labels relocated.</summary>
    private static GplRepair Detour(string id, int file, int offset, string original, string detour, bool onByDefault, string what) =>
        new(id, file, offset, GplAssembler.Assemble(original), GplAssembler.AssembleMovable(detour, out int[] labels), onByDefault, what) { Relocate = labels };

    /// <summary>The game's lines <paramref name="said"/> while Legcrusher is on the map, else <paramref name="without"/>.</summary>
    private static GplRepair WithoutLegcrusher(int n, int file, int offset, string[] said, string[] without, string where)
    {
        static string Say(string[] lines) => string.Join('\n', lines.Select(l => $"PRINT_STRING(115, \"{l}\")"));
        return Detour($"script-repairs.legcrusher-gone-{n}", file, offset, Say(said),
            $"ACCUM <= (((RANGE(gname[5], name(-326))))) < 999\nIF(@gone)\n{Say(said)}\ngone:\nELSE(@done)\n{Say(without)}\ndone:\nENDIF", true,
            $"{where} stops speaking of Legcrusher as if he were about once he's dead or gone");
    }

    /// <summary><paramref name="code"/> (GPL <paramref name="file"/>) with the <paramref name="repairs"/> that fit it applied.</summary>
    public static byte[] Apply(int file, byte[] code, IEnumerable<GplRepair> repairs)
    {
        byte[]? patched = null;
        List<byte>? detours = null;
        foreach (GplRepair r in repairs.Where(r => r.File == file))
        {
            if (r.Offset + r.Expect.Length > code.Length || !code.AsSpan(r.Offset, r.Expect.Length).SequenceEqual(r.Expect)
                || r.Replace.Length < r.Expect.Length || r.Replace.Length > r.Expect.Length && r.Expect.Length < 3)
            {
                continue;
            }
            patched ??= (byte[])code.Clone();
            if (r.Replace.Length == r.Expect.Length)
            {
                r.Replace.CopyTo(patched, r.Offset);
                continue;
            }
            detours ??= [GplOpcodes.ExitGpl]; // a script that ran off its end still stops there
            int at = code.Length + detours.Count;
            byte[] detour = [.. r.Replace, .. Jump(r.Offset + r.Expect.Length)];
            foreach (int i in r.Relocate)
            {
                int address = (detour[i] << 8 | detour[i + 1]) + at;
                (detour[i], detour[i + 1]) = ((byte)(address >> 8), (byte)address);
            }
            detours.AddRange(detour);
            patched.AsSpan(r.Offset, r.Expect.Length).Fill(GplOpcodes.ExitGpl);
            Jump(at).CopyTo(patched, r.Offset);
        }
        return detours is null ? patched ?? code : [.. patched!, .. detours];
    }

    /// <summary>JUMP to <paramref name="address"/> (a 15-bit number).</summary>
    private static byte[] Jump(int address) => [GplOpcodes.Jump, (byte)(address >> 8), (byte)address];
}

/// <summary>Scripts with <see cref="GplRepairs"/> applied to the <c>GPL</c> files as they load.</summary>
public sealed class RepairedScripts(IGplScripts inner, IReadOnlyList<GplRepair> repairs) : IGplScripts
{
    public byte[]? Load(int file, GplFileKind kind) =>
        inner.Load(file, kind) is { } code ? (kind == GplFileKind.Gpl ? GplRepairs.Apply(file, code, repairs) : code) : null;
}
