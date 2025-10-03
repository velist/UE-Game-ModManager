using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UEModManager.Models;

namespace UEModManager.Data
{
    /// <summary>
    /// 本地SQLite数据库上下文
    /// </summary>
    public class LocalDbContext : DbContext
    {
        private readonly ILogger<LocalDbContext>? _logger;

        public LocalDbContext()
        {
        }

        public LocalDbContext(DbContextOptions<LocalDbContext> options, ILogger<LocalDbContext>? logger = null)
            : base(options)
        {
            _logger = logger;
        }

        public DbSet<LocalUser> Users { get; set; } = null!;
        public DbSet<UserPreferences> UserPreferences { get; set; } = null!;
        public DbSet<LocalModCache> ModCaches { get; set; } = null!;
        public DbSet<AppConfiguration> Configurations { get; set; } = null!;
        public DbSet<UserSession> UserSessions { get; set; } = null!;
        public DbSet<FailedLoginAttempt> FailedLoginAttempts { get; set; } = null!;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured)
            {
                var dbPath = GetDatabasePath();
                _logger?.LogInformation($"配置SQLite数据库路径: {dbPath}");
                
                optionsBuilder.UseSqlite($"Data Source={dbPath}")
                             .EnableSensitiveDataLogging(false)
                             .EnableDetailedErrors(true);
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // 配置LocalUser
            modelBuilder.Entity<LocalUser>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.Email).IsUnique();
                entity.Property(e => e.Email).IsRequired();
                entity.Property(e => e.PasswordHash).IsRequired();
            });

            // 配置UserPreferences
            modelBuilder.Entity<UserPreferences>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.UserId).IsUnique(); // 每个用户只有一个偏好设置
                entity.HasOne(e => e.User)
                      .WithMany()
                      .HasForeignKey(e => e.UserId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // 配置LocalModCache
            modelBuilder.Entity<LocalModCache>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.ModId);
                entity.HasIndex(e => new { e.GameName, e.ModName });
                entity.Property(e => e.ModId).IsRequired();
                entity.Property(e => e.ModName).IsRequired();
                entity.Property(e => e.GameName).IsRequired();
            });

            // 配置AppConfiguration
            modelBuilder.Entity<AppConfiguration>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.Key).IsUnique();
                entity.Property(e => e.Key).IsRequired();
            });

            // 配置UserSession
            modelBuilder.Entity<UserSession>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.SessionToken).IsUnique();
                entity.HasIndex(e => e.UserId);
                entity.HasOne(e => e.User)
                      .WithMany()
                      .HasForeignKey(e => e.UserId)
                      .OnDelete(DeleteBehavior.Cascade);
                entity.Property(e => e.SessionToken).IsRequired();
            });

            // 配置FailedLoginAttempt
            modelBuilder.Entity<FailedLoginAttempt>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.Email);
                entity.HasIndex(e => e.AttemptTime);
                entity.HasIndex(e => e.UserId);
                entity.HasOne(e => e.User)
                      .WithMany()
                      .HasForeignKey(e => e.UserId)
                      .OnDelete(DeleteBehavior.SetNull);
                entity.Property(e => e.Email).IsRequired();
            });

            // 预设配置数据
            SeedData(modelBuilder);
        }

        private void SeedData(ModelBuilder modelBuilder)
        {
            // 预设应用程序配置
            modelBuilder.Entity<AppConfiguration>().HasData(
                new AppConfiguration { Id = 1, Key = "AppVersion", Value = "1.7.37", Description = "应用程序版本" },
                new AppConfiguration { Id = 2, Key = "DatabaseVersion", Value = "1.0.0", Description = "数据库结构版本" },
                new AppConfiguration { Id = 3, Key = "FirstRun", Value = "true", Description = "是否首次运行" },
                new AppConfiguration { Id = 4, Key = "CloudSyncEnabled", Value = "false", Description = "云同步是否启用" },
                new AppConfiguration { Id = 5, Key = "LastCloudSyncTime", Value = "1970-01-01T00:00:00Z", Description = "最后云同步时间" }
            );
        }

        /// <summary>
        /// 获取数据库文件路径
        /// </summary>
        private static string GetDatabasePath()
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var appDataDir = Path.Combine(appDataPath, "UEModManager");
            
            // 确保目录存在
            Directory.CreateDirectory(appDataDir);
            
            return Path.Combine(appDataDir, "local.db");
        }

        /// <summary>
        /// 确保数据库已创建
        /// </summary>
        public async Task<bool> EnsureDatabaseCreatedAsync()
        {
            try
            {
                var created = await Database.EnsureCreatedAsync();
                if (created)
                {
                    _logger?.LogInformation("本地SQLite数据库已创建");
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "创建本地数据库失败");
                return false;
            }
        }

        /// <summary>
        /// 获取数据库信息
        /// </summary>
        public async Task<DatabaseInfo> GetDatabaseInfoAsync()
        {
            var dbPath = GetDatabasePath();
            var fileInfo = new FileInfo(dbPath);
            
            var info = new DatabaseInfo
            {
                Path = dbPath,
                Size = fileInfo.Exists ? fileInfo.Length : 0,
                Created = fileInfo.Exists ? fileInfo.CreationTime : DateTime.MinValue,
                LastModified = fileInfo.Exists ? fileInfo.LastWriteTime : DateTime.MinValue,
                UserCount = await Users.CountAsync(),
                ModCacheCount = await ModCaches.CountAsync(),
                ConfigCount = await Configurations.CountAsync()
            };

            return info;
        }
    }

    /// <summary>
    /// 数据库信息
    /// </summary>
    public class DatabaseInfo
    {
        public string Path { get; set; } = string.Empty;
        public long Size { get; set; }
        public DateTime Created { get; set; }
        public DateTime LastModified { get; set; }
        public int UserCount { get; set; }
        public int ModCacheCount { get; set; }
        public int ConfigCount { get; set; }
    }
}

