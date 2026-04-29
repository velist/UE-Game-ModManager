using System.Collections.Generic;
using System.IO;
using System.Linq;
using UEModManager.Models;

namespace UEModManager.Services.Detection
{
    /// <summary>
    /// 包类型识别（纯函数）。
    ///
    /// 按文件扩展名映射到 <see cref="PackageKind"/>，并提供从多个文件聚合判定的辅助方法。
    /// 主项目原本散落在 <c>PackageImportService</c> 内的检测规则（DetectPackageKind /
    /// DetectPackageKindFromFiles / IsModJsonFile / KindToArtifactType）统一下沉至此，
    /// 让导入流水线的"识别"决策可独立单测，且供其他场景复用。
    /// </summary>
    public static class PackageKindDetector
    {
        /// <summary>按单文件扩展名识别 PackageKind。</summary>
        public static PackageKind DetectByExtension(string filePath)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext switch
            {
                ".dll" or ".exe" or ".so" => PackageKind.Plugin,
                ".ini" or ".cfg" or ".toml" or ".yaml" or ".yml" => PackageKind.Config,
                ".json" when !IsModJsonFile(filePath) => PackageKind.Config,
                _ => PackageKind.Mod
            };
        }

        /// <summary>
        /// 从多个文件聚合判定 PackageKind。
        ///
        /// 聚合规则：
        /// - 全部一致 → 返回该 kind。
        /// - 含 Mod → Mod（MOD 优先）。
        /// - 含 Plugin → Plugin。
        /// - 仅 Config → Config。
        /// </summary>
        public static PackageKind AggregateFromFiles(IEnumerable<string> filePaths)
        {
            if (filePaths == null) throw new System.ArgumentNullException(nameof(filePaths));

            var kinds = filePaths.Select(DetectByExtension).Distinct().ToList();
            if (kinds.Count == 0) return PackageKind.Mod;
            if (kinds.Count == 1) return kinds[0];
            if (kinds.Contains(PackageKind.Mod)) return PackageKind.Mod;
            if (kinds.Contains(PackageKind.Plugin)) return PackageKind.Plugin;
            return PackageKind.Config;
        }

        /// <summary>
        /// 识别"看起来像 MOD 输入的 JSON"（含 cns / dekcns 关键词的文件名）。
        /// 这类 JSON 不应归为 Config，而是作为 MOD 的元数据输入。
        /// </summary>
        public static bool IsModJsonFile(string filePath)
        {
            var name = Path.GetFileName(filePath).ToLowerInvariant();
            return name.Contains("dekcns") || name.Contains("cns");
        }

        /// <summary>把 PackageKind 翻译为对应的默认 ArtifactType。</summary>
        public static ArtifactType KindToArtifactType(PackageKind kind) => kind switch
        {
            PackageKind.Mod => ArtifactType.ModFile,
            PackageKind.Plugin => ArtifactType.PluginFile,
            PackageKind.Config => ArtifactType.ConfigFile,
            _ => ArtifactType.Other,
        };
    }
}
