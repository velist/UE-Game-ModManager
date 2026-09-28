using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Infrastructure;
using UEModManager.Models;
using UEModManager.Services.Conflict.Indexing;
using UEModManager.Services.Conflict.Model;
using UEModManager.Services.Persistence;

namespace UEModManager.Services.Conflict;

/// <summary>
/// 建包索引：把每个包的容器打开、读出资产列表、在需要时补算内容指纹。
///
/// 这一层负责全部 IO（打开文件、读数据区、读 PE 版本、落盘缓存），
/// 由此 Core 的 <c>PakIndexReader</c> / <c>AssetConflictDetector</c> 保持纯函数。
/// </summary>
public sealed class AssetIndexService
{
    private readonly ObjectStore _objectStore;
    private readonly ILogger<AssetIndexService> _logger;
    private readonly string _dataDirectory;

    /// <summary>
    /// 条目 → 指纹。键是"来源容器 + 数据偏移 + 存储大小"。
    ///
    /// 不用资产键做键：同一个资产可能同时出现在 pak 与 utoc 两条记录里，
    /// 按资产键存会让其中一条覆盖另一条。读取器给每个条目都带了 DataOffset 与 SourceFile。
    /// </summary>
    private readonly Dictionary<string, (string Fingerprint, FingerprintKind Kind)> _fingerprints
        = new(StringComparer.Ordinal);

    /// <summary>容器文件哈希 → 索引结果。同一个 pak 被多个包引用时只解析一次。</summary>
    private readonly Dictionary<string, ContainerIndexResult> _indexCache = new(StringComparer.OrdinalIgnoreCase);

    private string _currentGame = string.Empty;
    private bool _cacheLoaded;

    public AssetIndexService(ILogger<AssetIndexService> logger, ObjectStore objectStore)
        : this(logger, objectStore, AppPaths.DataDirectory)
    {
    }

    /// <summary>
    /// 指定数据目录的构造函数（测试用）。DI 走上面的两参构造函数 ——
    /// 容器无法解析 string，不会误选此重载。
    /// </summary>
    public AssetIndexService(ILogger<AssetIndexService> logger, ObjectStore objectStore, string dataDirectory)
    {
        _logger = logger;
        _objectStore = objectStore;
        _dataDirectory = dataDirectory;
    }

    /// <summary>
    /// 切换当前游戏。缓存按游戏分片：不同游戏的资产路径空间完全无关，
    /// 混在一起既浪费内存，也可能让同名的 pak 命中另一套索引。
    /// </summary>
    public async Task SetCurrentGameAsync(string gameName)
    {
        _currentGame = gameName ?? string.Empty;
        _fingerprints.Clear();
        _indexCache.Clear();
        _cacheLoaded = false;
        await LoadContainerCacheAsync();
    }

    /// <summary>
    /// 建一个包的资产索引。
    /// </summary>
    public async Task<ModAssetIndex> GetIndexAsync(Package package, ConflictStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(package);

        // 非 UE 引擎、以及所有非 MOD 包：没有容器可读，退化到文件路径。
        if (strategy != ConflictStrategy.ContainerAssets || package.Kind != PackageKind.Mod)
            return FilePathAssetIndexBuilder.Build(package);

        var entries = new List<AssetEntry>();
        var status = IndexStatus.Complete;
        string? detail = null;

        foreach (var artifact in package.Artifacts)
        {
            if (artifact.ArtifactType != ArtifactType.ModFile) continue;

            var extension = Path.GetExtension(artifact.RelativeTargetPath);
            // .ucas 是数据体、不是索引载体：它和 .utoc 一起出现，索引只在 .utoc 里。
            if (!extension.Equals(".pak", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".utoc", StringComparison.OrdinalIgnoreCase))
                continue;

            var path = ResolveSourcePath(artifact.RelativeSourcePath);
            if (path is null || !File.Exists(path))
            {
                _logger.LogWarning("容器文件不存在，跳过索引: {Path}", artifact.RelativeSourcePath);
                continue;
            }

            var result = await ReadContainerAsync(path, artifact.RelativeSourcePath);
            entries.AddRange(result.Entries);

            // 多个容器时取最差的那个：一个读不出来的容器不该被另一个的成功掩盖。
            if (result.Status != IndexStatus.Complete && status == IndexStatus.Complete)
            {
                status = result.Status;
                detail = $"{artifact.FileName}: {result.Detail}";
            }
        }

        // 同一个资产键只留一条：pak 与 utoc 并存时两边会报同一批资产。
        var deduped = entries
            .GroupBy(e => e.AssetKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        return new ModAssetIndex(
            PackageKey: package.PackageKey,
            DisplayName: package.DisplayName,
            Kind: package.Kind,
            ContentHash: package.ContentHash,
            Entries: deduped,
            Status: status,
            StatusDetail: detail);
    }

    /// <summary>
    /// 给重叠条目补算内容指纹，结果存进内部表，由
    /// <see cref="ApplyFingerprints"/> 应用到索引上。
    ///
    /// 先去比"大小 + 存储大小 + 压缩方式"：三者有任何不同就已经能断定内容不同，
    /// 无需读数据；三者全同才去读数据算哈希。绝大多数"确实不同"的比较因此不碰磁盘。
    /// </summary>
    public async Task FillFingerprintsAsync(
        IReadOnlyList<(string AssetKey, IReadOnlyList<(string PackageKey, AssetEntry Entry)> Entries)> overlaps)
    {
        ArgumentNullException.ThrowIfNull(overlaps);

        foreach (var (_, owners) in overlaps)
        {
            var shapeDiffers = owners
                .Select(o => (o.Entry.Size, o.Entry.StoredSize, o.Entry.CompressionMethod))
                .Distinct()
                .Count() > 1;

            foreach (var (_, entry) in owners)
            {
                // 非容器条目已经带 FileHash，不必再读一遍。
                if (entry.Fingerprint is not null) continue;

                if (shapeDiffers)
                {
                    _fingerprints[EntryKey(entry)] =
                        (ShapeFingerprint(entry), FingerprintKind.SizeOnly);
                    continue;
                }

                var hash = await ComputeContentFingerprintAsync(entry);
                _fingerprints[EntryKey(entry)] = hash is null
                    ? (ShapeFingerprint(entry), FingerprintKind.SizeOnly)
                    : (hash, FingerprintKind.ContentHash);
            }
        }
    }

    /// <summary>
    /// 导入后预热：后台把新包的索引建出来，用户打开冲突面板时就不必等。
    /// 任何异常都只记日志 —— 预热失败不该影响导入结果。
    /// </summary>
    public async Task WarmUpAsync(Package package)
    {
        try
        {
            await GetIndexAsync(package, ConflictStrategy.ContainerAssets);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "预热资产索引失败: {Key}", package?.PackageKey);
        }
    }

