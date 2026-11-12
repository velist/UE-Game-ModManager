using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UEModManager.Models;
using UEModManager.Views;
using Xunit;

namespace UEModManager.Tests.Views;

/// <summary>
/// 引擎下拉的项与代码里的引擎表必须同序同数。
///
/// 事故原型：这张表以前是两份 switch（"索引 → 引擎"与"引擎 → 索引"各一份），
/// 加引擎要改两处，漏一处就静默错位 —— 玩家选 Godot，程序按 Decima 处理，
/// 而且只在导入那一刻才看得出来。现在只有 <see cref="AddCustomGameDialog.EngineChoices"/>
/// 一份，XAML 的项数由这里钉住。
/// </summary>
public class AddCustomGameDialogSourceGuardTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "UEModManager.sln")))
            dir = dir.Parent;

        Assert.True(dir != null, "找不到仓库根目录（UEModManager.sln）");
        return dir!.FullName;
    }

    [Fact]
    public void 下拉项数必须与引擎表一致()
    {
        var xaml = File.ReadAllText(
            Path.Combine(RepoRoot(), "UEModManager", "Views", "AddCustomGameDialog.xaml"));

        // 第一项是"自动检测"，其余一项对一个引擎。
        var items = Regex.Matches(xaml, "<ComboBoxItem").Count;

        Assert.True(items == AddCustomGameDialog.EngineChoices.Length + 1,
            $"XAML 里有 {items} 个下拉项，引擎表有 {AddCustomGameDialog.EngineChoices.Length} 个引擎"
            + "（外加一个「自动检测」）—— 两边必须同序同数，否则选中的引擎与显示的那一项对不上");
    }

    [Fact]
    public void 引擎表里没有重复也没有未知()
    {
        var choices = AddCustomGameDialog.EngineChoices;

        Assert.Equal(choices.Length, choices.Distinct().Count());
        Assert.DoesNotContain(EngineType.Unknown, choices);
    }
}
