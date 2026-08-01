using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Models;
using UEModManager.Services;

namespace UEModManager.Tests.Services;

/// <summary>
/// 分类的"按游戏分片 + 真的从磁盘读回来"语义。
///
/// 背景：<see cref="NewCategoryService.SetCurrentGameAsync"/> 曾在整个生产代码里零调用
/// （MVVM 重构时从 code-behind 搬走后没接上），于是当前游戏名恒为空——分类文件丢掉游戏名
/// 前缀、所有游戏挤进同一份 "_categories.json"，而加载路径从不触发。用户看到的现象是
/// "新建的分类重启就没了"，且侧边栏"全部/已启用/已禁用"因为集合是空的而点了没反应。
/// 这些用例把加载、分片、老数据继承和拖拽排序的落盘一起钉住。
/// </summary>
public sealed class CategoryPersistenceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "uemm_cat_" + Guid.NewGuid().ToString("N")[..8]);

    public CategoryPersistenceTests() => Directory.CreateDirectory(_root);

    private NewCategoryService NewService()
        => new(NullLogger<NewCategoryService>.Instance, _root);

    /// <summary>取当前显示在"分类目录"列表里的分类名（系统分类是隐藏的，不算）。</summary>
    private static string[] VisibleNames(NewCategoryService service)
        => service.Categories.Where(c => !c.IsHidden).Select(c => c.Name).ToArray();

    private void WriteStore(string fileName, params string[] customNames)
    {
        var items = customNames
            .Select((n, i) => new CategoryItem { Name = n, FullPath = n, IsCustom = true, SortOrder = i })
            .ToList();
        File.WriteAllText(Path.Combine(_root, fileName), JsonSerializer.Serialize(items));
    }

    // ─── 加载 ───

    [Fact]
    public async Task 分类写进带游戏名的文件()
    {
        var service = NewService();
        await service.SetCurrentGameAsync("悟空");

        await service.AddCategoryAsync("甲");

        Assert.True(File.Exists(Path.Combine(_root, "悟空_categories.json")));
    }

    [Fact]
    public async Task 重启后自定义分类仍在()
    {
        // 这是缺陷的正面表述：加载路径以前根本不跑，用户新建的分类活不过一次启动
        var first = NewService();
        await first.SetCurrentGameAsync("悟空");
        await first.AddCategoryAsync("我的整合包");

        var second = NewService();
        await second.SetCurrentGameAsync("悟空");

        Assert.Equal(new[] { "我的整合包" }, VisibleNames(second));
    }

    [Fact]
    public async Task 两个游戏的分类互不串门()
    {
        var service = NewService();
        await service.SetCurrentGameAsync("悟空");
        await service.AddCategoryAsync("甲");

        // 剑星首次进入时会继承一份（老行为，保留），之后各改各的
        await service.SetCurrentGameAsync("剑星");
        await service.AddCategoryAsync("乙");

        await service.SetCurrentGameAsync("悟空");

        Assert.Equal(new[] { "甲" }, VisibleNames(service));
    }

    [Fact]
    public async Task 继承缺陷期共用文件里的分类()
    {
        // 老用户升级后的关键一步：分类都堆在没有游戏名前缀的那份文件里，
        // 改成按游戏分片时如果不认它，界面上就是"我的分类全没了"
        WriteStore("_categories.json", "面部", "武器");

        var service = NewService();
        await service.SetCurrentGameAsync("悟空");

        Assert.Equal(new[] { "面部", "武器" }, VisibleNames(service));
        Assert.True(File.Exists(Path.Combine(_root, "悟空_categories.json")));
    }

    [Fact]
    public async Task 只有一个自定义分类的共用文件也要继承()
    {
        // 旧的迁移门槛是"条数 > 3"（按含三个系统分类估的），
        // 对只攒了 1~3 个分类的用户就是直接丢弃
        WriteStore("_categories.json", "面部");

        var service = NewService();
        await service.SetCurrentGameAsync("悟空");

        Assert.Equal(new[] { "面部" }, VisibleNames(service));
    }

    [Fact]
    public async Task 共用文件不会被删掉_其他游戏还要靠它继承()
    {
        WriteStore("_categories.json", "面部");

        var service = NewService();
        await service.SetCurrentGameAsync("悟空");
        await service.SetCurrentGameAsync("剑星");

        Assert.True(File.Exists(Path.Combine(_root, "_categories.json")));
        Assert.Equal(new[] { "面部" }, VisibleNames(service));
    }

    [Fact]
    public async Task 本游戏删光分类后不会被别的游戏复活()
    {
        // 采纳标准对"自己的文件"和"别人的文件"必须不同：自己的文件哪怕只剩系统分类
        // 也是用户的最终意图，再去别处捞一份回来等于删除操作被撤销
        var service = NewService();
        await service.SetCurrentGameAsync("悟空");
        var 甲 = await service.AddCategoryAsync("甲");
        await service.RemoveCategoryAsync(甲);

        WriteStore("剑星_categories.json", "武器");

        await service.SetCurrentGameAsync("悟空");

        Assert.Empty(VisibleNames(service));
    }

    [Fact]
    public async Task 系统分类始终在集合里且不显示在分类目录中()
    {
        // 侧边栏"全部/已启用/已禁用"三个导航项是按 Name 到这个集合里查筛选目标的，
        // 少一条筛选就整个失效；但它们又不能在下方的分类目录里再画一遍
        WriteStore("悟空_categories.json", "武器");

        var service = NewService();
        await service.SetCurrentGameAsync("悟空");

        foreach (var name in CategoryItem.SystemNames)
        {
            var item = service.Categories.FirstOrDefault(c => c.Name == name);
            Assert.True(item != null, $"系统分类 {name} 不在集合里，侧边栏导航会失效");
            Assert.True(item!.IsHidden, $"系统分类 {name} 会和上方的导航项重复显示");
        }

        Assert.Equal(new[] { "武器" }, VisibleNames(service));
    }

    // ─── 排序 ───

    [Fact]
    public async Task 拖拽排序落盘()
    {
        // 缺陷二：拖拽只调 ObservableCollection.Move，顺序重启即还原
        var first = NewService();
        await first.SetCurrentGameAsync("悟空");
        await first.AddCategoryAsync("甲");
        await first.AddCategoryAsync("乙");
        var 丙 = await first.AddCategoryAsync("丙");

        await first.ReorderCategoryAsync(丙, first.Categories.IndexOf(first.Categories.First(c => c.Name == "甲")));

        var second = NewService();
        await second.SetCurrentGameAsync("悟空");

        Assert.Equal(new[] { "丙", "甲", "乙" }, VisibleNames(second));
    }

    [Fact]
    public async Task 排序落盘失败时顺序移回原位并上抛()
    {
        var service = NewService();
        await service.SetCurrentGameAsync("悟空");
        await service.AddCategoryAsync("甲");
        await service.AddCategoryAsync("乙");
        var 丙 = await service.AddCategoryAsync("丙");

        BreakDataFile(Path.Combine(_root, "悟空_categories.json"));

        await Assert.ThrowsAnyAsync<Exception>(() => service.ReorderCategoryAsync(丙, 3));

        Assert.Equal(new[] { "甲", "乙", "丙" }, VisibleNames(service));
        Assert.Equal(
            service.Categories.Select((_, i) => i).ToArray(),
            service.Categories.Select(c => c.SortOrder).ToArray());
    }

    /// <summary>把分类数据文件换成同名目录，让后续写入必然失败。</summary>
    private static void BreakDataFile(string path)
    {
        File.Delete(path);
        Directory.CreateDirectory(path);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不该让测试变红
        }
    }
}
