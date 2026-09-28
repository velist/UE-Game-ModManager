using System;

namespace UEModManager.Services.Conflict.Indexing;

/// <summary>
/// 资产键归一化。
///
/// 同一个资产的 <c>.uasset</c> / <c>.uexp</c> / <c>.ubulk</c> 永远一起被替换，
/// 按扩展名分开数会把"1 张贴图"报成"3 个资源"。因此键里去扩展名。
/// 路径本身还要统一成小写正斜杠、去掉 <c>../</c> 前缀与重复斜杠 ——
/// 不统一的话同一个文件在 pak 里和磁盘上会算出两个键，冲突检测直接漏报。
/// </summary>
internal static class AssetKeyNormalizer
{
    /// <summary>归一化完整路径：小写、正斜杠、去 <c>../</c>、合并重复斜杠、去掉开头斜杠。</summary>
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        var normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("../", StringComparison.Ordinal))
            normalized = normalized[3..];

        while (normalized.Contains("//", StringComparison.Ordinal))
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);

        return normalized.TrimStart('/').ToLowerInvariant();
    }

    /// <summary>归一化资产键：在 <see cref="NormalizePath"/> 基础上去掉扩展名。</summary>
    public static string NormalizeAssetKey(string path)
    {
        var normalized = NormalizePath(path);
        if (normalized.Length == 0) return string.Empty;

        var slash = normalized.LastIndexOf('/');
        var dot = normalized.LastIndexOf('.');
        // 点在最后一个斜杠之后才算扩展名；否则是目录名里的点（如 "mod.v2/foo"）。
        if (dot > slash && dot > 0) normalized = normalized[..dot];
        return normalized;
    }

    /// <summary>
    /// 归一化容器内的完整路径（含扩展名），用于展示。
    /// 目录名以 <c>/</c> 结尾、根目录本身是 <c>/</c>，直接相加会出现双斜杠 ——
    /// 交给归一化合并。
    /// </summary>
    public static string DisplayPath(string mountPoint, string directoryName, string fileName)
        => NormalizePath(mountPoint + directoryName + "/" + fileName);
}