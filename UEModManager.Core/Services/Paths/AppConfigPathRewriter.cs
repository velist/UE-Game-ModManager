using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UEModManager.Services.Paths;

/// <summary>
/// 一条路径重定位规则：<see cref="LegacyRoot"/> 下的数据已整体搬到 <see cref="TargetRoot"/>，
/// 于是指向旧根之下的绝对路径都要平移到新根下的同一相对位置。
///
/// <para>
/// 之所以要有这一层：<c>config.json</c> 里存的是绝对路径，而
/// <c>DataRelocationExecutor</c> 只搬目录不改配置。不平移的话，配置继续指向
/// 一个已经被清空、只剩墓碑的旧目录——备份写进去等于写进垃圾堆，
/// 游戏图标则直接加载失败（界面上就是图标空掉）。
/// </para>
/// </summary>
public sealed class PathRebaseRule
{
    /// <param name="legacyRoot">搬迁前的根目录。</param>
    /// <param name="targetRoot">搬迁后的根目录。</param>
    public PathRebaseRule(string legacyRoot, string targetRoot)
    {
        if (string.IsNullOrWhiteSpace(legacyRoot))
            throw new ArgumentException("旧根不能为空", nameof(legacyRoot));
        if (string.IsNullOrWhiteSpace(targetRoot))
            throw new ArgumentException("新根不能为空", nameof(targetRoot));

        LegacyRoot = legacyRoot;
        TargetRoot = targetRoot;
    }

    /// <summary>搬迁前的根目录。</summary>
    public string LegacyRoot { get; }

    /// <summary>搬迁后的根目录。</summary>
    public string TargetRoot { get; }

