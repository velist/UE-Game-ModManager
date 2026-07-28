using System;
using System.Collections.Generic;
using System.IO;

namespace UEModManager.Services.Import
{
    /// <summary>
    /// 导入解压临时目录的选盘策略（纯函数）。
    ///
    /// <para>
    /// 存在的理由：整合包解压后可能有几十 GB。此前的回落位置是安装目录下的
    /// <c>Data\.import-tmp</c>，而安装目录默认在 <c>%LOCALAPPDATA%\Programs</c>，
    /// 也就是系统盘——导入一个大整合包足以把 C 盘写满，而 Windows 在系统盘满时
    /// 的表现远比"导入失败"糟糕。数据目录迁到 <c>%LOCALAPPDATA%</c> 之后
    /// 这条回落更没有存在意义：临时数据本来就不该混进用户数据目录。
    /// </para>
    ///
    /// <para>
    /// 优先级按"与最终产物同盘"排序。<c>backupPath</c> 与 <c>modPath</c> 正是解压完
    /// 要复制过去的两个目标，把临时目录放在它们旁边就自然落在游戏/备份所在的盘上，
    /// 也就避开了系统盘；此外这两个目录都是本程序已经在写的位置，不需要额外的权限假设。
    /// 二者都不可用时才回落 <c>%TEMP%</c>——它至少是系统保证可写的，
    /// 而且清理工具认得，万一进程被杀留下残骸也有人收。
    /// </para>
    ///
    /// <para>
    /// IO 留给调用方：本类只按顺序产出候选，由调用方逐个尝试 <c>CreateDirectory</c>。
    /// 这样"备份盘被拔掉"这类只有真正建目录才发现的问题也能自动降级到下一个候选。
    /// </para>
    /// </summary>
    public static class ImportTempRootSelector
    {
        /// <summary>临时目录的固定名称。以点开头，避免被当成一个 MOD 目录展示。</summary>
        public const string FolderName = ".import-tmp";

        /// <summary>
        /// 按优先级产出临时根目录的候选列表（已去重、已剔除空白项）。
        /// 调用方应取第一个能成功创建的。
        /// </summary>
        /// <param name="backupPath">MOD 备份目录，通常与解压产物同盘，且必定是本程序在写的位置。</param>
        /// <param name="modPath">游戏的 MOD 目录，位于游戏所在盘。</param>
        /// <param name="systemTempPath">系统临时目录，通常是 <c>Path.GetTempPath()</c>。</param>
        public static IReadOnlyList<string> GetCandidates(
            string? backupPath, string? modPath, string systemTempPath)
        {
            if (string.IsNullOrWhiteSpace(systemTempPath))
                throw new ArgumentException("系统临时目录不能为空", nameof(systemTempPath));

            var candidates = new List<string>();

            Add(backupPath);
            Add(modPath);
            // 回落：%TEMP%\UEModManager\.import-tmp。多套一层应用名，
            // 免得直接在 %TEMP% 根下摆一个看不出归属的 .import-tmp。
            Add(Path.Combine(systemTempPath, "UEModManager"));

            return candidates;

            void Add(string? parent)
            {
                if (string.IsNullOrWhiteSpace(parent)) return;

                var root = Path.Combine(parent.Trim(), FolderName);
                foreach (var existing in candidates)
                {
                    if (string.Equals(existing, root, StringComparison.OrdinalIgnoreCase)) return;
                }
                candidates.Add(root);
            }
        }
    }
}
