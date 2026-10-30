using DarkSun.Data.Images;

namespace DarkSun.Game.Cinematics;

/// <summary>
/// DSUN.EXE's palette fades of a window (<c>0980:4FEB(window, colour)</c> out to
/// colour's DAC value, <c>0980:4C09(source, destination)</c> in from the colour
/// of the destination's top-left pixel). Both first find the colours the window
/// uses, then read each one's DAC value, waiting a retrace before the first and
/// before each whose place in the list (from the last) has bit 4 clear (a fade
/// in also sets each to the start colour as it goes, and then copies the window
/// on); then they step every channel toward its goal, Bresenham-wise so all
/// arrive together, one step a retrace, for as many steps as the largest
/// difference of any channel (in DAC units, 0–63). A retrace is 1/70 s in mode 13h.
/// </summary>
public static class PaletteFades
{
    /// <summary>Vertical retraces a second (VGA mode 13h).</summary>
    public const double RetracesPerSecond = 70;

    /// <summary>The retraces spent reading the DAC before a fade of <paramref name="colours"/> used colours.</summary>
    public static int ReadRetraces(int colours) => 1 + Enumerable.Range(0, colours).Count(i => (i & 16) == 0);

    /// <summary>
    /// The steps (a retrace each) of a fade between the <paramref name="used"/> colours of
    /// <paramref name="palette"/> and <paramref name="target"/>: the largest channel difference, in DAC units.
    /// </summary>
    public static int Steps(Palette palette, IEnumerable<int> used, Rgb target) =>
        used.Where(i => i < palette.Count).Select(i => palette[i])
            .Select(c => Math.Max(Diff(c.R, target.R), Math.Max(Diff(c.G, target.G), Diff(c.B, target.B))))
            .DefaultIfEmpty(0).Max();

    private static int Diff(byte a, byte b) => Math.Abs((a >> 2) - (b >> 2));
}
