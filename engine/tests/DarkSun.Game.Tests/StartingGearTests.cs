using DarkSun.Data.Items;
using DarkSun.Game.World;

namespace DarkSun.Game.Tests;

/// <summary>A new character's gear by class (<c>6ECD:1D48</c>, the table at <c>38CE:0000</c>).</summary>
public class StartingGearTests
{
    private static int[] Ojffs(ItemClasses classes, bool thriKreen = false) => [.. StartingGear.For(classes, thriKreen).Select(g => g.Ojff)];

    [Fact]
    public void EachClassHasItsOwnGear()
    {
        Assert.Equal([1022, 1023, 1026, 1012, 1020], Ojffs(ItemClasses.Fighter)); // leather chest, arm, leg; long sword; shield
        Assert.Equal([1012, 1185, 1023], Ojffs(ItemClasses.Gladiator));
        Assert.Equal([1012, 1022, 1023, 1017, 1070], Ojffs(ItemClasses.Ranger));
        Assert.Equal([1185, 1020, 1022, 1023], Ojffs(ItemClasses.FireCleric));
        Assert.Equal([1012, 1022, 1015], Ojffs(ItemClasses.Thief));
        Assert.Equal([1185, 1022, 1017, 1070], Ojffs(ItemClasses.Psionicist));
        Assert.Equal([1015, 1019], Ojffs(ItemClasses.Preserver));
        Assert.Equal([1185, 1015], Ojffs(ItemClasses.Druid));
    }

    [Fact]
    public void SeveralClassesGetTheFirstTheCodeTestsAndAThriKreenItsChatkcha()
    {
        Assert.Equal(Ojffs(ItemClasses.Fighter), Ojffs(ItemClasses.Fighter | ItemClasses.Thief));
        Assert.Equal(Ojffs(ItemClasses.EarthCleric), Ojffs(ItemClasses.EarthCleric | ItemClasses.Preserver));
        Assert.Equal(Ojffs(ItemClasses.Thief), Ojffs(ItemClasses.Psionicist | ItemClasses.Thief));
        Assert.Equal([1010], Ojffs(ItemClasses.Fighter, thriKreen: true));
        Assert.Equal([3, 10, 0], StartingGear.For(ItemClasses.Gladiator, false).Select(g => g.Slot)); // right hand, left hand, arm
    }
}
