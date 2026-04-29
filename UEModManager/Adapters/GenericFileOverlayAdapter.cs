using System;
using System.Collections.Generic;
using System.IO;
using UEModManager.Models;

namespace UEModManager.Adapters
{
    /// <summary>
    /// 通用文件覆盖适配器。
    /// 用于不需要引擎特定逻辑的宿主，提供基础的文件覆盖部署能力。
    /// 作为默认 fallback 适配器。
    /// </summary>
    public class GenericFileOverlayAdapter : IHostAdapter
    {
        public string AdapterKey => "generic-overlay";
        public string DisplayName => "通用文件覆盖";
        public EngineType EngineType => EngineType.Unknown;
        public DeploymentBackendType RecommendedBackend => DeploymentBackendType.Copy;

        public AdapterCapabilities Capabilities { get; } = new()
        {
            SupportsConflictDetection = false,
            SupportsConfigMerge = false,
            SupportsPlugins = false,
            SupportsHardLink = true,
            SupportsSymlink = true,
            SupportsAutoDetect = false,
            SupportsLaunchArgs = false,
            SupportsCNS = false
        };

        private static readonly HashSet<string> _modFileExtensions =
            [".pak", ".ucas", ".utoc", ".json", ".dll", ".pck", ".bin", ".ini", ".cfg"];
        private static readonly HashSet<string> _directImportExtensions =
            [".pak", ".ucas", ".utoc", ".json", ".dll", ".pck", ".bin", ".ini", ".cfg"];
        private static readonly string[] _defaultModPathPatterns = ["Mods", "mods"];
        private static readonly string[] _groupPriorityExtensions = [".pak", ".dll", ".pck"];

        public IReadOnlySet<string> ModFileExtensions => _modFileExtensions;
        public IReadOnlySet<string> DirectImportExtensions => _directImportExtensions;
        public string FileDialogFilter => "MOD文件|*.zip;*.rar;*.7z;*.pak;*.dll;*.pck|所有文件|*.*";
        public IReadOnlyList<string> DefaultModPathPatterns => _defaultModPathPatterns;
        public IReadOnlyList<string> GroupPriorityExtensions => _groupPriorityExtensions;

        public bool CanHandle(string gameName, EngineType? engineType = null)
        {
            // 通用适配器始终可用作 fallback
            return true;
        }

        public IReadOnlyList<string> GetSearchKeywords(string gameName)
        {
            return [gameName, gameName.Replace(" ", ""), gameName.Replace(" ", "_")];
        }

        public IReadOnlyList<string> GetExecutableKeywords(string gameName)
        {
            return [gameName, gameName.Replace(" ", "")];
        }

        public string GetDeployTargetPath(string gameRootPath, string gameName)
        {
            foreach (var pattern in _defaultModPathPatterns)
            {
                var candidate = Path.Combine(gameRootPath, pattern);
                if (Directory.Exists(candidate))
                    return candidate;
            }
            return Path.Combine(gameRootPath, "Mods");
        }

        public string GetBackupPath(string gameRootPath, string gameName)
        {
            return Path.Combine(gameRootPath, "Mods_backup");
        }

        public bool ShouldSkipDirectory(string directoryName, string gameName) => false;
    }
}
