using System;

namespace UEModManager.Services.Paths;

/// <summary>不弹首次运行引导的原因。每一条都对应一类"这台机器上已经有人用过"的证据。</summary>
public enum RepositorySetupSkipReason
{
    /// <summary>不适用（该弹）。</summary>
    NotSkipped,

    /// <summary>已经问过一次了（无论用户当时选了位置还是跳过）。</summary>
    AlreadyPrompted,

    /// <summary>配置里已有非空的仓库位置。</summary>
    RepositoryRootConfigured,

    /// <summary>当前生效的仓库目录里已经有包。</summary>
    RepositoryHasContent,

    /// <summary>旧默认仓库位置里已经有包。</summary>
    LegacyRepositoryHasContent,

    /// <summary>主配置已存在，说明用户配置过游戏路径。</summary>
    AppConfigExists,

    /// <summary>安装目录里有旧版本留下的数据。</summary>
    LegacyInstallDataExists,

    /// <summary>
    /// 探测本身失败（配置读不出来、磁盘查不动）。<b>证明不了这是全新安装就不弹</b>——
    /// 与其余六条一样落在"不打扰"这一侧，只是原因不同，日志里要能分得开。
    /// </summary>
    ProbeFailed,
}

/// <summary>
/// 首次运行时对"这台机器是否从没用过本程序"的一次观测。全部由主项目查盘/读配置后填入，
/// 本层只做判定。
///
/// <para>
/// 六个字段没有一个是"该弹"的正面证据 —— 它们<b>全都是反面证据</b>：任意一条为 true
/// 就说明这里已经有人用过，引导必须闭嘴。这个不对称是刻意的，见
/// <see cref="RepositorySetupPrompt"/> 的类注释。
/// </para>
/// </summary>
/// <param name="AlreadyPrompted">
/// 已问过标记。<b>必须是独立的一个标记，不能用"用户选没选过位置"代替</b>：
/// 跳过的人配置里什么都没有，拿"没有位置"当判据等于每次启动都拦他一次。
/// </param>
/// <param name="RepositoryRootConfigured">
/// 配置里的仓库位置非空。两类人会命中：在设置里改过位置的，
/// 以及旧仓库被搬迁器原地登记过的老用户。
/// </param>
/// <param name="CurrentRepositoryHasContent">当前生效的仓库目录里已有内容。</param>
/// <param name="LegacyRepositoryHasContent">
/// 旧默认仓库位置（<c>%APPDATA%\UEModManager\Repository</c>）里已有内容。
/// 与上一条重复得很有必要：搬迁器把它登记进配置这件事本身可能失败（配置不可写），
/// 那时 <paramref name="RepositoryRootConfigured"/> 是 false，只剩这一条能认出老用户。
/// </param>
/// <param name="AppConfigExists">
/// 主配置 <c>config.json</c> 存在（新位置或安装目录任一）。它只在用户配置过游戏路径后才被写出，
/// 是"真的用过这个软件"最直接的证据。
/// </param>
/// <param name="LegacyInstallDataExists">
/// 安装目录里有旧版数据（<c>Data</c> / <c>Backups</c>）。挡的是从 v1.x 升级上来、
/// 数据还没被搬走的老用户 —— 搬移开关至今关着，这类机器上的旧数据一直原样躺着。
/// </param>
public readonly record struct RepositorySetupProbe(
    bool AlreadyPrompted,
    bool RepositoryRootConfigured,
    bool CurrentRepositoryHasContent,
    bool LegacyRepositoryHasContent,
    bool AppConfigExists,
    bool LegacyInstallDataExists);

/// <summary>一次"该不该弹引导"的结论与理由（理由直接进日志，排障时要能一眼看懂为什么没弹）。</summary>
public readonly record struct RepositorySetupDecision(
    bool ShouldPrompt,
    RepositorySetupSkipReason SkipReason,
    string Reason)
{
    /// <inheritdoc/>
    public override string ToString()
        => ShouldPrompt ? $"弹出引导：{Reason}" : $"不弹引导（{SkipReason}）：{Reason}";
}

