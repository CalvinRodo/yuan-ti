namespace DarkSun.Data.Gff;

/// <summary>Anything resources can be read from by tag and number.</summary>
public interface IResourceSource
{
    bool Contains(string tag, int number);

    bool TryRead(string tag, int number, out ReadOnlyMemory<byte> data);
}
