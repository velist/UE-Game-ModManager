using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UEModManager.Services.Import
{
    /// <summary>
    /// 散装容器组：同一目录下、文件名相同、只有扩展名不同的一组主 MOD 载荷。
    ///
    /// 典型是 UE5 IoStore 的 <c>X.pak + X.utoc + X.ucas</c> —— 三个文件是同一个 MOD 的三个分片，
    /// 缺任何一个游戏都不加载；Decima 的 <c>X.core + X.stream</c> 同理。
    /// 压缩包与文件夹导入靠 <see cref="ModFileGrouper"/> 归组，这里管的是玩家把解压出来的
    /// 文件直接拖进来的那条路径。
    ///
    /// 判据刻意比 <see cref="ModFileGrouper.GroupByBaseName"/> 严：文件名必须完全相同。
    /// 那边会把 <c>X_1</c> 与 <c>X_2</c> 归到一起，是因为打包者已经用"放进同一个压缩包"
    /// 表达过它们是一伙的；下载目录里并排的 <c>Skin_1.pak</c> 与 <c>Skin_2.pak</c>
    /// 没有这层担保，更可能是两个互斥的款式。
    /// </summary>
    public static class LooseContainerSet
    {
        /// <summary>
        /// 取 <paramref name="filePath"/> 所在的容器组。
        /// </summary>
        /// <param name="filePath">玩家选中的那个文件。</param>
        /// <param name="directoryFiles">它所在目录下的全部文件（含它自己与否均可）。</param>
        /// <param name="containerExtensions">当前引擎的主 MOD 载荷扩展名（含点号）。</param>
        /// <returns>
        /// 组内全部文件，按文件名排序以保证结果稳定；<paramref name="filePath"/> 本身不是主载荷时
        /// 只返回它自己 —— 同名的 <c>.dll</c>、<c>.ini</c> 不是 MOD 的分片，安装位置另有讲究。
        /// </returns>
        public static IReadOnlyList<string> Members(
            string filePath,
            IEnumerable<string> directoryFiles,
            IReadOnlyCollection<string> containerExtensions)
        {
            ArgumentNullException.ThrowIfNull(filePath);
            ArgumentNullException.ThrowIfNull(directoryFiles);
            ArgumentNullException.ThrowIfNull(containerExtensions);

            if (!IsContainer(filePath, containerExtensions)) return [filePath];

            var stem = Path.GetFileNameWithoutExtension(filePath);
            var members = directoryFiles
                .Where(candidate => IsContainer(candidate, containerExtensions)
                    && Path.GetFileNameWithoutExtension(candidate).Equals(stem, StringComparison.OrdinalIgnoreCase))
                .Append(filePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return members;
        }

        /// <summary>
        /// 同一个容器组里被选中了多个文件时只留第一个：整组由 <see cref="Members"/> 一并带上，
        /// 再逐个导入就会把同一份内容登记两三遍。其余路径（文件夹、压缩包、插件）原样保留、顺序不变。
        /// </summary>
        public static IReadOnlyList<string> Coalesce(
            IEnumerable<string> paths,
            IReadOnlyCollection<string> containerExtensions)
        {
            ArgumentNullException.ThrowIfNull(paths);
            ArgumentNullException.ThrowIfNull(containerExtensions);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (var path in paths)
            {
                if (IsContainer(path, containerExtensions) && !seen.Add(SetKey(path))) continue;
                result.Add(path);
            }

            return result;
        }

        private static bool IsContainer(string path, IReadOnlyCollection<string> containerExtensions)
        {
            var extension = Path.GetExtension(path);
            if (extension.Length == 0) return false;

            foreach (var candidate in containerExtensions)
                if (extension.Equals(candidate, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>目录 + 不含扩展名的文件名：同一组的文件键相同。</summary>
        private static string SetKey(string path)
            => Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, Path.GetFileNameWithoutExtension(path));
    }
}
