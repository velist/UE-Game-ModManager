using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using UEModManager.Services;
using UEModManager.Services.Reconciliation;

namespace UEModManager.ViewModels
{
    /// <summary>
    /// 「刷新」：别信记着的，去磁盘上看一眼，再按看到的把列表、方案和游戏目录对齐。
    ///
    /// <para>
    /// <see cref="RefreshFromRepositoryAsync"/> 只是照着包索引和方案重画列表，从不核对磁盘。
    /// 玩家在资源管理器里删掉 MOD 库里的文件夹，列表照样显示它；游戏目录里装好的 MOD 被手动删掉、
    /// 或被游戏更新 / 重装清掉，列表照样显示"已启用"。这里先核对（<see cref="DiskReconcileService"/>），
    /// 每一种情形给玩家一个能直接解决的选择，最后再重画列表。
    /// </para>
    ///
    /// <para><b>四条不变式</b></para>
    /// <list type="number">
    /// <item><b>看不清的时候什么都不动。</b>MOD 库或游戏目录访问不到、已安装文件的记录读不出来，
    /// 都只说明看不清的是什么，不提任何移除、找回、装回去——移动硬盘没插好不等于 MOD 被删了。</item>
    /// <item><b>还存在的文件默认不删。</b>MOD 库里的没了、游戏目录里那份还在时，先问要不要找回；
    /// 移除记录默认把游戏里的文件原样留下（先移出部署清单、再删记录，顺序不能反）。</item>
    /// <item><b>玩家关掉对话框 = 什么都不改。</b></item>
    /// <item><b>动手前再看一眼。</b>对话框开着的时候文件可能已经回来了（从回收站还原、硬盘重新连上），
    /// 移除之前重新核对，只动仍然有问题的那些。</item>
    /// </list>
    /// </summary>
    public partial class MainViewModel
    {
        /// <summary>磁盘核对服务。没有它的宿主（部分测试）里，刷新退化成只重画列表。</summary>
        private readonly DiskReconcileService? _diskReconcile;

        /// <summary>「刷新」正在进行。界面据此把按钮置灰；也是防重入的闸。</summary>
        [ObservableProperty]
        private bool _isRefreshingFromDisk;

        /// <summary>一次刷新过程中攒下来的结果。</summary>
        private sealed class DiskRefreshWork
        {
            public List<string> Removed { get; } = [];
            public List<string> Recovered { get; } = [];
            public List<string> Reinstalled { get; } = [];
            public List<string> Disabled { get; } = [];
            public List<string> Forgotten { get; } = [];
            public List<string> Failures { get; } = [];

            /// <summary>
            /// 留在原处没处理的东西里，会挡住一切部署的那些（显示名）：
            /// 缺源文件却还启用着的 MOD、方案里找不到记录的启用项。
            /// </summary>
            public List<string> Blockers { get; } = [];

            /// <summary>方案或记录变了，游戏目录要跟着整理一次（撤掉不该在的、装回该在的）。</summary>
            public bool NeedsRedeploy { get; set; }

            /// <summary>整理成功后算作"重新装好"的 MOD。</summary>
            public List<string> ReinstallRequested { get; } = [];

            public bool AnythingDone
                => Removed.Count + Recovered.Count + Reinstalled.Count + Disabled.Count + Forgotten.Count > 0;

            /// <summary>这些问题留着没动：其中还启用着的会挡住部署。</summary>
            public void LeaveInPlace(IEnumerable<RepositoryFinding> findings)
                => Blockers.AddRange(findings.Where(f => f.BlocksDeployment).Select(f => f.DisplayName));
        }

