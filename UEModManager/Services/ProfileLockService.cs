using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Models;
using UEModManager.Services.Conflict;
using UEModManager.Services.Import;
using UEModManager.Services.Lock;
using UEModManager.Services.Security;

namespace UEModManager.Services
{
    /// <summary>
    /// Profile lock 文件 IO 适配器（Phase 12）。
    ///
    /// - <see cref="ExportAsync"/>：把当前 Profile + 包列表 + 冲突覆盖序列化为 lock 文件
    /// - <see cref="PreviewImportAsync"/>：读 lock 文件 + 与本地包仓库 diff，返回可导入分析
    /// - <see cref="ApplyImportAsync"/>：根据 lock 创建新 Profile 并填入包条目（缺失包会跳过）
    ///
    /// 纯逻辑（构造 / 比较）下沉到 Core 的 <see cref="ProfileLockBuilder"/> / <see cref="ProfileLockComparator"/>。
    /// </summary>
    public class ProfileLockService
    {
        private readonly ILogger<ProfileLockService> _logger;
        private readonly ProfileService _profileService;
        private readonly PackageRepository _packageRepo;
        private readonly ConflictAnalyzer? _conflictAnalyzer;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = null,  // ProfileLock 使用 [JsonPropertyName] 显式控制字段名
        };

        public ProfileLockService(
            ILogger<ProfileLockService> logger,
            ProfileService profileService,
            PackageRepository packageRepo,
            ConflictAnalyzer? conflictAnalyzer = null)
        {
            _logger = logger;
            _profileService = profileService;
            _packageRepo = packageRepo;
            _conflictAnalyzer = conflictAnalyzer;
        }

        /// <summary>
        /// 导出当前活跃 Profile 到 lock 文件。
        /// </summary>
        public async Task ExportAsync(string outputPath)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("Output path required", nameof(outputPath));

            var profile = _profileService.CurrentProfile
                ?? throw new InvalidOperationException("当前没有活跃 Profile，无法导出");

            var packagesByKey = _packageRepo.GetAllPackages()
                .ToDictionary(p => p.PackageKey, p => p, StringComparer.OrdinalIgnoreCase);

            var overrides = _conflictAnalyzer?.GetPortableOverrides(profile) ?? profile.ConflictOverrides;

            var lockFile = ProfileLockBuilder.Build(
                profile, packagesByKey, overrides,
                appVersion: GetAppVersion());

            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(lockFile, JsonOptions);
            await File.WriteAllTextAsync(outputPath, json);

