using UEModManager.Services.Paths;

namespace UEModManager.Core.Tests.Services.Paths;

/// <summary>
/// "首次运行要不要问用户 MOD 存哪儿"的判定。
///
/// <para>
/// <b>本类的重点全部压在"不误弹"上</b>，不是均衡覆盖。理由是错弹的代价远大于漏弹：
/// 一个已经在默认位置攒了几十 GB 的老用户被问到"MOD 放哪个盘"，他很可能会认真挑一个大盘，
/// 而引导只改配置指针、<b>不搬数据</b>，于是他原有的全部 MOD 在界面上当场消失。
/// 漏弹的代价只是少一次提醒。所以下面每一条反面证据都单独钉一遍：
/// 只要它成立，其余五条全部为假也不许弹。
/// </para>
/// </summary>
public class RepositorySetupPromptTests
{
    /// <summary>全新安装：六条反面证据一条都不成立。</summary>
    private static RepositorySetupProbe FreshInstall() => new(
        AlreadyPrompted: false,
        RepositoryRootConfigured: false,
        CurrentRepositoryHasContent: false,
        LegacyRepositoryHasContent: false,
        AppConfigExists: false,
        LegacyInstallDataExists: false);

    [Fact]
    public void 全新安装才弹()
    {
        var decision = RepositorySetupPrompt.Decide(FreshInstall());

        Assert.True(decision.ShouldPrompt);
        Assert.Equal(RepositorySetupSkipReason.NotSkipped, decision.SkipReason);
    }

    // ─── 老用户的每一种形态都不许弹 ───

    public static TheoryData<string, RepositorySetupProbe, RepositorySetupSkipReason> 老用户形态 => new()
    {
        {
            // 上次问过、用户点了"以后再说"：配置里什么都没有，唯一的证据就是这个标记。
            // 拿"有没有设置过位置"当判据的话，跳过的人每次启动都会被拦一次。
            "跳过引导之后",
            FreshInstall() with { AlreadyPrompted = true },
            RepositorySetupSkipReason.AlreadyPrompted
        },
        {
            // 在设置里改过位置的人，以及旧仓库被搬迁器原地登记过的老用户
            "已经自定义过仓库位置",
            FreshInstall() with { RepositoryRootConfigured = true },
            RepositorySetupSkipReason.RepositoryRootConfigured
        },
        {
            // 最危险的一种：默认位置已经躺着几十 GB
            "默认仓库里已经有包",
            FreshInstall() with { CurrentRepositoryHasContent = true },
            RepositorySetupSkipReason.RepositoryHasContent
        },
        {
            // 搬迁器登记失败（配置不可写）时，这是唯一还能认出老用户的证据
            "旧默认仓库里已经有包",
            FreshInstall() with { LegacyRepositoryHasContent = true },
            RepositorySetupSkipReason.LegacyRepositoryHasContent
        },
        {
            // config.json 只在用户配置过游戏路径后才被写出
            "主配置已存在",
            FreshInstall() with { AppConfigExists = true },
            RepositorySetupSkipReason.AppConfigExists
        },
        {
            // v1.x 升级上来、数据还留在安装目录（搬移开关至今关着）
            "安装目录里有旧版数据",
            FreshInstall() with { LegacyInstallDataExists = true },
            RepositorySetupSkipReason.LegacyInstallDataExists
        },
    };

    [Theory]
    [MemberData(nameof(老用户形态))]
    public void 任意一条使用痕迹成立就不弹(
        string 形态, RepositorySetupProbe probe, RepositorySetupSkipReason expected)
    {
        var decision = RepositorySetupPrompt.Decide(probe);

        Assert.False(decision.ShouldPrompt, $"{形态} 不该被打扰");
        Assert.Equal(expected, decision.SkipReason);
    }

    [Fact]
    public void 全部证据同时成立时也只报一条原因()
    {
        // 典型老用户其实会同时命中好几条。报哪一条只影响日志可读性，
        // 但必须稳定 —— 排障时"为什么没弹"要有唯一答案。
        var decision = RepositorySetupPrompt.Decide(new RepositorySetupProbe(
            true, true, true, true, true, true));

        Assert.False(decision.ShouldPrompt);
        Assert.Equal(RepositorySetupSkipReason.AlreadyPrompted, decision.SkipReason);
    }

    [Fact]
    public void 已问过的优先级高于所有其它证据()
    {
        // 用户已经做过决定，这件事比任何磁盘上的痕迹都更有资格终止判定
        var decision = RepositorySetupPrompt.Decide(
            FreshInstall() with { AlreadyPrompted = true, RepositoryRootConfigured = true });

        Assert.Equal(RepositorySetupSkipReason.AlreadyPrompted, decision.SkipReason);
    }

    [Fact]
    public void 跳过之后再也不问()
    {
        // 这条是硬性要求③的直接编码：判据必须是"已问过"，不能是"用户选没选"。
        // 第一次跑：弹。之后无论用户选了位置还是直接关掉窗口，标记都会被写上。
        Assert.True(RepositorySetupPrompt.Decide(FreshInstall()).ShouldPrompt);

        var afterSkip = FreshInstall() with { AlreadyPrompted = true };
        Assert.False(RepositorySetupPrompt.Decide(afterSkip).ShouldPrompt);

        var afterChoosing = FreshInstall() with
        {
            AlreadyPrompted = true,
            RepositoryRootConfigured = true,
        };
        Assert.False(RepositorySetupPrompt.Decide(afterChoosing).ShouldPrompt);
    }

    [Fact]
    public void 结论能自解释()
    {
        // 这个字符串会直接进启动日志，是排查"为什么我这台没弹/为什么弹了"的唯一线索
        var skipped = RepositorySetupPrompt.Decide(FreshInstall() with { AppConfigExists = true });
        var prompted = RepositorySetupPrompt.Decide(FreshInstall());

        Assert.Contains("不弹引导", skipped.ToString());
        Assert.Contains(nameof(RepositorySetupSkipReason.AppConfigExists), skipped.ToString());
        Assert.Contains("弹出引导", prompted.ToString());
        Assert.False(string.IsNullOrWhiteSpace(prompted.Reason));
    }
}
