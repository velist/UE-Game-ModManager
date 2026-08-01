using UEModManager.Services.Categories;

namespace UEModManager.Core.Tests.Services.Categories;

/// <summary>
/// 分类文件的命名与加载优先级。
///
/// 这一层的每一条都对应"用户的自定义分类看起来丢了"的一种具体走法：
/// 名字算错就是读了一份空文件；候选顺序排错就是拿别的游戏的分类盖掉本游戏的。
/// 尤其是那份没有游戏名前缀的 "_categories.json"——它是缺陷期所有游戏共用的存档，
/// 修好按游戏名分片之后，它必须仍然被认得出来，否则老用户升级即"分类清零"。
/// </summary>
public class CategoryStoreLayoutTests
{
    // ─── 文件名 ───

    [Fact]
    public void 游戏名作为文件名前缀()
    {
        Assert.Equal("悟空_categories.json", CategoryStoreLayout.FileNameFor("悟空"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 游戏名为空时退化成缺陷期的共用文件名(string? gameName)
    {
        // 这个退化行为是故意保留的：缺陷期写出的正是这个名字，改掉它等于读不回老数据
        Assert.Equal(CategoryStoreLayout.SharedLegacyFileName, CategoryStoreLayout.FileNameFor(gameName));
        Assert.Equal("_categories.json", CategoryStoreLayout.SharedLegacyFileName);
    }

    // ─── 候选顺序 ───

    [Fact]
    public void 本游戏自己的文件排在最前()
    {
        var order = CategoryStoreLayout.ResolveLoadOrder(
            "悟空", new[] { "剑星_categories.json", "_categories.json", "悟空_categories.json" });

        Assert.Equal("悟空_categories.json", order[0]);
    }

    [Fact]
    public void 缺陷期的共用文件优先于其他游戏的文件()
    {
        // 共用文件里装的是这位用户自己攒的分类，其他游戏的文件只是退而求其次的猜测
        var order = CategoryStoreLayout.ResolveLoadOrder(
            "悟空", new[] { "剑星_categories.json", "_categories.json" });

        Assert.Equal(new[] { "_categories.json", "剑星_categories.json" }, order);
    }

    [Fact]
    public void 其他游戏的文件保持传入顺序()
    {
        // 调用方按最后写入时间倒序传入，最近用过的游戏应当先被继承
        var order = CategoryStoreLayout.ResolveLoadOrder(
            "悟空", new[] { "明末_categories.json", "剑星_categories.json" });

        Assert.Equal(new[] { "明末_categories.json", "剑星_categories.json" }, order);
    }

    [Fact]
    public void 无关文件被忽略()
    {
        var order = CategoryStoreLayout.ResolveLoadOrder(
            "悟空", new[] { "悟空_packages.json", "config.json", "悟空_categories.json" });

        Assert.Equal(new[] { "悟空_categories.json" }, order);
    }

    [Fact]
    public void 文件名比较忽略大小写且不重复列出()
    {
        // Windows 文件系统不区分大小写，同一个文件被列两次会让调用方白读一遍
        var order = CategoryStoreLayout.ResolveLoadOrder(
            "悟空", new[] { "悟空_Categories.JSON", "悟空_Categories.JSON" });

        Assert.Single(order);
    }

    [Fact]
    public void 目录里没有任何分类文件时返回空()
    {
        Assert.Empty(CategoryStoreLayout.ResolveLoadOrder("悟空", Array.Empty<string>()));
        Assert.Empty(CategoryStoreLayout.ResolveLoadOrder("悟空", null));
    }

    [Fact]
    public void 当前游戏名为空时共用文件既是自己的也不会重复()
    {
        var order = CategoryStoreLayout.ResolveLoadOrder(
            "", new[] { "_categories.json", "剑星_categories.json" });

        Assert.Equal(new[] { "_categories.json", "剑星_categories.json" }, order);
    }
}
