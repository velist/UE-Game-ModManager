using System;
using System.Collections.Generic;
using System.IO;

namespace UEModManager.Services.Import
{
    /// <summary>
    /// 压缩包扩展名识别（纯函数）。
    ///
    /// 把散落在 PackageImportService 中的 4 处硬编码 <c>".zip" or ".rar" or ".7z"</c>
    /// 集中到一处，让"哪些文件视为压缩包"成为可独立单测的规则。
    /// </summary>
    public static class CompressedArchive
    {
        /// <summary>当前支持的压缩包扩展名集合（含点号，全部小写）。</summary>
        public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".zip", ".rar", ".7z",
        };

        /// <summary>
        /// 判断给定路径是否压缩包。null / 空 / 无扩展名都返回 false。
        /// </summary>
        public static bool IsCompressed(string? filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return false;
            var ext = Path.GetExtension(filePath);
            if (string.IsNullOrEmpty(ext)) return false;
            return Extensions.Contains(ext);
        }
    }
}