        /// <summary>
        /// 重新核对磁盘并按结果处理，最后重画列表。
        /// 同一时刻只跑一次：上一次还没结束、或别的操作正在进行时直接返回 <see cref="DiskRefreshResult.Skipped"/>。
        /// </summary>
        public async Task<DiskRefreshResult> RefreshFromDiskAsync(IDiskRefreshPrompts prompts)
        {
            ArgumentNullException.ThrowIfNull(prompts);

            // 别的操作（导入、部署）进行到一半时不核对：那时磁盘和记录本来就对不上
            if (IsRefreshingFromDisk || IsLoading) return new DiskRefreshResult { Skipped = true };

            IsRefreshingFromDisk = true;
            var work = new DiskRefreshWork();
            DiskReconcileReport? report = null;
            try
            {
                if (_diskReconcile != null && !string.IsNullOrEmpty(_gameConfig.CurrentGameName))
                {
                    var check = await CheckDiskAsync(reloadIndex: true);
                    report = check.Report;

                    if (report.CannotSeeClearly)
                    {
                        // 看不清：只说明看不清的是什么，其余一概不动
                        if (report.RepositoryRootUnreachable)
                            prompts.Notify(DiskRefreshText.RepositoryUnreachable(check.RepositoryRoot));
                        if (report.GameRootUnreachable)
                            prompts.Notify(DiskRefreshText.GameDirectoryUnreachable(check.GamePath));
                        if (report.DeploymentRecordUnknown)
                            prompts.Notify(DiskRefreshText.DeploymentRecordUnknown());
                    }
                    else if (report.UnknownEnabledKeys.Count > 0 && report.RegisteredPackageCount == 0)
                    {
                        // 方案里有启用的 MOD，索引里却一个包都没有：是索引没读出来，不是它们被一个个删了。
                        // 这时把方案里的引用清掉，等于替一次读盘失败把玩家的方案抹空。
                        prompts.Notify(DiskRefreshText.PackageRecordsUnreadable(report.UnknownEnabledKeys.Count));
                    }
                    else
                    {
                        await HandleRepositoryFindingsAsync(check, prompts, work);
                        await HandleUnknownProfileEntriesAsync(check, prompts, work);

                        // 前面动过之后重新核对：移除的不用再问，找回的现在可以装回去了
                        if (work.AnythingDone) check = await CheckDiskAsync(reloadIndex: false);

                        await HandleDeploymentFindingsAsync(check, prompts, work);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Reconcile] 刷新失败");
                work.Failures.Add(DiskRefreshText.RefreshFailed(ex.Message));
            }
            finally
            {
                // 无论处理到哪一步，列表都按现状重画一次（冲突角标、分类计数随之更新；
                // 主窗口订阅了 RepositoryRefreshed，左栏计数和加载器提示条也跟着刷新）
                await RefreshFromRepositoryAsync();
                IsRefreshingFromDisk = false;
            }

            if (work.Failures.Count > 0)
                prompts.Notify(DiskRefreshText.Failed(work.Failures));

            var done = DiskRefreshText.Done(
                work.Removed.Count, work.Recovered.Count, work.Reinstalled.Count,
                work.Disabled.Count, work.Forgotten.Count);
            if (done != null)
                prompts.Notify(done);
            else if (work.Failures.Count == 0 && report?.HasFindings != true)
                prompts.Notify(DiskRefreshText.NoProblems());

            return new DiskRefreshResult
            {
                Report = report,
                Removed = work.Removed,
                Recovered = work.Recovered,
                Reinstalled = work.Reinstalled,
                Disabled = work.Disabled,
                Forgotten = work.Forgotten,
                Failures = work.Failures,
            };
        }

        private async Task<DiskCheck> CheckDiskAsync(bool reloadIndex)
        {
            using var loading = BeginLoading(DiskRefreshText.Checking);
            if (reloadIndex) await _packageRepository.ReloadIndexAsync();
            return await _diskReconcile!.CheckAsync();
        }

        // ─── 仓库侧：MOD 库里的文件不见了 ───

        private async Task HandleRepositoryFindingsAsync(DiskCheck check, IDiskRefreshPrompts prompts, DiskRefreshWork work)
        {
            var report = check.Report;
            var lost = report.LostPackages.ToList();
            var partial = report.PartialPackages.ToList();
            if (lost.Count == 0 && partial.Count == 0) return;

            // 一个都找不到：先当成位置问题。玩家确认"确实是自己删的"才往下走。
            var removalConfirmed = false;
            if (report.SuspectedRepositoryLocationProblem)
            {
                if (prompts.Ask(DiskRefreshText.EverythingMissing(check.RepositoryRoot, lost)) != DiskRefreshAnswer.Primary)
                {
                    work.LeaveInPlace(lost);
                    return;
                }
                removalConfirmed = true;
            }

            // 游戏目录里它的文件有探测不了的：不知道游戏里还有没有它，这次先放着，不能按"游戏里没有"处理
            var deferred = lost.Concat(partial).Where(f => f.DeployedFilesUnknown).ToList();
            work.LeaveInPlace(deferred);
            lost = lost.Except(deferred).ToList();
            partial = partial.Except(deferred).ToList();

            // ① 游戏目录里还装着完整的一份：先问要不要找回
            var recoverable = lost.Concat(partial).Where(f => f.CanRecoverEverything).ToList();
            if (recoverable.Count > 0
                && prompts.Ask(DiskRefreshText.RecoverFromGame(recoverable)) == DiskRefreshAnswer.Primary)
            {
                var recovered = await RecoverFromGameAsync(recoverable, work);
                lost = lost.Where(f => !recovered.Contains(f.PackageKey)).ToList();
                partial = partial.Where(f => !recovered.Contains(f.PackageKey)).ToList();
            }

            // ② 整包丢失、游戏目录里也没有它的文件：移除只是清掉记录，没有任何文件可删
            var gone = lost.Where(f => !f.HasDeployedFiles).ToList();
            if (gone.Count > 0)
            {
                if (removalConfirmed || prompts.Ask(DiskRefreshText.RemoveLost(gone)) == DiskRefreshAnswer.Primary)
                    await RemovePackageRecordsAsync(gone, keepGameFiles: false, allowPartial: false, work);
                else
                    work.LeaveInPlace(gone);
            }

            // ③ 整包丢失、游戏目录里还留着它的文件：默认把文件留下
            var installed = lost.Where(f => f.HasDeployedFiles).ToList();
            if (installed.Count > 0)
            {
                switch (prompts.Ask(DiskRefreshText.RemoveStillInstalled(installed)))
                {
                    case DiskRefreshAnswer.Primary:
                        await RemovePackageRecordsAsync(installed, keepGameFiles: true, allowPartial: false, work);
                        break;
                    case DiskRefreshAnswer.Secondary:
                        await RemovePackageRecordsAsync(installed, keepGameFiles: false, allowPartial: false, work);
                        break;
                    default:
                        work.LeaveInPlace(installed);
                        break;
                }
            }

            // ④ 缺了一部分：不自动删。玩家选了移除才删，默认留着。
            if (partial.Count > 0)
            {
                if (prompts.Ask(DiskRefreshText.RemoveIncomplete(partial)) == DiskRefreshAnswer.Primary)
                    await RemovePackageRecordsAsync(partial, keepGameFiles: false, allowPartial: true, work);
                else
                    work.LeaveInPlace(partial);
            }
        }

        private async Task<HashSet<string>> RecoverFromGameAsync(
            IReadOnlyList<RepositoryFinding> recoverable, DiskRefreshWork work)
        {
            var recovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var loading = BeginLoading(DiskRefreshText.Recovering);

            foreach (var finding in recoverable)
            {
                // 要重算哈希、复制文件，放到后台线程
                var result = await Task.Run(() => _diskReconcile!.RecoverFromGameAsync(finding));
                if (result.Success)
                {
                    recovered.Add(finding.PackageKey);
                    work.Recovered.Add(finding.DisplayName);
                }
                else
                {
                    work.Failures.Add(DiskRefreshText.RecoverFailed(finding.DisplayName, result.Error));
                }
            }
            return recovered;
        }

        /// <summary>
        /// 把这些包从列表里移除：所有方案里的引用、仓库记录（连同包目录里剩下的东西）。
        ///
        /// <para>
        /// 与逐个删除（<see cref="DeletePackageModAsync"/>）做的是同样三件事，只是顺序不同。
        /// 逐个删除先撤游戏目录里的文件、再删记录，每删一个就重新规划一次部署——而规划要先构建最终视图，
        /// 别的已启用 MOD 只要还有一个缺源文件就会失败，于是两个 MOD 同时丢失时哪个都删不掉。
        /// 这里先处理记录，游戏目录留到最后统一整理一次（<see cref="DiskRefreshWork.NeedsRedeploy"/>）：
        /// 记录没了之后，部署清单里它名下的文件不再被任何方案需要，整理时自然会被撤掉。
        /// </para>
        ///
        /// <para><b>三处顺序都不能反：</b></para>
        /// <list type="number">
        /// <item><b>动手前重新核对。</b>提问到回答之间文件可能已经回来了；只移除此刻仍然有问题的包，
        /// 否则会把刚还原回来的文件夹连同记录一起删掉。</item>
        /// <item><b><paramref name="keepGameFiles"/> 时先把它名下的文件移出部署清单，成功了才往下走。</b>
        /// 清单还认这些文件的话，下一次部署就会把它们当成"该撤掉的"删掉——那正是玩家仅剩的一份。</item>
        /// <item><b>先清方案里的引用（立刻落盘），再删索引。</b>反过来的话，中途被杀或写盘失败会留下
        /// "索引里没有、方案里还启用着"的引用，此后所有部署都抛"方案引用的包不存在"，
        /// 而列表里已经找不到这个 MOD 可以去禁用。先清引用，最坏只是索引里多一个没人引用的包，下次刷新还能再处理。</item>
        /// </list>
        /// </summary>
        /// <param name="allowPartial">玩家同意移除的是"缺了一部分"的包：此刻仍然只缺一部分也照删。</param>
        private async Task RemovePackageRecordsAsync(
            IReadOnlyList<RepositoryFinding> findings, bool keepGameFiles, bool allowPartial, DiskRefreshWork work)
        {
            using var loading = BeginLoading(DiskRefreshText.Removing);

            var current = (await _diskReconcile!.CheckAsync()).Report;
            var stillBroken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!current.CannotSeeClearly)
            {
                stillBroken.UnionWith(current.LostPackages.Select(f => f.PackageKey));
                if (allowPartial) stillBroken.UnionWith(current.PartialPackages.Select(f => f.PackageKey));
            }

            var targets = findings.Where(f => stillBroken.Contains(f.PackageKey)).ToList();
            foreach (var skipped in findings.Except(targets))
                _logger.LogInformation("[Reconcile] {Key} 的文件在确认期间有了变化，这次不移除", skipped.PackageKey);
            // 现在看不清了（硬盘刚好断开）的留着不动；文件回来了的已经不是问题
            if (current.CannotSeeClearly) work.LeaveInPlace(findings);
            if (targets.Count == 0) return;

            if (keepGameFiles)
            {
                try
                {
                    // 读清单要逐个文件校验路径，放到后台线程
                    var keys = targets.Select(f => f.PackageKey).ToList();
                    await Task.Run(() => _diskReconcile!.ReleaseDeployedFilesAsync(keys));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Reconcile] 把文件移出部署清单失败，取消移除");
                    foreach (var finding in targets)
                        work.Failures.Add(DiskRefreshText.RemoveFailed(finding.DisplayName, ex.Message));
                    work.LeaveInPlace(targets);
                    return;
                }
            }

