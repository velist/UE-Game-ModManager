using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Health;

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

        public HealthCheckService(
            ILogger<HealthCheckService> logger,
            GameConfigService gameConfig,
            ProfileService profileService,
            PackageRepository packageRepo)
        {
            _logger = logger;
            _gameConfig = gameConfig;
            _profileService = profileService;
            _packageRepo = packageRepo;
        }

        /// <summary>
        /// 收集所有检查项并生成报告。
        /// </summary>
        public Task<HealthReport> CheckAsync()
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
            };

            var report = new HealthReport { Checks = checks };

            _logger.LogInformation(
                "[Health] Overall={Status} Ok={Ok} Warn={Warn} Err={Err}",
                report.OverallStatus, report.OkCount, report.WarningCount, report.ErrorCount);

            return Task.FromResult(report);
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
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var dbPath = Path.Combine(appData, "UEModManager", "local.db");
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
                var backupsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Backups");
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
}
