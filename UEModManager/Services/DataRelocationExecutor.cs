using System;
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
    /// </para>
    /// </summary>
    public sealed class DataRelocationExecutor
    {
        /// <summary>目录搬迁的墓碑文件名。</summary>
        public const string DirectoryTombstoneName = "migrated-to.txt";

        /// <summary>单文件搬迁的墓碑后缀。</summary>
        public const string FileTombstoneSuffix = ".migrated-to.txt";

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

        /// <summary>
        /// 目录存在<b>且有内容</b>（墓碑不算内容）。
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
                return Directory.EnumerateFileSystemEntries(path).Any(entry => !IsTombstone(entry));
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
        public void Execute(RelocationStep step, bool isFile)
        {
            switch (step.Action)
            {
                case RelocationAction.PurgeTargetThenCopy:
                    PurgeTarget(step, isFile);
                    CopyAndVerify(step, isFile);
                    WriteTombstone(step, isFile);
                    DeleteLegacy(step, isFile);
                    break;

                case RelocationAction.Copy:
                    CopyAndVerify(step, isFile);
                    WriteTombstone(step, isFile);
                    DeleteLegacy(step, isFile);
                    break;

                case RelocationAction.ResumeCleanup:
                    DeleteLegacy(step, isFile);
                    break;

                default:
                    throw new InvalidOperationException($"执行器不处理该动作: {step.Action}");
            }
        }

        /// <summary>复制并校验。校验不过直接抛，此时墓碑尚未写下，旧数据仍是唯一可信副本。</summary>
        public void CopyAndVerify(RelocationStep step, bool isFile)
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
                CopyDirectory(step.LegacyPath, step.TargetPath);
                VerifyDirectory(step.LegacyPath, step.TargetPath);
            }

            _logger?.LogInformation("[DataMigration] {Name} 已复制并校验：{From} → {To}",
                step.Name, step.LegacyPath, step.TargetPath);
        }

        /// <summary>递归复制目录，跳过墓碑（墓碑属于旧位置，跟到新位置会让下次探测误判）。</summary>
        public static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);

            foreach (var file in Directory.GetFiles(source))
            {
                if (IsTombstone(file)) continue;
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            }

            foreach (var dir in Directory.GetDirectories(source))
            {
                CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
            }
        }

        /// <summary>
        /// 校验目录：文件数与总字节数一致，且每个 JSON 都能解析。
        ///
        /// 只比字节数不足以发现"复制了但内容被截断成同样长度"的情况；
        /// JSON 恰好是本次搬移的绝大多数内容，解析一遍代价很低。
        /// </summary>
        public static void VerifyDirectory(string source, string target)
        {
            var sourceFiles = SafeEnumerate(source);
            var targetFiles = SafeEnumerate(target);

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
        public void WriteTombstone(RelocationStep step, bool isFile)
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
        public void DeleteLegacy(RelocationStep step, bool isFile)
        {
            if (isFile)
            {
                if (File.Exists(step.LegacyPath)) File.Delete(step.LegacyPath);
            }
            else if (Directory.Exists(step.LegacyPath))
            {
                foreach (var file in Directory.GetFiles(step.LegacyPath))
                {
                    if (!IsTombstone(file)) File.Delete(file);
                }
                foreach (var dir in Directory.GetDirectories(step.LegacyPath))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }

            _logger?.LogInformation("[DataMigration] {Name} 旧位置已清理：{Path}",
                step.Name, step.LegacyPath);
        }

        /// <summary>清空目标位置的无墓碑残留（上次中断留下的半份数据）。</summary>
        public void PurgeTarget(RelocationStep step, bool isFile)
        {
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

        private static System.Collections.Generic.List<string> SafeEnumerate(string root)
            => Directory.Exists(root)
                ? Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                    .Where(f => !IsTombstone(f)).ToList()
                : new System.Collections.Generic.List<string>();

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