            foreach (var finding in targets)
            {
                var referencesCleared = false;
                try
                {
                    // 删包目录是同步的磁盘操作（缺了一部分的包里可能还剩着大文件），放到后台线程。
                    // 方案变更事件的订阅方自己会切回界面线程，这里不用管。
                    await Task.Run(async () =>
                    {
                        await _profileService.RemovePackageReferencesAsync(finding.PackageKey);
                        referencesCleared = true;
                        // 包已经不在索引里时返回 false：那就没有别的要删了
                        await _packageRepository.DeletePackageAsync(
                            finding.PackageKey, _profileService.GetProfiles(), force: true);
                    });

                    work.Removed.Add(finding.DisplayName);
                    if (!keepGameFiles && finding.ManagedFileCount > 0) work.NeedsRedeploy = true;
                    _logger.LogInformation("[Reconcile] 已从列表移除 {Key}（保留游戏里的文件={Keep}）",
                        finding.PackageKey, keepGameFiles);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Reconcile] 移除 {Key} 失败", finding.PackageKey);
                    work.Failures.Add(DiskRefreshText.RemoveFailed(finding.DisplayName, ex.Message));
                    // 引用还在的话它仍然启用着、仍然挡路；引用已经清掉就只是索引里多一条记录
                    if (!referencesCleared) work.LeaveInPlace([finding]);
                    else if (!keepGameFiles && finding.ManagedFileCount > 0) work.NeedsRedeploy = true;
                }
            }
        }

        // ─── 方案里启用着、索引里却没有记录 ───

        /// <summary>
        /// 当前方案里启用着、包索引里却没有记录的包。
        ///
        /// <para>
        /// 这种引用会让最终视图建不出来（"方案引用的包不存在"），启用、禁用、启动全部失败，
        /// 而列表是照着索引画的，玩家在界面上根本看不到它、也就无从禁用。
        /// 重读索引之后尤其可能出现（索引被别的实例或手工恢复改过，方案没跟着变）。
        /// </para>
        ///
        /// <para>
        /// 去掉引用之前先把它名下的文件移出部署清单：记录都没了，不知道这些文件该不该删，
        /// 默认留下。走到这里时索引里至少还有一个包——索引整个读不出来的情形在上一层就拦下了。
        /// </para>
        /// </summary>
        private async Task HandleUnknownProfileEntriesAsync(DiskCheck check, IDiskRefreshPrompts prompts, DiskRefreshWork work)
        {
            var unknown = check.Report.UnknownEnabledKeys;
            if (unknown.Count == 0) return;

            if (prompts.Ask(DiskRefreshText.ForgetUnknownEntries(unknown)) != DiskRefreshAnswer.Primary)
            {
                work.Blockers.AddRange(unknown);
                return;
            }

            using var loading = BeginLoading(DiskRefreshText.Removing);
            try
            {
                await Task.Run(async () =>
                {
                    await _diskReconcile!.ReleaseDeployedFilesAsync(unknown);
                    foreach (var key in unknown)
                        await _profileService.RemovePackageReferencesAsync(key);
                });
                work.Forgotten.AddRange(unknown);
                _logger.LogInformation("[Reconcile] 已从方案里去掉找不到记录的包: {Keys}", string.Join(", ", unknown));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Reconcile] 清理方案里找不到记录的包失败");
                work.Failures.Add(DiskRefreshText.RefreshFailed(ex.Message));
                work.Blockers.AddRange(unknown);
            }
        }

        // ─── 游戏侧：装好的文件不见了 ───

        private async Task HandleDeploymentFindingsAsync(DiskCheck check, IDiskRefreshPrompts prompts, DiskRefreshWork work)
        {
            var missing = check.Report.RestorableDeployments;

            // 还有挡路的留在原处：最终视图建不出来，往游戏目录里装什么、撤什么都做不了
            if (work.Blockers.Count > 0)
            {
                if (work.NeedsRedeploy || missing.Count > 0)
                    prompts.Notify(DiskRefreshText.StillBlocked(work.Blockers));
                return;
            }

            if (missing.Count > 0)
            {
                if (work.NeedsRedeploy)
                {
                    // 前面的选择已经要整理一次游戏目录了，而整理就是按方案对齐——缺的文件会一并装回去。
                    // 这时再问"要不要装回去"，玩家选了"先不处理"也拦不住；所以不问，装完如实说。
                    work.ReinstallRequested.AddRange(missing.Select(m => m.DisplayName));
                }
                else
                {
                    switch (prompts.Ask(DiskRefreshText.MissingDeployments(missing)))
                    {
                        case DiskRefreshAnswer.Primary:
                            work.ReinstallRequested.AddRange(missing.Select(m => m.DisplayName));
                            work.NeedsRedeploy = true;
                            break;

                        case DiskRefreshAnswer.Secondary:
                            await using (await _profileService.BeginBatchAsync())
                            {
                                foreach (var finding in missing)
                                {
                                    await _profileService.SetPackageEnabledFlagAsync(finding.PackageKey, false);
                                    work.Disabled.Add(finding.DisplayName);
                                }
                            }
                            work.NeedsRedeploy = true;
                            break;
                    }
                }
            }

            if (!work.NeedsRedeploy) return;

            // 一次整理：缺的装回去，不再启用的撤掉，清单跟着对齐
            var result = await RedeployCurrentProfileAsync(DiskRefreshText.Tidying);
            if (result.Success)
                work.Reinstalled.AddRange(work.ReinstallRequested);
            else if (!result.IsCancelled)
                work.Failures.Add(DiskRefreshText.TidyFailed(result.Error));
        }
    }
}
