using System;
using System.Collections.Generic;
using System.IO;
using UEModManager.Models;

namespace UEModManager.Adapters
{
    /// <summary>
    /// Unreal Engine 宿主适配器。
    /// 提供 UE 游戏的路径映射、文件识别、搜索关键词等规则。
    /// 从 GameConfigService / EngineProfile / GamePathDialog 中抽取的 UE 特定逻辑。
    /// </summary>
    public class UnrealEngineAdapter : IHostAdapter
    {
        public string AdapterKey => "unreal-engine";
        public string DisplayName => "Unreal Engine";
        public EngineType EngineType => EngineType.UnrealEngine;
        public DeploymentBackendType RecommendedBackend => DeploymentBackendType.Copy;

        public AdapterCapabilities Capabilities { get; } = new()
        {
            SupportsConflictDetection = true,
            SupportsConfigMerge = false,
            SupportsPlugins = true,
            SupportsHardLink = true,
            SupportsSymlink = true,
            SupportsAutoDetect = true,
            SupportsLaunchArgs = false,
            SupportsCNS = false
        };

        private static readonly HashSet<string> _modFileExtensions = [".pak", ".ucas", ".utoc", ".json"];
        private static readonly HashSet<string> _directImportExtensions = [".pak", ".ucas", ".utoc", ".json"];
        private static readonly string[] _defaultModPathPatterns = ["Content/Paks/~mods", "Content/Paks/Mods"];
        private static readonly string[] _groupPriorityExtensions = [".pak", ".utoc", ".ucas"];

        public IReadOnlySet<string> ModFileExtensions => _modFileExtensions;
        public IReadOnlySet<string> DirectImportExtensions => _directImportExtensions;
        public string FileDialogFilter => "MOD文件|*.zip;*.rar;*.7z;*.pak;*.ucas;*.utoc|所有文件|*.*";
        public IReadOnlyList<string> DefaultModPathPatterns => _defaultModPathPatterns;
        public IReadOnlyList<string> GroupPriorityExtensions => _groupPriorityExtensions;

        // ─── 游戏关键词映射（从 GamePathDialog 抽取） ───

        private static readonly Dictionary<string, string[]> SearchKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            ["剑星"] = ["Stellar Blade", "StellarBlade", "剑星"],
            ["剑星（CNS模式）"] = ["Stellar Blade", "StellarBlade", "剑星"],
            ["黑神话·悟空"] = ["Black Myth Wukong", "BlackMythWukong", "Black Myth- Wukong", "Wukong", "黑神话", "悟空"],
            ["明末·渊虚之羽"] = ["Wuchang Fallen Feathers", "WuchangFallenFeathers", "Wuchang", "明末", "渊虚之羽"],
            ["光与影：33号远征队"] = ["Clair Obscur Expedition 33", "Expedition 33", "Expedition33", "Sandfall", "光与影", "33号远征队"],
            ["艾尔登法环"] = ["Elden Ring", "EldenRing", "艾尔登法环"],
            ["无主之地4"] = ["Borderlands 4", "Borderlands4", "BorderLands 4", "BorderLands4", "无主之地4"],
        };

        private static readonly Dictionary<string, string[]> ExecutableKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            ["剑星"] = ["Stellar Blade", "StellarBlade", "Stellarblade"],
            ["黑神话·悟空"] = ["Black Myth Wukong", "BlackMythWukong", "Black Myth- Wukong", "b1-win64-shipping"],
            ["明末·渊虚之羽"] = ["Wuchang Fallen Feathers", "WuchangFallenFeathers", "Wuchang", "Project_Plague"],
            ["光与影：33号远征队"] = ["Expedition33Steam-Win64-Shipping", "Expedition33", "Sandfall-Win64-Shipping", "Sandfall"],
            ["艾尔登法环"] = ["Elden Ring", "EldenRing"],
            ["无主之地4"] = ["Borderlands 4", "Borderlands4"],
        };

        // ─── 跳过目录 ───

        private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
        {
            "CustomNanosuitSystem"
        };

        // ─── 接口实现 ───

        public bool CanHandle(string gameName, EngineType? engineType = null)
        {
            if (engineType == EngineType.UnrealEngine) return true;
            return SearchKeywords.ContainsKey(gameName);
        }

        public IReadOnlyList<string> GetSearchKeywords(string gameName)
        {
            if (SearchKeywords.TryGetValue(gameName, out var keywords))
                return keywords;
            return [gameName, gameName.Replace(" ", ""), gameName.Replace(" ", "_")];
        }

        public IReadOnlyList<string> GetExecutableKeywords(string gameName)
        {
            if (ExecutableKeywords.TryGetValue(gameName, out var keywords))
                return keywords;
            return [gameName, gameName.Replace(" ", "")];
        }

        public string GetDeployTargetPath(string gameRootPath, string gameName)
        {
            // UE 游戏标准 MOD 路径
            foreach (var pattern in _defaultModPathPatterns)
            {
                var candidate = Path.Combine(gameRootPath, pattern);
                if (Directory.Exists(candidate))
                    return candidate;
            }
            // 默认使用 ~mods
            return Path.Combine(gameRootPath, _defaultModPathPatterns[0]);
        }

        public string GetBackupPath(string gameRootPath, string gameName)
        {
            return Path.Combine(gameRootPath, "Content", "Paks", "~mods_backup");
        }

        public bool ShouldSkipDirectory(string directoryName, string gameName)
        {
            return SkipDirectories.Contains(directoryName);
        }
    }

    /// <summary>
    /// 剑星 CNS 模式专用适配器。继承 UE 适配器，添加 CNS 特定规则。
    /// </summary>
    public class StellarBladeCNSAdapter : UnrealEngineAdapter
    {
        public new string AdapterKey => "unreal-engine-cns";
        public new string DisplayName => "Unreal Engine (CNS)";

        public new AdapterCapabilities Capabilities { get; } = new()
        {
            SupportsConflictDetection = true,
            SupportsConfigMerge = false,
            SupportsPlugins = true,
            SupportsHardLink = true,
            SupportsSymlink = true,
            SupportsAutoDetect = true,
            SupportsLaunchArgs = false,
            SupportsCNS = true
        };

        public new bool CanHandle(string gameName, EngineType? engineType = null)
        {
            return gameName.Contains("CNS", StringComparison.OrdinalIgnoreCase);
        }
    }
}
