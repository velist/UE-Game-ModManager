using System;
using System.Collections.Generic;
using System.Linq;
using UEModManager.Models;

namespace UEModManager.Services.Reconciliation
{
    /// <summary>一个文件在磁盘上的状态。</summary>
    public enum FileProbeState
    {
        /// <summary>确认存在，<see cref="FileProbe.Size"/> 有效。</summary>
        Present,

        /// <summary>确认不存在。</summary>
        Missing,

        /// <summary>
        /// 探测不了（权限不足、IO 错误、所在的盘不在线、路径非法）。<b>不等于丢失</b>：
        /// 只要有一个文件探测不了，就不对它所属的包下结论。
        /// </summary>
        Unknown,
    }

    /// <summary>一次文件探测的结果。由主项目读磁盘后给出，Core 只判定。</summary>
    public readonly record struct FileProbe(FileProbeState State, long Size)
    {
        public static FileProbe Present(long size) => new(FileProbeState.Present, size);
        public static FileProbe Missing { get; } = new(FileProbeState.Missing, 0);
        public static FileProbe Unknown { get; } = new(FileProbeState.Unknown, 0);
    }

    /// <summary>
    /// 核对要用到的磁盘探测。三个委托都不许抛异常——探测失败用 <see cref="FileProbe.Unknown"/> 表达。
    /// </summary>
    /// <param name="RepositoryFile">
    /// 探测一个仓库源文件。参数是 <see cref="PackageArtifact.RelativeSourcePath"/>（相对仓库根）。
    /// </param>
    /// <param name="GameFile">
    /// 探测一个已部署的文件。参数是 <see cref="ManagedDeploymentFile.TargetPath"/>（绝对路径）。
    /// </param>
    /// <param name="HasOtherRepositoryFiles">
    /// 包的 <c>files</c> 目录下是否还有<b>没登记</b>的文件。参数是包键；只在该包登记的文件一个都不剩时才会被调用。
    /// 探测不了时返回 true（当成"还有东西"，不会被判成整包丢失）。
    /// </param>
    public sealed record DiskProbes(
        Func<string, FileProbe> RepositoryFile,
        Func<string, FileProbe> GameFile,
        Func<string, bool> HasOtherRepositoryFiles);

    /// <summary>一次核对的输入：管理器<b>记着</b>的东西。</summary>
    public sealed record DiskReconcileInput
    {
        /// <summary>仓库索引里的包（当前游戏）。</summary>
        public required IReadOnlyList<Package> Packages { get; init; }

        /// <summary>部署状态里的受管文件：管理器装进游戏目录的每一个文件。</summary>
        public IReadOnlyList<ManagedDeploymentFile> ManagedFiles { get; init; } = [];

        /// <summary>当前方案里启用的包。</summary>
        public IReadOnlyCollection<string> EnabledPackageKeys { get; init; } = [];

        /// <summary>仓库根目录能不能访问到。false 时仓库侧不下任何结论。</summary>
        public bool RepositoryRootReachable { get; init; } = true;

        /// <summary>游戏目录能不能访问到。false 时游戏侧不下任何结论。</summary>
        public bool GameRootReachable { get; init; } = true;
    }

    /// <summary>仓库里缺了、但游戏目录里还装着一份内容相同的文件。</summary>
    /// <param name="RelativeSourcePath">要找回的仓库文件（相对仓库根）。</param>
    /// <param name="GameFilePath">游戏目录里的那一份（绝对路径）。</param>
    /// <param name="FileSize">登记的大小。</param>
    /// <param name="FileHash">登记的哈希；复制回去之前由主项目重算核对。</param>
    public sealed record RecoverableFile(string RelativeSourcePath, string GameFilePath, long FileSize, string FileHash);

