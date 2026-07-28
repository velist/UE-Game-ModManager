using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using UEModManager.Services.Paths;

namespace UEModManager.Services
{
    /// <summary>
    /// 数据搬迁的文件操作执行器。
    ///
    /// <para>
    /// 从 <see cref="DataLocationMigrator"/> 里单独拆出来，是因为断电/中断相关的
    /// bug 全部住在这几个文件操作里，而 Migrator 本身依赖 <see cref="UiPreferences"/>
    /// 的全局状态（真实的 <c>%APPDATA%</c> 文件），没法在测试里安全地驱动。
    /// 本类只认路径，不读任何配置，因此可以在临时目录上完整测试。
    /// </para>
    ///
    /// <para>
    /// 四步搬移的顺序不可换：<b>复制 → 校验 → 写墓碑 → 删源</b>。
    /// 墓碑是"复制完整且校验通过"的唯一证据，任何一步失败都保留旧数据不动。
    /// 四步之外还夹着一对进行中标记的写与清（见 <see cref="InProgressMarkerName"/>）：
    /// 复制前立、写完墓碑后清，用来证明"目标里的残留是我留下的"。
    /// </para>
    ///
    /// <para>
    /// 四个步骤方法都是 <c>virtual</c>，<see cref="Execute"/> 刻意不是。这条界线是有意的：
    /// 派生类只允许替换<b>某一步做了什么</b>（测试据此在指定阶段注入失败，复现断电），
    /// 不允许替换<b>步骤的先后顺序</b>——顺序正是本类要守住的不变量，能被覆盖就守不住了。
    /// </para>
    /// </summary>
    public class DataRelocationExecutor
    {
        /// <summary>目录搬迁的墓碑文件名。</summary>
        public const string DirectoryTombstoneName = "migrated-to.txt";

        /// <summary>单文件搬迁的墓碑后缀。</summary>
        public const string FileTombstoneSuffix = ".migrated-to.txt";

        /// <summary>
        /// "搬迁进行中"标记。写在<b>目标</b>位置，从复制开始起、到墓碑写下为止一直存在。
        ///
        /// <para>
        /// 它回答的是一个墓碑回答不了的问题：<b>目标位置里的东西，是不是搬迁器自己写的？</b>
        /// 没有它的话，"目标有内容且无墓碑"只能一律当成"上次中断的半份数据"清掉重来——
        /// 而路径归口之后，老用户升级后的全部新数据本来就写在新位置，
        /// 开关翻开的那一刻这套判据会把用户升级以来的劳动整个清空，再把升级前的旧状态盖回去。
        /// 有了标记，"我留下的残留"与"应用的真实数据"才区分得开。
        /// </para>
        /// </summary>
        public const string InProgressMarkerName = ".uemm-migration-in-progress";

        /// <summary>单文件搬迁的进行中标记后缀。</summary>
        public const string InProgressMarkerSuffix = ".uemm-migration-in-progress";

        private readonly ILogger? _logger;

        public DataRelocationExecutor(ILogger? logger = null)
        {
            _logger = logger;
        }