            _logger.LogInformation(
                "[Lock] Exported profile '{Name}' → {Path} ({Count} packages)",
                profile.Name, outputPath, lockFile.Packages.Count);
        }

        /// <summary>
        /// 读取 lock 文件并与本地包仓库做差异分析（不修改任何状态）。
        /// </summary>
        public async Task<(ProfileLock lockFile, ProfileLockDiff diff)> PreviewImportAsync(string lockFilePath)
        {
            if (!File.Exists(lockFilePath))
                throw new FileNotFoundException("Lock 文件不存在", lockFilePath);

            var json = await File.ReadAllTextAsync(lockFilePath);
            var lockFile = JsonSerializer.Deserialize<ProfileLock>(json, JsonOptions)
                ?? throw new InvalidOperationException("Lock 文件解析失败");

            if (lockFile.LockVersion > ProfileLockSchema.CurrentVersion)
                throw new InvalidOperationException(
                    $"Lock 文件版本 {lockFile.LockVersion} 高于当前应用支持的 {ProfileLockSchema.CurrentVersion}");

            ValidateLockFile(lockFile);

            var localPackages = _packageRepo.GetAllPackages()
                .ToDictionary(p => p.PackageKey, p => p, StringComparer.OrdinalIgnoreCase);

            var diff = ProfileLockComparator.Compare(lockFile, localPackages);

            _logger.LogInformation(
                "[Lock] Preview import {Path}: {Match} matched, {Miss} missing, {Hash} hash mismatch",
                lockFilePath, diff.MatchedCount, diff.MissingCount, diff.HashMismatchCount);

            return (lockFile, diff);
        }

        /// <summary>
        /// 根据 lock 创建新 Profile 并填入包条目。缺失的包会被跳过（用户应先导入对应包）。
        /// </summary>
        /// <returns>新建的 Profile（已切换为活跃）。</returns>
        public async Task<InstanceProfile> ApplyImportAsync(ProfileLock lockFile)
        {
            if (lockFile == null) throw new ArgumentNullException(nameof(lockFile));
            ValidateLockFile(lockFile);

            ValidateImportHost(lockFile);

            var localKeys = new HashSet<string>(
                _packageRepo.GetAllPackages().Select(p => p.PackageKey),
                StringComparer.OrdinalIgnoreCase);

            // 填入包条目（仅本地存在的）
            var entries = new List<ProfilePackageEntry>();
            foreach (var pkg in lockFile.Packages)
            {
                if (!localKeys.Contains(pkg.PackageKey)) continue;

                entries.Add(new ProfilePackageEntry
                {
                    PackageKey = pkg.PackageKey,
                    IsEnabled = pkg.IsEnabled,
                    Priority = pkg.Priority,
                    Kind = Enum.TryParse<PackageKind>(pkg.Kind, out var k) ? k : PackageKind.Mod,
                });
            }

            var overrides = RebaseImportedOverrides(lockFile.ConflictOverrides, entries);
            var newProfile = await _profileService.CreateProfileAsync(
                name: $"{lockFile.Profile.Name} (导入)",
                description: lockFile.Profile.Description,
                packages: entries,
                conflictOverrides: overrides,
                backendType: Enum.TryParse<DeploymentBackendType>(lockFile.Profile.BackendType, out var backend)
                    ? backend : DeploymentBackendType.Copy);

            await _profileService.SwitchProfileAsync(newProfile.Id);

            _logger.LogInformation(
                "[Lock] Imported profile '{Name}' with {Count} packages (skipped {Skipped} missing)",
                newProfile.Name, newProfile.Packages.Count,
                lockFile.Packages.Count - newProfile.Packages.Count);

            return newProfile;
        }

        private static string GetAppVersion()
        {
            try
            {
                return $"UEModManager {Assembly.GetExecutingAssembly().GetName().Version}";
            }
            catch { return "UEModManager"; }
        }

        // ─── Phase 12 进阶：整合包（lock + 包文件捆绑） ───

        private const string BundleLockEntryName = "profile.lock.json";
        private const string BundlePackagesPrefix = "packages/";
        private const long MaxBundleMetadataBytes = 16L * 1024 * 1024;

        /// <summary>
        /// 导出整合包（zip）：含 profile.lock.json + 所有引用包的物理文件。
        /// 接收方解压即可还原方案，无需先单独导入 MOD。
        /// </summary>
        public async Task ExportBundleAsync(string outputZipPath)
        {
            if (string.IsNullOrWhiteSpace(outputZipPath))
                throw new ArgumentException("Output path required", nameof(outputZipPath));

            var profile = _profileService.CurrentProfile
                ?? throw new InvalidOperationException("当前没有活跃 Profile，无法导出");

            var packagesByKey = _packageRepo.GetAllPackages()
                .ToDictionary(p => p.PackageKey, p => p, StringComparer.OrdinalIgnoreCase);

            var overrides = _conflictAnalyzer?.GetPortableOverrides(profile) ?? profile.ConflictOverrides;

            var lockFile = ProfileLockBuilder.Build(
                profile, packagesByKey, overrides,
                appVersion: GetAppVersion());

            var dir = Path.GetDirectoryName(outputZipPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (File.Exists(outputZipPath)) File.Delete(outputZipPath);

            int packagesAdded = 0;
            using (var stream = File.Create(outputZipPath))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                // 1. lock JSON
                var lockEntry = archive.CreateEntry(BundleLockEntryName, CompressionLevel.Optimal);
                using (var writer = new StreamWriter(lockEntry.Open()))
                {
                    await writer.WriteAsync(JsonSerializer.Serialize(lockFile, JsonOptions));
                }

                // 2. 每个引用包的目录
                foreach (var pkg in lockFile.Packages)
                {
                    var pkgDir = _packageRepo.Store.GetPackageDirectory(pkg.PackageKey);
                    if (!Directory.Exists(pkgDir))
                    {
                        _logger.LogDebug("[Lock] Package dir missing, skipped from bundle: {Key}", pkg.PackageKey);
                        continue;
                    }
                    await AddDirectoryToZipAsync(archive, pkgDir, $"{BundlePackagesPrefix}{pkg.PackageKey}");
                    packagesAdded++;
                }
            }

            _logger.LogInformation(
                "[Lock] Exported bundle '{Name}' → {Path} ({Count} packages bundled)",
                profile.Name, outputZipPath, packagesAdded);
        }

        /// <summary>
        /// 整合包导入预览结果。
        /// </summary>
        public sealed record BundlePreview(
            ProfileLock LockFile,
            ProfileLockDiff Diff,
            HashSet<string> PackageKeysInBundle);

        /// <summary>
        /// 读取整合包并预览导入。返回 lock + diff + 整合包内提供的包列表。
        /// </summary>
        public async Task<BundlePreview> PreviewBundleImportAsync(string zipPath)
        {
            if (!File.Exists(zipPath))
                throw new FileNotFoundException("整合包文件不存在", zipPath);

            using var archive = ZipFile.OpenRead(zipPath);

            var lockEntry = archive.GetEntry(BundleLockEntryName)
                ?? throw new InvalidOperationException(
                    $"整合包中缺少 {BundleLockEntryName}，可能不是有效的整合包");

            var json = await ReadBundleMetadataAsync(lockEntry);
            var lockFile = JsonSerializer.Deserialize<ProfileLock>(json, JsonOptions)
                ?? throw new InvalidOperationException("Lock 文件解析失败");

            if (lockFile.LockVersion > ProfileLockSchema.CurrentVersion)
                throw new InvalidOperationException(
                    $"整合包 lock 版本 {lockFile.LockVersion} 高于当前应用支持的 {ProfileLockSchema.CurrentVersion}");

            ValidateLockFile(lockFile);

            // 列出整合包内的包目录（packages/{key}/...）
            var bundleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                if (!entry.FullName.StartsWith(BundlePackagesPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                var rest = entry.FullName[BundlePackagesPrefix.Length..];
                var slash = rest.IndexOf('/');
                if (slash <= 0) continue;
                bundleKeys.Add(rest[..slash]);
            }

            var localPackages = _packageRepo.GetAllPackages()
                .ToDictionary(p => p.PackageKey, p => p, StringComparer.OrdinalIgnoreCase);
            var diff = ProfileLockComparator.Compare(lockFile, localPackages);

            _logger.LogInformation(
                "[Lock] Bundle preview {Path}: {Match} matched, {Miss} missing locally, {Bundled} bundled",
                zipPath, diff.MatchedCount, diff.MissingCount, bundleKeys.Count);

            return new BundlePreview(lockFile, diff, bundleKeys);
        }

        /// <summary>
        /// 应用整合包导入：把整合包中本地缺失的包解压到仓库，注册到 PackageRepository，
        /// 然后调用 <see cref="ApplyImportAsync"/> 创建新 Profile。
        /// </summary>
        public Task<InstanceProfile> ApplyBundleImportAsync(string zipPath, ProfileLock lockFile)
            => ApplyBundleImportAsync(zipPath, lockFile, ArchiveExtractor.MaxTotalExtractedBytes);

        internal async Task<InstanceProfile> ApplyBundleImportAsync(
            string zipPath, ProfileLock lockFile, long maximumExtractedBytes)
        {
            if (lockFile == null) throw new ArgumentNullException(nameof(lockFile));
            if (!File.Exists(zipPath)) throw new FileNotFoundException("整合包文件不存在", zipPath);
            ValidateLockFile(lockFile);
            ValidateImportHost(lockFile);
            var extractionBudget = new ExtractionBudget(maximumExtractedBytes);

            // 整合包导入是直接往仓库目录 ExtractToFile，不经过 ObjectStore 的写方法，
            // 所以自己问一次搬移闸门，而且要在开始解压<b>之前</b>问：一个整合包可能几 GB，
            // 解压完再撞上闸门，用户白等一遍还得看着那批文件随旧位置被清空。
            _packageRepo.Store.ThrowIfRelocating("导入整合包");

            var localKeys = new HashSet<string>(
                _packageRepo.GetAllPackages().Select(p => p.PackageKey),
                StringComparer.OrdinalIgnoreCase);

            int extracted = 0, registered = 0;
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                foreach (var pkg in lockFile.Packages)
                {
                    if (localKeys.Contains(pkg.PackageKey)) continue;

                    // PackageKey 来自整合包内的 profile.lock.json（完全不可信），
                    // 非法键直接跳过该包，不能让它参与任何路径拼接。
                    string pkgDir;
                    try
                    {
                        pkgDir = _packageRepo.Store.GetPackageDirectory(pkg.PackageKey);
                    }
                    catch (ArgumentException ex)
                    {
                        _logger.LogWarning(ex, "[Lock] Rejected unsafe package key in bundle: {Key}", pkg.PackageKey);
                        continue;
                    }

                    var prefix = $"{BundlePackagesPrefix}{pkg.PackageKey}/";
                    var bundled = archive.Entries
                        .Where(e => e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (bundled.Count == 0) continue;

                    // manifest 必须先于任何落盘动作验证。否则攻击者可以让目录名使用 A，
                    // manifest 却声明 B，随后注册流程会把 B 的元数据写进索引，
                    // 而实际文件仍躺在 A 目录里。
                    var manifestEntry = bundled.FirstOrDefault(e =>
                        string.Equals(e.FullName, $"{prefix}manifest.json", StringComparison.OrdinalIgnoreCase));
                    if (manifestEntry == null)
                    {
                        _logger.LogWarning("[Lock] Bundle package has no manifest entry: {Key}", pkg.PackageKey);
                        continue;
                    }

                    PackageManifest? manifest;
                    try
                    {
                        var manifestJson = await ReadBundleMetadataAsync(manifestEntry);
                        manifest = JsonSerializer.Deserialize<PackageManifest>(manifestJson, JsonOptions);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Lock] Failed to read package manifest: {Key}", pkg.PackageKey);
                        continue;
                    }

                    if (manifest == null
                        || manifest.Artifacts == null
                        || !string.Equals(
                            manifest.PackageKey, pkg.PackageKey, StringComparison.Ordinal))
                    {
                        _logger.LogWarning(
                            "[Lock] Manifest key does not match lock key: {ManifestKey} vs {LockKey}",
                            manifest?.PackageKey, pkg.PackageKey);
                        continue;
                    }

                    var invalidArtifact = false;
                    var expectedEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        $"{prefix}manifest.json"
                    };
                    foreach (var artifact in manifest.Artifacts)
                    {
                        var sourcePath = artifact.RelativeSourcePath?.Replace('\\', '/');
                        var artifactPrefix = $"{pkg.PackageKey}/files/";
                        if (string.IsNullOrWhiteSpace(sourcePath)
                            || !sourcePath.StartsWith(artifactPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogWarning(
                                "[Lock] Manifest artifact escapes package directory: {Key} -> {Path}",
                                pkg.PackageKey, artifact.RelativeSourcePath);
                            invalidArtifact = true;
                            break;
                        }

                        try
                        {
                            var artifactRelativePath = sourcePath[artifactPrefix.Length..];
                            _ = PathSanitizer.SafeCombine(
                                _packageRepo.Store.GetPackageFilesDirectory(pkg.PackageKey),
                                artifactRelativePath);
                        }
                        catch (ArgumentException ex)
                        {
                            _logger.LogWarning(
                                ex, "[Lock] Manifest artifact path is unsafe: {Key} -> {Path}",
                                pkg.PackageKey, sourcePath);
                            invalidArtifact = true;
                            break;
                        }

                        var artifactEntryName = $"{BundlePackagesPrefix}{sourcePath}";
                        if (!expectedEntries.Add(artifactEntryName))
                        {
                            _logger.LogWarning(
                                "[Lock] Manifest contains duplicate artifact path: {Key} -> {Path}",
                                pkg.PackageKey, sourcePath);
                            invalidArtifact = true;
                            break;
                        }

                        if (!bundled.Any(e => string.Equals(
                                e.FullName, artifactEntryName, StringComparison.OrdinalIgnoreCase)))
                        {
                            _logger.LogWarning(
                                "[Lock] Manifest artifact missing from bundle: {Key} -> {Path}",
                                pkg.PackageKey, artifactEntryName);
                            invalidArtifact = true;
                            break;
                        }
                    }

                    if (invalidArtifact) continue;

                    // 只允许 manifest 和清单中登记的 artifact。否则攻击者可以把额外文件
                    // 解进一个看似正常的包，后续部署/回收行为会被未登记实体污染。
                    var seenEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var entry in bundled)
                    {
                        if (!seenEntries.Add(entry.FullName)
                            || !expectedEntries.Contains(entry.FullName))
                        {
                            _logger.LogWarning(
                                "[Lock] Bundle contains duplicate or unregistered entry: {Entry}",
                                entry.FullName);
                            invalidArtifact = true;
                            break;
                        }
                    }

                    if (invalidArtifact) continue;

                    // 目录是否本来就在磁盘上：只有本次自己创建的才允许在失败时删掉。
                    // localKeys 已经排除了索引里有记录的键，但索引里没有、磁盘上有的残留
                    // （上一次整合包导入失败留下的）仍可能存在。
                    var pkgDirPreexisted = Directory.Exists(pkgDir);
                    if (pkgDirPreexisted)
                    {
                        _logger.LogWarning(
                            "[Lock] Package directory already exists but is not indexed, skip import: {Key}",
                            pkg.PackageKey);
                        continue;
                    }

                    Directory.CreateDirectory(pkgDir);

                    try
                    {
                        foreach (var entry in bundled)
                        {
                            var rel = entry.FullName[prefix.Length..];
                            if (string.IsNullOrWhiteSpace(rel)) continue;

                            var outPath = PathSanitizer.SafeCombine(pkgDir, rel);
                            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                                throw new InvalidDataException("整合包不允许包含符号链接");

                            if (entry.FullName.EndsWith('/'))
                            {
                                Directory.CreateDirectory(outPath);
                                continue;
                            }

                            var outDir = Path.GetDirectoryName(outPath);
                            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
                            using var source = entry.Open();
                            await using var destination = new FileStream(outPath, FileMode.CreateNew,
                                FileAccess.Write, FileShare.None);
                            using var limited = extractionBudget.Limit(destination);
                            await source.CopyToAsync(limited);
                        }
                    }
                    catch
                    {
                        CleanupUnregisteredBundleDirectory(pkg.PackageKey, pkgDir, pkgDirPreexisted);
                        throw;
                    }
                    extracted++;

                    // 解压后核对尺寸和已有短哈希；只有实体与 manifest 一致才允许进索引。
                    var artifactVerificationFailed = false;
                    foreach (var artifact in manifest.Artifacts)
                    {
                        var sourcePath = artifact.RelativeSourcePath.Replace('\\', '/');
                        var artifactPrefix = $"{pkg.PackageKey}/files/";
                        var artifactRelativePath = sourcePath[artifactPrefix.Length..];
                        var artifactPath = PathSanitizer.SafeCombine(
                            _packageRepo.Store.GetPackageFilesDirectory(pkg.PackageKey),
                            artifactRelativePath);

                        if (!File.Exists(artifactPath)
                            || (artifact.FileSize >= 0 && new FileInfo(artifactPath).Length != artifact.FileSize))
                        {
                            _logger.LogWarning(
                                "[Lock] Bundle artifact size/presence check failed: {Key} -> {Path}",
                                pkg.PackageKey, artifactRelativePath);
                            artifactVerificationFailed = true;
                            break;
                        }

                        if (!string.IsNullOrWhiteSpace(artifact.FileHash))
                        {
                            var actualHash = await ObjectStore.ComputeFileHashAsync(artifactPath);
                            if (!string.Equals(actualHash, artifact.FileHash, StringComparison.OrdinalIgnoreCase))
                            {
                                _logger.LogWarning(
                                    "[Lock] Bundle artifact hash check failed: {Key} -> {Path}",
                                    pkg.PackageKey, artifactRelativePath);
                                artifactVerificationFailed = true;
                                break;
                            }
                        }
                    }

                    if (artifactVerificationFailed)
                    {
                        CleanupUnregisteredBundleDirectory(pkg.PackageKey, pkgDir, pkgDirPreexisted);
                        continue;
                    }

                    // 从已验证过的 manifest.json 注册 Package。
                    // 注册不成 = 文件已经解进仓库、索引里却没有记录，正是导入路径上那种
                    // "用户看不见也删不掉"的孤儿目录，故这里补上与 PackageImportService
                    // 对称的补偿删除：只删本次自己解出来的目录。
                    var registeredThisPackage = false;
                    try
                    {
                        var newPkg = manifest.ToPackage();
                        await _packageRepo.RegisterPackageAsync(newPkg);
                        registered++;
                        registeredThisPackage = true;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Lock] Failed to register package from bundle: {Key}", pkg.PackageKey);
                    }

                    if (!registeredThisPackage)
                        CleanupUnregisteredBundleDirectory(pkg.PackageKey, pkgDir, pkgDirPreexisted);
                }
            }

            _logger.LogInformation(
                "[Lock] Bundle apply: extracted {Extracted} package dirs, registered {Registered} packages",
                extracted, registered);

            return await ApplyImportAsync(lockFile);
        }

        /// <summary>
        /// 整合包里的某个包解出来了、却没能登记进索引时，删掉本次自己解出来的目录。
        ///
        /// <para>
        /// 不这么做的话磁盘上就多出一个"有文件、索引里没记录"的目录：用户在界面上看不见它，
        /// 也就没有任何入口能删掉它，只能靠 <see cref="RepositoryReclaimService"/> 事后回收，
        /// 而带 manifest 的残留连事后回收都不会碰（那条判据保守到只提示不删）。
        /// </para>
        /// <para>
        /// 目录在本次导入之前就存在时不删：那可能是别的包/别的游戏的数据，
        /// 判据不足就不动手 —— 与 <c>PackageImportService.CleanupPartialImport</c> 一致。
        /// </para>
        /// </summary>
        private void CleanupUnregisteredBundleDirectory(string packageKey, string pkgDir, bool preexisted)
        {
            if (preexisted)
            {
                _logger.LogWarning(
                    "[Lock] Package dir existed before import, skip cleanup: {Key}", packageKey);
                return;
            }

            try
            {
                if (Directory.Exists(pkgDir)) Directory.Delete(pkgDir, true);
                _logger.LogInformation("[Lock] Cleaned up unregistered bundle package dir: {Key}", packageKey);
            }
            catch (Exception ex)
            {
                // 删不掉不阻断整合包导入的其余部分；残留会被 RepositoryReclaimService 找出来。
                _logger.LogWarning(ex, "[Lock] Failed to clean up bundle package dir: {Key}", packageKey);
            }
        }

        private static void ValidateLockFile(ProfileLock lockFile)
        {
            if (lockFile.LockVersion > ProfileLockSchema.CurrentVersion)
                throw new InvalidOperationException($"不支持 Lock 文件版本: {lockFile.LockVersion}");
            if (lockFile.Profile == null || lockFile.Host == null || lockFile.ConflictOverrides == null)
                throw new InvalidOperationException("Lock 文件缺少方案、游戏或覆盖规则");
            if (lockFile.Packages == null)
                throw new InvalidOperationException("Lock 文件缺少 packages 列表");

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var package in lockFile.Packages)
            {
                if (package == null || string.IsNullOrWhiteSpace(package.PackageKey))
                    throw new InvalidOperationException("Lock 文件包含空的 PackageKey");

                try
                {
                    PathSanitizer.SanitizeSegment(package.PackageKey, nameof(package.PackageKey));
                }
                catch (ArgumentException ex)
                {
                    throw new InvalidOperationException(
                        $"Lock 文件包含非法 PackageKey: {package.PackageKey}", ex);
                }

                if (!seen.Add(package.PackageKey))
                    throw new InvalidOperationException(
                        $"Lock 文件包含重复 PackageKey: {package.PackageKey}");
            }

            foreach (var (target, winner) in lockFile.ConflictOverrides)
            {
                try
                {
                    PathSanitizer.SanitizeSegment(winner, "winnerPackageKey");
                    ConflictOverridePaths.ToPortableKey(target, "", "");
                }
                catch (ArgumentException ex)
                {
                    throw new InvalidOperationException($"Lock 文件包含非法冲突覆盖: {target}", ex);
                }
            }
        }

        private void ValidateImportHost(ProfileLock lockFile)
        {
            if (!string.IsNullOrEmpty(lockFile.Host.GameName)
                && !string.Equals(lockFile.Host.GameName, _profileService.CurrentProfile?.HostGameName,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Lock 文件所属游戏与当前游戏不一致");
        }

        private static async Task<string> ReadBundleMetadataAsync(ZipArchiveEntry entry)
        {
            // lock / manifest 在预览时也会解压到内存，不能等文件提取阶段才施加限制。
            using var memory = new MemoryStream();
            using var limited = new ExtractionBudget(MaxBundleMetadataBytes).Limit(memory);
            using (var source = entry.Open()) await source.CopyToAsync(limited);
            memory.Position = 0;
            using var reader = new StreamReader(memory);
            return await reader.ReadToEndAsync();
        }

        private Dictionary<string, string> RebaseImportedOverrides(
            IReadOnlyDictionary<string, string> overrides, IReadOnlyList<ProfilePackageEntry> entries)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (target, winner) in overrides)
            {
                var key = ConflictOverridePaths.ToPortableKey(target, "", "");
                if (Path.IsPathRooted(target))
                {
                    // v1 已导出的绝对键没有根路径元数据。从胜者包的目标文件唯一匹配相对后缀。
                    // 不唯一或缺包时保留旧键，不把它猜测成其他目标的覆盖。
                    var package = _packageRepo.GetByKey(winner);
                    var entry = entries.FirstOrDefault(e =>
                        string.Equals(e.PackageKey, winner, StringComparison.OrdinalIgnoreCase));
                    if (package != null)
                    {
                        var normalizedTarget = target.Replace('\\', '/');
                        var candidates = package.Artifacts.Where(a => a.ArtifactType != ArtifactType.PreviewImage)
                            .Select(a => package.Kind == PackageKind.Mod
                                ? ConflictOverridePaths.ModPrefix + ConflictOverridePaths.SafeRelative(a.RelativeTargetPath)
                                : ConflictOverridePaths.GamePrefix + ConflictOverridePaths.SafeRelative(
                                    Path.Combine(entry?.TargetRootPath ?? package.TargetRootPath ?? "", a.RelativeTargetPath)))
                            .Where(candidate => normalizedTarget.EndsWith(
                                "/" + candidate[(candidate.IndexOf('/') + 1)..], StringComparison.OrdinalIgnoreCase))
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        if (candidates.Count == 1) key = candidates[0];
                        else _logger.LogWarning("[Lock] Cannot uniquely relocate legacy override: {Target}", target);
                    }
                }
                result[key] = winner;
            }
            return result;
        }

        // ─── ZIP 工具 ───

        private static async Task AddDirectoryToZipAsync(ZipArchive archive, string sourceDir, string entryRoot)
        {
            // 惰性枚举：一个包可能有上千个文件，没必要先把全部路径物化。
            // 目标 zip 在仓库之外（导出路径由用户选择），不会枚举到正在写入的自身。
            foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
                var entryName = $"{entryRoot}/{rel}";
                var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await using var fileStream = File.OpenRead(file);
                await fileStream.CopyToAsync(entryStream);
            }
        }
    }
}
