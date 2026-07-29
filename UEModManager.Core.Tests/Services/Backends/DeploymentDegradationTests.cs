using System.Linq;
using UEModManager.Services.Backends;

namespace UEModManager.Core.Tests.Services.Backends;

/// <summary>
/// 部署降级的分类、聚合与告知判定。
///
/// <para>
/// 这一整套存在的理由：用户在设置里选了「硬链接」，而包仓库默认在系统盘、游戏通常装在别的盘。
/// 硬链接建不了跨盘，后端逐个文件降级为复制——部署成功、MOD 能用、空间一点没省，
/// 而此前整个过程在界面上不留一个字。功能没坏，坏的是它没做用户以为它做的事。
/// </para>
/// </summary>
public class DeploymentDegradationTests
{
    private const string RepoOnC = @"C:\Users\a\AppData\Local\UEModManager\Repository\ModA\x.pak";
    private const string GameOnD = @"D:\Games\Wukong\Content\Paks\x.pak";

    private static DeploymentDegradation CrossVolume(int index = 0)
        => new(DeploymentDegradationKind.HardLinkCrossVolume,
            RepoOnC + index, GameOnD + index, "CreateHardLink 失败，Win32Error=1");

    // ─── 失败分类 ───

    [Fact]
    public void 跨盘失败按跨盘分类()
    {
        Assert.Equal(DeploymentDegradationKind.HardLinkCrossVolume,
            HardLinkFailureClassifier.Classify(
                HardLinkFailureClassifier.ErrorInvalidFunction, RepoOnC, GameOnD));
    }

    [Fact]
    public void 错误码不决定原因_同一个码在同盘时是另一回事()
    {
        // 真机上跨盘拿到的是 ERROR_INVALID_FUNCTION(1) 而不是直觉上的 17，
        // 错误码给哪一个取决于目标卷的驱动。所以判据只能是源与目标的盘根。
        Assert.Equal(DeploymentDegradationKind.HardLinkUnsupported,
            HardLinkFailureClassifier.Classify(
                HardLinkFailureClassifier.ErrorInvalidFunction, @"E:\repo\x.pak", @"E:\game\x.pak"));

        Assert.Equal(DeploymentDegradationKind.HardLinkCrossVolume,
            HardLinkFailureClassifier.Classify(
                HardLinkFailureClassifier.ErrorNotSameDevice, RepoOnC, GameOnD));
    }

    [Theory]
    [InlineData(5)]     // ERROR_ACCESS_DENIED
    [InlineData(32)]    // ERROR_SHARING_VIOLATION
    [InlineData(183)]   // ERROR_ALREADY_EXISTS
    [InlineData(1142)]  // ERROR_TOO_MANY_LINKS
    public void 其余失败一律不许降级(int win32Error)
    {
        // 悄悄复制一份会把一次真实故障伪装成部署成功，必须上抛走事务回滚
        Assert.Null(HardLinkFailureClassifier.Classify(win32Error, RepoOnC, GameOnD));
    }

    [Fact]
    public void 盘根算不出来时不报跨盘()
    {
        // "跨盘"配的解法是"把仓库搬到游戏所在的盘"，而我们连它在哪个盘都说不清
        Assert.Equal(DeploymentDegradationKind.HardLinkUnsupported,
            HardLinkFailureClassifier.Classify(1, @"repo\x.pak", GameOnD));
    }

    // ─── 聚合 ───

    [Fact]
    public void 同一原因的上万条只聚成一条()
    {
        // 这条是整套告知的核心：一次整合包部署有上万个文件，跨盘降级是整批同因的。
        // 不聚合就等于弹一万次提示。
        var collector = new DeploymentDegradationCollector();
        for (var i = 0; i < 10_000; i++) collector.Add(CrossVolume(i));

        var summaries = collector.Summarize();

        Assert.Single(summaries);
        Assert.Equal(DeploymentDegradationKind.HardLinkCrossVolume, summaries[0].Kind);
        Assert.Equal(10_000, summaries[0].FileCount);
        Assert.Equal(10_000, collector.Count);
    }

    [Fact]
    public void 样本路径取该原因下的第一条()
    {
        // 同因的降级共享同一对盘，一条样本就够说明是哪两个盘
        var collector = new DeploymentDegradationCollector();
        collector.Add(CrossVolume(1));
        collector.Add(CrossVolume(2));

        var summary = Assert.Single(collector.Summarize());
        Assert.Equal(RepoOnC + "1", summary.SourcePath);
        Assert.Equal(GameOnD + "1", summary.TargetPath);
    }

    [Fact]
    public void 不同原因各占一条且保持首次出现的顺序()
    {
        var collector = new DeploymentDegradationCollector();
        collector.Add(new DeploymentDegradation(
            DeploymentDegradationKind.BackendUnavailable, "", "", "后端不可用"));
        collector.Add(CrossVolume());
        collector.Add(CrossVolume());

        var summaries = collector.Summarize();

        Assert.Equal(
            new[] { DeploymentDegradationKind.BackendUnavailable, DeploymentDegradationKind.HardLinkCrossVolume },
            summaries.Select(s => s.Kind));
        Assert.Equal(new[] { 1, 2 }, summaries.Select(s => s.FileCount));
    }

    [Fact]
    public void 没有降级就是空的()
    {
        Assert.Empty(new DeploymentDegradationCollector().Summarize());
        Assert.Empty(DeploymentDegradationCollector.Summarize(null));
    }

    [Fact]
    public void 并发上报不丢也不炸()
    {
        // 后端在部署线程上抛事件，收集器必须自己扛住
        var collector = new DeploymentDegradationCollector();

        Parallel.For(0, 2000, i => collector.Add(CrossVolume(i)));

        Assert.Equal(2000, collector.Count);
        Assert.Equal(2000, Assert.Single(collector.Summarize()).FileCount);
    }

