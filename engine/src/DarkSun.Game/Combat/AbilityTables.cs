namespace DarkSun.Game.Combat;

/// <summary>
/// DSUN.EXE's ability tables (<c>DS:07A8</c>–<c>DS:0843</c>, 26 bytes each, by
/// score 0–25; <c>dsun-exe-reference.md</c> §8, "Ability tables"): AD&amp;D 2nd
/// edition's, with 18 counted without percentile strength.
/// </summary>
public static class AbilityTables
{
    // Scores 0–25; 0 reads as 1.
    private static readonly int[] StrDamageTable = [-4, -4, -2, -1, -1, -1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 4, 7, 8, 9, 10, 11, 12, 14];
    private static readonly int[] StrToHitTable = [-5, -5, -3, -3, -2, -2, -1, -1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 3, 4, 4, 5, 6, 7];
    private static readonly int[] DexReactionTable = [-6, -6, -4, -3, -2, -1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 2, 3, 3, 4, 4, 4, 5, 5];
    private static readonly int[] DexAcTable = [5, 5, 5, 4, 3, 2, 1, 0, 0, 0, 0, 0, 0, 0, 0, -1, -2, -3, -4, -4, -4, -5, -5, -5, -6, -6];
    private static readonly int[] ConPoisonTable = [-2, -2, -1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4];
    private static readonly int[] WisDefenceTable = [-6, -6, -4, -3, -2, -1, -1, -1, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4, 4, 4, 4, 4, 4, 4, 4];

    private static int At(int[] table, int score) => table[Math.Clamp(score, 0, table.Length - 1)];

    /// <summary>Strength's damage (<c>6868:030D</c>).</summary>
    public static int StrDamage(int str) => At(StrDamageTable, str);

    /// <summary>Strength's to-hit (<c>6868:0326</c>).</summary>
    public static int StrToHit(int str) => At(StrToHitTable, str);

    /// <summary>Dexterity's reaction: initiative and missile to-hit (<c>6868:033F</c>).</summary>
    public static int DexReaction(int dex) => At(DexReactionTable, dex);

    /// <summary>Dexterity's armour class adjustment (<c>6868:0358</c>; lower is better).</summary>
    public static int DexAc(int dex) => At(DexAcTable, dex);

    /// <summary>Constitution's save against poison (<c>6868:0371</c>).</summary>
    public static int ConPoison(int con) => At(ConPoisonTable, con);

    /// <summary>Wisdom's magical defence: saves against mind spells (<c>6868:038A</c>).</summary>
    public static int WisDefence(int wis) => At(WisDefenceTable, wis);
}
