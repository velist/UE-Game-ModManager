using System;
using System.Collections.Generic;
using System.Linq;

namespace UEModManager.Services.Categories;

/// <summary>
/// "把 MOD 归到某个分类"这件事的纯规则：哪些分类能当目标、归类后 MOD 的分类列表长什么样。
/// 无 IO、无状态、不碰 WPF。
///
/// 单独拆出来是因为这套规则同时被三处用到——右键菜单要用它筛出可选项、
/// ViewModel 落盘前要用它算新值、侧边栏的系统分类判定也是同一份名单。
/// 此前这份名单在代码里躺着三份拷贝（<c>CategoryItem.SystemNames</c>、
/// <c>NewCategoryService.SystemOrder</c>、以及散落的字符串字面量），
/// 加一个系统分类就得记得同时改三处，漏一处的表现是"某个筛选视图变成了可写的真分类"，
/// 用户能把 MOD 移进"已启用"，然后发现它既不是分类也筛不出东西。
/// </summary>
public static class ModCategoryAssignment
{
    /// <summary>
    /// MOD 未归类时的兜底分类名。与 <c>ModInfo.Categories</c> 的默认值一致。
    /// </summary>
    public const string Uncategorized = "未分类";

    /// <summary>
    /// 三个系统分类，按侧边栏导航项的固定顺序排列。
    ///
    /// 它们是筛选视图而不是真实分类：MOD 不存"我属于已启用"，
    /// "已启用"是按 <c>IsEnabled</c> 现算出来的。所以它们既不能当移动目标，
    /// 也不能被删除或重命名。
    /// </summary>
    public static readonly IReadOnlyList<string> SystemCategoryNames = new[] { "全部", "已启用", "已禁用" };

    /// <summary>
    /// 判断一个分类名能不能作为"移动到分类"的目标。
    ///
    /// 空白名和系统分类都不行。前者是脏数据（历史文件里出现过），
    /// 后者会让用户把 MOD"移动"到一个根本不存储归属的筛选视图里——
    /// 界面上看起来成功了，刷新一次就打回原形。
    /// </summary>
    public static bool IsAssignableTarget(string? name)
        => !string.IsNullOrWhiteSpace(name)
           && !SystemCategoryNames.Contains(name.Trim(), StringComparer.Ordinal);

    /// <summary>
    /// 从一批分类名里筛出可以作为移动目标的那些，保持传入顺序，并按序去重。
    ///
    /// 保持顺序是有意的：侧边栏的分类顺序是用户自己拖出来的，
    /// 右键子菜单按另一种顺序排会让人以为点错了地方。
    /// </summary>
    public static IReadOnlyList<string> SelectAssignableTargets(IEnumerable<string?>? names)
    {
        var result = new List<string>();
        if (names == null)
            return result;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (!IsAssignableTarget(name))
                continue;

            var trimmed = name!.Trim();
            if (seen.Add(trimmed))
                result.Add(trimmed);
        }

