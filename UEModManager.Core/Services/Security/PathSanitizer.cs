using System;
using System.IO;
using System.Linq;

namespace UEModManager.Services.Security;

public static class PathSanitizer
{
    /// <summary>校验相对子路径：拒绝绝对路径/盘符/UNC/.. 上跳。非法时抛 ArgumentException。</summary>
    public static string SanitizeRelative(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return string.Empty;
        }

        var raw = relativePath.Trim();

        if (raw.StartsWith(@"\\", StringComparison.Ordinal) || raw.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException($"目标路径不允许为 UNC 路径: {relativePath}", nameof(relativePath));
        }

        if (raw.Contains(':') || Path.IsPathFullyQualified(raw))
        {
            throw new ArgumentException($"目标路径不允许为绝对路径: {relativePath}", nameof(relativePath));
        }

        var path = raw.TrimStart('/', '\\');
        var parts = path.Split('/', '\\');
        if (parts.Any(part => part == ".."))
        {
            throw new ArgumentException($"目标路径不允许包含 ..: {relativePath}", nameof(relativePath));
        }

        return string.Join(
            Path.DirectorySeparatorChar.ToString(),
            parts.Where(part => part.Length > 0 && part != "."));
    }

    /// <summary>组合并二次校验结果确实落在 baseDir 内（防御纵深）。</summary>
    public static string SafeCombine(string baseDir, string? relativePath)
    {
        var combined = Path.GetFullPath(Path.Combine(baseDir, SanitizeRelative(relativePath)));
        var baseFull = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var combinedDir = combined.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var baseDirWithoutSlash = baseFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!combined.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(combinedDir, baseDirWithoutSlash, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"路径越出基目录: {relativePath}", nameof(relativePath));
        }

        return combined;
    }
}
