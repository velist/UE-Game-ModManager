using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace UEModManager.Services.Import
{
    /// <summary>
    /// MOD 文件分组算法（纯函数）。
    ///
    /// 解压后通常出现一组同基础名的不同后缀文件（如 <c>MyMod.pak / MyMod.utoc / MyMod.ucas</c>，
    /// 或 <c>MyMod_P.pak</c> 这样的 UE patch 后缀）。这些文件应归为同一 Package。
    /// 这里抽出三个职责：
    /// <list type="bullet">
    /// <item><see cref="ExtractBaseName"/>：去掉 UE patch 后缀（_数字 / _P / _p）。</item>
    /// <item><see cref="GroupByBaseName"/>：按基础名相同性合并文件。</item>
    /// <item><see cref="SelectGroupName"/>：从一组文件按宿主优先级扩展名挑代表名。</item>
    /// </list>
    /// </summary>
    public static class ModFileGrouper
    {
        private static readonly Regex BaseNameSuffixRegex =
            new(@"(_\d*|_P|_p)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// 提取文件名（不含扩展名）的基础部分：去掉末尾的 <c>_数字</c> / <c>_P</c> / <c>_p</c> 等 UE patch 后缀。
        /// </summary>
        /// <example>
        /// <c>MyMod_P</c> → <c>MyMod</c>；<c>MyMod_2</c> → <c>MyMod</c>；<c>MyMod</c> → <c>MyMod</c>。
        /// </example>
        public static string ExtractBaseName(string fileNameWithoutExtension)
        {
            if (fileNameWithoutExtension == null) return string.Empty;
            return BaseNameSuffixRegex.Replace(fileNameWithoutExtension, string.Empty);
        }

        /// <summary>
        /// 按 <see cref="ExtractBaseName"/> 的结果分组文件。返回 dict 的 key 是组内首个出现的"原始 fileName（不含扩展名）"，
        /// 这个 key 仅用作分组桶标识；调用方应使用 <see cref="SelectGroupName"/> 决定最终包名。
        /// </summary>
        /// <param name="filePaths">解压后的所有候选文件（绝对路径或相对路径都可，仅取文件名）。</param>
        /// <returns>组桶 → 组内文件列表（保留入参顺序）。dict 自带 OrdinalIgnoreCase 比较器。</returns>
        public static IReadOnlyDictionary<string, List<string>> GroupByBaseName(IEnumerable<string> filePaths)
        {
            if (filePaths == null) throw new ArgumentNullException(nameof(filePaths));

            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in filePaths)
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                var baseName = ExtractBaseName(fileName);

                string? matchedKey = null;
                foreach (var key in result.Keys)
                {
                    if (ExtractBaseName(key).Equals(baseName, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedKey = key;
                        break;
                    }
                }

                if (matchedKey != null)
                    result[matchedKey].Add(file);
                else
                    result[fileName] = new List<string> { file };
            }

            return result;
        }

        /// <summary>
        /// 从一组文件中按宿主提供的扩展名优先级挑选代表名。
        /// 同优先级时取"文件名（不含扩展名）最长"的文件，与原 PackageImportService 行为一致。
        /// 全部不命中优先级时回退到首文件的文件名（不含扩展名）。
        /// </summary>
        /// <param name="groupFiles">组内文件路径列表。</param>
        /// <param name="priorityExtensions">宿主推荐顺序的扩展名（含点号，大小写不敏感）。可空。</param>
        public static string? SelectGroupName(
            IList<string> groupFiles,
            IReadOnlyList<string>? priorityExtensions)
        {
            if (groupFiles == null || groupFiles.Count == 0) return null;

            if (priorityExtensions != null)
            {
                foreach (var ext in priorityExtensions)
                {
                    var candidate = groupFiles
                        .Where(f => Path.GetExtension(f).Equals(ext, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(f => Path.GetFileNameWithoutExtension(f).Length)
                        .Select(Path.GetFileNameWithoutExtension)
                        .FirstOrDefault();
                    if (candidate != null) return candidate;
                }
            }

            return Path.GetFileNameWithoutExtension(groupFiles[0]);
        }
    }
}