    /// <summary>一个包在仓库侧的问题。</summary>
    /// <param name="TotalFiles">这个包登记的源文件数。</param>
    /// <param name="MissingFiles">仓库里找不到的源文件（相对仓库根）。</param>
    /// <param name="ChangedFiles">还在、但大小和登记的不一样的源文件。</param>
    /// <param name="Recoverable">缺失的文件里，能从游戏目录找回的那些。</param>
    /// <param name="ManagedFileCount">管理器记着为它在游戏目录里装了几个文件（不管现在还在不在）。</param>
    /// <param name="DeployedFilesPresent">其中现在确认还在的（绝对路径）。</param>
    /// <param name="DeployedFilesUnknown">
    /// 它装在游戏目录里的文件有探测不了的（游戏目录访问不到等）。此时不知道游戏里还有没有它的文件，
    /// 不能按"游戏里没有"来处理。
    /// </param>
    public sealed record RepositoryFinding(
        string PackageKey,
        string DisplayName,
        bool IsEnabled,
        int TotalFiles,
        IReadOnlyList<string> MissingFiles,
        IReadOnlyList<string> ChangedFiles,
        IReadOnlyList<RecoverableFile> Recoverable,
        int ManagedFileCount,
        IReadOnlyList<string> DeployedFilesPresent,
        bool DeployedFilesUnknown)
    {
        /// <summary>缺失的文件每一个都能从游戏目录找回。</summary>
        public bool CanRecoverEverything => MissingFiles.Count > 0 && Recoverable.Count == MissingFiles.Count;

        /// <summary>
        /// 这个包启用着、又缺源文件：最终视图构建不出来，
        /// 所有部署（启用 / 禁用任何 MOD、启动游戏）都会因此失败，直到它被处理。
        /// </summary>
        public bool BlocksDeployment => IsEnabled && MissingFiles.Count > 0;

        /// <summary>游戏目录里还留着它的文件——处理这个包时不能顺手把这些文件删掉。</summary>
        public bool HasDeployedFiles => DeployedFilesPresent.Count > 0;
    }

    /// <summary>一个包在游戏侧的问题：管理器装过的文件不见了。</summary>
    /// <param name="IsKnownPackage">这个键是不是仓库索引里的包（否则是生成物、用户修复等）。</param>
    /// <param name="CanReinstall">按当前方案重新部署就能装回去：包还在、启用着、仓库里的源文件齐全。</param>
    /// <param name="ManagedFileCount">管理器记着为它装了几个文件。</param>
    /// <param name="MissingFiles">其中在游戏目录里找不到的（绝对路径）。</param>
    public sealed record DeploymentFinding(
        string PackageKey,
        string DisplayName,
        bool IsKnownPackage,
        bool IsEnabled,
        bool CanReinstall,
        int ManagedFileCount,
        IReadOnlyList<string> MissingFiles);

    /// <summary>核对结论。</summary>
    public sealed record DiskReconcileReport
    {
        /// <summary>仓库根目录访问不到（移动硬盘没插、网络盘掉线、文件夹被移走）。此时仓库侧的清单恒为空。</summary>
        public bool RepositoryRootUnreachable { get; init; }

        /// <summary>游戏目录访问不到。此时游戏侧的清单恒为空。</summary>
        public bool GameRootUnreachable { get; init; }

        /// <summary>
        /// 仓库根在，但登记的包<b>一个都找不到</b>。这更像是位置出了问题（指到了一个空文件夹），
        /// 而不是玩家把所有 MOD 都删了——界面必须先把它当成位置问题来问，不能直接提议移除。
        /// </summary>
        public bool SuspectedRepositoryLocationProblem { get; init; }

        /// <summary>源文件全部丢失的包。</summary>
        public IReadOnlyList<RepositoryFinding> LostPackages { get; init; } = [];

        /// <summary>源文件部分丢失的包（含"登记的全没了、但目录里还有别的文件"）。</summary>
        public IReadOnlyList<RepositoryFinding> PartialPackages { get; init; } = [];

        /// <summary>文件都在、只是大小和登记的不一样的包。不算故障（玩家可能自己改过），只供日志与体检参考。</summary>
        public IReadOnlyList<RepositoryFinding> ModifiedPackages { get; init; } = [];

        /// <summary>有文件探测不了、因此没有下结论的包。</summary>
        public IReadOnlyList<string> UncheckedPackageKeys { get; init; } = [];

        /// <summary>受管文件在游戏目录里丢失的包（按包汇总）。</summary>
        public IReadOnlyList<DeploymentFinding> MissingDeployments { get; init; } = [];

        /// <summary>核对过的包数。</summary>
        public int CheckedPackageCount { get; init; }

        /// <summary>核对过的仓库源文件数。</summary>
        public int CheckedSourceFileCount { get; init; }

        /// <summary>核对过的受管文件数。</summary>
        public int CheckedManagedFileCount { get; init; }

        /// <summary>已启用、文件在游戏目录里丢了、重新部署就能装回去的那些。</summary>
        public IReadOnlyList<DeploymentFinding> RestorableDeployments
            => MissingDeployments.Where(d => d.CanReinstall).ToList();

        /// <summary>有没有需要玩家知道的问题。</summary>
        public bool HasFindings
            => RepositoryRootUnreachable || GameRootUnreachable
               || LostPackages.Count > 0 || PartialPackages.Count > 0
               || MissingDeployments.Any(d => d.CanReinstall);
    }

