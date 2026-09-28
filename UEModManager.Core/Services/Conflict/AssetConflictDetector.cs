using System;
using System.Collections.Generic;
using System.Linq;
using UEModManager.Models;
using UEModManager.Services.Conflict.Model;

// 本命名空间里还有一个同名的旧 ConflictGroup（"胜者 + 败者集合"的分组对象，
// 由 ConflictGrouping 产出，只服务于旧面板）。C# 的名字解析让**本命名空间自己的类型**
// 优先于 using 别名，所以同名的别名在这里无效，只能换一个不与旧类型撞名的短名。
// 旧类型随 P2 删除；在那之前，本文件的 AssetGroup 一律指新模型。
using AssetGroup = UEModManager.Services.Conflict.Model.ConflictGroup;

namespace UEModManager.Services.Conflict;

/// <summary>
/// 把多个包的资产索引变成一份冲突报告。
///
/// 旧模型是"同一目标路径被多个包声明"的**文件级**冲突，需要用户为每条路径指定胜者：
/// 实测 20 个包报出 582 条路径冲突，其中 569 条集中在同一对 MOD 上，
/// 而真正的内容分歧只有 2 处。用户数的是 MOD，界面给的是文件，"582 个冲突"就此成立。
///
/// 新模型按**资产**比对，冲突组是"一批 MOD 改到了同一批资源" ——
/// 一个组给一个决策，用户不需要理解谁覆盖谁。
///
/// 分两次调用：<see cref="FindOverlaps"/> 找出重叠资产，调用方补算指纹，
/// 再调 <see cref="Build"/> 出报告。拆开的原因是算指纹要读容器数据区，
/// 那是主程序侧的 IO；Core 这一层只做纯计算。
/// </summary>
public static class AssetConflictDetector
{
    /// <summary>
    /// 第一遍：找出被 2 个以上包提供的资产键，供调用方补算指纹。
    ///
    /// 返回**每个包的全部条目**，不按包去重：一个资产键下通常挂着
    /// <c>.uasset</c> / <c>.uexp</c> / <c>.ubulk</c> 三份文件，它们共用一个键但各有各的指纹。
    /// 只留一条会让后面的"内容是否相同"只比三个文件中的一个 ——
    /// 两张贴图主文件相同、纹理数据不同（很常见）时会被误判成"内容相同"而静默丢进隐藏计数里。
    /// </summary>
    public static IReadOnlyList<(string AssetKey, IReadOnlyList<(string PackageKey, AssetEntry Entry)> Entries)>
        FindOverlaps(IReadOnlyList<ModAssetIndex> indexes)
    {
        ArgumentNullException.ThrowIfNull(indexes);

        return indexes
            .Where(i => i.Status is IndexStatus.Complete or IndexStatus.Partial)
            .SelectMany(index => index.Entries.Select(entry => (index.PackageKey, Entry: entry)))
            .GroupBy(x => x.Entry.AssetKey, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(x => x.PackageKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(g => (
                g.Key,
                (IReadOnlyList<(string, AssetEntry)>)g
                    .OrderBy(x => x.PackageKey, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Entry.DisplayPath, StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .ToList();
    }

    /// <summary>
    /// 第二遍：生成报告。<paramref name="indexes"/> 里重叠条目的
    /// <see cref="AssetEntry.Fingerprint"/> 此时应已填好。
    /// </summary>
    /// <param name="indexes">参与分析的包（调用方已按"已启用"筛过）。</param>
    /// <param name="rules">引擎的共享组件规则。空列表即不做共享组件分类。</param>
    /// <param name="provenance">
    /// 包 → 共享组件裁决所需的版本 / 修改时间。缺省或缺失某包时，
    /// 该包在裁决里既没有版本也没有时间，只能退回输入顺序。
    /// </param>
    public static ConflictReport Build(
        IReadOnlyList<ModAssetIndex> indexes,
        IReadOnlyList<SharedComponentRule> rules,
        IReadOnlyDictionary<string, SharedComponentProvenance>? provenance = null)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        ArgumentNullException.ThrowIfNull(rules);

        // ① 共享组件与说明文件先摘出去：它们本来就该重叠，不是用户要决定的事。
        var componentFiles = new List<ComponentFile>();
        var attachmentCount = 0;
        var filtered = StripNonModComponents(indexes, rules, componentFiles, ref attachmentCount);

        // ② 整包内容相同 → 重复，不参与后续比对。
        var (duplicates, comparable) = SplitDuplicates(filtered);

        // ③ 索引读不出来的包 → 无法分析，同样不参与比对。
        var unanalyzable = new List<ConflictMember>();
        var analyzable = new List<ModAssetIndex>();
        foreach (var index in comparable)
        {
            if (index.Status is IndexStatus.Encrypted or IndexStatus.Unsupported or IndexStatus.Failed)
                unanalyzable.Add(new ConflictMember(index.PackageKey, index.DisplayName));
            else
                analyzable.Add(index);
        }

        // ④⑤ 按资产键聚合所有者；指纹全同的重叠丢弃。
        var (shared, identicalHidden) = CollectSharedAssets(analyzable);

        // ⑥⑦ 所有者包集合相同的资产合并成组。
        var groups = BuildGroups(shared);

        // ⑧ 共享组件裁决。
        var components = ResolveSharedComponents(componentFiles, indexes, provenance);

        return new ConflictReport(
            Groups: groups,
            Duplicates: duplicates,
            SharedComponents: components,
            Unanalyzable: unanalyzable,
            Stats: new ConflictStats(
                EnabledMods: indexes.Count,
                IndexedAssets: analyzable.Sum(i => i.Entries.Count) + componentFiles.Count + attachmentCount,
                IdenticalOverlapsHidden: identicalHidden,
                SharedComponentFilesHidden: componentFiles.Count + attachmentCount));
    }

    /// <summary>一个命中共享组件规则的文件。</summary>
    private sealed record ComponentFile(
        string PackageKey, string ComponentName, string DisplayPath, string? Fingerprint);

    /// <summary>
    /// ① 摘出非 MOD 包里的共享组件条目与说明文件。
    ///
    /// 只对非 MOD 包的条目生效：pak 内部的路径是引擎资产路径，
    /// 与 MOD 自带 <c>ue4ss/</c> 目录里的文件没有可比性。
    /// </summary>
    private static List<ModAssetIndex> StripNonModComponents(
        IReadOnlyList<ModAssetIndex> indexes,
        IReadOnlyList<SharedComponentRule> rules,
        List<ComponentFile> componentFiles,
        ref int attachmentCount)
    {
        var result = new List<ModAssetIndex>(indexes.Count);

        foreach (var index in indexes)
        {
            if (index.Kind == PackageKind.Mod)
            {
                result.Add(index);
                continue;
            }

            var kept = new List<AssetEntry>();
            foreach (var entry in index.Entries)
            {
                // DisplayPath 优先：规则表里写的是 dwmapi.dll 这类带扩展名的文件名，
                // 而 pak 条目的 AssetKey 已去扩展名。两个都试，避免任一种来源漏判。
                var component = SharedComponentClassifier.Classify(entry.DisplayPath, rules)
                    ?? SharedComponentClassifier.Classify(entry.AssetKey, rules);

                if (component is not null)
                {
                    componentFiles.Add(new ComponentFile(
                        index.PackageKey, component, entry.DisplayPath, entry.Fingerprint));
                    continue;
                }

                if (SharedComponentClassifier.IsAttachment(entry.DisplayPath))
                {
                    attachmentCount++;
                    continue;
                }

                kept.Add(entry);
            }

            result.Add(index with { Entries = kept });
        }

        return result;
    }

    /// <summary>② 整包内容相同的归为重复，Primary 取输入里靠前者。</summary>
    private static (List<DuplicateGroup> Duplicates, List<ModAssetIndex> Comparable) SplitDuplicates(
        IReadOnlyList<ModAssetIndex> indexes)
    {
        var duplicates = new List<DuplicateGroup>();
        var comparable = new List<ModAssetIndex>();
        var buckets = new Dictionary<string, List<ModAssetIndex>>(StringComparer.OrdinalIgnoreCase);

        foreach (var index in indexes)
        {
            if (string.IsNullOrWhiteSpace(index.ContentHash))
            {
                comparable.Add(index);
                continue;
            }

            if (!buckets.TryGetValue(index.ContentHash, out var bucket))
                buckets[index.ContentHash] = bucket = [];
            bucket.Add(index);
        }

        // Primary 是输入里靠前的那个，副本按展示名排序 —— 展示顺序与输入顺序无关。
        foreach (var bucket in buckets.Values)
        {
            comparable.Add(bucket[0]);
            if (bucket.Count <= 1) continue;

            duplicates.Add(new DuplicateGroup(
                Primary: new ConflictMember(bucket[0].PackageKey, bucket[0].DisplayName),
                Copies: bucket.Skip(1)
                    .Select(i => new ConflictMember(i.PackageKey, i.DisplayName))
                    .OrderBy(m => m.DisplayName, StringComparer.Ordinal)
                    .ToList()));
        }

        return (duplicates, comparable);
    }

    /// <summary>一个资产键的共享情况。</summary>
    private sealed record SharedAsset(string AssetKey, string DisplayPath, List<ConflictMember> Owners);

    /// <summary>
    /// ④⑤ 按资产键聚合所有者，内容相同的重叠丢弃并计数。
    ///
    /// 比较的是**每个包在该资产键下的全部文件**拼出来的签名，而不是单个条目：
    /// 一个键下有 .uasset/.uexp/.ubulk 三份，只比其中一份会把
    /// "主文件相同、纹理数据不同"误判成相同。任一指纹为空 → 保守当成不同，
    /// 报一个可能多余的冲突好过漏报一个真实冲突。
    /// </summary>
    private static (List<SharedAsset> Shared, int IdenticalHidden) CollectSharedAssets(
        List<ModAssetIndex> analyzable)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var index in analyzable) names[index.PackageKey] = index.DisplayName;

        // assetKey → (packageKey → 该包在这个键下的全部条目)
        var byAsset = new Dictionary<string, Dictionary<string, List<AssetEntry>>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var index in analyzable)
        {
            foreach (var entry in index.Entries)
            {
                if (!byAsset.TryGetValue(entry.AssetKey, out var owners))
                {
                    byAsset[entry.AssetKey] = owners =
                        new Dictionary<string, List<AssetEntry>>(StringComparer.OrdinalIgnoreCase);
                    order.Add(entry.AssetKey);
                }
                if (!owners.TryGetValue(index.PackageKey, out var list))
                    owners[index.PackageKey] = list = [];
                list.Add(entry);
            }
        }

        var shared = new List<SharedAsset>();
        var identicalHidden = 0;

        foreach (var assetKey in order)
        {
            var owners = byAsset[assetKey];
            if (owners.Count <= 1) continue;

            if (SignaturesIdentical(owners.Values))
            {
                identicalHidden++;
                continue;
            }

            var displayPath = owners.Values.SelectMany(v => v).First().DisplayPath;
            shared.Add(new SharedAsset(
                assetKey,
                displayPath,
                owners.Keys.Select(k => new ConflictMember(k, names[k])).ToList()));
        }

        return (shared, identicalHidden);
    }

    /// <summary>
    /// 每个包在该资产键下的文件指纹集合是否一致。
    /// 任一指纹为空 → 视为不同（保守）。
    /// </summary>
    private static bool SignaturesIdentical(IEnumerable<List<AssetEntry>> owners)
    {
        string? first = null;
        foreach (var entries in owners)
        {
            var signature = Signature(entries);
            if (signature is null) return false;
            if (first is null) { first = signature; continue; }
            if (!string.Equals(first, signature, StringComparison.Ordinal)) return false;
        }
        return first is not null;
    }

    /// <summary>一个包在该资产键下全部文件的指纹签名；有任一指纹为空时返回 null。</summary>
    private static string? Signature(List<AssetEntry> entries)
    {
        var fingerprints = entries
            .OrderBy(e => e.DisplayPath, StringComparer.OrdinalIgnoreCase)
            .Select(e => e.Fingerprint)
            .ToList();
        if (fingerprints.Any(f => f is null)) return null;
        return string.Join("|", fingerprints!);
    }

    /// <summary>
    /// ⑥⑦ 所有者包集合相同的资产合并成一个组；组按共同资源数降序，再按首个展示名。
    ///
    /// 三个包改到同一张贴图是一个三人组，而不是三个两两组合的组 ——
    /// 用户要决定的是"这三个里留哪个"，不是"每对之间谁赢"。
    /// </summary>
    private static List<AssetGroup> BuildGroups(List<SharedAsset> shared)
    {
        var buckets = new Dictionary<string, List<SharedAsset>>(StringComparer.Ordinal);
        foreach (var item in shared)
        {
            var key = string.Join("\n", item.Owners
                .Select(o => o.PackageKey)
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
            if (!buckets.TryGetValue(key, out var bucket)) buckets[key] = bucket = [];
            bucket.Add(item);
        }

        var groups = new List<AssetGroup>();
        foreach (var bucket in buckets.Values)
        {
            var assets = bucket
                .Select(s => new ConflictAsset(
                    s.AssetKey,
                    s.DisplayPath,
                    AssetPathDescriber.Categorize(s.AssetKey),
                    AssetPathDescriber.ShortName(s.AssetKey)))
                .OrderBy(a => a.AssetKey, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var members = bucket[0].Owners
                .OrderBy(m => m.DisplayName, StringComparer.Ordinal)
                .ToList();

            var counts = new Dictionary<AssetCategory, int>();
            foreach (var asset in assets)
                counts[asset.Category] = counts.GetValueOrDefault(asset.Category) + 1;

            groups.Add(new AssetGroup(
                Members: members,
                SharedAssets: assets,
                Summary: new AssetCategorySummary(
                    counts, AssetPathDescriber.Summarize(assets.Select(a => a.Category)))));
        }

        return groups
            .OrderByDescending(g => g.SharedAssets.Count)
            .ThenBy(g => g.Members[0].DisplayName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// ⑧ 共享组件裁决。
    ///
    /// - Provider 内容指纹全同 → 取输入里靠前者，理由"内容相同"。
    /// - 不同 → 版本最高的那个，理由"版本较高 {v}"。
    /// - 都没有版本 → 修改时间最新的，理由"文件较新"。
    /// - 连时间都没有 → 退回输入顺序，理由"内容不同且无法比较版本"（不谎称内容相同）。
    /// </summary>
    private static List<SharedComponent> ResolveSharedComponents(
        List<ComponentFile> componentFiles,
        IReadOnlyList<ModAssetIndex> indexes,
        IReadOnlyDictionary<string, SharedComponentProvenance>? provenance)
    {
        var displayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < indexes.Count; i++)
        {
            displayNames[indexes[i].PackageKey] = indexes[i].DisplayName;
            order.TryAdd(indexes[i].PackageKey, i);
        }

        var result = new List<SharedComponent>();

        foreach (var group in componentFiles.GroupBy(f => f.ComponentName, StringComparer.Ordinal))
        {
            // Provider 按输入先后排序，用于"内容相同"时取靠前者。
            var providers = group
                .Select(f => f.PackageKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => order.GetValueOrDefault(p, int.MaxValue))
                .ToList();
            if (providers.Count == 0) continue;

            var contentsIdentical = ContentsIdentical(group.ToList(), providers);
            var (chosen, reason) = ChooseProvider(group.ToList(), providers, contentsIdentical, provenance);

            result.Add(new SharedComponent(
                Name: group.Key,
                Providers: providers.Select(p => new SharedComponentProvider(
                    PackageKey: p,
                    DisplayName: displayNames.GetValueOrDefault(p, p),
                    Version: provenance?.GetValueOrDefault(p)?.Version,
                    Modified: provenance?.GetValueOrDefault(p)?.Modified,
                    IdenticalToChosen: contentsIdentical
                        || string.Equals(p, chosen, StringComparison.OrdinalIgnoreCase))).ToList(),
                ChosenPackageKey: chosen,
                Reason: reason));
        }

        return result.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// 各 Provider 是否携带同一份组件。
    ///
    /// 比的是**所有 Provider 都提供的那些文件**，不是各自提供的全部文件：
    /// 落在同一路径上的文件才会真的互相覆盖，而各包自带的独有文件（例如
    /// UE4SS 下每个 Mod 自己的 Scripts）根本不冲突，把它们算进来会让
    /// "同一个 UE4SS 运行时"因为各带各的模组脚本而判成不同。
    ///
    /// 实测：超级筋斗云与超级识破各带 572 个 ue4ss 文件，其中 569 个路径重合
    /// 且内容逐字节相同，各自另有 3 个独有文件。按交集比 → "内容相同"，正确。
    /// </summary>
    private static bool ContentsIdentical(List<ComponentFile> files, List<string> providers)
    {
        if (providers.Count <= 1) return false;

        var byProvider = new Dictionary<string, List<ComponentFile>>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
            byProvider[provider] = files
                .Where(f => string.Equals(f.PackageKey, provider, StringComparison.OrdinalIgnoreCase))
                .ToList();

        var commonPaths = byProvider[providers[0]]
            .Select(f => f.DisplayPath)
            .Where(path => providers.All(p => byProvider[p]
                .Any(f => string.Equals(f.DisplayPath, path, StringComparison.OrdinalIgnoreCase))))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (commonPaths.Count == 0) return false;

        foreach (var path in commonPaths)
        {
            string? first = null;
            foreach (var provider in providers)
            {
                var fingerprint = byProvider[provider]
                    .First(f => string.Equals(f.DisplayPath, path, StringComparison.OrdinalIgnoreCase))
                    .Fingerprint;

                // 指纹读不出来时无从判定"相同"，保守按不同处理。
                if (fingerprint is null) return false;
                if (first is null) { first = fingerprint; continue; }
                if (!string.Equals(first, fingerprint, StringComparison.OrdinalIgnoreCase)) return false;
            }
        }

        return true;
    }

    private static (string Chosen, string Reason) ChooseProvider(
        List<ComponentFile> files,
        List<string> providers,
        bool contentsIdentical,
        IReadOnlyDictionary<string, SharedComponentProvenance>? provenance)
    {
        if (contentsIdentical) return (providers[0], "内容相同");

        if (provenance is not null)
        {
            // 版本最高者。用 System.Version 比较，1.10 才能正确排在 1.9 之后。
            var best = providers
                .Select(p => (PackageKey: p, Version: ParseVersion(provenance.GetValueOrDefault(p)?.Version)))
                .Where(x => x.Version is not null)
                .OrderByDescending(x => x.Version)
                .FirstOrDefault();
            if (best.Version is not null)
                return (best.PackageKey, $"版本较高 {provenance.GetValueOrDefault(best.PackageKey)?.Version}");

            // 没有版本就比文件时间。
            var newest = providers
                .Select(p => (PackageKey: p, Modified: provenance.GetValueOrDefault(p)?.Modified))
                .Where(x => x.Modified is not null)
                .OrderByDescending(x => x.Modified)
                .FirstOrDefault();
            if (newest.Modified is not null) return (newest.PackageKey, "文件较新");
        }

        // 内容确实不同、又没有版本也没有时间可比：只能按输入顺序取一个。
        // 这里**不能**沿用"内容相同" —— 理由是用来解释"为什么留下的是这份"的，
        // 在内容已经确认不同的前提下写"内容相同"是在说假话。
        return (providers[0], "内容不同且无法比较版本");
    }

    private static Version? ParseVersion(string? text)
        => Version.TryParse(text, out var version) ? version : null;
}
