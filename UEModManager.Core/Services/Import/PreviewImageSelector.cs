using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UEModManager.Services.Detection;

namespace UEModManager.Services.Import
{
    /// <summary>
    /// 预览图选择（纯函数）。
    ///
    /// 主项目 PackageImportService.FindPreviewInDirectory 的"先找 preview 前缀，再找任何图片"
    /// 选择逻辑下沉。IO（Directory.GetFiles）留主项目，本类只关心"给定一组文件名时如何挑"。
    /// </summary>
    public static class PreviewImageSelector
    {
        /// <summary>
        /// 从给定文件路径列表中选预览图。
        /// 规则：
        /// 1. 文件名（含扩展名）以 "preview" 开头（不区分大小写）的第一个；
        /// 2. 否则任何图片扩展名（复用 <see cref="ArtifactTypeDetector.IsImageExtension"/>）的第一个；
        /// 3. 都没有则返回 null。
        /// </summary>
        /// <param name="filePaths">候选文件路径列表（通常来自 Directory.GetFiles 顶级目录）。</param>
        public static string? Select(IEnumerable<string> filePaths)
        {
            if (filePaths == null) throw new ArgumentNullException(nameof(filePaths));

            var snapshot = filePaths as IList<string> ?? filePaths.ToList();

            foreach (var f in snapshot)
            {
                var name = Path.GetFileName(f);
                if (!string.IsNullOrEmpty(name)
                    && name.StartsWith("preview", StringComparison.OrdinalIgnoreCase))
                    return f;
            }

            foreach (var f in snapshot)
                if (ArtifactTypeDetector.IsImageExtension(f))
                    return f;

            return null;
        }
    }
}