    /// <summary>把刚补算的指纹应用到索引上，供 <c>AssetConflictDetector.Build</c> 使用。</summary>
    public IReadOnlyList<ModAssetIndex> ApplyFingerprints(IReadOnlyList<ModAssetIndex> indexes)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        if (_fingerprints.Count == 0) return indexes;

        return indexes.Select(index => index with
        {
            Entries = index.Entries.Select(entry =>
            {
                if (entry.Fingerprint is not null) return entry;
                if (!_fingerprints.TryGetValue(EntryKey(entry), out var stored)) return entry;
                return entry with { Fingerprint = stored.Fingerprint, FingerprintKind = stored.Kind };
            }).ToList()
        }).ToList();
    }

    /// <summary>
    /// 共享组件裁决所需的版本与时间：.dll/.exe 取 PE 版本资源，其余只有时间。
    /// </summary>
    public IReadOnlyDictionary<string, SharedComponentProvenance> CollectProvenance(
        IReadOnlyList<Package> packages)
    {
        ArgumentNullException.ThrowIfNull(packages);

        var result = new Dictionary<string, SharedComponentProvenance>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages)
        {
            string? version = null;
            DateTime? modified = null;

            foreach (var artifact in package.Artifacts)
            {
                var path = ResolveSourcePath(artifact.RelativeSourcePath);
                if (path is null || !File.Exists(path)) continue;

                if (version is null)
                {
                    var extension = Path.GetExtension(path);
                    if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                        || extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        var info = FileVersionInfo.GetVersionInfo(path);
                        if (!string.IsNullOrWhiteSpace(info.FileVersion)) version = info.FileVersion;
                    }
                }

                var stamp = File.GetLastWriteTimeUtc(path);
                if (modified is null || stamp > modified) modified = stamp;
            }

            result[package.PackageKey] = new SharedComponentProvenance(version, modified);
        }

        return result;
    }

    // ─── 容器读取与缓存 ───

    private async Task<ContainerIndexResult> ReadContainerAsync(string path, string sourceFile)
    {
        var fileHash = await ObjectStore.ComputeFileHashAsync(path);
        if (_indexCache.TryGetValue(fileHash, out var cached)) return cached;

        var result = await Task.Run(() =>
        {
            using var stream = File.OpenRead(path);
            IContainerIndexReader reader = ContainerFormat.Detect(stream) switch
            {
                ContainerKind.Pak => new PakIndexReader(),
                ContainerKind.IoStoreToc => new IoStoreTocReader(),
                _ => new UnsupportedContainerReader()
            };
            return reader.ReadIndex(stream, sourceFile);
        });

        if (result.Status != IndexStatus.Complete)
            _logger.LogInformation("容器索引未完整读出: {File} ({Status}) {Detail}",
                sourceFile, result.Status, result.Detail);

        _indexCache[fileHash] = result;
        await SaveContainerCacheAsync();
        return result;
    }

    /// <summary>
    /// 未识别格式的占位读取器：明确返回 Unsupported，而不是空条目 ——
    /// 空条目会被上游当成"这个包和谁都不冲突"。
    /// </summary>
    private sealed class UnsupportedContainerReader : IContainerIndexReader
    {
        public ContainerIndexResult ReadIndex(Stream stream, string sourceFile)
            => new(IndexStatus.Unsupported, "不是可识别的容器格式", [], -1);
    }

    private async Task<string?> ComputeContentFingerprintAsync(AssetEntry entry)
    {
        var path = ResolveSourcePath(entry.SourceFile);
        if (path is null || !File.Exists(path)) return null;

        return await Task.Run(() =>
        {
            try
            {
                using var stream = File.OpenRead(path);

                // 条目数据前面有一份 FPakEntry 头副本，必须先跳过去。
                var headerSize = PakIndexReader.PackedEntryHeaderSize(
                    new PakIndexReader.PackedPakEntry(
                        entry.DataOffset, entry.Size, entry.StoredSize,
                        entry.CompressionMethod, false, BlockCountGuess(entry)));
                var start = entry.DataOffset + headerSize;

                if (entry.DataOffset < 0 || entry.StoredSize <= 0
                    || start < 0 || start + entry.StoredSize > stream.Length)
                    return null;

                stream.Position = start;
                var buffer = new byte[entry.StoredSize];
                var read = 0;
                while (read < buffer.Length)
                {
                    var n = stream.Read(buffer, read, buffer.Length - read);
                    if (n <= 0) break;
                    read += n;
                }

                return Convert.ToHexString(SHA1.HashData(buffer.AsSpan(0, read)));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "读取条目内容失败: {Path}", path);
                return null;
            }
        });
    }

    /// <summary>
    /// 压缩块数的估计值。
    ///
    /// 编码条目里带着真实块数，但读索引的路径上没把它传出来；这里用
    /// <c>存储大小 / 压缩块大小</c> 反推。反推不准时下面的越界校验会兜住，
    /// 退化成 SizeOnly 指纹 —— 保守方向，不会把不同的内容判成相同。
    /// </summary>
    private static int BlockCountGuess(AssetEntry entry)
    {
        if (entry.CompressionMethod == 0) return 0;
        const int blockSize = 64 * 1024;
        return Math.Max(1, (int)((entry.StoredSize + blockSize - 1) / blockSize));
    }

    private static string ShapeFingerprint(AssetEntry entry)
        => $"size:{entry.Size}:{entry.StoredSize}:{entry.CompressionMethod}";

    private static string EntryKey(AssetEntry entry)
        => $"{entry.SourceFile}\n{entry.DataOffset}\n{entry.StoredSize}";

    // ─── 容器缓存落盘 ───

    private string CachePath => Path.Combine(_dataDirectory, $"{Sanitize(_currentGame)}_assetindex.json");

    private async Task LoadContainerCacheAsync()
    {
        if (_cacheLoaded) return;
        _cacheLoaded = true;

        try
        {
            if (!File.Exists(CachePath)) return;
            var json = await File.ReadAllTextAsync(CachePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, ContainerIndexResult>>(json);
            if (loaded is null) return;
            foreach (var (key, value) in loaded) _indexCache[key] = value;
            _logger.LogInformation("资产索引缓存已加载: {Count} 个容器", _indexCache.Count);
        }
        catch (Exception ex)
        {
            // 缓存坏了只影响速度，不该影响功能。
            _logger.LogWarning(ex, "资产索引缓存读取失败，将重新解析");
            _indexCache.Clear();
        }
    }

    private async Task SaveContainerCacheAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(_indexCache);
            await AtomicFileWriter.WriteAllTextAsync(CachePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "资产索引缓存写入失败");
        }
    }

    /// <summary>把游戏名变成安全的文件名片段。</summary>
    private static string Sanitize(string name)
    {
        if (string.IsNullOrEmpty(name)) return "unknown";
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return cleaned.Length == 0 ? "unknown" : cleaned;
    }

    /// <summary>
    /// 把仓库内的相对源路径解析成绝对路径。
    ///
    /// <c>RelativeSourcePath</c> 是相对**仓库根**的（实测形如
    /// <c>{包名}\files\{文件}</c>），不是相对包目录。
    /// </summary>
    private string? ResolveSourcePath(string? relativeSourcePath)
    {
        if (string.IsNullOrWhiteSpace(relativeSourcePath)) return null;
        try
        {
            return Path.Combine(_objectStore.RepositoryRoot,
                relativeSourcePath.Replace('/', Path.DirectorySeparatorChar));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "源路径解析失败: {Path}", relativeSourcePath);
            return null;
        }
    }
}