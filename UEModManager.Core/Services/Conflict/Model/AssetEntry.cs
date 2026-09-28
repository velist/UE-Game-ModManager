using System.Collections.Generic;

namespace UEModManager.Services.Conflict.Model;

/// <summary>
/// 一个容器（.pak / .utoc）索引的可读程度。
///
/// 只有 <see cref="Complete"/> 会参与冲突比对；<see cref="Partial"/> 是"能读到多少算多少"
/// （例如 pak 没有目录索引），仍然参与；其余三种都进"无法分析"，不参与比对 ——
/// 把一个读不出内容的容器当成"和谁都不冲突"，等于静默漏报。
/// </summary>
public enum IndexStatus
{
    /// <summary>索引完整读出。</summary>
    Complete,

    /// <summary>索引读出但只有一部分（没有目录索引，或只按包 ID 比对）。</summary>
    Partial,

    /// <summary>索引被加密，读不出条目。</summary>
    Encrypted,

    /// <summary>容器版本本模块不支持。</summary>
    Unsupported,

    /// <summary>解析失败（截断、结构异常）。</summary>
    Failed
}

/// <summary>
/// 内容指纹的可信程度。
/// </summary>
public enum FingerprintKind
{
    /// <summary>还没有指纹。</summary>
    None,

    /// <summary>读出了条目数据并算出内容哈希 —— 两个条目指纹相同即内容相同。</summary>
    ContentHash,

    /// <summary>
    /// 只用了大小与压缩方式。够判断"肯定不同"，判断不了"肯定相同"：
    /// 两个未压缩、大小相同的条目会被当成同一个，调用方必须按保守方向使用
    /// （宁可报冲突，不可漏报）。
    /// </summary>
    SizeOnly
}

/// <summary>
/// 容器里的一个资产条目。
/// </summary>
/// <param name="AssetKey">
/// 归一化资产键：小写、正斜杠、去 <c>../</c> 前缀、去扩展名。
/// 同一个资产的 .uasset / .uexp / .ubulk 归一化后共用一个键 —— 它们永远一起被替换，
/// 按扩展名分开数会把"1 张贴图"报成"3 个资源"。
/// </param>
/// <param name="DisplayPath">原始路径（含扩展名），仅用于展示。</param>
/// <param name="Fingerprint">内容指纹；null 表示尚未计算。</param>
/// <param name="Size">未压缩大小（pak 的 UncompressedSize；utoc 为 -1）。</param>
/// <param name="DataOffset">pak 条目数据偏移；utoc 为 -1。</param>
/// <param name="StoredSize">pak 条目在文件中的存储大小；utoc 为 -1。</param>
/// <param name="CompressionMethod">pak 压缩方式索引（0 = 未压缩）；utoc 为 -1。</param>
/// <param name="SourceFile">来自哪个容器文件（相对仓库的源路径），用于让用户知道去哪找。</param>
public sealed record AssetEntry(
    string AssetKey,
    string DisplayPath,
    string? Fingerprint,
    FingerprintKind FingerprintKind,
    long Size,
    long DataOffset,
    long StoredSize,
    int CompressionMethod,
    string SourceFile);

/// <summary>
/// 一个包的全部资产索引。
/// </summary>
/// <param name="PackageKey">包标识。</param>
/// <param name="DisplayName">展示名。</param>
/// <param name="Kind">包类型。非 MOD 的包条目是相对游戏根的文件路径，不走容器解析。</param>
/// <param name="ContentHash">
/// <c>Package.ContentHash</c>，整包重复判定用。两个包内容完全相同 → 是"重复"而不是"冲突"。
/// </param>
/// <param name="Entries">资产条目。</param>
/// <param name="Status">可读程度（多个容器时取最差的那个）。</param>
/// <param name="StatusDetail">状态补充说明（例如哪个容器的索引已加密）。</param>
public sealed record ModAssetIndex(
    string PackageKey,
    string DisplayName,
    Models.PackageKind Kind,
    string? ContentHash,
    IReadOnlyList<AssetEntry> Entries,
    IndexStatus Status,
    string? StatusDetail);
