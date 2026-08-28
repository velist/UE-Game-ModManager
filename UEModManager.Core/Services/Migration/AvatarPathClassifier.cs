using System;
using System.IO;

namespace UEModManager.Services.Migration
{
    /// <summary>头像路径的归属分类。</summary>
    public enum AvatarLocation
    {
        /// <summary>没有设置头像。</summary>
        Empty,

        /// <summary>已经在当前（新）头像目录下，无需迁移。</summary>
        AlreadyCurrent,

        /// <summary>在旧头像目录（安装目录下）里，需要迁移。</summary>
        Legacy,

        /// <summary>既不在新目录也不在旧目录：用户自选的任意位置、云端 URL、UNC、相对路径、
        /// 或格式已经损坏的字符串。<b>一律不动</b>。</summary>
        Foreign,
    }

    /// <summary>
    /// 判定 <c>Users.Avatar</c> 里那个路径属于哪一处（纯函数，不碰文件系统）。
    ///
    /// <para>
    /// 只回答"归属"这一个问题。目标文件是否已存在、内容是否相同、冲突要不要改名，
    /// 都依赖文件系统，留在主项目的 IO 层——把它们塞进本类会需要一堆调用方根本
    /// 拿不到的入参，是没有意义的抽象。
    /// </para>
    ///
    /// <para>
    /// <b>为什么不能直接 <c>StartsWith</c>：</b>
    /// 目录字符串可能带或不带尾分隔符，路径可能是 <c>..</c> 形式未规范化，
    /// 而且 <c>C:\X\Avatars</c> 会把 <c>C:\X\Avatars2\a.png</c> 误判成自己的子路径。
    /// 所以先 <see cref="Path.GetFullPath(string)"/> 规范化、再给目录补上分隔符、
    /// 最后按 <see cref="StringComparison.OrdinalIgnoreCase"/> 比较（Windows 路径不区分大小写）。
    /// </para>
    /// </summary>
    public static class AvatarPathClassifier
    {
        /// <summary>
        /// 判定 <paramref name="path"/> 的归属。
        /// </summary>
        /// <param name="path">数据库 <c>Users.Avatar</c> 列的原始值，可为 null。</param>
        /// <param name="legacyRoot">旧头像目录（安装目录下）。</param>
        /// <param name="currentRoot">当前头像目录（漫游数据下）。</param>
        public static AvatarLocation Classify(string? path, string legacyRoot, string currentRoot)
        {
            if (string.IsNullOrWhiteSpace(path)) return AvatarLocation.Empty;

            var full = TryNormalize(path);
            if (full is null) return AvatarLocation.Foreign;   // 含非法字符 / URL / 无法解析

            // 先判新目录：新旧两处若被配置成同一个位置，"已完成"的结论比"要迁移"更安全
            if (IsUnder(full, currentRoot)) return AvatarLocation.AlreadyCurrent;
            if (IsUnder(full, legacyRoot)) return AvatarLocation.Legacy;

            return AvatarLocation.Foreign;
        }

        /// <summary>规范化为绝对路径；失败返回 null 而不是抛出——调用方拿到的是数据库里的
        /// 历史遗留值，什么形态都可能有，不该由它承担异常。</summary>
        private static string? TryNormalize(string path)
        {
            try
            {
                return Path.GetFullPath(path.Trim());
            }
            catch
            {
                return null;
            }
        }

        /// <summary>判断 <paramref name="fullPath"/> 是否位于 <paramref name="root"/> 之下。</summary>
        private static bool IsUnder(string fullPath, string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return false;

            var normalizedRoot = TryNormalize(root);
            if (normalizedRoot is null) return false;

            // 补尾分隔符，避免 "…\Avatars" 命中 "…\Avatars2\a.png"
            if (!normalizedRoot.EndsWith(Path.DirectorySeparatorChar))
                normalizedRoot += Path.DirectorySeparatorChar;

            return fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }
    }
}
