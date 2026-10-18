using DarkSun.Data.Gff;

namespace DarkSun.Data.Regions;

/// <summary>
/// A region's <c>RDAT</c> record (<c>RESOURCE.GFF</c>, number = region, 49
/// bytes), which DSUN.EXE loads to <c>4931:025D</c> on entering the region
/// (<c>6BB8:0A41</c>). Known fields: the music, at <c>+0x2F</c>, played on
/// entry (<c>6BB8:030E</c> → <c>35D1:0265(5000, music)</c>); and two
/// colour-cycling ranges (first, count, speed × 10) at <c>+0</c> and <c>+3</c>.
/// </summary>
public sealed record RegionInfo(int Music, (int First, int Count, int Speed) Cycle1, (int First, int Count, int Speed) Cycle2)
{
    public const int Size = 0x31;
    public const int MusicOffset = 0x2F;

    /// <summary>The record for <paramref name="region"/>, or null if there is none.</summary>
    public static RegionInfo? Read(IResourceSource resources, int region) =>
        resources.TryRead("RDAT", region, out var data) && data.Length >= Size ? Parse(data.Span) : null;

    public static RegionInfo Parse(ReadOnlySpan<byte> data) =>
        new(data[MusicOffset], (data[0], data[1], data[2] / 10), (data[3], data[4], data[5] / 10));
}
