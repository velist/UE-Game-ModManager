using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UEModManager.Services.Import
{
    /// <summary>
    /// 一个"导入单元"：压缩包里的一个顶层目录，通常就是一个 MOD 自己的文件夹。
    /// </summary>
    /// <param name="Name">
    /// 单元名（顶层目录名）。<see cref="ArchiveImportUnitResolver.RootUnitName"/> 表示
    /// "散落在压缩包根下的文件"，它们不属于任何一个 MOD 文件夹。
    /// </param>
    /// <param name="Files">属于该单元的文件绝对路径（按首次出现顺序）。</param>
    public sealed record ArchiveImportUnit(string Name, IReadOnlyList<string> Files);

    /// <summary>
    /// 把一次解压的产物切成"导入单元"（纯函数）。
    ///
    /// 现场症状：导入 <c>0916.zip</c>（内含 7 个 MOD 文件夹、14 个 .pak 加 1177 个插件载荷文件）
    /// 之后，仓库里多出一个名叫 <c>0916</c> 的巨型包，里面混着 UE4SS 运行时、CSharpLoader
    /// 和一堆互不相干的 MOD 文件。用户看到的"一个 MOD"其实是整包内容。
    ///
    /// 原因：旧实现把整包所有 <c>.pak</c> 丢进同一个分组池按基础名分组，再把**全部**非 MOD
    /// 载荷折成一个以压缩包名命名的包。压缩包内顶层目录的信息在这一步被丢掉，
    /// 于是"一个文件夹 = 一个 MOD"这个由打包者表达出来的结构完全消失。
    ///
    /// 更严重的后果是冲突检测失效：所有文件都落在同一个包名下，
    /// <c>ConflictDetector</c> 按 PackageKey 去重后只剩一个声称者，
    /// "同一路径被两个包同时声明"因此永远不成立。实测该压缩包里有 583 处跨 MOD 重叠
    /// （581 处内容相同、2 处内容不同），全部因为并包而漏报。
    ///
    /// 规则：以解压根为基准，文件路径的**第一段**就是它的单元名。
    /// 根目录下的散装文件（没有第一段，或第一段其实是文件而非目录）归入根单元。
    /// </summary>
    public static class ArchiveImportUnitResolver
    {
        /// <summary>根单元：不属于任何顶层目录的文件。</summary>
        public const string RootUnitName = "";

        /// <summary>
        /// 按顶层目录切分文件，保持首次出现顺序。
        /// </summary>
        /// <param name="filePaths">解压产物的绝对路径。</param>
        /// <param name="rootDirectory">本次解压的根目录。</param>
        public static IReadOnlyList<ArchiveImportUnit> Partition(
            IEnumerable<string> filePaths, string rootDirectory)
        {
            if (filePaths == null) throw new ArgumentNullException(nameof(filePaths));
            if (rootDirectory == null) throw new ArgumentNullException(nameof(rootDirectory));

            var order = new List<string>();
            var buckets = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in filePaths)
            {
                var unit = ResolveUnitName(file, rootDirectory);
                if (!buckets.TryGetValue(unit, out var bucket))
                {
                    bucket = [];
                    buckets[unit] = bucket;
                    order.Add(unit);
                }
                bucket.Add(file);
            }

            var units = new List<ArchiveImportUnit>(order.Count);
            foreach (var name in order)
                units.Add(new ArchiveImportUnit(name, buckets[name]));
            return units;
        }

        /// <summary>
        /// 计算单个文件所属的单元名。
        /// </summary>
        public static string ResolveUnitName(string filePath, string rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("文件路径不能为空", nameof(filePath));
            if (string.IsNullOrWhiteSpace(rootDirectory)) return RootUnitName;

            var root = Path.GetFullPath(rootDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var relative = Path.GetRelativePath(root, Path.GetFullPath(filePath));

            // 不在解压根下（理论上不会发生）：交给调用方按裸文件名处理。
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathFullyQualified(relative))
                return RootUnitName;

            var separatorIndex = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
            if (separatorIndex < 0) return RootUnitName;

            var first = relative[..separatorIndex];
            if (string.IsNullOrEmpty(first)) return RootUnitName;

            // 走到这里说明 relative 还有第二段（separatorIndex >= 0），
            // 即第一段后面跟着子路径 —— 文件不可能有子路径，所以第一段必然是目录。
            // 旧实现用 Directory.Exists 再确认一次，但 Core 层不允许文件系统 IO，
            // 而"有没有第二段"已经足以判定，那次 IO 纯属多余。
            return first;
        }

        /// <summary>
        /// 切分结果是否"其实只是一个 MOD 的内部结构"。
        ///
        /// 顶层第一段是引擎/安装锚点目录（<c>b1</c>、<c>Binaries</c>、<c>Content</c>、<c>ue4ss</c> …）
        /// 时，它表达的不是"这里面有多个 MOD"，而是"这个 MOD 自带游戏目录层级"：
        /// 用户单独把解压出来的「超级识破」文件夹拖进来（内部直接是 <c>b1\Binaries\Win64\…</c>），
        /// 或某个压缩包内容就是 <c>b1\Binaries\…</c>，此时拿第一段当包名会得到名叫 <c>b1</c> 的包
        /// （第二个这样的 MOD 变成 <c>b1_2</c>），而 MOD 的真名被丢掉了。
        ///
        /// 判据只认锚点名，不改动切分本身：调用方据此把整个输入退回单单元路径，
        /// 用压缩包名 / 文件夹名给包命名。
        /// </summary>
        public static bool LooksLikeSingleModLayout(IReadOnlyList<ArchiveImportUnit> units)
        {
            if (units == null) throw new ArgumentNullException(nameof(units));
            return units.Any(u => ArchiveTargetPathResolver.IsInstallAnchor(u.Name));
        }
    }
}
