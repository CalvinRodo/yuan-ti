using System.Buffers.Binary;
using DarkSun.Data.Gff;
using DarkSun.Data.Images;

namespace DarkSun.Data.Regions;

/// <summary>A sprite at a pixel position on the region map (top-left corner).</summary>
public sealed record PlacedSprite(int X, int Y, ImageFrame Frame, bool Mirrored = false);

/// <summary>
/// An <c>ETAB</c> object: drawn with its frames' top-left corner at
/// (<paramref name="X"/>, <paramref name="Y"/>), which is how DSUN.EXE places
/// objects until something moves them.
/// </summary>
/// <param name="EtabX">The <c>ETAB</c> position, which DSUN.EXE also uses to depth-sort objects.</param>
/// <param name="Frames">All frames of the object's <c>BMP </c>; static objects show frame 0.</param>
/// <param name="Scmd">The <c>SCMD</c> animation number (<c>OJFF</c> +0x0E), or 0.</param>
public sealed record RegionObject(int EtabIndex, int Ojff, int EtabX, int EtabY, int X, int Y, bool Mirrored,
    IReadOnlyList<ImageFrame> Frames, int Scmd)
{
    public ImageFrame Frame => Frames[0];
}

/// <summary>
/// One explorable region (<c>RGNxx.GFF</c>), resolved to what is drawn and
/// where. Port of <c>src/region_renderer.py</c> (from John Glassmyer's
/// RegionTool.java). Layers, back to front: ground tiles (<c>RMAP</c> →
/// <c>TILE</c>), objects (<c>ETAB</c> → <c>OJFF</c> → <c>BMP </c>), and walls
/// (<c>GMAP</c> wall index → <c>WALL</c>).
/// </summary>
public sealed class Region
{
    public const int TileSize = 16;
    public const int DefaultWidth = 128;
    public const int WallIndexMask = 0x1F;

    private Region(int number, int width, int height, byte[] tileMap, byte[] flags,
        Dictionary<int, ImageFrame> tiles, List<RegionObject> objects, List<PlacedSprite> walls)
    {
        Number = number;
        Width = width;
        Height = height;
        TileMap = tileMap;
        Flags = flags;
        Tiles = tiles;
        Objects = objects;
        Walls = walls;
    }

    /// <summary>Region number: the <c>GMAP</c> resource number.</summary>
    public int Number { get; }

    /// <summary>Size in tiles.</summary>
    public int Width { get; }
    public int Height { get; }

    /// <summary><c>RMAP</c>: tile number per cell, row-major.</summary>
    public byte[] TileMap { get; }

    /// <summary><c>GMAP</c>: one <see cref="CellFlags"/> byte per cell, row-major.</summary>
    public byte[] Flags { get; }

    /// <summary>Ground tile images by tile number.</summary>
    public IReadOnlyDictionary<int, ImageFrame> Tiles { get; }

    /// <summary><c>ETAB</c> objects, in table order (drawn in this order).</summary>
    public IReadOnlyList<RegionObject> Objects { get; }

    /// <summary>Walls, row-major (drawn after the objects).</summary>
    public IReadOnlyList<PlacedSprite> Walls { get; }

    /// <summary>
    /// Load a region. <paramref name="auxiliary"/> files are searched after the
    /// region file for <c>OJFF</c>, <c>BMP </c> and <c>WALL</c> resources:
    /// <c>SEGOBJEX.GFF</c>, then <c>GPLDATA.GFF</c>.
    /// </summary>
    public static Region Load(GffFile regionFile, IReadOnlyList<GffFile> auxiliary)
    {
        List<GffFile> files = [regionFile, .. auxiliary];
        var all = new GffSet(files);

        ResourceDescriptor gmap = regionFile.DescribeResources().FirstOrDefault(r => r.Tag == "GMAP");
        if (gmap.Tag is null)
        {
            throw new InvalidDataException("region file has no GMAP resource");
        }
        int number = gmap.Number;
        byte[] flags = regionFile.Read("GMAP", number).ToArray();

        byte[] tileMap = (all.TryRead("RMAP", number, out var rmap) || all.TryRead("MAP ", number, out rmap))
            ? rmap.ToArray()
            : throw new InvalidDataException($"region {number} has no RMAP");
        int width = DefaultWidth;
        int height = (tileMap.Length + width - 1) / width;

        return new Region(number, width, height, tileMap, flags,
            LoadTiles(files), LoadObjects(all, number), LoadWalls(all, number, flags, width, height));
    }

    public CellFlags FlagsAt(int x, int y) =>
        x >= 0 && y >= 0 && x < Width && y < Height && y * Width + x < Flags.Length
            ? (CellFlags)Flags[y * Width + x]
            : CellFlags.None;

    /// <summary>Whether a cell blocks movement; cells off the map do, as in DSUN.EXE.</summary>
    public bool IsBlocked(int x, int y) =>
        x < 0 || y < 0 || x >= Width || y >= Height || FlagsAt(x, y).HasFlag(CellFlags.Blocked);

    /// <summary>Whether a cell blocks line of sight; cells off the map don't.</summary>
    public bool BlocksSight(int x, int y) => FlagsAt(x, y).HasFlag(CellFlags.BlocksSight);

