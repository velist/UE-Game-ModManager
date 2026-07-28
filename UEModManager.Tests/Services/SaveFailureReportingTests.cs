using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Models;
using UEModManager.Services;

namespace UEModManager.Tests.Services;

/// <summary>
/// 四个服务的"写失败必须报告给调用方"语义。
///
/// 背景：数据目录搬到 %LOCALAPPDATA% 之后，目标不可写（磁盘满、权限、杀软锁定、
/// 被同步盘占用）是真实会发生的。这四条保存路径此前把异常吞进空 catch，
/// 用户看到"操作成功"但数据根本没落盘，下次启动分类/配置/包索引凭空消失。
/// 而同一个根因下 ModDataService / ProfileService 却会弹错误框——分裂的失败语义
/// 比全都静默更糟。这些用例把"写不进去就抛"钉死。
///
/// 制造写失败的手法：在目标目录该在的位置放一个**同名文件**。
/// 之后任何 Directory.CreateDirectory 都会抛 IOException，
/// 且不需要动权限、不留残留，跨机器稳定复现。
/// </summary>
public sealed class SaveFailureReportingTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "uemm_fail_" + Guid.NewGuid().ToString("N")[..8]);

    public SaveFailureReportingTests() => Directory.CreateDirectory(_root);

    /// <summary>返回一个"注定建不出来的目录"路径：它的父级是一个文件。</summary>
    private string BlockedDirectory(string name)
    {
        var blocker = Path.Combine(_root, name);
        File.WriteAllText(blocker, "我是文件，不是目录");
        return Path.Combine(blocker, "Data");
    }

    private string WritableDirectory(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    // ─── NewCategoryService ───

    [Fact]
    public async Task 分类保存失败时抛给调用方()
    {
        var service = new NewCategoryService(
            NullLogger<NewCategoryService>.Instance, BlockedDirectory("categories"));

        await Assert.ThrowsAnyAsync<Exception>(() => service.AddCategoryAsync("我的整合包"));
    }

    [Fact]
    public async Task 分类保存失败时不把分类留在界面上()
    {
        // Categories 直接绑在侧边栏 ListBox 上。若只抛异常不回滚，用户会同时看到
        // 错误对话框和列表里那条新分类，重启后它又不见了。
        var service = new NewCategoryService(
            NullLogger<NewCategoryService>.Instance, BlockedDirectory("categories_rollback"));

        await Assert.ThrowsAnyAsync<Exception>(() => service.AddCategoryAsync("我的整合包"));

        Assert.Empty(service.Categories);
    }

    [Fact]
    public async Task 分类删除失败时把分类放回原位()
    {
        var directory = WritableDirectory("categories_delete");
        var service = new NewCategoryService(NullLogger<NewCategoryService>.Instance, directory);
        await service.AddCategoryAsync("甲");
        var 乙 = await service.AddCategoryAsync("乙");
        await service.AddCategoryAsync("丙");

        // 把数据文件换成目录，之后的 File.Replace/Move 必然失败
        BreakDataFile(directory);

        await Assert.ThrowsAnyAsync<Exception>(() => service.RemoveCategoryAsync(乙));

        Assert.Equal(new[] { "甲", "乙", "丙" }, service.Categories.Select(c => c.Name));
    }

    [Fact]
    public async Task 分类保存成功时落盘()
    {
        var directory = WritableDirectory("categories_ok");
        var service = new NewCategoryService(NullLogger<NewCategoryService>.Instance, directory);

        await service.AddCategoryAsync("我的整合包");

        var file = Assert.Single(Directory.GetFiles(directory, "*_categories.json"));
        // JSON 序列化默认把非 ASCII 转义成 \uXXXX，故反序列化后再断言
        var saved = System.Text.Json.JsonSerializer.Deserialize<List<CategoryItem>>(File.ReadAllText(file));
        Assert.Equal("我的整合包", Assert.Single(saved!).Name);
    }

    [Fact]
    public void 分类服务仍能从容器解析()
    {
        // 新增的 (ILogger, string) 重载只给测试用：容器无法解析 string，
        // 必须仍然选中单参数构造函数，否则应用启动即失败。
        Assert.NotNull(Resolve<NewCategoryService>());
    }

    // ─── GameConfigService ───

    [Fact]
    public async Task 配置保存失败时抛给调用方()
    {
        // 这里装的是游戏安装路径。此前静默失败的表现是：设完路径界面正常，重启后路径没了。
        var service = new GameConfigService(
            NullLogger<GameConfigService>.Instance,
            Path.Combine(BlockedDirectory("config"), "config.json"));

        await Assert.ThrowsAnyAsync<Exception>(() => service.SaveConfigAsync());
    }

    [Fact]
    public async Task 配置加载路径不因写失败而中断()
    {
        // 加载时的顺带修正（旧版本备份路径）没有任何 UI 能承接错误，
        // 抛出去只会让主界面连游戏名都拿不到，故这一条必须保持静默。
        var configPath = Path.Combine(WritableDirectory("config_load"), "config.json");
        File.WriteAllText(configPath,
            """{"GameName":"剑星","BackupPath":"C:\\App\\bin\\net6.0-windows\\Backups"}""");

        // 用独占读锁挡住 AtomicFileWriter 的 File.Replace
        using var hold = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var service = new GameConfigService(NullLogger<GameConfigService>.Instance, configPath);

        await service.LoadConfigAsync();

        Assert.Equal("剑星", service.CurrentGameName);
    }

    // ─── PackageRepository ───

    [Fact]
    public async Task 包索引保存失败时抛给调用方()
    {
        // 索引丢了等于所有 MOD 的登记信息丢了：仓库里文件还在，界面上什么都不剩。
        var repository = new PackageRepository(
            NullLogger<PackageRepository>.Instance,
            new ObjectStore(NullLogger<ObjectStore>.Instance, WritableDirectory("repo_ok")),
            BlockedDirectory("index"));

        await Assert.ThrowsAnyAsync<Exception>(
            () => repository.RegisterPackageAsync(MakePackage("整合包A")));
    }

    [Fact]
    public async Task 包索引读失败时先备份再回落空列表()
    {
        // 回落空列表本身是对的（读不出索引不该让启动中断），但空列表会被下一次保存
        // 全量覆盖，原索引就此消失，所以覆盖前必须留下副本。
        var indexDirectory = WritableDirectory("index_corrupt");
        var indexPath = Path.Combine(indexDirectory, "悟空_packages.json");
        const string corrupt = """[{"PackageKey":"整合""";
        File.WriteAllText(indexPath, corrupt);

        var repository = new PackageRepository(
            NullLogger<PackageRepository>.Instance,
            new ObjectStore(NullLogger<ObjectStore>.Instance, WritableDirectory("repo_corrupt")),
            indexDirectory);

        await repository.SetCurrentGameAsync("悟空");

        Assert.Empty(repository.GetAllPackages());
        var backup = Assert.Single(Directory.GetFiles(indexDirectory, "*_packages.json.corrupt-*.bak"));
        Assert.Equal(corrupt, File.ReadAllText(backup));
    }

    [Fact]
    public void 包仓库仍能从容器解析()
    {
        Assert.NotNull(Resolve<PackageRepository>());
    }

    // ─── ObjectStore ───

    [Fact]
    public void 预览图保存失败时抛给调用方()
    {
        // 此前返回 null，调用方只能猜"大概是图片文件读不了"，
        // 于是把磁盘满/权限不足指到了错误的方向。
        var source = Path.Combine(_root, "preview.png");
        File.WriteAllText(source, "fake png");
        var store = new ObjectStore(
            NullLogger<ObjectStore>.Instance, BlockedDirectory("repository"));

        Assert.ThrowsAny<Exception>(() => store.StorePreviewImage("整合包A", source));
    }

    [Fact]
    public void 预览图保存成功时返回落盘路径()
    {
        var source = Path.Combine(_root, "preview_ok.png");
        File.WriteAllText(source, "fake png");
        var store = new ObjectStore(
            NullLogger<ObjectStore>.Instance, WritableDirectory("repository_ok"));

        var stored = store.StorePreviewImage("整合包A", source);

        Assert.True(File.Exists(stored));
    }

    [Fact]
    public void 对象仓库仍能从容器解析()
    {
        Assert.NotNull(Resolve<ObjectStore>());
    }

    // ─── 辅助 ───

    private static T Resolve<T>() where T : class
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ObjectStore>();
        services.AddSingleton<PackageRepository>();
        services.AddSingleton<NewCategoryService>();

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<T>();
    }

    private static Package MakePackage(string key) => new()
    {
        PackageKey = key,
        DisplayName = key,
    };

    /// <summary>把已有的分类数据文件换成同名目录，让后续写入必然失败。</summary>
    private static void BreakDataFile(string directory)
    {
        var file = Directory.GetFiles(directory, "*_categories.json").Single();
        File.Delete(file);
        Directory.CreateDirectory(file);
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
