using System;
using System.Collections.Generic;
using System.IO;
using UEModManager.Services.Security;

namespace UEModManager.Services.Conflict;

/// <summary>
/// 方案覆盖规则的持久化路径：@mod/ 与 @game/ 相对于本机 MOD / 游戏根。
/// 兼容旧的绝对键；lock 导出与新编辑不再把安装盘符锁进方案。
/// </summary>
public static class ConflictOverridePaths
{
    public const string ModPrefix = "@mod/";
    public const string GamePrefix = "@game/";

    public static Dictionary<string, string> Resolve(
        IReadOnlyDictionary<string, string> overrides, string modPath, string gamePath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, winner) in overrides)
            result[ResolveKey(key, modPath, gamePath)] = winner;
        return result;
    }

    public static Dictionary<string, string> MakePortable(
        IReadOnlyDictionary<string, string> overrides, string modPath, string gamePath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, winner) in overrides)
            result[ToPortableKey(key, modPath, gamePath)] = winner;
        return result;
    }

    public static string ToPortableKey(string key, string modPath, string gamePath)
    {
        var normalized = key.Replace('\\', '/');
        if (normalized.StartsWith(ModPrefix, StringComparison.OrdinalIgnoreCase))
            return ModPrefix + SafeRelative(normalized[ModPrefix.Length..]);
        if (normalized.StartsWith(GamePrefix, StringComparison.OrdinalIgnoreCase))
            return GamePrefix + SafeRelative(normalized[GamePrefix.Length..]);
        if (!Path.IsPathRooted(key)) return GamePrefix + SafeRelative(key);
        if (TryRelative(modPath, key, out var modRelative)) return ModPrefix + modRelative;
        if (TryRelative(gamePath, key, out var gameRelative)) return GamePrefix + gameRelative;
        // 旧版本其他安装目录的键，等 lock 导入时结合包的目标文件信息确定映射。
        return Path.GetFullPath(key);
    }

    public static string ResolveKey(string key, string modPath, string gamePath)
    {
        var normalized = key.Replace('\\', '/');
        if (normalized.StartsWith(ModPrefix, StringComparison.OrdinalIgnoreCase))
            return PathSanitizer.SafeCombine(modPath, SafeRelative(normalized[ModPrefix.Length..]));
        if (normalized.StartsWith(GamePrefix, StringComparison.OrdinalIgnoreCase))
            return PathSanitizer.SafeCombine(gamePath, SafeRelative(normalized[GamePrefix.Length..]));
        return Path.IsPathRooted(key)
            ? Path.GetFullPath(key)
            : PathSanitizer.SafeCombine(gamePath, SafeRelative(key));
    }

    public static string SafeRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
            throw new ArgumentException("覆盖规则必须指定根目录内的文件路径", nameof(path));
        return PathSanitizer.SanitizeRelative(path).Replace('\\', '/');
    }

    private static bool TryRelative(string root, string path, out string relative)
    {
        relative = "";
        if (string.IsNullOrWhiteSpace(root)) return false;
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) return false;
        relative = Path.GetRelativePath(fullRoot, fullPath).Replace('\\', '/');
        return true;
    }
}
