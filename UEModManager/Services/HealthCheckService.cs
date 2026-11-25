using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Linq;
using UEModManager.Health;
using UEModManager.Localization;
using UEModManager.Services.InstallLayout;

namespace UEModManager.Services
{
    /// <summary>
    /// 健康检查服务（IO 适配器）。
    ///
    /// 收集运行环境关键状态：
    /// - 当前游戏路径是否存在
    /// - 包仓库目录是否可访问
    /// - 当前 Profile 是否设置
    /// - 备份目录是否可写
    /// - SQLite 数据库文件是否存在
    ///
    /// 纯渲染逻辑下沉到 Core 的 <see cref="HealthReport"/> / <see cref="HealthReportFormatter"/>。
    /// </summary>
    public class HealthCheckService
    {
        private readonly ILogger<HealthCheckService> _logger;
        private readonly GameConfigService _gameConfig;
        private readonly ProfileService _profileService;
        private readonly PackageRepository _packageRepo;
        private readonly ResolvedViewBuilder? _viewBuilder;
        private readonly DeploymentStateStore? _stateStore;
        private readonly Ue4ssService? _ue4ss;

        public HealthCheckService(
            ILogger<HealthCheckService> logger,
            GameConfigService gameConfig,
            ProfileService profileService,
            PackageRepository packageRepo,
            ResolvedViewBuilder? viewBuilder = null,
            DeploymentStateStore? stateStore = null,
            Ue4ssService? ue4ss = null)
        {
            _ue4ss = ue4ss;
            _logger = logger;
            _gameConfig = gameConfig;
            _profileService = profileService;
            _packageRepo = packageRepo;
            _viewBuilder = viewBuilder;
            _stateStore = stateStore;
        }

        /// <summary>体检项名称：已装文件是否都在游戏会读取的位置。</summary>
        public const string InstallPlacementCheckName = "InstallPlacement";

        /// <summary>体检项名称：UE 游戏的脚本 MOD 加载器 UE4SS 是否就绪。</summary>
        public const string Ue4ssCheckName = "UE4SS";

        /// <summary>
        /// 检查「已装的 MOD 是否都在游戏会读取的位置」。
        ///
        /// 以前的体检只看几个目录存不存在，于是插件全装进了游戏不读的目录也照样全绿。
        /// 这里比对两样东西：管理器记着自己装了哪些文件、按现行规则它们该在哪。
        /// 对不上的（<see cref="InstallPlacementReport.Drift"/>）重新部署一次就能挪到位，
        /// 界面据此给出一键修复；装不到有效位置的包（<see cref="InstallPlacementReport.Issues"/>）说明原因。
        /// </summary>
        public async Task<InstallPlacementReport> CheckInstallPlacementAsync()
        {
            var empty = new InstallPlacementReport(new DeploymentDrift([], [], []), []);
            if (_viewBuilder == null || _stateStore == null) return empty;
            try
            {
                var snapshot = _viewBuilder.DescribePlacement();
                var state = await _stateStore.ReadAsync(
                    _gameConfig.CurrentGameName, _gameConfig.CurrentGamePath, _gameConfig.CurrentModPath);
                var drift = DeploymentDriftDetector.Detect(
                    state.Files.Select(f => new OwnedTarget(f.TargetPath, f.PackageKey)), snapshot.ExpectedTargets);
                return new InstallPlacementReport(drift, snapshot.Issues);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Health] 安装位置检查失败");
                return empty;
            }
        }

        private async Task<HealthCheck> CheckInstallPlacementItemAsync()
        {
            var report = await CheckInstallPlacementAsync();
            if (report.Drift.HasDrift)
            {
                return new(InstallPlacementCheckName, HealthStatus.Warning,
                    UiText.Interpolate($"{report.Drift.Misplaced.Count} 个文件装在了游戏不会读取的位置（{string.Join(UiText.Get("、"), report.Drift.PackageKeys)}），重新部署即可修复"),
                    report.Drift.Misplaced[0]);
            }
            if (report.Issues.Count > 0)
            {
                return new(InstallPlacementCheckName, HealthStatus.Warning,
                    UiText.Interpolate($"{report.Issues.Count} 个包装不到游戏会读取的位置，没有部署"),
                    string.Join("; ", report.Issues.Select(i => $"{i.PackageKey}: {i.Kind}")));
            }
            return new(InstallPlacementCheckName, HealthStatus.Ok, "OK", _gameConfig.CurrentPluginRootPath);
        }

