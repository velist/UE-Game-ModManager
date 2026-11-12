using System;
using UEModManager.Models;
using UEModManager.Views;
using Xunit;

namespace UEModManager.Tests.Views;

/// <summary>
/// 导入向导的文件选择器必须按当前游戏的引擎给过滤器。
///
/// 以前这里写死 UE 的扩展名名单（.pak/.utoc/.ucas），于是非 UE 玩家在自己的
/// MOD 文件上只能切"所有文件"—— Godot 的 .pck、暗黑4 的 .mpq、RE 的 .tex 都不在名单里，
/// 而引擎表里每个引擎都写好了自己的名单（<see cref="EngineProfile.FileDialogFilter"/>）。
/// </summary>
public sealed class ImportDialogFilterTests
{
    [Fact]
    public void 引擎过滤器被原样用上()
    {
        var filter = EngineProfile.Get(EngineType.Godot).FileDialogFilter;

        var resolved = ImportDialog.ResolveFileFilter(filter);

        Assert.Equal(filter, resolved);
        Assert.Contains("*.pck", resolved);
    }

    [Fact]
    public void 没有引擎过滤器时退回通用名单()
    {
        // 兜底名单至少要认压缩包与 UE 三件套，否则连主流程都进不去。
        foreach (var empty in new[] { null, "", "   " })
        {
            var resolved = ImportDialog.ResolveFileFilter(empty);

            Assert.Contains("*.zip", resolved);
            Assert.Contains("*.pak", resolved);
            Assert.Contains("所有文件", resolved);
        }
    }

    [Fact]
    public void 每个引擎的过滤器都非空且认压缩包()
    {
        // 主窗口把 CurrentEngineProfile.FileDialogFilter 直接交给向导，
        // 任何一个引擎漏填都会让那个游戏的玩家拿到空过滤器（什么都选不了）。
        foreach (var engine in Enum.GetValues<EngineType>())
        {
            var filter = EngineProfile.Get(engine).FileDialogFilter;

            Assert.False(string.IsNullOrWhiteSpace(filter), $"{engine} 没有文件过滤器");
            Assert.Contains("*.zip", filter);
            Assert.Contains("*.rar", filter);
            Assert.Contains("*.7z", filter);
        }
    }
}
