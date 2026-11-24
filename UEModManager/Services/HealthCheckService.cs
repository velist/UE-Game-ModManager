using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Linq;
using UEModManager.Health;
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

        public HealthCheckService(
            ILogger<HealthCheckService> logger,
            GameConfigService gameConfig,
            ProfileService profileService,
            PackageRepository packageRepo,
            ResolvedViewBuilder? viewBuilder = null,
            DeploymentStateStore? stateStore = null)
        {
            _logger = logger;
            _gameConfig = gameConfig;
            _profileService = profileService;
            _packageRepo = packageRepo;
            _viewBuilder = viewBuilder;
            _stateStore = stateStore;
        }

        /// <summary>体检项名称：已装文件是否都在游戏会读取的位置。</summary>
        public const string InstallPlacementCheckName = "InstallPlacement";

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
                    $"{report.Drift.Misplaced.Count} 个文件装在了游戏不会读取的位置（{string.Join("、", report.Drift.PackageKeys)}），重新部署即可修复",
                    report.Drift.Misplaced[0]);
            }
            if (report.Issues.Count > 0)
            {
                return new(InstallPlacementCheckName, HealthStatus.Warning,
                    $"{report.Issues.Count} 个包装不到游戏会读取的位置，没有部署",
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

            var report = new HealthReport { Checks = checks };

            _logger.LogInformation(
                "[Health] Overall={Status} Ok={Ok} Warn={Warn} Err={Err}",
                report.OverallStatus, report.OkCount, report.WarningCount, report.ErrorCount);

            return report;
        }

        // ─── 单项检查 ───

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
                    "MOD 路径指向了脚本加载器的目录，pak 类 MOD 装在这里不会被游戏加载；请在设置里重新自动检测 MOD 路径", path);
            return new("ModPath", HealthStatus.Ok, "OK", path);
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
