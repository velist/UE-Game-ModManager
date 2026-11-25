using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using UEModManager.Models;
using UEModManager.Services.InstallLayout;
using UEModManager.Services.Loaders;

namespace UEModManager.Services
{
    /// <summary>
    /// UE4SS 的检测（IO 适配器）。判定规则在 Core 的 <see cref="Ue4ssLoader"/>。
    ///
    /// 正式版线（v2.1.x）只做检测与提示：UE4SS 由玩家自己下载，把压缩包拖进来走普通的导入流水线，
    /// 做成一个插件包再部署，因此和别的 MOD 一样能停用、能卸载。查新、下载与一键安装在 2.2.0 线上。
    /// </summary>
    public sealed class Ue4ssService
    {
        private readonly ILogger<Ue4ssService> _logger;
        private readonly GameConfigService _gameConfig;
        private readonly PackageRepository _repository;
        private readonly ProfileService _profiles;

        public Ue4ssService(
            ILogger<Ue4ssService> logger,
            GameConfigService gameConfig,
            PackageRepository repository,
            ProfileService profiles)
        {
            _logger = logger;
            _gameConfig = gameConfig;
            _repository = repository;
            _profiles = profiles;
        }

        /// <summary>当前游戏是否可能用到 UE4SS（虚幻引擎）。</summary>
        public bool IsRelevant => _gameConfig.CurrentEngineType == EngineType.UnrealEngine;

        /// <summary>
        /// 检测当前游戏插件根（游戏本体所在目录）里的安装状态。不是 UE 游戏、或游戏路径没配好时返回 null。
        /// </summary>
        public Ue4ssDetection? Detect()
        {
            if (!IsRelevant) return null;
            var root = _gameConfig.CurrentPluginRootPath;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
            return _gameConfig.DetectUe4ss();
        }

        /// <summary>
        /// 仓库里由管理器管着的 UE4SS 本体包（自带代理 DLL 与 UE4SS 本体的插件包），最新导入的在前。
        /// 一键安装的、玩家把官方压缩包拖进来的、MOD 整合包里自带的都算。
        /// </summary>
        public Package? FindManagedPackage()
        {
            // 规则里的脚本 MOD 目录要探测磁盘，取一次给所有包用
            var rules = _gameConfig.CurrentInstallLayoutRules;
            return _repository.GetAllPackages()
                .Where(package => IsRuntimePackage(package, rules))
                .OrderByDescending(p => p.ImportedAt)
                .FirstOrDefault();
        }

        /// <summary>
        /// 当前方案里已启用的脚本 MOD（UE4SS 的 Lua / C++ MOD）文件夹名。空表示这位玩家用不上 UE4SS，不要打扰他。
        /// </summary>
        public IReadOnlyList<string> EnabledScriptModNames()
        {
            var profile = _profiles.CurrentProfile;
            if (!IsRelevant || profile == null) return [];

            var rules = _gameConfig.CurrentInstallLayoutRules;
            var names = new List<string>();
            foreach (var entry in profile.Packages.Where(e => e.IsEnabled))
            {
                var package = _repository.GetByKey(entry.PackageKey);
                if (package == null || package.Kind == PackageKind.Mod) continue;
                if (!string.IsNullOrWhiteSpace(entry.TargetRootPath ?? package.TargetRootPath)) continue;
                names.AddRange(Ue4ssLoader.ScriptModNames(NormalizedPaths(package, rules), rules));
            }
            return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool IsRuntimePackage(Package package, InstallLayoutRules rules)
        {
            if (package.Kind == PackageKind.Mod || !string.IsNullOrWhiteSpace(package.TargetRootPath)) return false;
            var paths = NormalizedPaths(package, rules);
            return InstallLayoutNormalizer.IsLoaderRuntime(paths, rules) && Ue4ssLoader.Detect(paths).IsInstalled;
        }

        private static IReadOnlyList<string> NormalizedPaths(Package package, InstallLayoutRules rules)
            => InstallLayoutNormalizer.Normalize(
                package.Artifacts
                    .Where(a => a.ArtifactType != ArtifactType.PreviewImage)
                    .Select(a => a.RelativeTargetPath ?? string.Empty).ToList(),
                rules);
    }
}