        return result;
    }

    /// <summary>
    /// 算出"移动到 <paramref name="target"/>"之后 MOD 的分类列表。
    ///
    /// 语义是"移动"而不是"追加"，所以结果只有目标这一个元素：
    /// <c>ModInfo.PrimaryCategory</c> 取的是首元素，追加的话主分类不会变，
    /// 用户点了"移动到武器"却看到卡片上还写着原分类。
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="target"/> 不是合法的移动目标。</exception>
    public static List<string> BuildCategoriesFor(string? target)
    {
        if (!IsAssignableTarget(target))
            throw new ArgumentException($"「{target}」不能作为移动目标（系统分类或空名称）。", nameof(target));

        return new List<string> { target!.Trim() };
    }

    /// <summary>
    /// MOD 是否已经就在目标分类里了（首分类即目标）。用于跳过无意义的落盘。
    ///
    /// 只比首元素而不是"是否包含"：一个 MOD 可能同时带多个标签，
    /// 但"移动"关心的是主分类有没有变。
    /// </summary>
    public static bool IsAlreadyIn(IEnumerable<string?>? currentCategories, string? target)
    {
        if (!IsAssignableTarget(target))
            return false;

        var first = currentCategories?.FirstOrDefault();
        return first != null && string.Equals(first.Trim(), target!.Trim(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 一个包在界面上归在哪些分类下：有标签就是标签本身，一个都没有就是 <see cref="Uncategorized"/>。
    ///
    /// 列表的"分类"列、侧边栏计数、按分类筛选读的都是由这条规则算出的 <c>ModInfo.Categories</c>；
    /// 侧边栏补目录也必须用同一条，否则会出现"列表写着未分类、左侧却没有未分类"。
    /// </summary>
    public static List<string> DisplayCategoriesFor(IEnumerable<string?>? tags)
    {
        var result = Normalize(tags);
        if (result.Count == 0)
            result.Add(Uncategorized);
        return result;
    }

    /// <summary>
    /// 侧边栏"分类目录"里还缺哪些分类，按应当追加的顺序给出。
    ///
    /// 系统分类、空白名、目录里已有的（按名字精确匹配）都不算缺。
    /// 同一批里出现在 <paramref name="canonicalOrder"/> 中的内置分类按它排在前面，
    /// 其余按首次出现的顺序排在后面 —— 首次同步时一次补进好几个分类，
    /// 顺序不能取决于哪个包碰巧先被扫到。
    /// </summary>
    public static IReadOnlyList<string> SelectMissingCategories(
        IEnumerable<string?>? wanted, IEnumerable<string> existing, IReadOnlyList<string> canonicalOrder)
    {
        var have = new HashSet<string>(existing, StringComparer.Ordinal);
        return SelectAssignableTargets(wanted)
            .Where(name => !have.Contains(name))
            .Select((name, index) => (name, index, rank: IndexIn(canonicalOrder, name)))
            .OrderBy(x => x.rank < 0 ? int.MaxValue : x.rank)
            .ThenBy(x => x.index)
            .Select(x => x.name)
            .ToList();
    }

    /// <summary>
    /// 分类改名后，一个包的标签该变成什么。包不带 <paramref name="from"/> 时返回 null，表示不用动它。
    ///
    /// 就地替换而不是删掉再追加：标签顺序决定主分类（<c>PrimaryCategory</c> 取首个），
    /// 改名不该顺带换掉主分类。替换后与已有标签重名的合并成一个。
    /// </summary>
    public static List<string>? RenameTag(IEnumerable<string?>? tags, string from, string to)
    {
        var current = Normalize(tags);
        if (!current.Contains(from.Trim(), StringComparer.Ordinal))
            return null;

        return Normalize(current.Select(t => string.Equals(t, from.Trim(), StringComparison.Ordinal) ? to : t));
    }

    /// <summary>
    /// 删除分类后，一个包的标签该变成什么。包不带 <paramref name="name"/> 时返回 null，表示不用动它。
    ///
    /// 不管它是主分类还是附带的标签都要去掉：只要还有一个包带着它，
    /// 下一次刷新就会按标签把这个分类重新补回侧边栏。
    /// 结果可以是空列表 —— 那就是"未分类"（见 <see cref="DisplayCategoriesFor"/>），
    /// 不需要往包上写一个"未分类"字面量。
    /// </summary>
    public static List<string>? RemoveTag(IEnumerable<string?>? tags, string name)
    {
        var current = Normalize(tags);
        if (!current.Contains(name.Trim(), StringComparer.Ordinal))
            return null;

        return current.Where(t => !string.Equals(t, name.Trim(), StringComparison.Ordinal)).ToList();
    }

    /// <summary>去空白、去首尾空格、按序去重。</summary>
    private static List<string> Normalize(IEnumerable<string?>? tags)
    {
        var result = new List<string>();
        if (tags == null)
            return result;

        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag))
                continue;
            var trimmed = tag.Trim();
            if (!result.Contains(trimmed, StringComparer.Ordinal))
                result.Add(trimmed);
        }
        return result;
    }

    private static int IndexIn(IReadOnlyList<string> order, string name)
    {
        for (var i = 0; i < order.Count; i++)
            if (string.Equals(order[i], name, StringComparison.Ordinal))
                return i;
        return -1;
    }
}
