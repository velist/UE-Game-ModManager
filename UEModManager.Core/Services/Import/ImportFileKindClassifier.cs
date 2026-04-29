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
        /// 优先级：压缩包 > DirectImport > 兜底为 Plugin。
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

            return ImportFileKind.Plugin;
        }
    }
}