        /// <summary>
        /// 收集所有检查项并生成报告。
        /// </summary>
        public async Task<HealthReport> CheckAsync()
        {
            var checks = new List<HealthCheck>
            {
                CheckCurrentGamePath(),
                CheckModPath(),
                CheckBackupPath(),
                CheckObjectStoreRoot(),
                CheckCurrentProfile(),
                CheckSqliteDb(),
                CheckBackupsDirectoryWritable(),
                await CheckInstallPlacementItemAsync(),
            };

            if (CheckUe4ss() is { } ue4ss) checks.Add(ue4ss);

            if (await CheckModFilesAsync() is { } modFiles) checks.Add(modFiles);

            var report = new HealthReport { Checks = checks };

            _logger.LogInformation(
                "[Health] Overall={Status} Ok={Ok} Warn={Warn} Err={Err}",
                report.OverallStatus, report.OkCount, report.WarningCount, report.ErrorCount);

            return report;
        }

        // ─── 单项检查 ───

        /// <summary>体检项名称：记着的 MOD 文件在磁盘上是不是都还在。</summary>
        public const string ModFilesCheckName = "ModFiles";

        /// <summary>
        /// 记着的 MOD 文件在磁盘上是不是都还在：MOD 库里的源文件、游戏目录里装好的文件。
        ///
        /// <para>
        /// 以前的体检只看几个目录在不在：MOD 路径不存在时只写一句"部署时会创建"，
        /// 哪怕方案里明明有启用的包——到底是从没装进去、还是装过又被删了，报告里看不出来。
        /// 这一项把它写明白，排查「MOD 没加载」时不用再猜。
        /// </para>
        ///
        /// <para>
        /// 与「刷新」用的是同一个核对（<see cref="DiskReconcileService"/>），只读、不改任何东西。
        /// 没有部署清单可读的宿主（部分测试）里不出这一项。
        /// </para>
        /// </summary>
        private async Task<HealthCheck?> CheckModFilesAsync()
        {
            if (_stateStore == null) return null;
            try
            {
                var check = await new DiskReconcileService(_logger, _packageRepo, _profileService, _gameConfig, _stateStore)
                    .CheckAsync();
                var report = check.Report;
                var problems = new List<string>();
                string? detail = null;

                if (report.RepositoryRootUnreachable)
                {
                    problems.Add(UiText.Get("MOD 库的位置访问不到，没法核对 MOD 文件"));
                    detail ??= check.RepositoryRoot;
                }
                if (report.GameRootUnreachable)
                {
                    problems.Add(UiText.Get("游戏目录访问不到，没法核对已安装的 MOD 文件"));
                    detail ??= check.GamePath;
                }
                if (report.DeploymentRecordUnknown)
                    problems.Add(UiText.Get("已安装文件的记录读不出来，没法核对游戏目录里的 MOD 文件"));

                if (report.UnknownEnabledKeys.Count > 0)
                {
                    var keys = string.Join(UiText.Get("、"), report.UnknownEnabledKeys);
                    problems.Add(UiText.Interpolate($"方案里启用着 {report.UnknownEnabledKeys.Count} 个找不到记录的 MOD（{keys}），启用、禁用和启动都会因此失败"));
                }

                var broken = report.LostPackages.Concat(report.PartialPackages).ToList();
                if (broken.Count > 0)
                {
                    var names = string.Join(UiText.Get("、"), broken.Select(f => f.PackageKey));
                    problems.Add(UiText.Interpolate($"{broken.Count} 个 MOD 在 MOD 库里的文件丢失或不完整（{names}）"));
                    detail ??= broken[0].MissingFiles.FirstOrDefault();
                }

                var missing = report.MissingDeployments.Where(d => d.IsEnabled).ToList();
                if (missing.Count > 0)
                {
                    var names = string.Join(UiText.Get("、"), missing.Select(d => d.PackageKey));
                    problems.Add(UiText.Interpolate($"{missing.Count} 个已启用 MOD 的文件在游戏目录里丢失（{names}），它们现在在游戏里不会生效"));
                    detail ??= missing[0].MissingFiles.FirstOrDefault();
                }

                if (problems.Count > 0)
                {
                    problems.Add(UiText.Get("点 MOD 列表上方的「刷新」（F5）处理"));
                    return new(ModFilesCheckName, HealthStatus.Warning, string.Join(UiText.Get("；"), problems), detail);
                }

                return new(ModFilesCheckName, HealthStatus.Ok,
                    UiText.Interpolate($"{report.CheckedPackageCount} 个 MOD 的 {report.CheckedSourceFileCount} 个源文件、{report.CheckedManagedFileCount} 个已安装文件都在"));
            }
            catch (Exception ex)
            {
                // 体检只描述现状：这一项查不了不能连累其它项
                _logger.LogWarning(ex, "[Health] MOD 文件核对失败");
                return null;
            }
        }