    /// <summary>
    /// 把管理器记着的东西和磁盘对一遍。纯函数：磁盘上的事实全部经 <see cref="DiskProbes"/> 传入。
    ///
    /// <para><b>要解决的不一致</b></para>
    /// 列表是照着包索引和方案画的，从来不看磁盘。玩家在资源管理器里删掉仓库里某个 MOD 的文件夹，
    /// 列表照样显示它，要到启用时才出错；游戏目录里装好的 MOD 被手动删掉、或被游戏更新 / 重装清掉，
    /// 列表照样显示"已启用"。
    ///
    /// <para><b>只比"在不在 + 多大"</b></para>
    /// 不重算哈希：一个 pak 几个 GB，为了一次刷新把整个仓库读一遍是不可接受的。
    /// 内容级的校验留给管理中心的「检查缺失文件」。
    ///
    /// <para><b>宁可少报，不可错报</b></para>
    /// 这份报告会被用来提议"从列表里移除"。因此：位置访问不到时那一侧一律不下结论；
    /// 探测不了的文件不算丢失；登记的文件全没了但目录里还有别的东西，也不算整包丢失。
    /// </summary>
    public static class DiskReconciler
    {
        public static DiskReconcileReport Reconcile(DiskReconcileInput input, DiskProbes probes)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (probes == null) throw new ArgumentNullException(nameof(probes));
            if (input.Packages == null) throw new ArgumentException("包清单不能为空引用", nameof(input));

            var enabled = new HashSet<string>(input.EnabledPackageKeys ?? [], StringComparer.OrdinalIgnoreCase);
            var managedByKey = (input.ManagedFiles ?? [])
                .Where(f => !string.IsNullOrEmpty(f.TargetPath))
                .ToLookup(f => f.PackageKey ?? string.Empty, StringComparer.OrdinalIgnoreCase);

            // 游戏侧的探测结果两处要用（游戏侧的丢失、仓库侧的"能不能找回"），每个文件只探一次。
            var gameProbes = new Dictionary<string, FileProbe>(StringComparer.OrdinalIgnoreCase);
            FileProbe ProbeGame(string targetPath)
            {
                if (!input.GameRootReachable) return FileProbe.Unknown;
                if (!gameProbes.TryGetValue(targetPath, out var probe))
                    gameProbes[targetPath] = probe = probes.GameFile(targetPath);
                return probe;
            }

            var lost = new List<RepositoryFinding>();
            var partial = new List<RepositoryFinding>();
            var modified = new List<RepositoryFinding>();
            var inconclusiveKeys = new List<string>();
            var checkedPackages = 0;
            var checkedSourceFiles = 0;

            if (input.RepositoryRootReachable)
            {
                foreach (var package in input.Packages)
                {
                    var files = RegisteredFiles(package);
                    if (files.Count == 0) continue; // 没登记任何文件，无从谈"丢失"

                    checkedPackages++;
                    var missing = new List<PackageArtifact>();
                    var changed = new List<string>();
                    var inconclusive = false;

                    foreach (var artifact in files)
                    {
                        checkedSourceFiles++;
                        var probe = probes.RepositoryFile(artifact.RelativeSourcePath);
                        if (probe.State == FileProbeState.Unknown)
                        {
                            // 有一个探测不了，这个包就不下结论，剩下的也不必再探
                            inconclusive = true;
                            break;
                        }

                        if (probe.State == FileProbeState.Missing)
                            missing.Add(artifact);
                        // 负数表示登记时没记大小，不比
                        else if (artifact.FileSize >= 0 && probe.Size != artifact.FileSize)
                            changed.Add(artifact.RelativeSourcePath);
                    }

                    if (inconclusive)
                    {
                        inconclusiveKeys.Add(package.PackageKey);
                        continue;
                    }
                    if (missing.Count == 0 && changed.Count == 0) continue;

                    var deployed = managedByKey[package.PackageKey]
                        .GroupBy(f => f.TargetPath, StringComparer.OrdinalIgnoreCase)
                        .Select(g => g.First())
                        .ToList();
                    var finding = new RepositoryFinding(
                        package.PackageKey,
                        string.IsNullOrWhiteSpace(package.DisplayName) ? package.PackageKey : package.DisplayName,
                        enabled.Contains(package.PackageKey),
                        files.Count,
                        missing.Select(a => a.RelativeSourcePath).ToList(),
                        changed,
                        FindRecoverable(missing, deployed, ProbeGame),
                        deployed.Count,
                        deployed.Where(f => ProbeGame(f.TargetPath).State == FileProbeState.Present)
                            .Select(f => f.TargetPath).ToList(),
                        deployed.Any(f => ProbeGame(f.TargetPath).State == FileProbeState.Unknown));

                    if (missing.Count == 0)
                        modified.Add(finding);
                    else if (missing.Count == files.Count && !probes.HasOtherRepositoryFiles(package.PackageKey))
                        lost.Add(finding);
                    else
                        partial.Add(finding);
                }
            }

