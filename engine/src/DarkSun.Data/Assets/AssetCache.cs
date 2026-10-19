using System.Runtime.InteropServices;
using DarkSun.Data.Gff;
using DarkSun.Data.Images;

namespace DarkSun.Data.Assets;

/// <summary>
/// Decoded resources, kept so that each is decoded once per session: image
/// frames and palettes, keyed by the resource's bytes. A resource read
/// through any <see cref="GffFile"/>, <see cref="GffSet"/> or mod overlay is
/// the same slice of the same array each time (mods' files are read once),
/// so it is found again however it was reached. The original files stay the
/// only source; nothing is converted on disk.
/// </summary>
public sealed class AssetCache
{
    private readonly Dictionary<ArraySegment<byte>, IReadOnlyList<ImageFrame>> _frames = [];
    private readonly Dictionary<ArraySegment<byte>, Palette> _palettes = [];
    private readonly Lock _lock = new();

    public int FrameSets => _frames.Count;

    public int Palettes => _palettes.Count;

    /// <summary>Decoded frames of an image resource's bytes.</summary>
    /// <exception cref="InvalidDataException">The image can't be decoded (not cached).</exception>
    public IReadOnlyList<ImageFrame> Frames(ReadOnlyMemory<byte> data) =>
        Get(_frames, data, d => ImageDecoder.ReadFrames(d.Span));

    /// <summary>Decoded frames of a resource, or null if <paramref name="source"/> has none.</summary>
    public IReadOnlyList<ImageFrame>? Frames(IResourceSource source, string tag, int number) =>
        source.TryRead(tag, number, out var data) ? Frames(data) : null;

    /// <summary>The first frame of a resource, or null if there is none.</summary>
    public ImageFrame? Frame(IResourceSource source, string tag, int number) =>
        Frames(source, tag, number) is [var first, ..] ? first : null;

    /// <summary>A palette from its resource bytes; the same bytes give the same instance.</summary>
    public Palette Palette(ReadOnlyMemory<byte> data) => Get(_palettes, data, d => Images.Palette.FromBytes(d.Span));

    public Palette? Palette(IResourceSource source, string tag, int number) =>
        source.TryRead(tag, number, out var data) ? Palette(data) : null;

    public void Clear()
    {
        lock (_lock)
        {
            _frames.Clear();
            _palettes.Clear();
        }
    }

    private T Get<T>(Dictionary<ArraySegment<byte>, T> cache, ReadOnlyMemory<byte> data, Func<ReadOnlyMemory<byte>, T> decode)
    {
        if (!MemoryMarshal.TryGetArray(data, out ArraySegment<byte> key))
        {
            return decode(data);
        }
        lock (_lock)
        {
            if (cache.TryGetValue(key, out T? found))
            {
                return found;
            }
        }
        T value = decode(data);
        lock (_lock)
        {
            cache[key] = value;
        }
        return value;
    }
}
