// Usage: DarkSun.CineDump <game-dir> <cinematic> <out-dir> [<seconds-between-snapshots>]
//
// Plays cinematic 0 (intro), 1 (arrival) or 100 (ending) from CINE.GFF as
// fast as it can, printing when each ACF script starts and writing the
// screen as a PPM image every so often (default: every second).

using System.Globalization;
using DarkSun.Data.Gff;
using DarkSun.Game.Cinematics;

if (args.Length is < 3 or > 4)
{
    Console.Error.WriteLine("usage: DarkSun.CineDump <game-dir> <cinematic> <out-dir> [<seconds-between-snapshots>]");
    return 2;
}
GffFile cine = GffFile.Open(Path.Combine(args[0], "CINE.GFF"));
int number = int.Parse(args[1], CultureInfo.InvariantCulture);
string outDir = args[2];
double every = args.Length > 3 ? double.Parse(args[3], CultureInfo.InvariantCulture) : 1.0;
Directory.CreateDirectory(outDir);

var player = new CinematicPlayer(cine);
player.MusicStarted += n => Console.WriteLine($"{player.Seconds,7:F2}s  music GSEQ {n}");
player.PlayCinematic(number);
int script = -1;
double nextShot = 0;
int shots = 0;
while (!player.Finished && player.Seconds < 600)
{
    player.Step();
    if (player.CurrentScript != script && player.CurrentScript != 0)
    {
        script = player.CurrentScript;
        Console.WriteLine($"{player.Seconds,7:F2}s  ACF {script}");
    }
    if (player.Seconds >= nextShot)
    {
        nextShot += every;
        string path = Path.Combine(outDir, $"{shots++:D4}_{player.Seconds:F1}s_acf{player.CurrentScript}.ppm");
        WritePpm(path, player.Screen, player.Palette);
    }
}
Console.WriteLine($"{player.Seconds,7:F2}s  end; {shots} snapshots"
    + (player.UnsupportedOps.Count > 0 ? $"; unsupported ops {string.Join(", ", player.UnsupportedOps.Select(o => $"0x{o:x2}"))}" : ""));
return 0;

static void WritePpm(string path, byte[] screen, byte[] palette)
{
    using var file = File.Create(path);
    file.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{CinematicPlayer.Width} {CinematicPlayer.Height}\n255\n"));
    var rgb = new byte[screen.Length * 3];
    for (int i = 0; i < screen.Length; i++)
    {
        for (int c = 0; c < 3; c++)
        {
            int v = palette[screen[i] * 3 + c];
            rgb[i * 3 + c] = (byte)(v << 2 | v >> 4);
        }
    }
    file.Write(rgb);
}
