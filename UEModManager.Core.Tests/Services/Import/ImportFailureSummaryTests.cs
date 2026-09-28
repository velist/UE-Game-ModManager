using UEModManager.Services.Import;

namespace UEModManager.Core.Tests.Services.Import;

/// <summary>
/// 导入失败汇总。
///
/// 现场症状：一次导入里部分文件失败（例如仓库目录不可写、索引写不进去），
/// 用户看到的是"对话框正常关闭、列表里少了几个 MOD"，没有任何解释。
/// 原因是 ImportAsync 每个文件各自 try，失败只留在结果对象里，
/// 而提示窗口又在对话框关闭之后才弹（owner 已不可见，提示落到了主窗口后面）。
/// </summary>
public sealed class ImportFailureSummaryTests
{
    private const string Unknown = "原因未知，详细信息请查看日志。";

    [Fact]
    public void Build_AllSucceeded_ReturnsNull()
    {
        // 全部成功不该弹任何窗。
        var summary = ImportFailureSummary.Build(
            [(true, null), (true, "")], Unknown);

        Assert.Null(summary);
    }

    [Fact]
    public void Build_PartialFailure_CountsFailures()
    {
        var summary = ImportFailureSummary.Build(
            [(true, null), (false, "目标目录不可写"), (false, "目标目录不可写")], Unknown);

        Assert.NotNull(summary);
        Assert.Equal("{0} 个文件中有 {1} 个导入失败：", summary!.HeaderTemplate);
        Assert.Equal([3, 2], summary.HeaderArguments);
        // 同一原因出现两次只列一行：一次导入 20 个文件都因同一原因失败不该弹 20 行。
        Assert.Equal(["目标目录不可写"], summary.Reasons);
        Assert.Contains("3 个文件中有 2 个导入失败：", summary.Message);
    }

    [Fact]
    public void Build_AllFailed_UsesPlainHeader()
    {
        // 全失败时不说"3 个里有 3 个失败"，只说"导入失败："。
        var summary = ImportFailureSummary.Build(
            [(false, "仓库目录只读"), (false, "仓库目录只读"), (false, "仓库目录只读")], Unknown);

        Assert.NotNull(summary);
        Assert.Equal("导入失败：", summary!.HeaderTemplate);
        Assert.Empty(summary.HeaderArguments);
        Assert.StartsWith("导入失败：", summary.Message);
        Assert.Single(summary.Reasons);
    }

    [Fact]
    public void Build_BlankReason_FallsBackToUnknownText()
    {
        var summary = ImportFailureSummary.Build([(false, "   ")], Unknown);

        Assert.NotNull(summary);
        Assert.Equal([Unknown], summary!.Reasons);
    }

    [Fact]
    public void Build_NullResults_Throws()
        => Assert.Throws<ArgumentNullException>(() => ImportFailureSummary.Build(null!, Unknown));
}