    /// <summary>
    /// 若 <paramref name="path"/> 位于旧根之下（或就是旧根本身），返回它在新根下的对应位置；
    /// 不需要改写时返回 <c>null</c>——调用方据此原样保留旧值。
    ///
    /// <para>
    /// 返回 <c>null</c> 的几种情形都是"不能动"而非"出错"：路径为空、不在旧根之下、
    /// 已经在新根之下（重复启动时的幂等保证）、新旧根是同一个位置、
    /// 路径本身没法解析（宁可留着一个读不懂的值，也不要拿它拼出一个更离谱的新值）。
    /// </para>
    /// </summary>
    public string? TryRebase(string? path)
    {
        var normalizedPath = Normalize(path);
        var normalizedLegacy = Normalize(LegacyRoot);
        var normalizedTarget = Normalize(TargetRoot);

        if (normalizedPath is null || normalizedLegacy is null || normalizedTarget is null) return null;

        // 新旧根其实是同一个位置：搬迁没有真正发生，改写只会平白重写一遍配置
        if (PathsEqual(normalizedLegacy, normalizedTarget)) return null;

        // 已经在新根之下 —— 上一次已经改写过。这条在新根嵌套于旧根之下时尤其要紧
        // （否则会一层层往下套出 Backups\Mods\Mods\…），先判它再判旧根。
        if (IsUnderOrEqual(normalizedPath, normalizedTarget)) return null;

        if (!IsUnderOrEqual(normalizedPath, normalizedLegacy)) return null;

        var relative = normalizedPath.Substring(normalizedLegacy.Length).TrimStart(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return relative.Length == 0 ? normalizedTarget : Path.Combine(normalizedTarget, relative);
    }

    /// <summary>
    /// 归一化：去空白、解析 <c>..</c> 与正反斜杠、去掉尾部分隔符。
    /// 解析不了（空串、含非法字符）时返回 <c>null</c>。
    /// </summary>
    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        try
        {
            // GetFullPath 只做字符串运算，不碰磁盘；相对路径按当前工作目录展开，
            // 与应用真正使用这个值时的语义一致。
            var full = Path.GetFullPath(value.Trim());
            var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return trimmed.Length == 0 ? full : trimmed;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// <paramref name="path"/> 是否就是 <paramref name="root"/> 或位于其下。
    ///
    /// <para>
    /// 必须落在分隔符边界上：只比前缀的话，<c>C:\App\Backups2</c> 会被当成
    /// <c>C:\App\Backups</c> 的子目录，用户放在隔壁目录的备份会被改写到一个不存在的位置。
    /// </para>
    /// </summary>
    private static bool IsUnderOrEqual(string path, string root)
    {
        if (PathsEqual(path, root)) return true;
        if (path.Length <= root.Length) return false;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;

        // root 已去尾部分隔符，但盘符根（"C:\" → "C:"）除外的其余情况下界都在这一位
        var boundary = path[root.Length];
        return boundary == Path.DirectorySeparatorChar || boundary == Path.AltDirectorySeparatorChar;
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 配置改写结果。<see cref="Changed"/> 为 false 时 <see cref="Json"/> 就是传入的原文，
/// 调用方据此决定"完全不写盘"——不改写就不落盘，重复启动才不会反复扰动 config.json。
/// </summary>
/// <param name="Changed">是否有值被改写。</param>
/// <param name="Json">改写后的 JSON 文本（无改动时为原文）。</param>
/// <param name="ChangedEntries">被改写的条目描述，仅供日志取证。</param>
public sealed record AppConfigRewriteResult(
    bool Changed,
    string Json,
    IReadOnlyList<string> ChangedEntries);

/// <summary>
/// <c>config.json</c> 里绝对路径的改写器。纯函数：文本进，文本出，不碰磁盘。
///
/// <para>
/// <b>只改这两个字段</b>，因为只有它们指向被搬迁的数据：
/// <list type="bullet">
/// <item><c>BackupPath</c> —— 形如 <c>{安装目录}\Backups\{游戏名}_备份</c>，
/// 随"MOD 备份"项搬到 <c>{LOCALAPPDATA}\…\Backups\Mods</c> 之下。</item>
/// <item><c>GameIcons</c> 的每个值 —— 形如 <c>{安装目录}\Data\GameIcons\xxx.png</c>，
/// 随"数据索引"项搬到 <c>{LOCALAPPDATA}\…\Data</c> 之下。</item>
/// </list>
/// <c>GamePath</c> / <c>ModPath</c> / <c>PluginPaths</c> 指向游戏安装位置，
/// 与本次搬迁无关，动了反而会把用户的游戏配置指到不存在的地方。
/// </para>
///
/// <para>
/// 用 <see cref="JsonNode"/> 做定点改写而非"反序列化成 AppConfig 再序列化回去"：
/// 后者会把当前模型不认识的字段（旧版本残留、将来新增的字段）一并丢掉。
/// </para>
/// </summary>
public static class AppConfigPathRewriter
{
    /// <summary>MOD 备份目录字段名。</summary>
    public const string BackupPathProperty = "BackupPath";

    /// <summary>游戏图标映射字段名（游戏名 → 图标文件绝对路径）。</summary>
    public const string GameIconsProperty = "GameIcons";

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    /// <summary>
    /// 改写配置文本。
    /// </summary>
    /// <param name="json"><c>config.json</c> 原文。解析不了时抛 <see cref="JsonException"/>，
    /// 由调用方决定如何取证——这里不能自作主张返回"无改动"，那会把损坏的配置伪装成正常。</param>
    /// <param name="modBackupsRule">
    /// MOD 备份的搬迁规则；<c>null</c> 表示该项<b>没有</b>搬迁成功，
    /// 此时绝不能改写 <c>BackupPath</c>——数据还在旧位置，改了配置就指向一个空目录。
    /// </param>
    /// <param name="dataDirectoryRule">数据索引的搬迁规则，语义同上，管辖 <c>GameIcons</c>。</param>
    public static AppConfigRewriteResult Rewrite(
        string json,
        PathRebaseRule? modBackupsRule,
        PathRebaseRule? dataDirectoryRule)
    {
        if (json is null) throw new ArgumentNullException(nameof(json));

        var unchanged = new AppConfigRewriteResult(false, json, Array.Empty<string>());
        if (modBackupsRule is null && dataDirectoryRule is null) return unchanged;

        // 根不是对象（null / 数组）时无从下手，按无改动处理：这类文件 GameConfigService
        // 自己也会当成损坏配置去备份重建，迁移器不必抢在前面动它。
        if (JsonNode.Parse(json) is not JsonObject root) return unchanged;

        var changes = new List<string>();

        if (modBackupsRule != null)
        {
            RewriteScalar(root, BackupPathProperty, modBackupsRule, changes);
        }

        if (dataDirectoryRule != null)
        {
            RewriteMapValues(root, GameIconsProperty, dataDirectoryRule, changes);
        }

        return changes.Count == 0
            ? unchanged
            : new AppConfigRewriteResult(true, root.ToJsonString(SerializerOptions), changes);
    }

    private static void RewriteScalar(
        JsonObject root, string property, PathRebaseRule rule, List<string> changes)
    {
        if (!TryFindProperty(root, property, out var key)) return;
        if (root[key] is not JsonValue value || !value.TryGetValue<string>(out var oldPath)) return;

        var rebased = rule.TryRebase(oldPath);
        if (rebased is null) return;

        root[key] = rebased;
        changes.Add($"{key}: {oldPath} → {rebased}");
    }

    private static void RewriteMapValues(
        JsonObject root, string property, PathRebaseRule rule, List<string> changes)
    {
        if (!TryFindProperty(root, property, out var key)) return;
        if (root[key] is not JsonObject map) return;

        // 先取键快照：JsonObject 在遍历中被赋值会抛 InvalidOperationException
        foreach (var entryKey in map.Select(pair => pair.Key).ToList())
        {
            if (map[entryKey] is not JsonValue value || !value.TryGetValue<string>(out var oldPath)) continue;

            var rebased = rule.TryRebase(oldPath);
            if (rebased is null) continue;

            map[entryKey] = rebased;
            changes.Add($"{key}[{entryKey}]: {oldPath} → {rebased}");
        }
    }

    /// <summary>
    /// 按名字找属性，大小写不敏感。序列化器写出来的一定是 PascalCase，
    /// 但手工改过的配置不一定，找不到就整字段跳过比误判为"没有这个字段"要稳。
    /// </summary>
    private static bool TryFindProperty(JsonObject root, string property, out string actualKey)
    {
        foreach (var pair in root)
        {
            if (string.Equals(pair.Key, property, StringComparison.OrdinalIgnoreCase))
            {
                actualKey = pair.Key;
                return true;
            }
        }

        actualKey = string.Empty;
        return false;
    }
}
