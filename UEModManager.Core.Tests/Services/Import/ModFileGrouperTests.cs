using System;
using System.Collections.Generic;
using System.Linq;
using UEModManager.Services.Import;

namespace UEModManager.Core.Tests.Services.Import;

public class ModFileGrouperTests
{
    // ─── ExtractBaseName ───

    [Theory]
    [InlineData("MyMod",     "MyMod")]
    [InlineData("MyMod_P",   "MyMod")]
    [InlineData("MyMod_p",   "MyMod")]
    [InlineData("MyMod_2",   "MyMod")]
    [InlineData("MyMod_99",  "MyMod")]
    [InlineData("MyMod_",    "MyMod")] // 仅一个下划线 + 0 个数字
    [InlineData("Foo_Bar",   "Foo_Bar")] // _Bar 不是数字也不是 P/p，保留
    [InlineData("",          "")]
    public void ExtractBaseName_StripsUePatchSuffix(string input, string expected)
    {
        Assert.Equal(expected, ModFileGrouper.ExtractBaseName(input));
    }

    [Fact]
    public void ExtractBaseName_Null_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ModFileGrouper.ExtractBaseName(null!));
    }

    // ─── GroupByBaseName ───

    [Fact]
    public void GroupByBaseName_SameBaseName_GroupedTogether()
    {
        var files = new[]
        {
            "/tmp/MyMod.pak",
            "/tmp/MyMod.utoc",
            "/tmp/MyMod.ucas",
        };

        var groups = ModFileGrouper.GroupByBaseName(files);

        Assert.Single(groups);
        Assert.Equal(3, groups.Values.First().Count);
    }

    [Fact]
    public void GroupByBaseName_PatchVariant_JoinsBaseGroup()
    {
        var files = new[]
        {
            "/tmp/MyMod.pak",
            "/tmp/MyMod_P.pak",
            "/tmp/MyMod_2.pak",
        };

        var groups = ModFileGrouper.GroupByBaseName(files);

        Assert.Single(groups);
        Assert.Equal(3, groups.Values.First().Count);
    }

    [Fact]
    public void GroupByBaseName_DifferentBaseNames_SeparateGroups()
    {
        var files = new[]
        {
            "/tmp/ModA.pak",
            "/tmp/ModB.pak",
            "/tmp/ModC.pak",
        };

        var groups = ModFileGrouper.GroupByBaseName(files);

        Assert.Equal(3, groups.Count);
    }

    [Fact]
    public void GroupByBaseName_MixedSet()
    {
        var files = new[]
        {
            "/tmp/Alpha.pak",
            "/tmp/Alpha.utoc",
            "/tmp/Beta.pak",
            "/tmp/Beta_P.pak",
            "/tmp/Gamma.pak",
        };

        var groups = ModFileGrouper.GroupByBaseName(files);

        Assert.Equal(3, groups.Count);
        Assert.Equal(2, groups.Values.ElementAt(0).Count); // Alpha + Alpha
        Assert.Equal(2, groups.Values.ElementAt(1).Count); // Beta + Beta_P
        Assert.Single(groups.Values.ElementAt(2));         // Gamma
    }

    [Fact]
    public void GroupByBaseName_CaseInsensitive()
    {
        var files = new[]
        {
            "/tmp/MyMod.pak",
            "/tmp/MYMOD.utoc",
            "/tmp/mymod_P.pak",
        };

        var groups = ModFileGrouper.GroupByBaseName(files);

        Assert.Single(groups);
        Assert.Equal(3, groups.Values.First().Count);
    }

    [Fact]
    public void GroupByBaseName_Empty_ReturnsEmptyDict()
    {
        var groups = ModFileGrouper.GroupByBaseName(Array.Empty<string>());
        Assert.Empty(groups);
    }

    [Fact]
    public void GroupByBaseName_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ModFileGrouper.GroupByBaseName(null!));
    }

    // ─── SelectGroupName ───

    [Fact]
    public void SelectGroupName_PriorityHit_ReturnsLongestName()
    {
        var files = new[]
        {
            "/tmp/Short.pak",
            "/tmp/SuperLongName.pak",
            "/tmp/Mid.utoc",
        };

        var name = ModFileGrouper.SelectGroupName(files, [".pak", ".utoc"]);

        Assert.Equal("SuperLongName", name);
    }

    [Fact]
    public void SelectGroupName_FirstPriorityWinsOverSecond()
    {
        var files = new[]
        {
            "/tmp/Foo.utoc",   // 第二优先级
            "/tmp/Bar.pak",    // 第一优先级，即便名字短也胜出
        };

        var name = ModFileGrouper.SelectGroupName(files, [".pak", ".utoc"]);

        Assert.Equal("Bar", name);
    }

    [Fact]
    public void SelectGroupName_NoPriorityMatch_FallbackToFirst()
    {
        var files = new[]
        {
            "/tmp/AAA.dll",
            "/tmp/BBB.exe",
        };

        var name = ModFileGrouper.SelectGroupName(files, [".pak", ".utoc"]);

        Assert.Equal("AAA", name);
    }

    [Fact]
    public void SelectGroupName_NullPriority_FallbackToFirst()
    {
        var files = new[] { "/tmp/Hello.pak" };

        var name = ModFileGrouper.SelectGroupName(files, null);

        Assert.Equal("Hello", name);
    }

    [Fact]
    public void SelectGroupName_EmptyFiles_ReturnsNull()
    {
        Assert.Null(ModFileGrouper.SelectGroupName(Array.Empty<string>(), [".pak"]));
    }

    [Fact]
    public void SelectGroupName_NullFiles_ReturnsNull()
    {
        Assert.Null(ModFileGrouper.SelectGroupName(null!, [".pak"]));
    }

    [Fact]
    public void SelectGroupName_PriorityCaseInsensitive()
    {
        var files = new[] { "/tmp/Foo.PAK" };
        var name = ModFileGrouper.SelectGroupName(files, [".pak"]);
        Assert.Equal("Foo", name);
    }
}
