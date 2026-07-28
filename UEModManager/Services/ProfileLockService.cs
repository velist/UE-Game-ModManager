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

            var overrides = _conflictAnalyzer?.GetOverrides();

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

            var localKeys = new HashSet<string>(
                _packageRepo.GetAllPackages().Select(p => p.PackageKey),
                StringComparer.OrdinalIgnoreCase);

            var newProfile = await _profileService.CreateProfileAsync(
                name: $"{lockFile.Profile.Name} (导入)",
                description: lockFile.Profile.Description);

            // 填入包条目（仅本地存在的）
            foreach (var pkg in lockFile.Packages)
            {
                if (!localKeys.Contains(pkg.PackageKey)) continue;

                newProfile.Packages.Add(new ProfilePackageEntry
                {
                    PackageKey = pkg.PackageKey,
                    IsEnabled = pkg.IsEnabled,
                    Priority = pkg.Priority,
                    Kind = Enum.TryParse<PackageKind>(pkg.Kind, out var k) ? k : PackageKind.Mod,
                });
            }

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

            var overrides = _conflictAnalyzer?.GetOverrides();

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

            ProfileLock lockFile;
            using (var reader = new StreamReader(lockEntry.Open()))
            {
                var json = await reader.ReadToEndAsync();
                lockFile = JsonSerializer.Deserialize<ProfileLock>(json, JsonOptions)
                    ?? throw new InvalidOperationException("Lock 文件解析失败");
            }

            if (lockFile.LockVersion > ProfileLockSchema.CurrentVersion)
                throw new InvalidOperationException(
                    $"整合包 lock 版本 {lockFile.LockVersion} 高于当前应用支持的 {ProfileLockSchema.CurrentVersion}");

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
        public async Task<InstanceProfile> ApplyBundleImportAsync(string zipPath, ProfileLock lockFile)
        {
            if (lockFile == null) throw new ArgumentNullException(nameof(lockFile));
            if (!File.Exists(zipPath)) throw new FileNotFoundException("整合包文件不存在", zipPath);

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

                    // 目录是否本来就在磁盘上：只有本次自己创建的才允许在失败时删掉。
                    // localKeys 已经排除了索引里有记录的键，但索引里没有、磁盘上有的残留
                    // （上一次整合包导入失败留下的）仍可能存在。
                    var pkgDirPreexisted = Directory.Exists(pkgDir);
                    Directory.CreateDirectory(pkgDir);

                    foreach (var entry in bundled)
                    {
                        var rel = entry.FullName[prefix.Length..];
                        if (string.IsNullOrWhiteSpace(rel)) continue;

                        // zip 条目名同样不可信：SafeCombine 会拒绝 .. 上跳与绝对路径，
                        // 并二次校验结果确实落在 pkgDir 内。
                        string outPath;
                        try
                        {
                            outPath = PathSanitizer.SafeCombine(pkgDir, rel);
                        }
                        catch (ArgumentException ex)
                        {
                            _logger.LogWarning(ex, "[Lock] Rejected unsafe bundle entry: {Entry}", entry.FullName);
                            continue;
                        }

                        // 目录条目（FullName 以 / 结尾）跳过
                        if (entry.FullName.EndsWith('/'))
                        {
                            Directory.CreateDirectory(outPath);
                            continue;
                        }

                        var outDir = Path.GetDirectoryName(outPath);
                        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
                        entry.ExtractToFile(outPath, overwrite: true);
                    }
                    extracted++;

                    // 从解压出的 manifest.json 注册 Package。
                    // 注册不成 = 文件已经解进仓库、索引里却没有记录，正是导入路径上那种
                    // "用户看不见也删不掉"的孤儿目录，故这里补上与 PackageImportService
                    // 对称的补偿删除：只删本次自己解出来的目录。
                    var manifestPath = _packageRepo.Store.GetManifestPath(pkg.PackageKey);
                    var registeredThisPackage = false;
                    if (File.Exists(manifestPath))
                    {
                        try
                        {
                            var manifestJson = await File.ReadAllTextAsync(manifestPath);
                            var manifest = JsonSerializer.Deserialize<PackageManifest>(manifestJson, JsonOptions);
                            if (manifest != null)
                            {
                                var newPkg = manifest.ToPackage();
                                await _packageRepo.RegisterPackageAsync(newPkg);
                                registered++;
                                registeredThisPackage = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "[Lock] Failed to register package from bundle: {Key}", pkg.PackageKey);
                        }
                    }
                    else
                    {
                        // 整合包里这个包缺 manifest.json —— 解出来的文件永远不会被登记。
                        _logger.LogWarning("[Lock] Bundle package has no manifest.json: {Key}", pkg.PackageKey);
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