        /// <summary>该路径是否是墓碑文件。</summary>
        public static bool IsTombstone(string path)
        {
            var name = Path.GetFileName(path);
            return string.Equals(name, DirectoryTombstoneName, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(FileTombstoneSuffix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>取某项的墓碑路径。</summary>
        public static string GetTombstonePath(string legacyPath, bool isFile)
            => isFile
                ? legacyPath + FileTombstoneSuffix
                : Path.Combine(legacyPath, DirectoryTombstoneName);

        /// <summary>该路径是否是搬迁进行中标记。</summary>
        public static bool IsInProgressMarker(string path)
        {
            var name = Path.GetFileName(path);
            return string.Equals(name, InProgressMarkerName, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(InProgressMarkerSuffix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 该路径是否是搬迁器自己的元数据（墓碑或进行中标记）。
        /// 这类文件不算数据：不参与内容判定、不复制、不校验、也不随删源被抹掉。
        /// </summary>
        public static bool IsMigratorMetadata(string path)
            => IsTombstone(path) || IsInProgressMarker(path);

        /// <summary>取某项的进行中标记路径（位于<b>目标</b>侧）。</summary>
        public static string GetInProgressMarkerPath(string targetPath, bool isFile)
            => isFile
                ? targetPath + InProgressMarkerSuffix
                : Path.Combine(targetPath, InProgressMarkerName);

        /// <summary>
        /// 目录存在<b>且有内容</b>（搬迁器自己的元数据不算内容）。
        ///
        /// 空目录按"不存在"处理：应用启动时会主动创建一批空目录（App 建 Data、
        /// 各服务构造函数建自己的目录），把它们当成"有旧数据要迁"会凭空产生
        /// 一堆无意义的搬移与墓碑。
        /// </summary>
        public static bool DirectoryHasContent(string path)
        {
            try
            {
                if (!Directory.Exists(path)) return false;
                return Directory.EnumerateFileSystemEntries(path)
                    .Any(entry => !IsMigratorMetadata(entry));
            }
            catch
            {
                // 权限/占用等问题按"不存在"处理，最坏结果是本次不迁移
                return false;
            }
        }

        /// <summary>文件是否存在（不抛）。</summary>
        public static bool FileExists(string path)
        {
            try { return File.Exists(path); }
            catch { return false; }
        }

        /// <summary>执行一步。调用方负责捕获异常——本方法失败即表示该项数据仍完整留在旧位置。</summary>
        /// <param name="excludedChildDirectories">
        /// 源目录下需原样留下、不参与本步搬移的**顶层子目录名**。
        /// 用于把嵌套在别人内部、但目标位置完全不同的数据拆成独立一项
        /// （部署事务备份住在 <c>Data\Backups</c>，目标却是 <c>Backups\Deployments</c>）。
        /// 排除项不复制、不计入校验、也不删源，因此两项互不干扰：
        /// 谁先执行都一样，一项失败也不会污染另一项。
        /// </param>
        public void Execute(RelocationStep step, bool isFile,
            IReadOnlyCollection<string>? excludedChildDirectories = null)
        {
            switch (step.Action)
            {
                case RelocationAction.PurgeTargetThenCopy:
                    PurgeTarget(step, isFile);
                    MarkInProgress(step, isFile);
                    CopyAndVerify(step, isFile, excludedChildDirectories);
                    WriteTombstone(step, isFile);
                    ClearInProgressMarker(step, isFile);
                    DeleteLegacy(step, isFile, excludedChildDirectories);
                    break;

                case RelocationAction.Copy:
                    MarkInProgress(step, isFile);
                    CopyAndVerify(step, isFile, excludedChildDirectories);
                    WriteTombstone(step, isFile);
                    ClearInProgressMarker(step, isFile);
                    DeleteLegacy(step, isFile, excludedChildDirectories);
                    break;

                case RelocationAction.ResumeCleanup:
                    DeleteLegacy(step, isFile, excludedChildDirectories);
                    break;

                default:
                    throw new InvalidOperationException($"执行器不处理该动作: {step.Action}");
            }
        }

        /// <summary>
        /// 在目标位置立下"搬迁进行中"的记号。<b>必须在往目标写第一个字节之前</b>，
        /// 否则中途断电留下的残留就没有归属证据，下次启动分不清那是自己的半份数据
        /// 还是应用的真实数据。非 virtual：它和四步的先后关系是不变量的一部分。
        /// </summary>
        private static void MarkInProgress(RelocationStep step, bool isFile)
        {
            if (!isFile) Directory.CreateDirectory(step.TargetPath);

            var marker = GetInProgressMarkerPath(step.TargetPath, isFile);
            var markerDir = Path.GetDirectoryName(marker);
            if (!string.IsNullOrEmpty(markerDir)) Directory.CreateDirectory(markerDir);

            File.WriteAllText(marker,
                $"UEModManager 正在把 {step.LegacyPath} 搬到此处（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）。" +
                $"{Environment.NewLine}若此文件长期存在，说明上次搬迁被中断，下次启动会自动清理重来。");
        }

        /// <summary>
        /// 清掉进行中标记。位置卡在<b>写墓碑之后、删源之前</b>：墓碑一落地，目标就是权威副本，
        /// 再有人来清空它就是毁数据了。提前清则会让"写墓碑前断电"的残留失去归属证据。
        /// </summary>
        private static void ClearInProgressMarker(RelocationStep step, bool isFile)
        {
            var marker = GetInProgressMarkerPath(step.TargetPath, isFile);
            if (File.Exists(marker)) File.Delete(marker);
        }

        /// <summary>复制并校验。校验不过直接抛，此时墓碑尚未写下，旧数据仍是唯一可信副本。</summary>
        public virtual void CopyAndVerify(RelocationStep step, bool isFile,
            IReadOnlyCollection<string>? excludedChildDirectories = null)
        {
            if (isFile)
            {
                var targetDir = Path.GetDirectoryName(step.TargetPath);
                if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);
                File.Copy(step.LegacyPath, step.TargetPath, overwrite: true);
                VerifyFile(step.LegacyPath, step.TargetPath);
            }
            else
            {
                CopyDirectory(step.LegacyPath, step.TargetPath, excludedChildDirectories);
                VerifyDirectory(step.LegacyPath, step.TargetPath, excludedChildDirectories);
            }

            _logger?.LogInformation("[DataMigration] {Name} 已复制并校验：{From} → {To}",
                step.Name, step.LegacyPath, step.TargetPath);
        }

        /// <summary>
        /// 递归复制目录，跳过搬迁器自己的元数据（墓碑属于旧位置，跟到新位置会让下次探测误判；
        /// 进行中标记同理，它描述的是这一次搬迁的状态，不是数据）。
        /// <paramref name="excludedChildDirectories"/> 只对<b>顶层</b>生效——排除的是
        /// 具名的一项数据，而不是所有同名子目录。
        /// </summary>
        public static void CopyDirectory(string source, string target,
            IReadOnlyCollection<string>? excludedChildDirectories = null)
        {
            Directory.CreateDirectory(target);

            foreach (var file in Directory.GetFiles(source))
            {
                if (IsMigratorMetadata(file)) continue;
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            }

            foreach (var dir in Directory.GetDirectories(source))
            {
                if (IsExcluded(dir, excludedChildDirectories)) continue;
                CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
            }
        }

        /// <summary>
        /// 校验目录：文件数与总字节数一致，且每个 JSON 都能解析。
        ///
        /// 只比字节数不足以发现"复制了但内容被截断成同样长度"的情况；
        /// JSON 恰好是本次搬移的绝大多数内容，解析一遍代价很低。
        /// </summary>
        public static void VerifyDirectory(string source, string target,
            IReadOnlyCollection<string>? excludedChildDirectories = null)
        {
            var sourceFiles = SafeEnumerate(source, excludedChildDirectories);
            var targetFiles = SafeEnumerate(target, excludedChildDirectories);

            if (sourceFiles.Count != targetFiles.Count)
            {
                throw new IOException(
                    $"校验失败：文件数不一致（源 {sourceFiles.Count}，目标 {targetFiles.Count}）");
            }

            var sourceBytes = sourceFiles.Sum(f => new FileInfo(f).Length);
            var targetBytes = targetFiles.Sum(f => new FileInfo(f).Length);
            if (sourceBytes != targetBytes)
            {
                throw new IOException(
                    $"校验失败：总字节数不一致（源 {sourceBytes}，目标 {targetBytes}）");
            }

            foreach (var json in targetFiles.Where(f =>
                         f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            {
                VerifyJsonParsable(json);
            }
        }

        /// <summary>校验单文件：字节数一致，JSON 可解析。</summary>
        public static void VerifyFile(string source, string target)
        {
            var sourceLength = new FileInfo(source).Length;
            var targetLength = new FileInfo(target).Length;
            if (sourceLength != targetLength)
            {
                throw new IOException(
                    $"校验失败：字节数不一致（源 {sourceLength}，目标 {targetLength}）");
            }

            if (target.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                VerifyJsonParsable(target);
            }
        }

        /// <summary>写墓碑。此刻起该项被视为"已完成复制与校验"。</summary>
        public virtual void WriteTombstone(RelocationStep step, bool isFile)
        {
            var content =
                $"此位置的数据已于 {DateTime.Now:yyyy-MM-dd HH:mm:ss} 迁移至：{Environment.NewLine}" +
                $"{step.TargetPath}{Environment.NewLine}{Environment.NewLine}" +
                "此文件由 UEModManager 自动生成，用于避免重复迁移，请勿删除。";

            var tombstone = GetTombstonePath(step.LegacyPath, isFile);
            var tombstoneDir = Path.GetDirectoryName(tombstone);
            if (!string.IsNullOrEmpty(tombstoneDir)) Directory.CreateDirectory(tombstoneDir);
            File.WriteAllText(tombstone, content);
        }

        /// <summary>
        /// 清理旧位置。目录情况下**保留墓碑**：整个删掉的话下次启动会因"旧位置不存在"
        /// 而判定为未迁移过——结论虽然相同，但排障时看不出这里发生过什么。
        /// </summary>
        public virtual void DeleteLegacy(RelocationStep step, bool isFile,
            IReadOnlyCollection<string>? excludedChildDirectories = null)
        {
            if (isFile)
            {
                if (File.Exists(step.LegacyPath)) File.Delete(step.LegacyPath);
            }
            else if (Directory.Exists(step.LegacyPath))
            {
                foreach (var file in Directory.GetFiles(step.LegacyPath))
                {
                    if (!IsMigratorMetadata(file)) File.Delete(file);
                }
                foreach (var dir in Directory.GetDirectories(step.LegacyPath))
                {
                    // 排除项没被复制过，删掉就是直接销毁用户数据。
                    if (IsExcluded(dir, excludedChildDirectories)) continue;
                    Directory.Delete(dir, recursive: true);
                }
            }

            _logger?.LogInformation("[DataMigration] {Name} 旧位置已清理：{Path}",
                step.Name, step.LegacyPath);
        }

        /// <summary>
        /// 清空目标位置的残留。
        ///
        /// <para>
        /// <b>只清自己留下的。</b>判据是目标侧的进行中标记：有标记才说明这堆东西是上一次
        /// 被中断的搬迁写的，清掉是唯一正确的做法（绝不能在半份数据上继续追加）。
        /// 没有标记却有内容，说明目标是被别人正常使用的位置——路径归口之后老用户升级以来的
        /// 全部新数据就在那里——此时清空等于把用户升级后的劳动删光、再拿升级前的旧状态盖回去。
        /// 宁可让这一项失败、让用户看到"迁移未完成"，也绝不能动它。
        /// </para>
        /// </summary>
        public virtual void PurgeTarget(RelocationStep step, bool isFile)
        {
            if (!FileExists(GetInProgressMarkerPath(step.TargetPath, isFile)))
            {
                throw new IOException(
                    $"拒绝清空目标：{step.TargetPath} 已有内容，但没有搬迁进行中标记，" +
                    "说明这些数据不是上次中断的残留，而是新位置被正常使用后写下的。" +
                    $"本项保持原样，{step.LegacyPath} 的数据也一并保留。");
            }

            _logger?.LogWarning(
                "[DataMigration] {Name} 目标位置存在无墓碑的残留（上次中断），清空后重新复制：{Path}",
                step.Name, step.TargetPath);

            if (isFile)
            {
                if (File.Exists(step.TargetPath)) File.Delete(step.TargetPath);
            }
            else if (Directory.Exists(step.TargetPath))
            {
                Directory.Delete(step.TargetPath, recursive: true);
            }
        }

        /// <summary>该顶层子目录是否被排除在本步搬移之外。</summary>
        private static bool IsExcluded(string directoryPath,
            IReadOnlyCollection<string>? excludedChildDirectories)
        {
            if (excludedChildDirectories is null || excludedChildDirectories.Count == 0) return false;
            var name = Path.GetFileName(directoryPath.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return excludedChildDirectories.Any(
                excluded => string.Equals(excluded, name, StringComparison.OrdinalIgnoreCase));
        }

        private static List<string> SafeEnumerate(
            string root, IReadOnlyCollection<string>? excludedChildDirectories = null)
        {
            if (!Directory.Exists(root)) return new List<string>();

            var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => !IsMigratorMetadata(f));

            if (excludedChildDirectories is { Count: > 0 })
            {
                var excludedRoots = Directory.GetDirectories(root)
                    .Where(dir => IsExcluded(dir, excludedChildDirectories))
                    .Select(dir => dir + Path.DirectorySeparatorChar)
                    .ToList();

                files = files.Where(f => !excludedRoots.Any(
                    prefix => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
            }

            return files.ToList();
        }

        private static void VerifyJsonParsable(string path)
        {
            try
            {
                using var _ = JsonDocument.Parse(File.ReadAllText(path));
            }
            catch (JsonException ex)
            {
                throw new IOException($"校验失败：{path} 复制后无法解析", ex);
            }
        }
    }
}
