using System;
using System.Collections.Generic;
using UEModManager.Services.Categories;

namespace UEModManager.Core.Tests.Services.Categories;

/// <summary>
/// "移动到分类"的规则。
///
/// 这些断言对应的不是抽象的正确性，而是一个具体事故：右键菜单里的"移动到分类"
/// 从 v2.0 基线起就是个死菜单——XAML 里挂着，C# 里零引用，样式里连能显示子项的
/// 模板都没有。重新接线时最容易踩的两个坑各有一条测试守着：
/// 把系统分类也列进可选项（移进去等于什么都没做），
/// 以及把"移动"实现成"追加"（主分类不变，用户看不出移动生效）。
/// </summary>
public class ModCategoryAssignmentTests
{
    // ─── 目标合法性 ───

    [Theory]
    [InlineData("全部")]
    [InlineData("已启用")]
    [InlineData("已禁用")]
    public void 系统分类不能作为移动目标(string name)
    {
        // 这三个是按 IsEnabled 现算的筛选视图，不存储归属。
        // 放进菜单的话，用户点完会看到一次"成功"，刷新后 MOD 回到原分类。
        Assert.False(ModCategoryAssignment.IsAssignableTarget(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空白名不能作为移动目标(string? name)
    {
        Assert.False(ModCategoryAssignment.IsAssignableTarget(name));
    }

    [Theory]
    [InlineData("武器")]
    [InlineData("未分类")]
    [InlineData("面部")]
    public void 普通分类可以作为移动目标(string name)
    {
        // "未分类" 是真实分类（ModInfo.Categories 的默认值），不是筛选视图，
        // 必须允许移回去——否则用户没有任何办法撤销一次误分类。
        Assert.True(ModCategoryAssignment.IsAssignableTarget(name));
    }

    // ─── 筛选可选项 ───

    [Fact]
    public void 筛选可选项时剔除系统分类并保持原有顺序()
    {
        var names = new[] { "全部", "已启用", "已禁用", "武器", "面部", "未分类" };

        var targets = ModCategoryAssignment.SelectAssignableTargets(names);

        // 顺序即侧边栏顺序：用户自己拖出来的排序，菜单里换个顺序会让人以为点错了地方
        Assert.Equal(new[] { "武器", "面部", "未分类" }, targets);
    }

    [Fact]
    public void 筛选可选项时按序去重()
    {
        var names = new[] { "武器", "面部", "武器" };

        Assert.Equal(new[] { "武器", "面部" }, ModCategoryAssignment.SelectAssignableTargets(names));
    }

    [Fact]
    public void 筛选可选项容忍空输入()
    {
        Assert.Empty(ModCategoryAssignment.SelectAssignableTargets(null));
        Assert.Empty(ModCategoryAssignment.SelectAssignableTargets(new string?[] { null, "", "  " }));
    }

    [Fact]
    public void 只有系统分类时可选项为空_菜单应据此禁用自己()
    {
        var targets = ModCategoryAssignment.SelectAssignableTargets(
            new[] { "全部", "已启用", "已禁用" });

        Assert.Empty(targets);
    }

    // ─── 计算移动结果 ───

    [Fact]
    public void 移动是替换而不是追加()
    {
        // PrimaryCategory 取首元素。追加的话主分类不变，
        // 用户点了"移动到武器"却看到卡片上还写着原分类。
        var result = ModCategoryAssignment.BuildCategoriesFor("武器");

        Assert.Equal(new[] { "武器" }, result);
    }

    [Fact]
    public void 计算移动结果时去掉首尾空白()
    {
        Assert.Equal(new[] { "武器" }, ModCategoryAssignment.BuildCategoriesFor("  武器  "));
    }

    [Theory]
    [InlineData("已启用")]
    [InlineData("")]
    [InlineData(null)]
    public void 非法目标直接抛而不是悄悄产出脏数据(string? target)
    {
        Assert.Throws<ArgumentException>(() => ModCategoryAssignment.BuildCategoriesFor(target));
    }

    // ─── 幂等判断 ───

    [Fact]
    public void 已在目标分类时判定为无需移动()
    {
        Assert.True(ModCategoryAssignment.IsAlreadyIn(new List<string> { "武器" }, "武器"));
    }

    [Fact]
    public void 只比主分类而不是是否包含()
    {
        // 一个 MOD 可以带多个标签，但"移动"关心的是主分类有没有变。
        // 用 Contains 判断的话，带着 ["面部","武器"] 的 MOD 移到"武器"会被当成
        // 无需处理而直接跳过，主分类永远停在"面部"。
        var current = new List<string> { "面部", "武器" };

        Assert.False(ModCategoryAssignment.IsAlreadyIn(current, "武器"));
        Assert.True(ModCategoryAssignment.IsAlreadyIn(current, "面部"));
    }

    [Fact]
    public void 空分类列表不判定为已在目标()
    {
        Assert.False(ModCategoryAssignment.IsAlreadyIn(new List<string>(), "武器"));
        Assert.False(ModCategoryAssignment.IsAlreadyIn(null, "武器"));
    }

    // ─── 系统分类名单本身 ───

    [Fact]
    public void 系统分类名单与侧边栏三个导航项一致()
    {
        // 名单是单一事实来源：CategoryItem.SystemNames 和 NewCategoryService.SystemOrder
        // 都取自这里。顺序也有意义——NormalizeForDisplay 按它把三项钉在最前面。
        Assert.Equal(new[] { "全部", "已启用", "已禁用" },
            ModCategoryAssignment.SystemCategoryNames);
    }

    // ─── 包在界面上显示成哪些分类 ───
    //
    // 事故：分类目录（侧边栏）和列表"分类"列是两份数据，从没有人把后者同步给前者，
    // 列表每行都写着"人物/其他"，左侧目录却是空的。同步必须和列表用同一条显示规则。

    [Fact]
    public void 没有标签的包显示为未分类()
    {
        Assert.Equal(new[] { "未分类" }, ModCategoryAssignment.DisplayCategoriesFor(new List<string>()));
        Assert.Equal(new[] { "未分类" }, ModCategoryAssignment.DisplayCategoriesFor(null));
        Assert.Equal(new[] { "未分类" }, ModCategoryAssignment.DisplayCategoriesFor(new[] { " ", "" }));
    }

    [Fact]
    public void 有标签的包按标签显示且保持顺序()
    {
        Assert.Equal(new[] { "武器", "人物" },
            ModCategoryAssignment.DisplayCategoriesFor(new[] { " 武器", "人物", "武器" }));
    }

    // ─── 侧边栏缺哪些分类 ───

    private static readonly string[] Canonical = { "面部", "人物", "武器", "服装", "发型", "修改", "其他", "未分类" };

    [Fact]
    public void 已有的系统的空白的都不算缺()
    {
        var missing = ModCategoryAssignment.SelectMissingCategories(
            new[] { "人物", "全部", "  ", "已启用", "武器" },
            new[] { "全部", "已启用", "已禁用", "人物" },
            Canonical);

        Assert.Equal(new[] { "武器" }, missing);
    }

    [Fact]
    public void 内置分类按固定顺序排在前面其余按出现顺序排在后面()
    {
        // 首次同步一次补进好几个：顺序不能取决于哪个包碰巧先被扫到。
        var missing = ModCategoryAssignment.SelectMissingCategories(
            new[] { "未分类", "自定义B", "其他", "自定义A", "人物", "其他" },
            Array.Empty<string>(),
            Canonical);

        Assert.Equal(new[] { "人物", "其他", "未分类", "自定义B", "自定义A" }, missing);
    }

    // ─── 改名 / 删除分类后标签怎么变 ───
    //
    // 分类的权威存储是包上的标签。只改侧边栏的话，下一次刷新会按旧标签
    // 把旧分类重新补回来——改名变成"多出一个同名旧分类"，删除变成"删了又长回来"。

    [Fact]
    public void 改名就地替换不改变主分类()
    {
        Assert.Equal(new[] { "人物", "兵器" },
            ModCategoryAssignment.RenameTag(new[] { "人物", "武器" }, "武器", "兵器"));
        Assert.Equal(new[] { "兵器", "人物" },
            ModCategoryAssignment.RenameTag(new[] { "武器", "人物" }, "武器", "兵器"));
    }

    [Fact]
    public void 改名后与已有标签重名时合并()
    {
        Assert.Equal(new[] { "人物" },
            ModCategoryAssignment.RenameTag(new[] { "武器", "人物" }, "武器", "人物"));
    }

    [Fact]
    public void 不带该分类的包改名和删除都不用动()
    {
        Assert.Null(ModCategoryAssignment.RenameTag(new[] { "人物" }, "武器", "兵器"));
        Assert.Null(ModCategoryAssignment.RenameTag(new List<string>(), "武器", "兵器"));
        Assert.Null(ModCategoryAssignment.RemoveTag(new[] { "人物" }, "武器"));
        Assert.Null(ModCategoryAssignment.RemoveTag(null, "武器"));
    }

    [Fact]
    public void 删除分类时附带标签也要去掉()
    {
        // 只处理"主分类是它"的包，第二个标签是它的包会让分类在下次刷新时复活。
        Assert.Equal(new[] { "人物" },
            ModCategoryAssignment.RemoveTag(new[] { "人物", "武器" }, "武器"));
    }

    [Fact]
    public void 删掉唯一的标签后是空列表也就是未分类()
    {
        var tags = ModCategoryAssignment.RemoveTag(new[] { "武器" }, "武器");

        Assert.NotNull(tags);
        Assert.Empty(tags);
        Assert.Equal(new[] { "未分类" }, ModCategoryAssignment.DisplayCategoriesFor(tags));
    }
}
