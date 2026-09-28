using System.Collections.Generic;
using UEModManager.Services.Conflict.Model;

namespace UEModManager.Services.Conflict.Indexing;

/// <summary>
/// 一个容器的索引读取结果。
/// </summary>
/// <param name="Status">可读程度。</param>
/// <param name="Detail">状态补充说明，失败时是异常消息。</param>
/// <param name="Entries">读出的条目；读不出时为联系。</param>
/// <param name="FormatVersion">容器格式版本，未知时为 -1。</param>
public sealed record ContainerIndexResult(
    IndexStatus Status, string? Detail, IReadOnlyList<AssetEntry> Entries, int FormatVersion);

/// <summary>
/// 容器索引读取器：只读索引，不读数据区。
///
/// 实现必须捕获全部解析异常并降级成 <see cref="IndexStatus.Failed"/> ——
/// 一个 MOD 的容器格式异常不该让整个冲突分析失败。
/// </summary>
public interface IContainerIndexReader
{
    /// <summary>
    /// 读取索引。<paramref name="stream"/> 必须可 Seek；调用方负责打开与释放文件。
    /// </summary>
    /// <param name="stream">容器文件流。</param>
    /// <param name="sourceFile">来源文件标识，写进每个条目便于溯源。</param>
    ContainerIndexResult ReadIndex(System.IO.Stream stream, string sourceFile);
}
