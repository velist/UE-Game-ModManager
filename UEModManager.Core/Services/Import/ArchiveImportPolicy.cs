using System;
using System.Collections.Generic;
using System.IO;

namespace UEModManager.Services.Import
{
    /// <summary>
    /// 解压产物的筛选策略（纯函数）。
    ///
    /// 取代原先"只保留 EngineProfile.ModFileExtensions 白名单"的过滤方式。
    /// 旧策略会把插件型 MOD 的载荷整包丢掉——UE4SS 的 .lua、CSharpLoader 的
    /// .dll，以及 .ini/.txt/.bin 都在白名单之外，而丢弃过程不留任何日志，
    /// 用户只看到"导入成功"却永远不生效（实测：1194 个文件里丢掉 1174 个）。
    ///
    /// 现改为"黑名单 + 全量保留"：只排除明确的开发副产品与归档残留，
    /// 其余文件一律保留。
    /// </summary>
    public static class ArchiveImportPolicy
    {
        /// <summary>
        /// 明确排除的扩展名（含点号，小写）。只放"装到游戏里没有任何意义"的文件。
        /// </summary>
        public static readonly IReadOnlySet<string> ExcludedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // 调试符号：体积巨大且游戏不读取
            ".pdb", ".ipdb", ".ncb", ".ilk", ".exp", ".lib", ".obj",

            // 日志与崩溃转储
            ".log", ".dmp", ".mdmp",

            // 版本库/操作系统垃圾
            ".ds_store", ".gitignore", ".gitattributes", ".gitkeep", ".thumbs",

            // 可执行程序本体：属于独立的第三方程序（如风灵月影修改器），
            // 挂到游戏目录反而有害
            ".exe", ".msi", ".bat", ".cmd", ".ps1",

            // 哈希清单与签名：替换/整合后必然失效，留着会让引擎校验报错
            ".sig", ".sha", ".md5", ".sfv",
        };

        /// <summary>扩展名是否属于"明确排除"。</summary>
        public static bool IsExcluded(string filePath)
        {
            var ext = Path.GetExtension(filePath);
            return !string.IsNullOrEmpty(ext) && ExcludedExtensions.Contains(ext);
        }

        /// <summary>文件是否属于归档/系统残留（如 macOS 的 ._foo、__MACOSX 下的文件）。</summary>
        public static bool IsJunkName(string filePath)
        {
            var name = Path.GetFileName(filePath);
            if (string.IsNullOrEmpty(name)) return true;
            if (name.StartsWith("._", StringComparison.Ordinal)) return true;
            if (name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)) return true;

            var dir = Path.GetDirectoryName(filePath);
            while (!string.IsNullOrEmpty(dir))
            {
                if (string.Equals(Path.GetFileName(dir), "__MACOSX", StringComparison.OrdinalIgnoreCase)) return true;
                dir = Path.GetDirectoryName(dir);
            }

            return false;
        }

        /// <summary>
        /// 综合判定：该解压产物是否应当导入。
        /// </summary>
        public static bool ShouldImport(string filePath)
            => !IsExcluded(filePath) && !IsJunkName(filePath);
    }

    /// <summary>
    /// 一次导入的保留/丢弃统计。用于把"丢了多少"写进日志与导入结果——
    /// 旧实现是静默丢弃，用户与日志都无从察觉。
    /// </summary>
    public sealed record ArchiveDropReport(int KeptCount, long KeptBytes, int DroppedCount, long DroppedBytes)
    {
        public static readonly ArchiveDropReport Empty = new(0, 0, 0, 0);

        /// <summary>是否有文件被丢弃。</summary>
        public bool HasDrops => DroppedCount > 0;

        /// <summary>丢弃比例（0..1），无文件时为 0。</summary>
        public double DroppedRatio
        {
            get
            {
                var total = KeptCount + DroppedCount;
                return total == 0 ? 0 : (double)DroppedCount / total;
            }
        }

        /// <summary>面向日志/UI 的一句话摘要。</summary>
        public string Describe()
            => HasDrops
                ? $"保留 {KeptCount} 个文件（{KeptBytes} 字节），排除 {DroppedCount} 个（{DroppedBytes} 字节）"
                : $"保留 {KeptCount} 个文件（{KeptBytes} 字节），无排除";
    }
}
