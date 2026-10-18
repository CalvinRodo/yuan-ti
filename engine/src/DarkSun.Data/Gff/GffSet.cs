namespace DarkSun.Data.Gff;

/// <summary>
/// Several resource sources searched in order, first match wins (port of
/// <c>GffFileList</c> in <c>src/gff.py</c>). Regions, for example, read from
/// the region file, then <c>GPLDATA.GFF</c>, then <c>SEGOBJEX.GFF</c>. A mod
/// folder can be put first to override resources.
/// </summary>
public sealed class GffSet(IEnumerable<IResourceSource> sources) : IResourceSource
{
    private readonly List<IResourceSource> _sources = [.. sources];

    public bool Contains(string tag, int number) => _sources.Any(s => s.Contains(tag, number));

    public bool TryRead(string tag, int number, out ReadOnlyMemory<byte> data)
    {
        foreach (IResourceSource source in _sources)
        {
            if (source.TryRead(tag, number, out data))
            {
                return true;
            }
        }
        data = default;
        return false;
    }

    public ReadOnlyMemory<byte> Read(string tag, int number) =>
        TryRead(tag, number, out ReadOnlyMemory<byte> data)
            ? data
            : throw new KeyNotFoundException($"no resource {GffFile.NormalizeTag(tag)}-{number} in any source");
}
