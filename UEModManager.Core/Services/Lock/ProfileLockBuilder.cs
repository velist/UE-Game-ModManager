using System;
using System.Collections.Generic;
using System.Linq;
using UEModManager.Models;

namespace UEModManager.Services.Lock
{
    /// <summary>
    /// ProfileLock 构造与比较的纯函数集合。
    ///
    /// <see cref="Build"/>: 给定 Profile + 包字典 + 覆盖规则 + 元数据 → 生成 ProfileLock
    /// <see cref="ProfileLockComparator.Compare"/>: 给定 ProfileLock + 本地包字典 → 生成 ProfileLockDiff
    /// </summary>
    public static class ProfileLockBuilder
    {
        public static ProfileLock Build(
            InstanceProfile profile,
            IReadOnlyDictionary<string, Package> packagesByKey,
            IReadOnlyDictionary<string, string>? conflictOverrides = null,
            string appVersion = "")
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (packagesByKey == null) throw new ArgumentNullException(nameof(packagesByKey));

            var lockPackages = profile.Packages
                .Select(entry =>
                {
                    packagesByKey.TryGetValue(entry.PackageKey, out var pkg);
                    return new ProfileLockPackage
                    {
                        PackageKey = entry.PackageKey,
                        DisplayName = pkg?.DisplayName ?? entry.PackageKey,
                        Kind = (pkg?.Kind ?? PackageKind.Mod).ToString(),
                        Version = pkg?.Version ?? "1.0.0",
                        IsEnabled = entry.IsEnabled,
                        Priority = entry.Priority,
                        ContentHash = pkg?.ContentHash,
                    };
                })
                .ToList();

            return new ProfileLock
            {
                ExportedByApp = appVersion,
                Host = new ProfileLockHost { GameName = profile.HostGameName },
                Profile = new ProfileLockProfile
                {
                    Name = profile.Name,
                    Description = profile.Description,
                    BackendType = profile.BackendType.ToString(),
                },
                Packages = lockPackages,
                ConflictOverrides = conflictOverrides == null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string>(conflictOverrides, StringComparer.OrdinalIgnoreCase),
            };
        }
    }

    public enum LockPackageImportStatus
    {
        Matched,
        HashMismatch,
        Missing,
    }

    public sealed record LockPackageDiff(
        string PackageKey,
        string DisplayName,
        LockPackageImportStatus Status,
        string? LockedHash,
        string? LocalHash);

    public sealed class ProfileLockDiff
    {
        public List<LockPackageDiff> PackageDiffs { get; init; } = [];

        public int MatchedCount => PackageDiffs.Count(d => d.Status == LockPackageImportStatus.Matched);
        public int MissingCount => PackageDiffs.Count(d => d.Status == LockPackageImportStatus.Missing);
        public int HashMismatchCount => PackageDiffs.Count(d => d.Status == LockPackageImportStatus.HashMismatch);

        public bool CanImportFully => MissingCount == 0 && HashMismatchCount == 0;
    }

    public static class ProfileLockComparator
    {
        public static ProfileLockDiff Compare(
            ProfileLock lockFile,
            IReadOnlyDictionary<string, Package> localPackagesByKey)
        {
            if (lockFile == null) throw new ArgumentNullException(nameof(lockFile));
            if (localPackagesByKey == null) throw new ArgumentNullException(nameof(localPackagesByKey));

            var diffs = new List<LockPackageDiff>();

            foreach (var lockPkg in lockFile.Packages)
            {
                if (!localPackagesByKey.TryGetValue(lockPkg.PackageKey, out var localPkg))
                {
                    diffs.Add(new LockPackageDiff(
                        lockPkg.PackageKey, lockPkg.DisplayName,
                        LockPackageImportStatus.Missing,
                        lockPkg.ContentHash, null));
                    continue;
                }

                if (!string.IsNullOrEmpty(lockPkg.ContentHash)
                    && !string.IsNullOrEmpty(localPkg.ContentHash)
                    && !string.Equals(lockPkg.ContentHash, localPkg.ContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    diffs.Add(new LockPackageDiff(
                        lockPkg.PackageKey, lockPkg.DisplayName,
                        LockPackageImportStatus.HashMismatch,
                        lockPkg.ContentHash, localPkg.ContentHash));
                    continue;
                }

                diffs.Add(new LockPackageDiff(
                    lockPkg.PackageKey, lockPkg.DisplayName,
                    LockPackageImportStatus.Matched,
                    lockPkg.ContentHash, localPkg.ContentHash));
            }

            return new ProfileLockDiff { PackageDiffs = diffs };
        }
    }
}
