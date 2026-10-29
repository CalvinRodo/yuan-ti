namespace DarkSun.Game.Audio;

/// <summary>What DSUN.EXE knows of each music track besides its sequence.</summary>
public static class MusicTracks
{
    // DS:1675 + 3n, the first of track n's three bytes: its highest level.
    // Track 8 has seven sections but allows six.
    private static readonly byte[] HighestLevel =
    [
        4, 4, 4, 6, 2, 8, 2, 1, 6, 5, 5, 6, 3, 8, 8, 5,
        6, 1, 6, 6, 6, 4, 2, 3, 1, 1, 1, 1, 1, 1, 1,
    ];

    /// <summary>
    /// The highest music level track <paramref name="track"/> takes
    /// (<c>361A:0000</c> ignores higher ones): its number of sections, 1 for a
    /// track without; 1 for a track DSUN.EXE doesn't have.
    /// </summary>
    public static int Levels(int track) => track >= 0 && track < HighestLevel.Length ? HighestLevel[track] : 1;
}
