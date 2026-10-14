using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using UEModManager.Models;
using UEModManager.Services.Conflict;
using UEModManager.Services.Conflict.Indexing;
using UEModManager.Services.Conflict.Model;

namespace UEModManager.Core.Tests.Services.Conflict;

/// <summary>
/// 在真实仓库上跑一次索引：确认解析器对现场文件成立，而不只是对夹具成立。
///
/// 仓库不存在时整个测试直接返回（记为通过）—— CI 与别的机器上没有
/// <c>D:\UEModManager\Repository</c>，把"没有样本"当成失败会把测试变成环境检查。
/// 这类"有样本才深查"的用例仍是有效的：开发机上它每天都在跑。
/// </summary>
public class RealPakSmokeTests
{
    private const string RepositoryPath = @"D:\UEModManager\Repository";

    /// <summary>期望出现重叠的 MOD 对（按仓库目录名）。</summary>
    private static readonly (string A, string B)[] ExpectedOverlaps =
    [
        ("指头大佬-披风搭配任一贴图使用（先下这个~）", "神珍大圣套披风（战损避火罩版）贴图未竟"),
        ("指头大佬-披风搭配任一贴图使用（先下这个~）", "神珍大圣套披风（避火罩)"),
        ("指头大佬-披风搭配任一贴图使用（先下这个~）", "红底龙纹披风(替换神珍大圣胸甲)"),
        ("神珍大圣套披风（战损避火罩版）贴图未竟", "神珍大圣套披风（避火罩)"),
        ("神珍大圣套披风（战损避火罩版）贴图未竟", "红底龙纹披风(替换神珍大圣胸甲)"),
        ("神珍大圣套披风（避火罩)", "红底龙纹披风(替换神珍大圣胸甲)"),
        ("nezha_yao_1145_p", "真大圣套（金属感）贴图")
    ];

    [Fact]
    public void 真实仓库的每个pak都能读出条目()
    {
        if (!Directory.Exists(RepositoryPath)) return;

        var paks = Directory.GetFiles(RepositoryPath, "*.pak", SearchOption.AllDirectories);
        if (paks.Length == 0) return;

        foreach (var pak in paks)
        {
            using var stream = File.OpenRead(pak);
            var result = new PakIndexReader().ReadIndex(stream, pak);

            Assert.True(result.Status == IndexStatus.Complete,
                $"{Path.GetFileName(pak)}: 期望 Complete，实际 {result.Status} ({result.Detail})");
            Assert.True(result.Entries.Count > 0, $"{Path.GetFileName(pak)}: 读出 0 个条目");
        }
    }

    [Fact]
    public void 真实仓库里期望的MOD对确实重叠()
    {
        if (!Directory.Exists(RepositoryPath)) return;

        var keyToAssets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.GetDirectories(RepositoryPath))
        {
            var paks = Directory.GetFiles(directory, "*.pak");
            if (paks.Length == 0) continue;

            var assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pak in paks)
            {
                using var stream = File.OpenRead(pak);
                var result = new PakIndexReader().ReadIndex(stream, pak);
                if (result.Status != IndexStatus.Complete) continue;
                foreach (var entry in result.Entries) assets.Add(entry.AssetKey);
            }

            keyToAssets[Path.GetFileName(directory)] = assets;
        }

        foreach (var (a, b) in ExpectedOverlaps)
        {
            if (!keyToAssets.TryGetValue(a, out var assetsA)) continue;
            if (!keyToAssets.TryGetValue(b, out var assetsB)) continue;

            var common = assetsA.Intersect(assetsB, StringComparer.OrdinalIgnoreCase).ToList();
            Assert.True(common.Count > 0, $"期望 {a} 与 {b} 有共同资产，实际 0 个");
        }
    }

    [Fact]
    public void 统一容器格式的pak与utoc能被识别()
    {
        if (!Directory.Exists(RepositoryPath)) return;

        var paks = Directory.GetFiles(RepositoryPath, "*.pak", SearchOption.AllDirectories);
        if (paks.Length == 0) return;

        foreach (var pak in paks)
        {
            using var stream = File.OpenRead(pak);
            // .pak 里可能混着 utoc 魔数开头的文件，识别必须以内容为准。
            var kind = ContainerFormat.Detect(stream);
            Assert.True(kind == ContainerKind.Pak || kind == ContainerKind.IoStoreToc,
                $"{Path.GetFileName(pak)}: 识别为 {kind}");
        }
    }

    [Fact]
    public void 仓库manifest里的ContentHash可用于整包重复判定()
    {
        if (!Directory.Exists(RepositoryPath)) return;

        // 红色经文金箍棒 / 红色经文金箍棒_2 是同一个 pak 导入两次，
        // ContentHash 相同 —— 这正是"重复"要报的东西。
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.GetDirectories(RepositoryPath))
        {
            var manifestPath = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifestPath)) continue;

            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("ContentHash", out var hash)) continue;

            hashes[Path.GetFileName(directory)] = hash.GetString() ?? string.Empty;
        }

        if (hashes.Count == 0) return;

        // 仓库里没有这对样本时同样跳过：这个用例验证的是“重复能被判定出来”，
        // 而不是“这台机器上一定导过这两份”。缺样本就退化成环境检查——
        // 与本类“有样本才深查”的约定一致（同文件里重叠用例就是这么处理的）。
        if (!hashes.ContainsKey("红色经文金箍棒") || !hashes.ContainsKey("红色经文金箍棒_2")) return;

        var groups = hashes
            .Where(h => !string.IsNullOrEmpty(h.Value))
            .GroupBy(h => h.Value, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();

        Assert.Contains(groups, g => g.Any(h => h.Key == "红色经文金箍棒")
                                     && g.Any(h => h.Key == "红色经文金箍棒_2"));
    }
}