            // 一个确认还在的包都没有：先当成位置问题。有文件探测不了的包不能证明位置是对的，不算数。
            var suspected = lost.Count > 0 && lost.Count + inconclusiveKeys.Count == checkedPackages;

            var missingDeployments = new List<DeploymentFinding>();
            var checkedManagedFiles = 0;
            if (input.GameRootReachable)
            {
                var known = input.Packages
                    .GroupBy(p => p.PackageKey, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                var cannotReinstall = new HashSet<string>(
                    lost.Concat(partial).Select(f => f.PackageKey).Concat(inconclusiveKeys),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var group in managedByKey)
                {
                    var targets = group.Select(f => f.TargetPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    checkedManagedFiles += targets.Count;
                    var gone = targets.Where(t => ProbeGame(t).State == FileProbeState.Missing).ToList();
                    if (gone.Count == 0) continue;

                    known.TryGetValue(group.Key, out var package);
                    var isEnabled = enabled.Contains(group.Key);
                    var displayName = !string.IsNullOrWhiteSpace(package?.DisplayName) ? package!.DisplayName
                        : group.Select(f => f.PackageDisplayName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                          ?? group.Key;

                    missingDeployments.Add(new DeploymentFinding(
                        group.Key, displayName,
                        IsKnownPackage: package != null,
                        IsEnabled: isEnabled,
                        CanReinstall: package != null && isEnabled && input.RepositoryRootReachable
                                      && !cannotReinstall.Contains(group.Key),
                        ManagedFileCount: targets.Count,
                        MissingFiles: gone));
                }
            }

            return new DiskReconcileReport
            {
                // 一个包都没登记时，仓库目录不存在是全新安装的正常状态（目录要到第一次导入才创建）
                RepositoryRootUnreachable = !input.RepositoryRootReachable
                                            && input.Packages.Any(p => RegisteredFiles(p).Count > 0),
                // 同理：什么都没装进游戏目录时，游戏目录在不在不影响任何 MOD 的状态
                GameRootUnreachable = !input.GameRootReachable && managedByKey.Count > 0,
                SuspectedRepositoryLocationProblem = suspected,
                LostPackages = lost,
                PartialPackages = partial,
                ModifiedPackages = modified,
                UncheckedPackageKeys = inconclusiveKeys,
                MissingDeployments = missingDeployments,
                CheckedPackageCount = checkedPackages,
                CheckedSourceFileCount = checkedSourceFiles,
                CheckedManagedFileCount = checkedManagedFiles,
            };
        }

        /// <summary>包登记的源文件，同一个路径只算一次（manifest 损坏时可能重复登记）。</summary>
        private static List<PackageArtifact> RegisteredFiles(Package package)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<PackageArtifact>();
            foreach (var artifact in package.Artifacts ?? [])
            {
                if (string.IsNullOrWhiteSpace(artifact?.RelativeSourcePath)) continue;
                if (seen.Add(artifact.RelativeSourcePath.Replace('\\', '/'))) result.Add(artifact);
            }
            return result;
        }

        /// <summary>
        /// 缺失的源文件里，哪些在游戏目录里还有一份内容相同的。
        ///
        /// 按<b>同一个包 + 同一个哈希 + 同一个大小</b>对：路径对不上没关系（部署时会归位、会套包目录），
        /// 内容相同的就是同一个文件。合并出来的配置、被改过的文件哈希不同，自然对不上。
        /// 这里只确认"在、大小对"；哈希由主项目在复制回去时重算，不为一次刷新去读大文件。
        /// </summary>
        private static List<RecoverableFile> FindRecoverable(
            List<PackageArtifact> missing, List<ManagedDeploymentFile> deployed, Func<string, FileProbe> probeGame)
        {
            var result = new List<RecoverableFile>();
            if (missing.Count == 0 || deployed.Count == 0) return result;

            foreach (var artifact in missing)
            {
                if (string.IsNullOrWhiteSpace(artifact.FileHash) || artifact.FileSize < 0) continue;

                var copy = deployed.FirstOrDefault(f =>
                {
                    if (f.FileSize != artifact.FileSize
                        || !string.Equals(f.FileHash, artifact.FileHash, StringComparison.OrdinalIgnoreCase))
                        return false;
                    var probe = probeGame(f.TargetPath);
                    return probe.State == FileProbeState.Present && probe.Size == artifact.FileSize;
                });
                if (copy != null)
                    result.Add(new RecoverableFile(
                        artifact.RelativeSourcePath, copy.TargetPath, artifact.FileSize, artifact.FileHash));
            }
            return result;
        }
    }
}
