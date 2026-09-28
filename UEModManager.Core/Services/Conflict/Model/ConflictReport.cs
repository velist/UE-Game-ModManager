using System.Collections.Generic;

namespace UEModManager.Services.Conflict.Model;

/// <summary>
/// 一个冲突组里的一名成员。
/// </summary>
/// <param name="PackageKey">包标识。</param>
/// <param name="DisplayName">展示名（按钮上写的就是它）。</param>
public sealed record ConflictMember(string PackageKey, string DisplayName);

/// <summary>
/// 组内一个真正内容不同的共享资产。
/// </summary>
/// <param name="AssetKey">归一化资产键。</param>
/// <param name="DisplayPath">原始路径，展示用。</param>
/// <param name="Category">类别（贴图 / 模型 / …）。</param>
/// <param name="ShortName">短名，展示用。</param>
public sealed record ConflictAsset(
    string AssetKey, string DisplayPath, AssetCategory Category, string ShortName);

/// <summary>
/// 组内共享资源的类别统计，用于"都修改了 2 个相同资源：1 张贴图、1 个模型"。
/// </summary>
/// <param name="Counts">类别 → 数量。</param>
/// <param name="Summary">已经拼好的中文摘要。</param>
public sealed record AssetCategorySummary(IReadOnlyDictionary<AssetCategory, int> Counts, string Summary);

/// <summary>
/// 一组相互冲突的 MOD：它们改到了同一批资源、且内容不同。
///
/// "同时开启时只有一个会生效" —— 组是**一个决策单位**，用户在这里选一个，
/// 不需要理解谁覆盖谁。
/// </summary>
/// <param name="Members">2 个及以上成员，按展示名排序。</param>
/// <param name="SharedAssets">内容不同的共享资产。内容相同的重叠不进这里（也不进组）。</param>
/// <param name="Summary">共享资产的类别统计。</param>
public sealed record ConflictGroup(
    IReadOnlyList<ConflictMember> Members,
    IReadOnlyList<ConflictAsset> SharedAssets,
    AssetCategorySummary Summary);

/// <summary>
/// 内容完全相同的包。装哪个都一样，所以是"删掉多余的"而不是"选一个"。
/// </summary>
/// <param name="Primary">保留的那个（<c>indexes</c> 里靠前者）。</param>
/// <param name="Copies">可以删掉的副本。</param>
public sealed record DuplicateGroup(ConflictMember Primary, IReadOnlyList<ConflictMember> Copies);

/// <summary>
/// 多个包各自携带同一份运行时组件（UE4SS / CSharpLoader / 代理 DLL）。
///
/// 这类文件本来就是"带一份就够"，不是用户要决定的事情，所以自动裁决后单独列出。
/// </summary>
/// <param name="Name">组件名，如"UE4SS 运行时"。</param>
/// <param name="Providers">携带该组件的包。</param>
/// <param name="ChosenPackageKey">自动保留哪个包的版本。</param>
/// <param name="Reason">裁决理由："内容相同" / "版本较高 1.2.3" / "文件较新"。</param>
public sealed record SharedComponent(
    string Name,
    IReadOnlyList<SharedComponentProvider> Providers,
    string ChosenPackageKey,
    string Reason);

/// <summary>
/// 一个包携带的共享组件版本信息。
/// </summary>
/// <param name="PackageKey">包标识。</param>
/// <param name="DisplayName">展示名。</param>
/// <param name="Version">PE 文件版本（仅 .dll/.exe 有），否则 null。</param>
/// <param name="Modified">文件最后写入时间。</param>
/// <param name="IdenticalToChosen">与最终保留的那份内容是否相同。</param>
public sealed record SharedComponentProvider(
    string PackageKey, string DisplayName, string? Version, System.DateTime? Modified, bool IdenticalToChosen);

/// <summary>
/// 报告统计。
/// </summary>
/// <param name="EnabledMods">参与分析的已启用包数。</param>
/// <param name="IndexedAssets">索引到的资产条目总数。</param>
/// <param name="IdenticalOverlapsHidden">内容完全相同、因此没有报成冲突的重叠数。</param>
/// <param name="SharedComponentFilesHidden">判定为共享组件 / 说明文件、因此不参与比对的文件数。</param>
public sealed record ConflictStats(
    int EnabledMods, int IndexedAssets, int IdenticalOverlapsHidden, int SharedComponentFilesHidden);

/// <summary>
/// 一次冲突分析的完整结果 —— 面板上看到的一切都来自这里。
/// </summary>
public sealed record ConflictReport(
    IReadOnlyList<ConflictGroup> Groups,
    IReadOnlyList<DuplicateGroup> Duplicates,
    IReadOnlyList<SharedComponent> SharedComponents,
    IReadOnlyList<ConflictMember> Unanalyzable,
    ConflictStats Stats);
