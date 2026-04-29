using System.Collections.Generic;
using System.IO;
using System.Linq;
using UEModManager.Models;

namespace UEModManager.Services.Detection
{
    /// <summary>
    /// Artifact 类型识别（纯函数）。
    ///
    /// 不同来源的判定规则有微妙差异（迁移时已知 isPlugin 上下文，导入时按 mod 扩展名表兜底），
    /// 因此提供两个入口：<see cref="DetectForMigration"/> 和 <see cref="DetectForImport"/>，
    /// 调用方按场景选用。两者共享 <see cref="IsImageExtension"/>。
    /// </summary>
    public static class ArtifactTypeDetector
    {
        private static readonly HashSet<string> ImageExtensions = new(System.StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp",
        };

        /// <summary>常见配置扩展名（INI/CFG/TOML/YAML 系列）。</summary>
        private static readonly HashSet<string> ConfigExtensions = new(System.StringComparer.OrdinalIgnoreCase)
        {
            ".ini", ".cfg", ".toml", ".yaml", ".yml",
        };

        /// <summary>常见插件二进制扩展名。</summary>
        private static readonly HashSet<string> PluginExtensions = new(System.StringComparer.OrdinalIgnoreCase)
        {
            ".dll", ".exe", ".so",
        };

        /// <summary>
        /// 数据迁移场景下的识别。已知该包是否插件（来自旧 ModInfo.IsPlugin）。
        ///
        /// 规则：
        /// - <paramref name="isPluginContext"/>=true → 全部归 PluginFile
        /// - .json/.ini/.cfg/.toml/.yaml/.yml → ConfigFile（迁移时 .json 不做 CNS 区分）
        /// - .dll/.exe/.so → PluginFile
        /// - 图片扩展名 → PreviewImage
        /// - 其他 → ModFile（迁移兜底假设是 MOD 内容）
        /// </summary>
        public static ArtifactType DetectForMigration(string filePath, bool isPluginContext)
        {
            if (isPluginContext) return ArtifactType.PluginFile;

            var ext = Path.GetExtension(filePath).ToLowerInvariant();

            if (ext == ".ini" || ext == ".cfg" || ext == ".json"
                || ext == ".toml" || ext == ".yaml" || ext == ".yml")
                return ArtifactType.ConfigFile;
            if (PluginExtensions.Contains(ext))
                return ArtifactType.PluginFile;
            if (ImageExtensions.Contains(ext))
                return ArtifactType.PreviewImage;
            return ArtifactType.ModFile;
        }

        /// <summary>
        /// 导入场景下的识别。需要调用方传入"该宿主认可的 MOD 扩展名表"
        /// （来自 IHostAdapter.ModFileExtensions）。
        ///
        /// 规则：
        /// - 图片扩展名 → PreviewImage
        /// - .dll/.exe/.so → PluginFile
        /// - .ini/.cfg/.toml/.yaml/.yml → ConfigFile（注意：不含 .json，与导入流水线对齐）
        /// - 命中 modExtensions → ModFile
        /// - 其他 → Other（导入兜底为 Other 让上层决定丢弃 vs 保留）
        /// </summary>
        public static ArtifactType DetectForImport(
            string filePath,
            IReadOnlyCollection<string> modExtensions)
        {
            if (modExtensions == null) throw new System.ArgumentNullException(nameof(modExtensions));

            var ext = Path.GetExtension(filePath).ToLowerInvariant();

            if (ImageExtensions.Contains(ext)) return ArtifactType.PreviewImage;
            if (PluginExtensions.Contains(ext)) return ArtifactType.PluginFile;
            if (ConfigExtensions.Contains(ext)) return ArtifactType.ConfigFile;
            if (modExtensions.Contains(ext)) return ArtifactType.ModFile;
            return ArtifactType.Other;
        }

        /// <summary>判断扩展名是否为常见图片格式。</summary>
        public static bool IsImageExtension(string extOrPath)
        {
            if (string.IsNullOrEmpty(extOrPath)) return false;
            // 允许传入完整路径或仅扩展名
            var ext = extOrPath.StartsWith('.')
                ? extOrPath.ToLowerInvariant()
                : Path.GetExtension(extOrPath).ToLowerInvariant();
            return ImageExtensions.Contains(ext);
        }
    }
}
