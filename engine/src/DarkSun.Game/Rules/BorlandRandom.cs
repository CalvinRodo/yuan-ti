namespace DarkSun.Game.Rules;

/// <summary>
/// The original game's random number generator: Borland C++ 3.0's
/// <c>rand</c> (DSUN.EXE <c>0000:0822</c>). Keeping it means scripted and
/// rules-driven rolls follow the same distribution as the original.
/// </summary>
public sealed class BorlandRandom
{
    public const int RandMax = 0x7FFF;

    private uint _seed;

    public BorlandRandom(uint seed = 1) => _seed = seed;

    /// <summary>
    /// The game seeds once at start-up with <c>srand((unsigned)time(NULL))</c>,
    /// i.e. the low 16 bits of the time, zero-extended.
    /// </summary>
    public static BorlandRandom FromTime(DateTimeOffset time) =>
        new((ushort)time.ToUnixTimeSeconds());

    /// <summary><c>srand</c>: the seed is the 16-bit argument, zero-extended.</summary>
    public void Seed(ushort seed) => _seed = seed;

    /// <summary><c>rand</c>: 0..<see cref="RandMax"/>.</summary>
    public int Next()
    {
        _seed = unchecked(_seed * 0x015A4E35u + 1);
        return (int)(_seed >> 16) & RandMax;
    }

    /// <summary>
    /// GPL <c>RAND n</c> (<c>gpl_op_52_RAND</c>): 0..n, computed as
    /// <c>(int16)((int32)rand() * (n + 1) / 0x8000)</c>. The original draws
    /// the number before evaluating <c>n</c>; callers that evaluate
    /// expressions with side effects must keep that order.
    /// </summary>
    public short Range(short n) => (short)(Next() * (n + 1) / 0x8000);
}
