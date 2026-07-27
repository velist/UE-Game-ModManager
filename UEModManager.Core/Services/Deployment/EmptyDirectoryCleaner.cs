using System;
using System.IO;

namespace UEModManager.Services.Deployment
{
    /// <summary>
    /// 删除已部署文件后，清理其留下的空目录。
    ///
    /// 此前 DeploymentService / CopyBackend / HardLinkBackend 各有一份逐字相同的实现，
    /// 且都是"从目标文件所在目录一路向上删空目录"，不校验是否还在部署范围内：
    /// 禁用最后一个 MOD 会连 <c>~mods</c> 目录本身一起删掉，上层若也空就继续上删。
    /// 本类要求显式传入部署根，根目录自身及其之外的任何目录都不会被删除。
    ///
    /// IO 通过可选委托注入，便于单测；不传则使用真实的 <see cref="Directory"/>。
    /// </summary>
    public static class EmptyDirectoryCleaner
    {
        /// <summary>
        /// 从 <paramref name="startDirectory"/> 逐级向上删除空目录，
        /// 遇到非空目录、不存在的目录、删除失败，或到达 <paramref name="stopAtRoot"/> 时停止。
        /// <paramref name="stopAtRoot"/> 自身永不删除。
        /// </summary>
        /// <param name="startDirectory">起点目录（通常是被删文件所在目录）。</param>
        /// <param name="stopAtRoot">部署根，删除范围的硬边界。</param>
        /// <param name="directoryExists">目录是否存在，默认 <see cref="Directory.Exists"/>。</param>
        /// <param name="isDirectoryEmpty">目录是否为空，默认"无任何文件与子目录"。</param>
        /// <param name="deleteDirectory">删除目录，默认 <see cref="Directory.Delete(string)"/>。</param>
        /// <returns>实际删除的目录数量。</returns>
        public static int CleanUpwards(
            string? startDirectory,
            string? stopAtRoot,
            Func<string, bool>? directoryExists = null,
            Func<string, bool>? isDirectoryEmpty = null,
            Action<string>? deleteDirectory = null)
        {
            if (string.IsNullOrWhiteSpace(startDirectory) || string.IsNullOrWhiteSpace(stopAtRoot))
                return 0;

            var root = TryNormalize(stopAtRoot);
            var current = TryNormalize(startDirectory);
            if (root == null || current == null) return 0;

            var exists = directoryExists ?? Directory.Exists;
            var isEmpty = isDirectoryEmpty ?? (d => Directory.GetFileSystemEntries(d).Length == 0);
            var delete = deleteDirectory ?? (d => Directory.Delete(d));

            var deleted = 0;
            while (!string.IsNullOrEmpty(current) && IsStrictlyInside(current, root))
            {
                if (!exists(current) || !isEmpty(current)) break;

                try
                {
                    delete(current);
                }
                catch
                {
                    // 目录被占用/无权限：到此为止，不再继续向上（best-effort 清理）
                    break;
                }

                deleted++;
                current = Path.GetDirectoryName(current);
            }

            return deleted;
        }

        /// <summary>
        /// 由"绝对目标路径"与"相对目标路径"反推部署根：前者去掉后者这一段后缀。
        ///
        /// 模型里没有单独记录部署根（<c>DeploymentOperation</c> 只有这两个字段），
        /// 但二者天然满足 <c>TargetPath = 部署根 + RelativeTargetPath</c>。
        /// 反推不出来（数据不匹配、旧事务）时返回 null —— 调用方应当放弃清理：
        /// 宁可留下一个空目录，也不能在没有边界的情况下向上删。
        /// </summary>
        public static string? ResolveDeploymentRoot(string? targetPath, string? relativeTargetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath) || string.IsNullOrWhiteSpace(relativeTargetPath))
                return null;

            var normalizedTarget = TryNormalize(targetPath);
            if (normalizedTarget == null) return null;

            var suffix = NormalizeSeparators(relativeTargetPath).Trim(Path.DirectorySeparatorChar);
            if (suffix.Length == 0) return null;

            // Windows 桌面应用：路径比较一律忽略大小写
            if (!normalizedTarget.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return null;

            var root = normalizedTarget[..(normalizedTarget.Length - suffix.Length)];
            root = root.TrimEnd(Path.DirectorySeparatorChar);

            // 只剩盘符（"C:"）或空串说明反推结果不可信
            return root.Length <= 2 ? null : root;
        }

        private static string NormalizeSeparators(string path)
            => path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                   .Replace('\\', Path.DirectorySeparatorChar);

        private static string? TryNormalize(string path)
        {
            try
            {
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            }
            catch
            {
                // 非法路径：调用方按"无法确定边界"处理
                return null;
            }
        }

        private static bool IsStrictlyInside(string path, string root)
            => path.Length > root.Length
               && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
               && (path[root.Length] == Path.DirectorySeparatorChar
                   || path[root.Length] == Path.AltDirectorySeparatorChar);
    }
}
