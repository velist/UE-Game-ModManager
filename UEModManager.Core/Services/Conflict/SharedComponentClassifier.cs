using System;
using System.Collections.Generic;
using System.Linq;

namespace UEModManager.Services.Conflict;

/// <summary>
/// 一条共享组件规则：命中 <see cref="PathPrefixes"/> 或 <see cref="FileNames"/> 的
/// 文件属于该组件。
/// </summary>
public sealed record SharedComponentRule(
    string ComponentName, string[] PathPrefixes, string[] FileNames);

/// <summary>
/// 判定一个文件是否属于某个"带一份就够"的运行时组件，或者干脆只是说明文件。
///
/// 规则只作用于非 MOD 包的条目 —— 那些条目本来就是相对游戏根的文件路径。
/// pak 内部的资产不走这里：pak 里的路径是引擎资产路径，和一个 MOD 自带的
/// <c>ue4ss/</c> 目录没有可比性。
/// </summary>
public static class SharedComponentClassifier
{
    /// <summary>
    /// 命中返回组件名，否则 null。
    /// </summary>
    /// <param name="relativePath">相对游戏根的归一化路径（小写正斜杠）。</param>
    /// <param name="rules">引擎的共享组件规则。</param>
    public static string? Classify(string relativePath, IReadOnlyList<SharedComponentRule> rules)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || rules is null || rules.Count == 0) return null;

        var path = Normalize(relativePath);
        var fileName = FileNameOf(path);

        foreach (var rule in rules)
        {
            foreach (var prefix in rule.PathPrefixes)
            {
                if (path.StartsWith(Normalize(prefix), StringComparison.OrdinalIgnoreCase))
                    return rule.ComponentName;
            }

            foreach (var name in rule.FileNames)
            {
                if (string.Equals(fileName, name, StringComparison.OrdinalIgnoreCase))
                    return rule.ComponentName;
            }
        }

        return null;
    }

    /// <summary>
    /// 是否是"说明文件"：文件名匹配 readme* / 说明* / *.txt，且位于单元根或游戏根。
    ///
    /// 位置条件不可省：<c>ue4ss/Mods/readme.txt</c> 是运行时的一部分，
    /// 丢掉它会让 UE4SS 少一个文件。只有散落在根上的才是不影响运行的说明。
    ///
    /// 这个重载只认"文件就在传入的基目录根上"。单元被包了一层 MOD 目录时会
    /// 漏判（实测 0916.zip 里就是这个形状），改用
    /// <see cref="IsAttachment(string, bool)"/>。
    /// </summary>
    public static bool IsAttachment(string relativePath) => IsAttachment(relativePath, wrapped: false);

    /// <summary>
    /// 是否是"说明文件"。
    /// </summary>
    /// <param name="relativePath">相对传入基目录的路径。</param>
    /// <param name="wrapped">
    /// 这些文件是否整体位于一个包装目录之下。
    ///
    /// <para>
    /// 导入单元就是解压根下的一个顶层目录（见 <c>ArchiveImportUnitResolver</c>），
    /// 而调用方传来的是相对**解压根**的路径。于是实测 0916.zip 里的说明文件
    /// 落进来是 <c>红色经文金箍棒/steam属性口令.txt</c> —— 比"根上"低了一层，
    /// 但它在单元自己看来就在单元的根上。
    /// </para>
    ///
    /// <para>
    /// 判据是"这个单元是否只有一个顶层目录"：只有一个时，那一层是包装，
    /// 说明文件就在单元根上；有多个（<c>ue4ss/</c> 与 <c>b1/</c> 并列）时，
    /// 那些目录是真实的安装位置，不能当作包装 —— 否则 <c>ue4ss/Mods/mods.txt</c>
    /// 这类运行时文件会被当说明丢掉，MOD 直接不工作。
    /// </para>
    /// </param>
    public static bool IsAttachment(string relativePath, bool wrapped)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;

        var path = Normalize(relativePath);
        var fileName = FileNameOf(path);

        // 只认到"单元根"这么深：不包装时不允许多出任何一层目录。
        var allowedDepth = wrapped ? 1 : 0;
        if (path.Count(c => c == '/') > allowedDepth) return false;

        if (fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return true;
        if (fileName.StartsWith("readme", StringComparison.OrdinalIgnoreCase)) return true;
        if (fileName.StartsWith("说明", StringComparison.Ordinal)) return true;

        return false;
    }

    private static string Normalize(string path)
    {
        var normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        while (normalized.Contains("//", StringComparison.Ordinal))
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        return normalized.TrimStart('/');
    }

    private static string FileNameOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }
}
