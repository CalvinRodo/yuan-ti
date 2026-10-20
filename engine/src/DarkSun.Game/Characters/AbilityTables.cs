namespace DarkSun.Game.Characters;

/// <summary>
/// DSUN.EXE's ability tables (<c>DS:07A8</c>–<c>DS:0843</c>, 26 bytes each, by
/// score 0–25; <c>dsun-exe-reference.md</c> §8, "Ability tables"): AD&amp;D 2nd
/// edition's, with 18 counted without percentile strength. Part of
/// <see cref="CharacterRules"/>, so <c>rules/characters.json</c> can change them;
/// a score past the end reads the last entry.
/// </summary>
public sealed record AbilityTables(
    IReadOnlyList<int> StrDamage, IReadOnlyList<int> StrToHit, IReadOnlyList<int> DexReaction,
    IReadOnlyList<int> DexDefence, IReadOnlyList<int> ConPoison, IReadOnlyList<int> WisDefence)
{
    public static AbilityTables Default { get; } = new(
        [-4, -4, -2, -1, -1, -1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 4, 7, 8, 9, 10, 11, 12, 14],
        [-5, -5, -3, -3, -2, -2, -1, -1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 3, 4, 4, 5, 6, 7],
        [-6, -6, -4, -3, -2, -1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 2, 3, 3, 4, 4, 4, 5, 5],
        [5, 5, 5, 4, 3, 2, 1, 0, 0, 0, 0, 0, 0, 0, 0, -1, -2, -3, -4, -4, -4, -5, -5, -5, -6, -6],
        [-2, -2, -1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4],
        [-6, -6, -4, -3, -2, -1, -1, -1, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4, 4, 4, 4, 4, 4, 4, 4]);

    private static int At(IReadOnlyList<int> table, int score) => table.Count == 0 ? 0 : table[Math.Clamp(score, 0, table.Count - 1)];

    /// <summary>Strength's damage (<c>6868:030D</c>).</summary>
    public int StrDamageOf(int str) => At(StrDamage, str);

    /// <summary>Strength's to-hit (<c>6868:0326</c>).</summary>
    public int StrToHitOf(int str) => At(StrToHit, str);

    /// <summary>Dexterity's reaction: initiative and missile to-hit (<c>6868:033F</c>).</summary>
    public int DexReactionOf(int dex) => At(DexReaction, dex);

    /// <summary>Dexterity's armour class adjustment (<c>6868:0358</c>; lower is better).</summary>
    public int DexDefenceOf(int dex) => At(DexDefence, dex);

    /// <summary>Constitution's save against poison (<c>6868:0371</c>).</summary>
    public int ConPoisonOf(int con) => At(ConPoison, con);

    /// <summary>Wisdom's magical defence: saves against mind spells (<c>6868:038A</c>).</summary>
    public int WisDefenceOf(int wis) => At(WisDefence, wis);
}