    // ─── 什么时候值得说一次 ───

    private static IReadOnlyList<DeploymentDegradationSummary> CrossVolumeSummary(int files = 128)
        => DeploymentDegradationCollector.Summarize(
            Enumerable.Range(0, files).Select(i => CrossVolume(i)));

    [Fact]
    public void 没降级就不说()
    {
        var decision = DeploymentDegradationNotice.Decide(
            Array.Empty<DeploymentDegradationSummary>(), lastNotifiedSignature: null);

        Assert.False(decision.ShouldNotify);
    }

    [Fact]
    public void 第一次遇到这套盘的组合要说()
    {
        var decision = DeploymentDegradationNotice.Decide(CrossVolumeSummary(), null);

        Assert.True(decision.ShouldNotify);
        Assert.False(string.IsNullOrEmpty(decision.Signature));
    }

    [Fact]
    public void 同一套组合只说一次()
    {
        // 弹多了比不弹更糟：用户会开始无视所有弹窗
        var first = DeploymentDegradationNotice.Decide(CrossVolumeSummary(), null);
        var second = DeploymentDegradationNotice.Decide(CrossVolumeSummary(), first.Signature);

        Assert.False(second.ShouldNotify);
        Assert.Equal(first.Signature, second.Signature);
    }

    [Fact]
    public void 文件数变了仍然算同一套组合()
    {
        // 下一次部署换了别的 MOD，文件数必然不同，但情况一点没变，不该再说一遍
        var first = DeploymentDegradationNotice.Decide(CrossVolumeSummary(128), null);
        var second = DeploymentDegradationNotice.Decide(CrossVolumeSummary(3), first.Signature);

        Assert.False(second.ShouldNotify);
    }

    [Fact]
    public void 换了盘就再说一次()
    {
        // 用户把仓库搬到了别的盘、或者换了一个装在别的盘上的游戏 —— 结论真的变了
        var told = DeploymentDegradationNotice.Decide(CrossVolumeSummary(), null).Signature;

        var moved = DeploymentDegradationCollector.Summarize(new[]
        {
            new DeploymentDegradation(DeploymentDegradationKind.HardLinkCrossVolume,
                @"E:\Repository\x.pak", @"F:\Games\Wukong\x.pak", "CreateHardLink 失败，Win32Error=1"),
        });

        Assert.True(DeploymentDegradationNotice.Decide(moved, told).ShouldNotify);
    }

    [Fact]
    public void 换了降级原因也再说一次()
    {
        // "换个盘就行"和"这个盘的格式不支持"是两句完全不同的解法
        var told = DeploymentDegradationNotice.Decide(CrossVolumeSummary(), null).Signature;

        var unsupported = DeploymentDegradationCollector.Summarize(new[]
        {
            new DeploymentDegradation(DeploymentDegradationKind.HardLinkUnsupported,
                RepoOnC, GameOnD, "CreateHardLink 失败，Win32Error=1"),
        });

        Assert.True(DeploymentDegradationNotice.Decide(unsupported, told).ShouldNotify);
    }

    // ─── 文案 ───

    [Fact]
    public void 文案说清楚发生了什么_有什么影响_怎么才能用上()
    {
        var content = DeploymentDegradationNotice.BuildContent(CrossVolumeSummary(128));

        Assert.Contains("硬链接", content.Title);
        Assert.Contains("C 盘", content.Message);          // MOD 存在哪
        Assert.Contains("D 盘", content.Message);          // 游戏在哪
        Assert.Contains("128 个文件", content.Message);     // 影响面
        Assert.Contains("设置 → 部署与仓库", content.Message); // 怎么改
        Assert.Contains("可以直接玩", content.Message);      // 这不是错误
    }

    [Fact]
    public void 文案里不许出现错误码和卷这种词()
    {
        // 面向普通玩家：技术细节留在日志里（Detail 字段），用户的词是"盘"不是"卷"
        var content = DeploymentDegradationNotice.BuildContent(CrossVolumeSummary());

        Assert.DoesNotContain("Win32", content.Message);
        Assert.DoesNotContain("卷", content.Message);
        Assert.DoesNotContain("CreateHardLink", content.Message);
    }

    [Fact]
    public void 盘符说不清时换一句不点名的说法()
    {
        // 指错盘会让用户照着搬一遍几十 GB 的仓库，然后发现什么都没变
        var summaries = DeploymentDegradationCollector.Summarize(new[]
        {
            new DeploymentDegradation(DeploymentDegradationKind.HardLinkCrossVolume,
                "", "", "CreateHardLink 失败，Win32Error=1"),
        });

        var content = DeploymentDegradationNotice.BuildContent(summaries);

        Assert.Contains("不在同一个盘", content.Message);
        Assert.Contains("游戏所在的那个盘", content.Message);
        Assert.DoesNotContain("null", content.Message);
    }

    [Fact]
    public void 多种原因时以影响最大的那条为主句()
    {
        var summaries = DeploymentDegradationCollector.Summarize(
            Enumerable.Repeat(
                new DeploymentDegradation(DeploymentDegradationKind.BackendUnavailable, "", "", "x"), 1)
            .Concat(Enumerable.Range(0, 50).Select(i => CrossVolume(i))));

        var content = DeploymentDegradationNotice.BuildContent(summaries);

        Assert.Contains("硬链接", content.Title);
        Assert.Contains("50 个文件", content.Message);
        Assert.Contains("另有 1 个文件", content.Message);
    }

    [Fact]
    public void 没有降级时生成文案是编程错误()
    {
        Assert.Throws<ArgumentException>(() =>
            DeploymentDegradationNotice.BuildContent(Array.Empty<DeploymentDegradationSummary>()));
    }
}