    /// <summary>
    /// The region's palette: the <c>GPLDATA.GFF</c> <c>PAL </c> with the
    /// region's number. Region 255 (<c>RGNFF.GFF</c>) has none; like
    /// <c>precompute.py</c>, fall back to the first <c>PAL </c> in
    /// <c>RESOURCE.GFF</c> (*unverified*: which palette the game uses there).
    /// </summary>
    public Palette LoadPalette(GffFile gplData, GffFile resource)
    {
        if (gplData.TryRead("PAL", Number, out var data))
        {
            return Palette.FromBytes(data.Span);
        }
        ResourceDescriptor first = resource.DescribeResources().First(r => r.Tag == "PAL ");
        return Palette.FromBytes(resource.Read(first.Tag, first.Number).Span);
    }

    /// <summary>First frame of every <c>TILE</c> in the files, first file wins.</summary>
    private static Dictionary<int, ImageFrame> LoadTiles(List<GffFile> files)
    {
        var tiles = new Dictionary<int, ImageFrame>();
        foreach (GffFile file in files)
        {
            foreach (ResourceDescriptor r in file.DescribeResources().Where(r => r.Tag == "TILE"))
            {
                if (tiles.ContainsKey(r.Number))
                {
                    continue;
                }
                try
                {
                    var frames = ImageDecoder.ReadFrames(file.Read(r.Tag, r.Number).Span);
                    if (frames.Count > 0)
                    {
                        tiles[r.Number] = frames[0];
                    }
                }
                catch (InvalidDataException)
                {
                    // A tile that can't be decoded is left out, as in region_renderer.py.
                }
            }
        }
        return tiles;
    }

    /// <summary>
    /// <c>ETAB</c>: 8-byte records of int16 x, int16 y, int8 elevation, flags
    /// (bit 7: mirrored), int16 −<c>OJFF</c> number. The <c>OJFF</c> gives the
    /// anchor (int16 at 2 and 4, plus int8 at 0x0A for y), the <c>BMP </c>
    /// number (uint16 at 0x0C) and the <c>SCMD</c> number (int16 at 0x0E);
    /// the position is computed as in <c>object_from_ojff</c> (<c>1DF3:1073</c>).
    /// </summary>
    private static List<RegionObject> LoadObjects(GffSet all, int number)
    {
        var objects = new List<RegionObject>();
        if (!all.TryRead("ETAB", number, out var etabMemory))
        {
            return objects;
        }
        ReadOnlySpan<byte> etab = etabMemory.Span;
        var sprites = new Dictionary<int, ObjectSprite?>();

        for (int at = 0; at + 8 <= etab.Length; at += 8)
        {
            int x = BinaryPrimitives.ReadInt16LittleEndian(etab[at..]);
            int y = BinaryPrimitives.ReadInt16LittleEndian(etab[(at + 2)..]);
            bool mirrored = (etab[at + 5] & 0x80) != 0;
            int ojff = Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(etab[(at + 6)..]));

            if (!sprites.TryGetValue(ojff, out var sprite))
            {
                sprite = LoadObjectSprite(all, ojff);
                sprites[ojff] = sprite;
            }
            if (sprite is { } s)
            {
                objects.Add(new RegionObject(at / 8, ojff, x, y, x - s.Definition.AnchorX, y - s.Definition.AnchorY,
                    mirrored, s.Frames, s.Definition.Scmd));
            }
        }
        return objects;
    }

    private sealed record ObjectSprite(ObjectDefinition Definition, IReadOnlyList<ImageFrame> Frames);

    private static ObjectSprite? LoadObjectSprite(GffSet all, int ojff)
    {
        if (!all.TryRead("OJFF", ojff, out var record) || record.Length < ObjectDefinition.Size)
        {
            return null;
        }
        ObjectDefinition definition = ObjectDefinition.Parse(record.Span);
        if (!all.TryRead("BMP ", definition.Bmp, out var image))
        {
            return null;
        }
        var frames = ImageDecoder.ReadFrames(image.Span);
        return frames.Count == 0 ? null : new ObjectSprite(definition, frames);
    }

    /// <summary>
    /// A cell with wall index i draws <c>WALL</c> number region × 100 + i − 1,
    /// bottom-centred on the cell.
    /// </summary>
    private static List<PlacedSprite> LoadWalls(GffSet all, int number, byte[] flags, int width, int height)
    {
        var walls = new List<PlacedSprite>();
        var frames = new Dictionary<int, ImageFrame?>();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int cell = y * width + x;
                if (cell >= flags.Length)
                {
                    break;
                }
                int wallIndex = flags[cell] & WallIndexMask;
                if (wallIndex == 0)
                {
                    continue;
                }
                int wall = number * 100 + wallIndex - 1;
                if (!frames.TryGetValue(wall, out ImageFrame? frame))
                {
                    frame = all.TryRead("WALL", wall, out var data) && ImageDecoder.ReadFrames(data.Span) is [var first, ..]
                        ? first
                        : null;
                    frames[wall] = frame;
                }
                if (frame is not null)
                {
                    walls.Add(new PlacedSprite(x * TileSize + 8 - frame.Width / 2, y * TileSize + 16 - frame.Height, frame));
                }
            }
        }
        return walls;
    }
}
