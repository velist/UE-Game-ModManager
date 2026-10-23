using System;
using System.Collections.Generic;
using System.IO;

namespace UEModManager.Services.Import
{
    /// <summary>
    /// 导入文件类型分类（决定 ImportSingleAsync 走哪条分支）。
    /// </summary>
    public enum ImportFileKind
    {
        /// <summary>压缩包（.zip/.rar/.7z）→ 走 ImportCompressedAsync。</summary>
        Compressed,

        /// <summary>命中宿主 DirectImportExtensions（.pak 等）→ 走 ImportDirectFileAsync。</summary>
        DirectImport,

        /// <summary>其他扩展名 → 走 ImportAsPluginAsync 兜底。</summary>
        Plugin,

        /// <summary>
        /// 明显不是 MOD 的文件（说明、网页、文档、图片、快捷方式）→ 不导入，提示玩家已跳过。
        /// 以前这类文件走插件兜底，被当成一个 MOD 包导进仓库，启用后还会被部署进游戏目录。
        /// </summary>
        NotAMod,
    }

    /// <summary>
    /// 导入文件分类决策（纯函数）。
    ///
    /// 主项目 PackageImportService.ImportSingleAsync 的扩展名 if-else 链下沉为
    /// 单一函数，调用方按结果分发到对应的 IO 分支。
    /// </summary>
    public static class ImportFileKindClassifier
    {
        /// <summary>
        /// 根据文件扩展名分类。
        /// 优先级：压缩包 > DirectImport > 明显不是 MOD（跳过）> 兜底为 Plugin。
        /// </summary>
        /// <param name="filePath">待导入的文件路径（仅取扩展名）。</param>
        /// <param name="directImportExtensions">来自宿主适配器的"直接导入"扩展名表（含点号，大小写不敏感）。</param>
        public static ImportFileKind Classify(
            string filePath,
            IReadOnlyCollection<string> directImportExtensions)
        {
            if (directImportExtensions == null) throw new ArgumentNullException(nameof(directImportExtensions));

            if (CompressedArchive.IsCompressed(filePath))
                return ImportFileKind.Compressed;

            var ext = Path.GetExtension(filePath ?? string.Empty);
            foreach (var allowed in directImportExtensions)
                if (string.Equals(allowed, ext, StringComparison.OrdinalIgnoreCase))
                    return ImportFileKind.DirectImport;

            // 放在宿主扩展名之后：引擎自己认的格式（如暗黑4 的 .dds）优先按 MOD 处理
            if (IsNotAModFile(filePath ?? string.Empty))
                return ImportFileKind.NotAMod;

            return ImportFileKind.Plugin;
        }

        /// <summary>说明 / 网页 / 文档 / 图片 / 视频 / 快捷方式这类单独导入毫无意义的文件。</summary>
        private static readonly HashSet<string> NotAModExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".nfo", ".rtf", ".pdf", ".doc", ".docx",
            ".url", ".lnk", ".html", ".htm",
            ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp",
            ".mp4", ".webm", ".mkv",
        };

        /// <summary>单独导入时是否应直接跳过（文件名以 readme / 说明 开头也算）。</summary>
        public static bool IsNotAModFile(string filePath)
        {
            var name = Path.GetFileName(filePath);
            if (string.IsNullOrEmpty(name)) return false;
            if (NotAModExtensions.Contains(Path.GetExtension(name))) return true;
            return name.StartsWith("readme", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("说明", StringComparison.Ordinal);
        }
    }
}
