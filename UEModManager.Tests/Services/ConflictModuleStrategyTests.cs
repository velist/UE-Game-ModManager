using System;
using System.IO;
using UEModManager.Models;
using UEModManager.Services.Conflict;
using UEModManager.Tests.Services;
using Xunit;

namespace UEModManager.Tests.Services;

/// <summary>
/// 冲突比对策略必须跟着**这一次要分析的那个游戏**走。
///
/// 策略决定"按容器资产比（UE）还是按文件路径比（Unity / RE / Godot / Decima / 暗黑4）"。
/// 它以前读的是配置里的"当前引擎"，而两处切换游戏的路径（初始化与设置窗口）
/// 更新配置与通知冲突模块的顺序并不一致 —— 读当前状态就会慢一拍，
/// 于是拿上一个游戏的语义去分析这一个游戏的 MOD。
/// </summary>
public sealed class ConflictModuleStrategyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "UEModManager.Tests", "ConflictStrategy", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task 策略跟着入参游戏的引擎走而不是跟着当前配置()
    {
        var f = await ConflictTestHost.OpenAsync(_root);

        // 宿主把当前游戏设成黑神话·悟空（UE）→ 按容器资产比对
        Assert.Equal(ConflictStrategy.ContainerAssets, f.Conflicts.CurrentStrategy);

        // 直接把 RE 引擎的游戏交给它：策略必须立刻变成按文件路径，
        // 哪怕配置里"当前游戏"还是黑神话·悟空。
        await f.Conflicts.SetCurrentGameAsync("生化危机9");
        Assert.Equal(ConflictStrategy.FilePath, f.Conflicts.CurrentStrategy);

        // 切回 UE 游戏同样立刻生效（反向也要成立，否则只是单向巧合）。
        await f.Conflicts.SetCurrentGameAsync("黑神话·悟空");
        Assert.Equal(ConflictStrategy.ContainerAssets, f.Conflicts.CurrentStrategy);
    }

    [Fact]
    public async Task 游戏名为空时退回当前引擎的策略()
    {
        var f = await ConflictTestHost.OpenAsync(_root);

        await f.Conflicts.SetCurrentGameAsync("");

        Assert.Equal(ConflictStrategy.ContainerAssets, f.Conflicts.CurrentStrategy);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* 临时目录留给系统清理 */ }
        catch (UnauthorizedAccessException) { }
    }
}
