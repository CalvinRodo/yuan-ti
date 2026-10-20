using System.Text;
using DarkSun.Data.Text;

namespace DarkSun.Data.Tests;

public class TextTests
{
    [Fact]
    public void DecodesLatin1UpToNulWithUnixLineEnds()
    {
        byte[] raw = [.. Encoding.Latin1.GetBytes("Tyr\r\nAthas\rDrajé\r\n"), 0, (byte)'x'];

        Assert.Equal("Tyr\nAthas\nDrajé\n", GameText.Decode(raw));
    }

    [Fact]
    public void SplitsSpellsAtTheFirstColon()
    {
        var spell = SpellInfo.Parse(1, Encoding.Latin1.GetBytes("ARMOR: Creates a shield: AC6.\r\n"));

        Assert.Equal(new SpellInfo(1, "ARMOR", "Creates a shield: AC6."), spell);
        Assert.Equal(21000, spell.Icon); // ICON 21000 is Armor's
        Assert.Equal(3000, SpellInfo.Parse(139, "DETONATE\r\n"u8).Icon); // the powers' are 3000–3033
        Assert.Null(SpellInfo.Parse(249, "RESTORE\r\n"u8).Icon);
        Assert.Equal("ODD", SpellInfo.Parse(2, "ODD\r\n"u8).Name);
    }

    [Fact]
    public void NamesAreFixedRecordsWithJunkAfterTheNul()
    {
        static byte[] Record(string name, string junk)
        {
            var r = new byte[NameTable.RecordSize];
            Encoding.Latin1.GetBytes(name + "\0" + junk).AsSpan(0, Math.Min(NameTable.RecordSize, name.Length + 1 + junk.Length)).CopyTo(r);
            return r;
        }
        byte[] data = [.. Record("Sling", "te\\NAMEIX\\*.oda\0Ø\0ÿ"), .. Record("Long Sword", "IX\\*.oda"), .. "Bone"u8];

        NameTable names = NameTable.Parse(data);

        Assert.Equal(["Sling", "Long Sword", "Bone"], names.Names);
        Assert.Equal("Long Sword", names[1]);
        Assert.Null(names[3]);
        Assert.Null(names[-1]);
    }
}
