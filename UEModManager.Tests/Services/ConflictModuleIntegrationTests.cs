using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Models;
using UEModManager.Services;
using UEModManager.Services.Backends;
using UEModManager.Services.Config;
using UEModManager.Services.Conflict;
using UEModManager.Services.DeploymentPlanning;
using System.Windows.Threading;
using UEModManager.Tests.Infrastructure;
using UEModManager.Core.Tests.Services.Conflict;
using UEModManager.ViewModels;

namespace UEModManager.Tests.Services;

/// <summary>
/// 冲突模块的端到端验证：**真实 pak → 真实导入 → 真实索引 → 真实分析 → 真实禁用**。
///
/// 全程走生产链路上的真实服务（只把数据目录等位置参数换成本次临时目录），
/// 判据是"面板上会看到几组冲突"。这样做的价值在于覆盖
/// <c>PakFixtureBuilder</c> / <c>PakIndexReader</c> / <c>AssetIndexService</c> /
/// <c>AssetConflictDetector</c> / <c>ConflictModuleService</c> / <c>MainViewModel</c>
/// 之间的接缝 —— 每一层自己单测都过、接起来却报 0 组冲突，正是这次重做要防的事。
///
/// pak 夹具从 UEModManager.Core.Tests 链接进来（见 csproj 的 Compile Include）：
/// 让测试 host 项目各留一份格式实现，两份迟早会分叉。
/// 服务装配见 <see cref="ConflictTestHost"/>，与冲突面板的接线用例共用同一份宿主。
/// </summary>
public sealed class ConflictModuleIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "UEModManager.Tests", "ConflictModule", Guid.NewGuid().ToString("N"));

    private const string MountPoint = "../../../b1/Content/00MainHZ/Characters/Wukong/";

    /// <summary>
    /// 找不到目标必须报错。
    ///
    /// 事故原型：面板拿到的是另一个没有 Refresh 过的 MainViewModel，AllMods 是空的，
    /// 每个 key 都匹配不上、方法返回 Ok，于是"已禁用 X 并重新部署"照常显示、
    /// 主列表和游戏目录纹丝不动。空列表 + Ok 就是把"什么都没做"报成成功。
    /// </summary>
    [Fact]
    public async Task 批量禁用遇到找不到的包时失败而不是静默成功()
    {
        var f = await ConflictTestHost.OpenAsync(_root);
        await f.ImportPaksAsync(("披风甲", Pak("Textures/", "T_A.uasset", [1, 1])));
        // 刻意不 EnableAllAsync：AllMods 从未被刷新过，就是"空列表"的那份现场。

        var result = await f.OnStaAsync(() => f.Main.DisableManyAsync(["不存在的包"]));

        Assert.False(result.Success);
        Assert.Contains("不存在的包", result.Error);
        Assert.Contains("找不到 MOD", result.Error);
    }

    /// <summary>
    /// 仓库刷新之后必须通知主窗口重算计数。
    ///
    /// 事故原型（2026-09-23 实机）：面板里"一键处理"禁用了 7 个 MOD，方案文件与磁盘都已变，
    /// 但主窗口左栏仍写"已启用 20"——左栏计数是 code-behind 手工算的，面板关掉后没人通知它。
    /// 这里钉住：一次 DisableManyAsync 至少触发一次 RepositoryRefreshed，且触发时 AllMods
    /// 已反映新的启用状态（订阅方据此重算才是对的数字）。
    /// </summary>
    [Fact]
    public async Task 批量禁用后触发仓库刷新事件_且事件里已能看到新的启用状态()
    {
        var f = await ConflictTestHost.OpenAsync(_root);
        await f.ImportPaksAsync(
            ("披风甲", Pak("Textures/", "T_A.uasset", [1, 1])),
            ("披风乙", Pak("Textures/", "T_A.uasset", [2, 2])));
        await f.EnableAllAsync();

        var enabledSeenInEvent = new List<int>();
        await f.OnStaAsync(() =>
        {
            f.Main.RepositoryRefreshed += () => enabledSeenInEvent.Add(f.Main.AllMods.Count(m => m.IsEnabled));
            return Task.CompletedTask;
        });

        var result = await f.OnStaAsync(() => f.Main.DisableManyAsync(["披风乙"]));

        Assert.True(result.Success, result.Error);
        Assert.NotEmpty(enabledSeenInEvent);
        Assert.Equal(1, enabledSeenInEvent[^1]);
    }

    /// <summary>
    /// 一键处理的默认规则：**每个冲突组保留显示顺序里的第一个成员，禁用其余**。
    ///
    /// 造一条链 A↔B、B↔C：三个包两两不都重叠，冲突组是"A 与 B"和"B 与 C"。
    /// 显示顺序里 B 在前，两个组都保留 B，于是 A 与 C 被禁用 —— 这条同时锁住
    /// "一个包出现在多个组里时只判一次"的行为。
    /// </summary>
    [Fact]
    public async Task 一键处理每组保留第一个成员_禁用链式冲突里的其余_MOD并归零()
    {
        var f = await ConflictTestHost.OpenAsync(_root);
        await f.ImportPaksAsync(
            ("只共享A", Pak("Textures/", "T_A.uasset", [1, 1])),
            ("共享全部", Pak(("Textures/", "T_A.uasset", [2, 2]),
                             ("Textures/", "T_B.uasset", [2, 2]))),
            ("只共享B", Pak("Textures/", "T_B.uasset", [3, 3])));
        await f.EnableAllAsync();

        var report = await f.Conflicts.AnalyzeAsync();
        Assert.Equal(2, report.Groups.Count);
        // 每组第一个是 B：两两分组的成员都按展示名排序，B 排在两侧各自的前面。
        Assert.All(report.Groups, g => Assert.Equal("共享全部", g.Members[0].PackageKey));

        // 走面板真正的"一键处理"入口：它自己从已分析的组推出该禁用哪些，
        // 这里再断言这个清单恰好是 A 与 C（B 被两个组都判为保留）。
        var panel = new ConflictViewModel(f.Conflicts, f.Main, NullLogger<ConflictViewModel>.Instance);
        var result = await f.OnStaAsync(async () =>
        {
            await panel.LoadAsync();
            Assert.Equal(2, panel.Groups.Count);
            Assert.Equal(["只共享A", "只共享B"], panel.PlanDefaultResolution().OrderBy(k => k, System.StringComparer.Ordinal));
            return await panel.ResolveAllAsync();
        });
        Assert.True(result.Success, result.Error);

        var after = await f.Conflicts.AnalyzeAsync();
        Assert.Empty(after.Groups);
        Assert.True(f.Main.AllMods.Single(m => m.RealName == "共享全部").IsEnabled);
        Assert.False(f.Main.AllMods.Single(m => m.RealName == "只共享A").IsEnabled);
        Assert.False(f.Main.AllMods.Single(m => m.RealName == "只共享B").IsEnabled);
    }

    /// <summary>两个 MOD 改同一张贴图（内容不同）→ 1 组 2 成员；第三个无关。</summary>
    [Fact]
    public async Task 同贴图不同内容的两个MOD分析出一组两成员_禁用其中一个后归零()
    {
        var f = await ConflictTestHost.OpenAsync(_root);
        await f.ImportPaksAsync(
            ("披风甲", Pak("Textures/", "T_WuKong_Born_Cloth_Ring_D.uasset", [1, 1, 1, 1])),
            ("披风乙", Pak("Textures/", "T_WuKong_Born_Cloth_Ring_D.uasset", [2, 2, 2, 2])),
            ("无关", Pak("Meshes/", "SK_Wukong_Helmet.uasset", [9, 9])));
        await f.EnableAllAsync();

        var report = await f.Conflicts.AnalyzeAsync();

        var group = Assert.Single(report.Groups);
        Assert.Equal(2, group.Members.Count);
        // 排序用 Ordinal：默认比较器跟系统语言走（中文拼音 vs en-US 的码点顺序），
        // CI 的英文环境下顺序会翻转，本机中文环境则不会。
        Assert.Equal(["披风乙", "披风甲"], group.Members.Select(m => m.PackageKey).OrderBy(k => k, System.StringComparer.Ordinal));
        // 无关那个 MOD 一个资产都没重叠，不该出现在任何组里。
        Assert.DoesNotContain(group.Members, m => m.PackageKey == "无关");

        // 卡片角标读的就是这个数：该包出现在几个组里。
        Assert.Equal(1, f.Conflicts.ConflictCountFor("披风甲"));
        Assert.Equal(0, f.Conflicts.ConflictCountFor("无关"));

        // 禁用其中一个后重新分析：重叠只剩一个声称者，组必须消失。
        // 这条同时验证"面板动作后重新分析"这条路走通 —— 禁用真的改到了
        // ConflictModuleService.AnalyzeAsync 读的那份启用集合。
        var disabled = await f.OnStaAsync(() => f.Main.DisableManyAsync(["披风乙"]));
        Assert.True(disabled.Success, disabled.Error);

        var after = await f.Conflicts.AnalyzeAsync();
        Assert.Empty(after.Groups);
        Assert.Equal(0, f.Conflicts.ConflictCountFor("披风甲"));
    }

    /// <summary>
    /// 插件包各带一份 mods.json（同内容）与 version.dll（不同内容）：
    /// 这两个文件在游戏目录里同路径，属于"带一份就够"的运行时，不该报成 MOD 冲突。
    /// </summary>
    [Fact]
    public async Task 插件包共享运行时文件不算冲突_但写入覆盖规则()
    {
        var f = await ConflictTestHost.OpenAsync(_root);
        await f.ImportArchiveAsync(CreateArchive(_root,
            ("插件甲", [
                ("b1/Binaries/Win64/ue4ss/Mods/mods.json", "SAME-MODS-JSON"),
                ("b1/Binaries/Win64/version.dll", "VERSION-DLL-OLD"),
            ]),
            ("插件乙", [
                ("b1/Binaries/Win64/ue4ss/Mods/mods.json", "SAME-MODS-JSON"),
                ("b1/Binaries/Win64/version.dll", "VERSION-DLL-NEW-PLUS"),
            ])));
        await f.EnableAllAsync();

        var report = await f.Conflicts.AnalyzeAsync();

        // 这些都是共享组件：内容相同的那份没有行为差异，内容不同的那份由部署层裁决。
        Assert.Empty(report.Groups);
        Assert.Contains(report.SharedComponents, c => c.Name == "UE4SS 运行时" && c.Reason == "内容相同");
        Assert.Contains(report.SharedComponents, c => c.Name == "CSharpLoader 运行时");

        // 内容不同的共享组件必须落成覆盖规则 —— 部署层就是靠它决定同路径落盘哪一份。
        Assert.Contains(f.Conflicts.GetOverrides().Keys,
            key => key.EndsWith("version.dll", StringComparison.OrdinalIgnoreCase));
        // 内容相同的不写：写进去没有行为差异，只会撑大方案文件。
        Assert.DoesNotContain(f.Conflicts.GetOverrides().Keys,
            key => key.EndsWith("mods.json", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>同一个 pak 导两次：第二次在入口被拦，仓库里仍然只有一个包。</summary>
    [Fact]
    public async Task 导入相同内容的pak第二次被拦下()
    {
        var f = await ConflictTestHost.OpenAsync(_root);
        var directory = Path.Combine(_root, "重复导入");
        Directory.CreateDirectory(directory);

        var first = Path.Combine(directory, "红色经文金箍棒.pak");
        File.WriteAllBytes(first, Pak("Textures/", "T_Stick.uasset", [7, 7, 7]));

        var imported = Assert.Single(await f.Imports.ImportSingleAsync(first));
        Assert.True(imported.Success, imported.ErrorMessage);

        // 同一个内容换个文件名再导一次，模拟"从别处又下了一份"。
        var again = Path.Combine(directory, "红色经文金箍棒-副本.pak");
        File.WriteAllBytes(again, File.ReadAllBytes(first));
        var second = Assert.Single(await f.Imports.ImportSingleAsync(again));

        Assert.False(second.Success);
        // 断言必须与进程级 UI 语言无关：UiText.Get 会按当前语言返回中文或英文，
        // 而"当前语言"由别的测试类（LanguageIntegrationTests 等）并行改动。
        // 用包里那个恒定的展示名来判定，才不会被语言状态搅成偶发失败。
        Assert.Contains("红色经文金箍棒", second.ErrorMessage);
        Assert.Single(f.Repository.GetAllPackages(), p => p.Kind == PackageKind.Mod);
    }

    /// <summary>
    /// 单元根上的说明文件不部署 —— 否则游戏 Win64 根会多出一堆无关文件。
    ///
    /// 复刻实测 0916.zip 的两种形状：「三元两刃枪替换为红樱枪_P/steam属性口令.txt」
    /// （说明文件是单元里唯一的文件）与「自动闪避/说明与事项.txt」。
    /// 同时验证 <c>ue4ss/Mods/mods.txt</c> 不被误丢 —— 它是 UE4SS 运行时的一部分。
    /// </summary>
    [Fact]
    public async Task 说明文件不进入包_但运行时里的mods_txt保留()
    {
        var f = await ConflictTestHost.OpenAsync(_root);
        await f.ImportArchiveAsync(CreateArchive(_root,
            ("两款披风", [
                ("steam属性口令.txt", "这是口令"),
                ("b1/Content/Paks/~mods/SomeMod_P.pak", "PAK-BYTES"),
            ]),
            ("自动闪避", [
                ("说明与事项.txt", "这是说明"),
                ("b1/Content/Paks/~mods/Other_P.pak", "PAK-BYTES-2"),
            ]),
            ("不改名的", [
                ("ue4ss/Mods/mods.txt", "ue4ss/Mods/BPML.lua"),
                ("b1/Content/Paks/~mods/Plugin_P.pak", "PAK-BYTES-3"),
            ]),
            // 单元内容直接就是 ue4ss/...：只有这一个顶层目录，而它是**安装锚点**，
            // 不是包装目录。里面的 load_order.txt 属于运行时，同样不能当说明丢掉。
            ("单拖进来的UE4SS", [
                ("ue4ss/Mods/BPModLoaderMod/load_order.txt", "mods.txt"),
            ])));

        var artifacts = f.Repository.GetAllPackages().SelectMany(p => p.Artifacts).ToList();

        // 两个说明文件都不该出现在任何包里。
        Assert.DoesNotContain(artifacts, a => a.FileName is "steam属性口令.txt" or "说明与事项.txt");
        Assert.DoesNotContain(artifacts, a => a.FileName.Contains("口令", StringComparison.Ordinal));
        // 但运行时里的两个 .txt 必须还在 —— 它们是 UE4SS 的一部分。
        // mods.txt 所在的单元有 ue4ss/ 与 b1/ 两个顶层目录，load_order.txt 所在的单元
        // 只有 ue4ss/ 但这一个目录是安装锚点：两种形状都不该被当包装。
        Assert.Contains(artifacts, a => a.FileName.Equals("mods.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(artifacts, a => a.FileName.Equals("load_order.txt", StringComparison.OrdinalIgnoreCase));

        // 说明文件被丢掉后，两个 MOD 各自只剩那个 pak。
        foreach (var key in new[] { "SomeMod_P", "Other_P" })
        {
            var package = Assert.Single(f.Repository.GetAllPackages(), p => p.PackageKey == key);
            Assert.Equal(".pak", Path.GetExtension(Assert.Single(package.Artifacts).FileName));
        }
    }

    // ─── 夹具 ───

    /// <summary>在挂载点下造一个未压缩、单条目的 pak。</summary>
    private static byte[] Pak(string directory, string fileName, byte[] data)
        => PakFixtureBuilder.Build(MountPoint, [new PakFixtureBuilder.FixtureEntry(directory, fileName, data)]);

    /// <summary>同上，一个 pak 里放多个条目（链式冲突要用同一个包改到两批资源）。</summary>
    private static byte[] Pak(params (string Directory, string FileName, byte[] Data)[] entries)
        => PakFixtureBuilder.Build(MountPoint,
            [.. entries.Select(e => new PakFixtureBuilder.FixtureEntry(e.Directory, e.FileName, e.Data))]);

    /// <summary>造一个顶层是 MOD 名、内部自带 <c>b1/</c> 层级的压缩包。</summary>
    private static string CreateArchive(
        string root, params (string Unit, (string Path, string Content)[] Files)[] units)
    {
        var staging = Path.Combine(root, "staging-" + Guid.NewGuid().ToString("N"));
        foreach (var (unit, files) in units)
            foreach (var (relative, content) in files)
            {
                var path = Path.Combine(staging, unit, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
            }

        Directory.CreateDirectory(root);
        var zip = Path.Combine(root, "archive-" + Guid.NewGuid().ToString("N") + ".zip");
        ZipFile.CreateFromDirectory(staging, zip);
        return zip;
    }


    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }
}
