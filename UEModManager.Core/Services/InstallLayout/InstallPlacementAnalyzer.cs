using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UEModManager.Models;

namespace UEModManager.Services.InstallLayout
{
    /// <summary>包装不到游戏会读取的位置的原因。</summary>
    public enum PlacementIssueKind
    {
        /// <summary>包里是几套互斥的方案（每个文件夹一套同名配置），需要玩家选一套，不能全铺进游戏。</summary>
        ChoiceVariants,

        /// <summary>只有零散的加载器脚本，缺 MOD 文件夹（没有 MOD 名就不知道该放进哪里）。</summary>
        LooseLoaderScripts
    }

    /// <summary>一个包的落位问题。</summary>
    /// <param name="Variants">多选一时各方案的文件夹名。</param>
    public sealed record PlacementIssue(
        string PackageKey, string DisplayName, PlacementIssueKind Kind, IReadOnlyList<string> Variants);

    /// <summary>
    /// 判断一个（已归位的）插件 / 配置包能不能落到游戏会读取的位置。纯函数。
    ///
    /// 这类包以前照样被铺进游戏目录并显示「部署成功」，玩家只看到 MOD 不生效。
    /// 认出来之后部署层不再落盘，体检与导入结果里明说原因。
    /// </summary>
    public static class InstallPlacementAnalyzer
    {
        public static PlacementIssue? Analyze(Package package, InstallLayoutRules rules)
        {
            if (package == null) throw new ArgumentNullException(nameof(package));
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            if (package.Kind == PackageKind.Mod || !string.IsNullOrWhiteSpace(package.TargetRootPath)) return null;

            var paths = package.Artifacts
                .Where(a => a.ArtifactType != ArtifactType.PreviewImage && !string.IsNullOrEmpty(a.RelativeTargetPath))
                .Select(a => InstallLayoutNormalizer.Split(a.RelativeTargetPath))
                .Where(p => p.Length > 0)
                .ToList();
            if (paths.Count == 0) return null;

            var variants = FindChoiceVariants(paths, rules);
            if (variants.Count > 0)
                return new PlacementIssue(package.PackageKey, package.DisplayName, PlacementIssueKind.ChoiceVariants, variants);

            return HasOnlyLooseLoaderScripts(paths, rules)
                ? new PlacementIssue(package.PackageKey, package.DisplayName, PlacementIssueKind.LooseLoaderScripts, [])
                : null;
        }

        /// <summary>
        /// 多选一：至少两个顶层文件夹各带一份「游戏只读一份」的同名配置（Engine.ini…）。
        /// </summary>
        private static List<string> FindChoiceVariants(List<string[]> paths, InstallLayoutRules rules)
        {
            if (rules.SingletonConfigFileNames.Count == 0) return [];
            return paths
                .Where(p => p.Length >= 2 && rules.SingletonConfigFileNames.Contains(p[^1], StringComparer.OrdinalIgnoreCase))
                .GroupBy(p => p[^1], StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Select(p => p[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
                .Where(folders => folders.Count >= 2)
                .OrderByDescending(folders => folders.Count)
                .FirstOrDefault() ?? [];
        }

        private static bool HasOnlyLooseLoaderScripts(List<string[]> paths, InstallLayoutRules rules)
        {
            if (rules.LoaderScriptExtensions.Count == 0 || string.IsNullOrEmpty(rules.ScriptModsDirectory)) return false;
            var modsDirectory = InstallLayoutNormalizer.Split(rules.ScriptModsDirectory);
            var scripts = paths.Where(p => rules.LoaderScriptExtensions.Contains(
                Path.GetExtension(p[^1]), StringComparer.OrdinalIgnoreCase)).ToList();
            if (scripts.Count == 0) return false;
            // 只要有一个脚本已经在 MOD 目录下，这就是个正常的脚本 MOD。
            return !scripts.Any(p => p.Length > modsDirectory.Length
                && modsDirectory.Select((segment, i) => string.Equals(p[i], segment, StringComparison.OrdinalIgnoreCase)).All(x => x));
        }
    }
}