/// <summary>
/// 首次运行时"要不要引导用户挑一个包仓库位置"的判定（纯函数，不碰 IO）。
///
/// <para><b>要解决的问题</b></para>
/// 包仓库存的是 MOD 包实体，重度用户能攒到几十 GB，而默认位置在系统盘
/// （<c>%LOCALAPPDATA%\UEModManager\Repository</c>）。设置里能改，但用户得先知道有这回事。
/// 改默认值不行 —— 那会影响所有新安装且照顾不到已有习惯；引导则把选择权交给用户，
/// 而且只在第一次出现。
///
/// <para><b>为什么判据是"一票否决"而不是"综合打分"</b></para>
/// 这个框弹错的代价是不对称的：
/// <list type="bullet">
/// <item>漏弹 = 一个新用户没被告知可以换盘，他的仓库落在 C 盘，随时能在设置里改回来。
/// 损失是一次提醒。</item>
/// <item>错弹 = 一个已经在默认位置攒了几十 GB 的老用户，在启动时被问"MOD 包放哪"。
/// 他很可能会认真选一个大盘 —— 而引导只改配置指针，<b>不搬数据</b>，
/// 于是他原有的全部 MOD 在界面上当场消失（新仓库是空的）。这是数据事故级别的观感。</item>
/// </list>
/// 所以本判定的默认动作是<b>不弹</b>：六条反面证据里只要命中任意一条就闭嘴，
/// 必须全部不成立 —— 即"这台机器上找不到任何使用痕迹" —— 才弹。宁可漏弹。
///
/// <para><b>判定必须跑在数据搬迁器之后</b></para>
/// 搬迁器的原地登记会把老用户的旧仓库位置写进配置，正是
/// <see cref="RepositorySetupProbe.RepositoryRootConfigured"/> 这条判据的主要来源。
/// 抢在它前面判，"老用户 + 旧仓库有数据"这一形态就会在登记发生前被读成"未配置"。
/// 时序由 <c>App.ShowAuthenticationWindow</c> 保证，并有源码守卫测试钉住。
/// </summary>
public static class RepositorySetupPrompt
{
    /// <summary>判定。</summary>
    public static RepositorySetupDecision Decide(RepositorySetupProbe probe)
    {
        // 顺序即优先级，只影响日志里报哪一条原因；任意一条命中的结论都是"不弹"。
        // 已问过排第一，因为它是唯一一条"用户已经做过决定"的证据，
        // 其余五条都只是"这台机器有使用痕迹"的旁证。
        if (probe.AlreadyPrompted)
        {
            return Skip(RepositorySetupSkipReason.AlreadyPrompted,
                "此前已经问过一次仓库位置，无论当时选了还是跳过，都不再打扰");
        }

        if (probe.RepositoryRootConfigured)
        {
            return Skip(RepositorySetupSkipReason.RepositoryRootConfigured,
                "配置里已有仓库位置（用户改过，或旧仓库已被搬迁器原地登记）");
        }

        if (probe.CurrentRepositoryHasContent)
        {
            return Skip(RepositorySetupSkipReason.RepositoryHasContent,
                "当前仓库目录里已经有包，换位置会让这些包在界面上消失");
        }

        if (probe.LegacyRepositoryHasContent)
        {
            return Skip(RepositorySetupSkipReason.LegacyRepositoryHasContent,
                "旧默认仓库位置里已经有包（搬迁器未能登记，但数据确实存在）");
        }

        if (probe.AppConfigExists)
        {
            return Skip(RepositorySetupSkipReason.AppConfigExists,
                "主配置已存在，说明用户配置过游戏路径，不是全新安装");
        }

        if (probe.LegacyInstallDataExists)
        {
            return Skip(RepositorySetupSkipReason.LegacyInstallDataExists,
                "安装目录里有旧版本留下的数据，是升级上来的老用户");
        }

        return new RepositorySetupDecision(true, RepositorySetupSkipReason.NotSkipped,
            "没有找到任何使用痕迹，判定为全新安装");
    }

    private static RepositorySetupDecision Skip(RepositorySetupSkipReason reason, string message)
        => new(false, reason, message);
}
