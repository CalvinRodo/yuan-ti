using System.Buffers.Binary;
using DarkSun.Data.Gff;

namespace DarkSun.Data.Regions;

/// <summary>
/// A region's <c>RDAT</c> record (<c>RESOURCE.GFF</c>, number = region, 49
/// bytes), which DSUN.EXE loads to <c>4931:025D</c> on entering the region
/// (<c>6BB8:0A41</c>). Known fields: the music, at <c>+0x2F</c>, played on
/// entry (<c>6BB8:030E</c> → <c>35D1:0265(5000, music)</c>); and two
/// colour-cycling ranges (first, count, speed × 10) at <c>+0</c> and <c>+3</c>;
/// and the music level a fight takes (<see cref="FightLevel"/>).
/// </summary>
public sealed record RegionInfo(int Music, (int First, int Count, int Speed) Cycle1, (int First, int Count, int Speed) Cycle2)
{
    public const int Size = 0x31;
    public const int MusicOffset = 0x2F;
    public const int FightCreaturesOffset = 6, FightLevelsOffset = 0x1A, FightLevelOffset = 0x2E;

    /// <summary>Up to 10 creatures (<c>RDFF</c> numbers, words, ended by 0) whose fights have their own music level.</summary>
    public IReadOnlyList<int> FightCreatures { get; init; } = [];

    /// <summary>The levels, one for each of <see cref="FightCreatures"/>.</summary>
    public IReadOnlyList<int> FightLevels { get; init; } = [];

    /// <summary>The level of any other fight.</summary>
    public int DefaultFightLevel { get; init; }

    /// <summary>
    /// The music level when a fight with creature <paramref name="rdff"/>
    /// starts (<c>6BB8:0EFA</c>). DSUN.EXE reads the levels as words
    /// (<c>mov dl, es:[bx+0x277]</c>, bx = 2i), so creature i's level is the
    /// byte at <c>+0x1A + 2i</c>, though the records hold one byte per creature
    /// there: often 0, which leaves the music as it is.
    /// </summary>
    public int FightLevel(int rdff)
    {
        int i = FightCreatures.ToList().IndexOf(rdff);
        return rdff != 0 && i >= 0 ? FightLevels[i] : DefaultFightLevel;
    }

    /// <summary>The record for <paramref name="region"/>, or null if there is none.</summary>
    public static RegionInfo? Read(IResourceSource resources, int region) =>
        resources.TryRead("RDAT", region, out var data) && data.Length >= Size ? Parse(data.Span) : null;

    public static RegionInfo Parse(ReadOnlySpan<byte> data)
    {
        var creatures = new List<int>();
        var levels = new List<int>();
        for (int i = 0; i < 10; i++)
        {
            int rdff = BinaryPrimitives.ReadInt16LittleEndian(data[(FightCreaturesOffset + 2 * i)..]);
            if (rdff == 0)
            {
                break;
            }
            creatures.Add(rdff);
            levels.Add(data[FightLevelsOffset + 2 * i]);
        }
        return new(data[MusicOffset], (data[0], data[1], data[2] / 10), (data[3], data[4], data[5] / 10))
        {
            FightCreatures = creatures,
            FightLevels = levels,
            DefaultFightLevel = data[FightLevelOffset],
        };
    }
}
