// Usage: DarkSun.MusicRender <game-dir> <file.gff> <number> <out.wav> [<seconds>] [<soundfont.sf2>]
//
// Renders GSEQ <number> from <file.gff> (RESOURCE.GFF: 1-23, CINE.GFF: 26-29)
// to a 16-bit stereo WAV file, 60 seconds by default, and prints its peak and
// RMS levels. The SoundFont is found as the engine finds it (DARKSUN_SOUNDFONT,
// then the system's) unless one is given.

using System.Globalization;
using DarkSun.Data.Gff;
using DarkSun.Game.Audio;
using MeltySynth;

if (args.Length is < 4 or > 6)
{
    Console.Error.WriteLine("usage: DarkSun.MusicRender <game-dir> <file.gff> <number> <out.wav> [<seconds>] [<soundfont.sf2>]");
    return 2;
}
GffFile gff = GffFile.Open(Path.Combine(args[0], args[1]));
int number = int.Parse(args[2], CultureInfo.InvariantCulture);
double seconds = args.Length > 4 ? double.Parse(args[4], CultureInfo.InvariantCulture) : 60;
string? soundFont = args.Length > 5 ? args[5] : SoundFontLocator.Find();
if (soundFont is null)
{
    Console.Error.WriteLine("no SoundFont found; set DARKSUN_SOUNDFONT or pass one");
    return 1;
}
if (!gff.TryRead("GSEQ", number, out var xmi))
{
    Console.Error.WriteLine($"no GSEQ {number} in {args[1]}");
    return 1;
}

const int rate = 44100;
var player = new MusicPlayer(new SoundFont(soundFont), rate);
player.Play(xmi.ToArray());
int total = (int)(seconds * rate);
var left = new float[total];
var right = new float[total];
for (int at = 0; at < total; at += 1024)
{
    int n = Math.Min(1024, total - at);
    player.Render(left.AsSpan(at, n), right.AsSpan(at, n));
}

using (var file = new BinaryWriter(File.Create(args[3])))
{
    file.Write("RIFF"u8);
    file.Write(36 + total * 4);
    file.Write("WAVEfmt "u8);
    file.Write(16);
    file.Write((short)1);
    file.Write((short)2);
    file.Write(rate);
    file.Write(rate * 4);
    file.Write((short)4);
    file.Write((short)16);
    file.Write("data"u8);
    file.Write(total * 4);
    for (int i = 0; i < total; i++)
    {
        file.Write((short)Math.Clamp(left[i] * 32767, -32768, 32767));
        file.Write((short)Math.Clamp(right[i] * 32767, -32768, 32767));
    }
}
double peak = 0, sum = 0;
int lastSound = 0;
for (int i = 0; i < total; i++)
{
    double v = Math.Max(Math.Abs(left[i]), Math.Abs(right[i]));
    peak = Math.Max(peak, v);
    sum += left[i] * left[i];
    if (v > 0.001)
    {
        lastSound = i;
    }
}
Console.WriteLine($"{args[1]} GSEQ {number} with {Path.GetFileName(soundFont)}: {seconds:F0} s, peak {peak:F2}, "
    + $"RMS {Math.Sqrt(sum / total):F3}, sound until {(double)lastSound / rate:F1} s, still playing: {player.IsPlaying}");
return 0;
