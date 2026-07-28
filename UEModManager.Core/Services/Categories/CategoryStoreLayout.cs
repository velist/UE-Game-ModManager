using System;
using System.Collections.Generic;
using System.Linq;

namespace UEModManager.Services.Categories;

/// <summary>
/// 分类存储文件的命名与加载优先级。纯字符串逻辑，无 IO、无状态。
///
/// 之所以值得单独一层：分类文件按游戏名分片（"悟空_categories.json"），
/// 而历史上有一整段时间当前游戏名从未被设置（<c>NewCategoryService.SetCurrentGameAsync</c>
/// 在生产代码里零调用），所有游戏的分类都写进了没有前缀的 "_categories.json"。
/// 加载时得在"本游戏自己的文件 / 那份缺陷期的共用文件 / 其他游戏的文件"之间排一个
/// 优先级，排错的代价是用户的自定义分类看起来平白丢了。
/// </summary>
public static class CategoryStoreLayout
{
    /// <summary>分类文件的固定后缀。</summary>
    public const string FileSuffix = "_categories.json";

    /// <summary>
    /// 游戏名为空时写出的文件名，也就是缺陷期所有游戏共用的那一份。
    /// </summary>
    public const string SharedLegacyFileName = FileSuffix;

    /// <summary>
    /// 给定游戏的分类文件名。游戏名为空时退化成共用的遗留文件名——
    /// 保持这个退化行为，缺陷期写出的数据才找得回来。
    /// </summary>
    public static string FileNameFor(string? gameName)
        => (gameName?.Trim() ?? string.Empty) + FileSuffix;

    /// <summary>
    /// 排出加载候选的先后顺序：本游戏自己的文件 → 缺陷期的共用文件 → 其他游戏的文件。
    ///
    /// 其他游戏的文件保持 <paramref name="existingFileNames"/> 的传入顺序
    /// （调用方按最后写入时间倒序给，最近用过的游戏优先被继承）。
    /// 不以 <see cref="FileSuffix"/> 结尾的名字直接忽略，重复项只保留第一次出现。
    /// </summary>
    public static IReadOnlyList<string> ResolveLoadOrder(string? gameName, IEnumerable<string>? existingFileNames)
    {
        var ordered = new List<string>();
        if (existingFileNames == null)
            return ordered;

        var candidates = existingFileNames
            .Where(n => !string.IsNullOrEmpty(n)
                        && n.EndsWith(FileSuffix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        void Take(string? name)
        {
            if (name == null) return;
            if (ordered.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
            ordered.Add(name);
        }

        var own = FileNameFor(gameName);
        Take(candidates.FirstOrDefault(n => string.Equals(n, own, StringComparison.OrdinalIgnoreCase)));
        Take(candidates.FirstOrDefault(n => string.Equals(n, SharedLegacyFileName, StringComparison.OrdinalIgnoreCase)));

        foreach (var name in candidates)
            Take(name);

        return ordered;
    }
}
