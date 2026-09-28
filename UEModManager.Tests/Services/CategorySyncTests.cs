using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Models;
using UEModManager.Services;
using UEModManager.Tests.Infrastructure;

namespace UEModManager.Tests.Services;

/// <summary>
/// 侧边栏"分类目录"与包上标签的同步，以及改名 / 删除分类的全链路。
///
/// <para>
/// 事故：分类的权威存储是 <c>Package.Tags</c>（导入时分类器写进去），侧边栏读的却是另一份分类文件，
/// 从没有人把前者同步给后者。列表右侧每行都写着"人物 / 其他"，左侧目录是空的；
/// 右键"移动到分类"的子项由目录生成，于是也是空的，用户没有任何地方能改分类。
/// </para>
///
/// <para>
/// 补上同步以后，改名和删除就必须连包上的标签一起改：只改目录的话，下一次刷新会按旧标签
/// 把旧分类补回来 —— 改名变成"新名字 0 个 + 旧名字原样"，删除变成"删了又长回来"。
/// </para>
/// </summary>
public sealed class CategorySyncTests : IDisposable
{
    private const string Game = "黑神话·悟空";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "UEModManager.Tests", "CategorySync", Guid.NewGuid().ToString("N"));

    public CategorySyncTests() => Directory.CreateDirectory(_root);

    // ─── 服务层：补齐目录 ───

    [Fact]
    public async Task 补齐的分类会落盘重启后还在()
    {
        var data = Path.Combine(_root, "Data");
        var service = new NewCategoryService(NullLogger<NewCategoryService>.Instance, data);
        await service.SetCurrentGameAsync(Game);

        var added = await service.EnsureCategoriesAsync(new[] { "自定义", "人物", "全部", "人物" });
        Assert.Equal(2, added);

        var reopened = new NewCategoryService(NullLogger<NewCategoryService>.Instance, data);
        await reopened.SetCurrentGameAsync(Game);
        Assert.Equal(new[] { "人物", "自定义" }, VisibleNames(reopened.Categories));

        // 内置分类名不能标成自定义：自定义分类在英文界面下原样显示，侧边栏会写"人物"而列表写 Characters。
        Assert.False(reopened.Categories.Single(c => c.Name == "人物").IsCustom);
        Assert.True(reopened.Categories.Single(c => c.Name == "自定义").IsCustom);
    }

    [Fact]
    public async Task 已有的分类不会重复补()
    {
        var service = new NewCategoryService(NullLogger<NewCategoryService>.Instance, Path.Combine(_root, "Data"));
        await service.SetCurrentGameAsync(Game);
        await service.AddCategoryAsync("武器");

        Assert.Equal(0, await service.EnsureCategoriesAsync(new[] { "武器" }));
        Assert.Equal(new[] { "武器" }, VisibleNames(service.Categories));
    }

    // ─── 主视图模型：刷新即同步 ───

    [Fact]
    public async Task 刷新后侧边栏有列表里出现的每一个分类()
        => await StaApplication.RunAsync(async () =>
        {
            var host = await OpenWithPackagesAsync();

            // 按内置顺序排；没有标签的包在列表里显示"未分类"，目录里也得有"未分类"。
            Assert.Equal(new[] { "人物", "武器", "未分类" }, VisibleNames(host.Main.Categories.Categories));
            Assert.Equal(2, Count(host, "人物"));
            Assert.Equal(1, Count(host, "武器"));
            Assert.Equal(1, Count(host, "未分类"));

            // 右键"移动到分类"的子项由目录生成：目录补齐了，菜单才有东西可选。
            Assert.Equal(new[] { "人物", "武器", "未分类" },
                host.Main.Categories.AssignableCategories.Select(c => c.Name));
        });

    // ─── 改名 ───

    [Fact]
    public async Task 改名会连带改掉包上的标签重启后旧名不会复活()
        => await StaApplication.RunAsync(async () =>
        {
            var host = await OpenWithPackagesAsync();

            var result = await host.Main.RenameCategoryAsync(Category(host, "武器"), "兵器");

            Assert.True(result.Success, result.Error);
            // 就地替换：B 的主分类仍是原来的第一个标签位置。
            Assert.Equal(new[] { "兵器", "人物" }, Tags(host, "pkg-b"));
            Assert.Equal(new[] { "人物", "兵器", "未分类" }, VisibleNames(host.Main.Categories.Categories));
            Assert.Equal(1, Count(host, "兵器"));

            // 落盘了：从磁盘重新读仓库，再刷新一次，旧名字也不会被补回目录。
            await host.Repository.SetCurrentGameAsync(Game);
            Assert.Equal(new[] { "兵器", "人物" }, Tags(host, "pkg-b"));
            await host.Main.RefreshFromRepositoryAsync();
            Assert.DoesNotContain("武器", VisibleNames(host.Main.Categories.Categories));
        });

    [Fact]
    public async Task 改成已有的名字会被拒绝且什么都不动()
        => await StaApplication.RunAsync(async () =>
        {
            var host = await OpenWithPackagesAsync();

            var result = await host.Main.RenameCategoryAsync(Category(host, "武器"), "人物");

            Assert.False(result.Success);
            Assert.Equal(new[] { "武器", "人物" }, Tags(host, "pkg-b"));
            Assert.Equal(new[] { "人物", "武器", "未分类" }, VisibleNames(host.Main.Categories.Categories));
        });

    // ─── 删除 ───

    [Fact]
    public async Task 删除会从所有包上摘掉该分类包括附带的标签()
        => await StaApplication.RunAsync(async () =>
        {
            var host = await OpenWithPackagesAsync();

            var result = await host.Main.DeleteCategoryAsync(Category(host, "人物"));

            Assert.True(result.Success, result.Error);
            Assert.Empty(Tags(host, "pkg-a"));                   // 只有这一个标签 → 未分类
            Assert.Equal(new[] { "武器" }, Tags(host, "pkg-b"));  // 人物只是它的附带标签，也要摘掉
            Assert.DoesNotContain("人物", VisibleNames(host.Main.Categories.Categories));
            Assert.Equal(2, Count(host, "未分类"));

            // 删了不会长回来。
            await host.Repository.SetCurrentGameAsync(Game);
            await host.Main.RefreshFromRepositoryAsync();
            Assert.DoesNotContain("人物", VisibleNames(host.Main.Categories.Categories));
        });

    // ─── 工具 ───

    /// <summary>
    /// 三个包：A 只有"人物"，B 是"武器"为主、"人物"附带，C 没有标签（未分类）。刷新一次主列表。
    /// </summary>
    private async Task<ConflictTestHost.Host> OpenWithPackagesAsync()
    {
        var host = await ConflictTestHost.OpenAsync(_root);
        foreach (var (key, tags) in new[]
                 {
                     ("pkg-a", new List<string> { "人物" }),
                     ("pkg-b", new List<string> { "武器", "人物" }),
                     ("pkg-c", new List<string>())
                 })
        {
            await host.Repository.RegisterPackageAsync(new Package
            {
                PackageKey = key,
                DisplayName = key,
                HostGameName = Game,
                Tags = tags
            });
        }

        await host.Main.RefreshFromRepositoryAsync();
        return host;
    }

    private static string[] VisibleNames(IEnumerable<CategoryItem> categories)
        => categories.Where(c => !c.IsHidden).Select(c => c.Name).ToArray();

    private static CategoryItem Category(ConflictTestHost.Host host, string name)
        => host.Main.Categories.Categories.Single(c => c.Name == name);

    private static int Count(ConflictTestHost.Host host, string name) => Category(host, name).Count;

    private static List<string> Tags(ConflictTestHost.Host host, string key)
        => host.Repository.GetByKey(key)!.Tags;

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }
}
