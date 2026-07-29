using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace UEModManager.Services.Paths;

/// <summary>
/// 引导界面上列出的一个盘。由主项目查 <c>DriveInfo</c> 后填入。
/// </summary>
/// <param name="RootPath">卷根，例如 <c>D:\</c>。用户选它时会作为目录选择器的起点。</param>
/// <param name="DisplayName">盘符加卷标，例如 <c>D: 数据</c>；没有卷标时就只有盘符。</param>
/// <param name="Kind">卷类型。</param>
/// <param name="AvailableBytes">可用字节数；查不到为 <c>null</c>。</param>
/// <param name="TotalBytes">总容量；查不到为 <c>null</c>。</param>
/// <param name="IsCurrentDefault">当前默认仓库位置是否就在这个盘上。</param>
public sealed record RepositoryDriveOption(
    string RootPath,
    string DisplayName,
    RepositoryVolumeKind Kind,
    long? AvailableBytes,
    long? TotalBytes,
    bool IsCurrentDefault);

/// <summary>
/// 引导界面上盘位列表的排序与推荐（纯函数，不碰 IO）。
///
/// <para>
/// 硬性要求是"帮用户做决定，而不是甩一个路径选择器给他"。对一个普通玩家来说，
/// 需要判断的其实只有一件事：哪个盘装得下几十 GB 而且不会哪天不见。
/// 于是排序规则就三条，且每一条都对应一个真实后果：
/// </para>
/// <list type="number">
/// <item><b>固定盘排在最前。</b>可移动盘拔掉、网络位置断线，都会让整个仓库当场读不到；
/// 把它们混在列表中间等于默认它们和本机硬盘是一回事。</item>
/// <item><b>固定盘之间按可用空间降序。</b>这正是用户唯一要看的数字。</item>
/// <item><b>查不到空间的排最后。</b>不是因为它们更差，而是因为界面上它们只能显示"未知"，
/// 放在前面会挤掉真正能帮他做判断的那几行。</item>
/// </list>
///
/// <para>
/// <b>推荐项刻意可以为空。</b>只有固定盘才够资格被推荐；一台只有系统盘、且系统盘也快满了的
/// 机器上，与其硬推一个位置，不如什么都不推、让用户自己选 —— 推荐一个装不下的盘
/// 比不推荐更糟。
/// </para>
/// </summary>
public static class RepositoryDriveAdvisor
{
    /// <summary>按上面三条规则排序。</summary>
    public static IReadOnlyList<RepositoryDriveOption> Rank(IEnumerable<RepositoryDriveOption> drives)
    {
        if (drives is null) throw new ArgumentNullException(nameof(drives));

        var ordered = drives
            .OrderBy(d => KindRank(d.Kind))
            .ThenBy(d => d.AvailableBytes is null ? 1 : 0)
            .ThenByDescending(d => d.AvailableBytes ?? 0)
            .ThenBy(d => d.RootPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ReadOnlyCollection<RepositoryDriveOption>(ordered);
    }

    /// <summary>
    /// 从已排序的列表里挑一个推荐项：可用空间最大、且满足
    /// <see cref="RepositoryLocationValidator.RecommendedFreeBytes"/> 的固定盘。
    /// 没有符合条件的盘时返回 <c>null</c>。
    /// </summary>
    public static RepositoryDriveOption? Recommend(IEnumerable<RepositoryDriveOption> drives)
    {
        if (drives is null) throw new ArgumentNullException(nameof(drives));

        return drives
            .Where(d => d.Kind == RepositoryVolumeKind.Fixed)
            .Where(d => d.AvailableBytes >= RepositoryLocationValidator.RecommendedFreeBytes)
            .OrderByDescending(d => d.AvailableBytes ?? 0)
            .ThenBy(d => d.RootPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static int KindRank(RepositoryVolumeKind kind) => kind switch
    {
        RepositoryVolumeKind.Fixed => 0,
        RepositoryVolumeKind.Unknown => 1,
        RepositoryVolumeKind.Removable => 2,
        RepositoryVolumeKind.Network => 3,
        _ => 4,
    };
}
