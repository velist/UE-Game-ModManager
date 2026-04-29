using System;
using System.Collections.Generic;
using System.IO;
using UEModManager.Models;

namespace UEModManager.Adapters
{
    /// <summary>
    /// 示例 Host Adapter — 演示给一个虚构的 "MyEngine" 游戏添加 MOD 支持需要的全部代码。
    ///
    /// 这是一个**可独立编译**的最小完整示例：
    /// - 仅依赖 UEModManager.Core（无 WPF / 主项目耦合）
    /// - 实现完整的 IHostAdapter 契约
    /// - 可作为第三方贡献者新增游戏支持的起点
    ///
    /// 如何用：
    /// 1. 复制本项目到自己的 fork，重命名 namespace / 类名
    /// 2. 改 AdapterKey / DisplayName / 关键词等字段为目标游戏
    /// 3. 调整路径计算（GetDeployTargetPath / GetBackupPath）
    /// 4. 把编译产物 dll 放到主程序 plugins/ 目录（如果支持运行时加载）
    ///    或者把代码合并回主项目并在 App.xaml.cs 注册
    ///
    /// 完整说明：docs/playbooks/writing-host-adapter.md
    /// </summary>
    public class SampleEngineAdapter : IHostAdapter
    {
        public string AdapterKey => "sample-engine";

        public string DisplayName => "示例引擎适配器";

        public EngineType EngineType => EngineType.Unknown;

        public AdapterCapabilities Capabilities => new()
        {
            SupportsConflictDetection = true,
            SupportsConfigMerge = false,
            SupportsPlugins = false,
            SupportsHardLink = true,
            SupportsSymlink = true,
            SupportsAutoDetect = true,
        };

        public IReadOnlySet<string> ModFileExtensions { get; }
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mod", ".pak" };

        public IReadOnlySet<string> DirectImportExtensions { get; }
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mod" };

        public string FileDialogFilter
            => "示例引擎 MOD|*.mod;*.pak|压缩包|*.zip;*.rar;*.7z|所有文件|*.*";

        public IReadOnlyList<string> DefaultModPathPatterns { get; }
            = new[] { "Mods", "MyGame/Content/Mods" };

        public IReadOnlyList<string> GroupPriorityExtensions { get; }
            = new[] { ".mod" };

        public bool CanHandle(string gameName, EngineType? engineType = null)
        {
            // 严格匹配：名字含 "MyGame" 或 "SampleEngine"
            // 不要太宽松，否则会误抢其他游戏
            return gameName != null
                && (gameName.Contains("MyGame", StringComparison.OrdinalIgnoreCase)
                    || gameName.Contains("SampleEngine", StringComparison.OrdinalIgnoreCase));
        }

        public IReadOnlyList<string> GetSearchKeywords(string gameName)
            => new[] { gameName ?? "", "MyGame", "SampleEngine" };

        public IReadOnlyList<string> GetExecutableKeywords(string gameName)
            => new[] { "MyGame.exe", "MyGameLauncher.exe", "SampleEngine.exe" };

        public string GetDeployTargetPath(string gameRootPath, string gameName)
            => Path.Combine(gameRootPath, "Mods");

        public string GetBackupPath(string gameRootPath, string gameName)
            => Path.Combine(gameRootPath, ".UEModManagerBackup");

        public bool ShouldSkipDirectory(string directoryName, string gameName)
            => directoryName.StartsWith(".", StringComparison.Ordinal)
            || string.Equals(directoryName, "node_modules", StringComparison.OrdinalIgnoreCase);

        public DeploymentBackendType RecommendedBackend => DeploymentBackendType.Copy;
    }
}