        private HealthCheck CheckCurrentGamePath()
        {
            var path = _gameConfig.CurrentGamePath;
            if (string.IsNullOrWhiteSpace(path))
                return new("CurrentGamePath", HealthStatus.Warning, "未配置游戏路径");
            if (!Directory.Exists(path))
                return new("CurrentGamePath", HealthStatus.Error, $"路径不存在: {path}");
            return new("CurrentGamePath", HealthStatus.Ok, "OK", path);
        }

        private HealthCheck CheckModPath()
        {
            var path = _gameConfig.CurrentModPath;
            if (string.IsNullOrWhiteSpace(path))
                return new("ModPath", HealthStatus.Warning, "未配置 MOD 路径");
            if (!Directory.Exists(path))
                return new("ModPath", HealthStatus.Warning, $"路径不存在（部署时会创建）: {path}");
            // MOD 路径是给 pak 这类资源容器用的。指到加载器目录里（玩家以为这样能让脚本 MOD 生效）
            // 只会让 pak MOD 装进游戏不读的地方；脚本 MOD 的位置由管理器按规则决定，不看这个设置。
            var loaders = _gameConfig.CurrentInstallLayoutRules.LoaderDirectories;
            if (path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => loaders.Contains(segment, StringComparer.OrdinalIgnoreCase)))
                return new("ModPath", HealthStatus.Warning,
                    UiText.Get("MOD 路径指向了脚本加载器的目录，pak 类 MOD 装在这里不会被游戏加载；请在设置里重新自动检测 MOD 路径"), path);
            return new("ModPath", HealthStatus.Ok, "OK", path);
        }

        /// <summary>
        /// UE 游戏的脚本 MOD（Lua / C++）全靠 UE4SS 加载：没装的话位置放得再对也不生效，
        /// 玩家只看到「部署成功、游戏里没效果」。没有脚本 MOD 的玩家用不上它，不报警告。
        /// </summary>
        private HealthCheck? CheckUe4ss()
        {
            var detection = _ue4ss?.Detect();
            if (_ue4ss == null || detection == null) return null;

            var root = _gameConfig.CurrentPluginRootPath;
            var scriptMods = _ue4ss.EnabledScriptModNames();
            // 正式版线没有一键安装：告诉玩家去官方发布页下载、拖进来装（2.2.0 线这里指向提示条里的「一键安装」）
            const string fix = "从 UE4SS 官方发布页下载压缩包后拖进管理器即可安装；部分游戏需要 MOD 作者指定的 UE4SS 版本，以 MOD 说明为准";

            switch (detection.State)
            {
                case Loaders.Ue4ssInstallState.Installed:
                    return new(Ue4ssCheckName, HealthStatus.Ok,
                        UiText.Interpolate($"已安装，脚本 MOD 目录：{detection.ScriptModsDirectory}"), root);

                case Loaders.Ue4ssInstallState.Incomplete when detection.Problem == Loaders.Ue4ssProblem.MissingCore:
                    return new(Ue4ssCheckName, HealthStatus.Warning,
                        UiText.Get("UE4SS 安装不完整：找不到 UE4SS.dll（常见原因是被杀毒软件删除），脚本 MOD 不会生效，游戏启动时还可能弹出报错"),
                        UiText.Get(fix));

                case Loaders.Ue4ssInstallState.Incomplete:
                    return new(Ue4ssCheckName, HealthStatus.Warning,
                        UiText.Get("UE4SS 安装不完整：没有代理 DLL（如 dwmapi.dll），游戏启动时不会加载 UE4SS，脚本 MOD 不会生效"),
                        UiText.Get(fix));

                default:
                    if (scriptMods.Count == 0)
                        return new(Ue4ssCheckName, HealthStatus.Ok, UiText.Get("未安装（当前没有启用需要它的脚本 MOD）"), root);
                    return new(Ue4ssCheckName, HealthStatus.Warning,
                        UiText.Interpolate($"未安装 UE4SS：已启用的 {scriptMods.Count} 个脚本 MOD 不会生效"),
                        UiText.Get(fix));
            }
        }

        private HealthCheck CheckBackupPath()
        {
            var path = _gameConfig.CurrentBackupPath;
            if (string.IsNullOrWhiteSpace(path))
                return new("BackupPath", HealthStatus.Warning, "未配置备份路径");
            return new("BackupPath", HealthStatus.Ok, "OK", path);
        }

        private HealthCheck CheckObjectStoreRoot()
        {
            try
            {
                var root = _packageRepo.Store.RepositoryRoot;
                if (!Directory.Exists(root))
                    return new("ObjectStore", HealthStatus.Warning, $"仓库目录将延迟创建: {root}");
                return new("ObjectStore", HealthStatus.Ok, "OK", root);
            }
            catch (Exception ex)
            {
                return new("ObjectStore", HealthStatus.Error, "无法访问仓库", ex.Message);
            }
        }

        private HealthCheck CheckCurrentProfile()
        {
            var profile = _profileService.CurrentProfile;
            if (profile == null)
                return new("CurrentProfile", HealthStatus.Warning, "未设置活跃 Profile");
            return new("CurrentProfile", HealthStatus.Ok,
                $"\"{profile.Name}\" ({profile.Packages.Count} 个包)");
        }

        private static HealthCheck CheckSqliteDb()
        {
            try
            {
                // 必须与 LocalDbContext 实际打开的是同一个文件，否则"检查通过"只是在
                // 报告另一个碰巧存在的库，等于没查。
                var dbPath = Infrastructure.AppPaths.LocalDatabaseFile;
                if (!File.Exists(dbPath))
                    return new("SqliteDb", HealthStatus.Warning, $"数据库文件不存在: {dbPath}");
                var size = new FileInfo(dbPath).Length;
                return new("SqliteDb", HealthStatus.Ok, $"{size / 1024.0:F1} KB", dbPath);
            }
            catch (Exception ex)
            {
                return new("SqliteDb", HealthStatus.Error, "数据库检查失败", ex.Message);
            }
        }

        private static HealthCheck CheckBackupsDirectoryWritable()
        {
            try
            {
                var backupsDir = Infrastructure.AppPaths.DeploymentBackupsDirectory;
                Directory.CreateDirectory(backupsDir);
                var probePath = Path.Combine(backupsDir, ".health_probe");
                File.WriteAllText(probePath, "ok");
                File.Delete(probePath);
                return new("BackupsWritable", HealthStatus.Ok, "OK", backupsDir);
            }
            catch (Exception ex)
            {
                return new("BackupsWritable", HealthStatus.Error, "无法写入备份目录", ex.Message);
            }
        }
    }

    /// <summary>安装位置检查的结果。</summary>
    /// <param name="Drift">装错位置、重新部署即可修复的文件。</param>
    /// <param name="Issues">装不到游戏会读取的位置的包。</param>
    public sealed record InstallPlacementReport(DeploymentDrift Drift, IReadOnlyList<PlacementIssue> Issues);
}
