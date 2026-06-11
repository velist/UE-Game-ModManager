using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
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
        private const string BaselineMigrationId = "20250825183311_AddUserAdminAndLockFields";
        private const string EfProductVersion = "8.0.0";

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
                new AppConfiguration { Id = 1, Key = "AppVersion", Value = "2.0.3-beta", Description = "应用程序版本" },
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
        /// 确保数据库已创建并应用迁移。
        /// </summary>
        public async Task<bool> EnsureDatabaseCreatedAsync()
        {
            try
            {
                await AdoptLegacyDatabaseIfNeededAsync();
                await Database.MigrateAsync();
                await EnsureLegacySchemaCompatibilityAsync();

                _logger?.LogInformation("本地SQLite数据库已初始化并应用迁移");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "初始化本地数据库失败");
                return false;
            }
        }

        private async Task AdoptLegacyDatabaseIfNeededAsync()
        {
            var connection = Database.GetDbConnection();
            var shouldClose = connection.State != ConnectionState.Open;
            if (shouldClose)
            {
                await connection.OpenAsync();
            }

            try
            {
                var hasUsersTable = await TableExistsAsync(connection, "Users");
                var hasHistoryTable = await TableExistsAsync(connection, "__EFMigrationsHistory");

                if (!hasUsersTable || hasHistoryTable)
                {
                    return;
                }

                await ExecuteNonQueryAsync(connection, """
                    CREATE TABLE "__EFMigrationsHistory" (
                        "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                        "ProductVersion" TEXT NOT NULL
                    );
                    """);
                await ExecuteNonQueryAsync(connection,
                    $"INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('{BaselineMigrationId}', '{EfProductVersion}');");

                _logger?.LogWarning("检测到无迁移历史的老本地数据库，已写入基线迁移记录: {MigrationId}", BaselineMigrationId);
            }
            finally
            {
                if (shouldClose)
                {
                    await connection.CloseAsync();
                }
            }
        }

        private async Task EnsureLegacySchemaCompatibilityAsync()
        {
            var connection = Database.GetDbConnection();
            var shouldClose = connection.State != ConnectionState.Open;
            if (shouldClose)
            {
                await connection.OpenAsync();
            }

            try
            {
                await EnsureLegacyTablesAsync(connection);
                await EnsureLegacyColumnsAsync(connection);
                await EnsureLegacyIndexesAsync(connection);
                await SeedConfigurationsIfMissingAsync(connection);
            }
            finally
            {
                if (shouldClose)
                {
                    await connection.CloseAsync();
                }
            }
        }

        private static async Task EnsureLegacyTablesAsync(DbConnection connection)
        {
            await ExecuteNonQueryAsync(connection, """
                CREATE TABLE IF NOT EXISTS "Configurations" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_Configurations" PRIMARY KEY AUTOINCREMENT,
                    "Key" TEXT NOT NULL,
                    "Value" TEXT NULL,
                    "Description" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL
                );
                """);

            await ExecuteNonQueryAsync(connection, """
                CREATE TABLE IF NOT EXISTS "ModCaches" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_ModCaches" PRIMARY KEY AUTOINCREMENT,
                    "ModId" TEXT NOT NULL,
                    "ModName" TEXT NOT NULL,
                    "Description" TEXT NULL,
                    "Version" TEXT NULL,
                    "Author" TEXT NULL,
                    "GameName" TEXT NOT NULL,
                    "LocalPath" TEXT NULL,
                    "DownloadUrl" TEXT NULL,
                    "FilePath" TEXT NULL,
                    "IsInstalled" INTEGER NOT NULL DEFAULT 0,
                    "IsEnabled" INTEGER NOT NULL DEFAULT 0,
                    "IsFavorite" INTEGER NOT NULL DEFAULT 0,
                    "FileSize" INTEGER NOT NULL DEFAULT 0,
                    "InstallDate" TEXT NOT NULL DEFAULT '0001-01-01T00:00:00',
                    "CacheTime" TEXT NOT NULL DEFAULT '0001-01-01T00:00:00',
                    "CachedAt" TEXT NOT NULL DEFAULT '0001-01-01T00:00:00',
                    "LastUpdated" TEXT NOT NULL DEFAULT '0001-01-01T00:00:00',
                    "DownloadCount" INTEGER NOT NULL DEFAULT 0,
                    "Rating" TEXT NOT NULL DEFAULT '0'
                );
                """);

            await ExecuteNonQueryAsync(connection, """
                CREATE TABLE IF NOT EXISTS "Users" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_Users" PRIMARY KEY AUTOINCREMENT,
                    "Email" TEXT NOT NULL,
                    "Username" TEXT NULL,
                    "PasswordHash" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "LastLoginAt" TEXT NOT NULL,
                    "IsActive" INTEGER NOT NULL DEFAULT 1,
                    "IsLocked" INTEGER NOT NULL DEFAULT 0,
                    "IsAdmin" INTEGER NOT NULL DEFAULT 0,
                    "Avatar" TEXT NULL,
                    "DisplayName" TEXT NULL
                );
                """);

            await ExecuteNonQueryAsync(connection, """
                CREATE TABLE IF NOT EXISTS "FailedLoginAttempts" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_FailedLoginAttempts" PRIMARY KEY AUTOINCREMENT,
                    "Email" TEXT NOT NULL,
                    "UserId" INTEGER NULL,
                    "AttemptTime" TEXT NOT NULL,
                    "IpAddress" TEXT NULL,
                    "UserAgent" TEXT NULL,
                    "FailureReason" TEXT NULL,
                    CONSTRAINT "FK_FailedLoginAttempts_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE SET NULL
                );
                """);

            await ExecuteNonQueryAsync(connection, """
                CREATE TABLE IF NOT EXISTS "UserPreferences" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_UserPreferences" PRIMARY KEY AUTOINCREMENT,
                    "UserId" INTEGER NOT NULL,
                    "DefaultGamePath" TEXT NULL,
                    "Language" TEXT NULL,
                    "Theme" TEXT NULL,
                    "AutoCheckUpdates" INTEGER NOT NULL DEFAULT 1,
                    "AutoBackup" INTEGER NOT NULL DEFAULT 1,
                    "ShowNotifications" INTEGER NOT NULL DEFAULT 1,
                    "MinimizeToTray" INTEGER NOT NULL DEFAULT 0,
                    "EnableCloudSync" INTEGER NOT NULL DEFAULT 0,
                    "LastSyncAt" TEXT NOT NULL DEFAULT '0001-01-01T00:00:00',
                    "UpdatedAt" TEXT NOT NULL DEFAULT '0001-01-01T00:00:00',
                    CONSTRAINT "FK_UserPreferences_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
                );
                """);

            await ExecuteNonQueryAsync(connection, """
                CREATE TABLE IF NOT EXISTS "UserSessions" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_UserSessions" PRIMARY KEY AUTOINCREMENT,
                    "UserId" INTEGER NOT NULL,
                    "SessionToken" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "ExpiresAt" TEXT NOT NULL,
                    "LastAccessAt" TEXT NULL,
                    "IsActive" INTEGER NOT NULL DEFAULT 1,
                    "DeviceInfo" TEXT NULL,
                    CONSTRAINT "FK_UserSessions_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
                );
                """);
        }

        private static async Task EnsureLegacyColumnsAsync(DbConnection connection)
        {
            var tableColumns = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Configurations"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Key"] = "TEXT NOT NULL DEFAULT ''",
                    ["Value"] = "TEXT NULL",
                    ["Description"] = "TEXT NULL",
                    ["CreatedAt"] = "TEXT NOT NULL DEFAULT '0001-01-01T00:00:00'",
                    ["UpdatedAt"] = "TEXT NOT NULL DEFAULT '0001-01-01T00:00:00'",
                },
                ["ModCaches"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ModId"] = "TEXT NOT NULL DEFAULT ''",
                    ["ModName"] = "TEXT NOT NULL DEFAULT ''",
                    ["Description"] = "TEXT NULL",
                    ["Version"] = "TEXT NULL",
                    ["Author"] = "TEXT NULL",
                    ["GameName"] = "TEXT NOT NULL DEFAULT ''",
                    ["LocalPath"] = "TEXT NULL",
                    ["DownloadUrl"] = "TEXT NULL",
                    ["FilePath"] = "TEXT NULL",
                    ["IsInstalled"] = "INTEGER NOT NULL DEFAULT 0",
                    ["IsEnabled"] = "INTEGER NOT NULL DEFAULT 0",
                    ["IsFavorite"] = "INTEGER NOT NULL DEFAULT 0",
                    ["FileSize"] = "INTEGER NOT NULL DEFAULT 0",
                    ["InstallDate"] = "TEXT NOT NULL DEFAULT '0001-01-01T00:00:00'",
                    ["CacheTime"] = "TEXT NOT NULL DEFAULT '0001-01-01T00:00:00'",
                    ["CachedAt"] = "TEXT NOT NULL DEFAULT '0001-01-01T00:00:00'",
                    ["LastUpdated"] = "TEXT NOT NULL DEFAULT '0001-01-01T00:00:00'",
                    ["DownloadCount"] = "INTEGER NOT NULL DEFAULT 0",
                    ["Rating"] = "TEXT NOT NULL DEFAULT '0'",
                },
                ["Users"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Email"] = "TEXT NOT NULL DEFAULT ''",
                    ["Username"] = "TEXT NULL",
                    ["PasswordHash"] = "TEXT NOT NULL DEFAULT ''",
                    ["CreatedAt"] = "TEXT NOT NULL DEFAULT '0001-01-01T00:00:00'",
                    ["LastLoginAt"] = "TEXT NOT NULL DEFAULT '0001-01-01T00:00:00'",
                    ["IsActive"] = "INTEGER NOT NULL DEFAULT 1",
                    ["IsLocked"] = "INTEGER NOT NULL DEFAULT 0",
                    ["IsAdmin"] = "INTEGER NOT NULL DEFAULT 0",
                    ["Avatar"] = "TEXT NULL",
                    ["DisplayName"] = "TEXT NULL",
                },
                ["FailedLoginAttempts"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Email"] = "TEXT NOT NULL DEFAULT ''",
                    ["UserId"] = "INTEGER NULL",
                    ["AttemptTime"] = "TEXT NOT NULL DEFAULT '0001-01-01T00:00:00'",
                    ["IpAddress"] = "TEXT NULL",
                    ["UserAgent"] = "TEXT NULL",
                    ["FailureReason"] = "TEXT NULL",
                },
                ["UserPreferences"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["UserId"] = "INTEGER NOT NULL DEFAULT 0",
                    ["DefaultGamePath"] = "TEXT NULL",
                    ["Language"] = "TEXT NULL",
                    ["Theme"] = "TEXT NULL",
                    ["AutoCheckUpdates"] = "INTEGER NOT NULL DEFAULT 1",
                    ["AutoBackup"] = "INTEGER NOT NULL DEFAULT 1",
                    ["ShowNotifications"] = "INTEGER NOT NULL DEFAULT 1",
                    ["MinimizeToTray"] = "INTEGER NOT NULL DEFAULT 0",
                    ["EnableCloudSync"] = "INTEGER NOT NULL DEFAULT 0",
                    ["LastSyncAt"] = "TEXT NOT NULL DEFAULT '0001-01-01T00:00:00'",
                    ["UpdatedAt"] = "TEXT NOT NULL DEFAULT '0001-01-01T00:00:00'",
                },
                ["UserSessions"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["UserId"] = "INTEGER NOT NULL DEFAULT 0",
                    ["SessionToken"] = "TEXT NOT NULL DEFAULT ''",
                    ["CreatedAt"] = "TEXT NOT NULL DEFAULT '0001-01-01T00:00:00'",
                    ["ExpiresAt"] = "TEXT NOT NULL DEFAULT '0001-01-01T00:00:00'",
                    ["LastAccessAt"] = "TEXT NULL",
                    ["IsActive"] = "INTEGER NOT NULL DEFAULT 1",
                    ["DeviceInfo"] = "TEXT NULL",
                },
            };

            foreach (var (table, columns) in tableColumns)
            {
                var existingColumns = await GetColumnNamesAsync(connection, table);
                foreach (var (column, definition) in columns)
                {
                    if (existingColumns.Contains(column))
                    {
                        continue;
                    }

                    await ExecuteNonQueryAsync(connection, $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition};");
                }
            }
        }

        private static async Task EnsureLegacyIndexesAsync(DbConnection connection)
        {
            var indexStatements = new[]
            {
                "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Configurations_Key\" ON \"Configurations\" (\"Key\");",
                "CREATE INDEX IF NOT EXISTS \"IX_ModCaches_ModId\" ON \"ModCaches\" (\"ModId\");",
                "CREATE INDEX IF NOT EXISTS \"IX_ModCaches_GameName_ModName\" ON \"ModCaches\" (\"GameName\", \"ModName\");",
                "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Users_Email\" ON \"Users\" (\"Email\");",
                "CREATE INDEX IF NOT EXISTS \"IX_FailedLoginAttempts_AttemptTime\" ON \"FailedLoginAttempts\" (\"AttemptTime\");",
                "CREATE INDEX IF NOT EXISTS \"IX_FailedLoginAttempts_Email\" ON \"FailedLoginAttempts\" (\"Email\");",
                "CREATE INDEX IF NOT EXISTS \"IX_FailedLoginAttempts_UserId\" ON \"FailedLoginAttempts\" (\"UserId\");",
                "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_UserPreferences_UserId\" ON \"UserPreferences\" (\"UserId\");",
                "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_UserSessions_SessionToken\" ON \"UserSessions\" (\"SessionToken\");",
                "CREATE INDEX IF NOT EXISTS \"IX_UserSessions_UserId\" ON \"UserSessions\" (\"UserId\");",
            };

            foreach (var statement in indexStatements)
            {
                await ExecuteNonQueryAsync(connection, statement);
            }
        }

        private static async Task SeedConfigurationsIfMissingAsync(DbConnection connection)
        {
            var now = DateTime.Now.ToString("O");
            var statements = new[]
            {
                $"INSERT OR IGNORE INTO \"Configurations\" (\"Id\", \"Key\", \"Value\", \"Description\", \"CreatedAt\", \"UpdatedAt\") VALUES (1, 'AppVersion', '2.0.3-beta', '应用程序版本', '{now}', '{now}');",
                $"INSERT OR IGNORE INTO \"Configurations\" (\"Id\", \"Key\", \"Value\", \"Description\", \"CreatedAt\", \"UpdatedAt\") VALUES (2, 'DatabaseVersion', '1.0.0', '数据库结构版本', '{now}', '{now}');",
                $"INSERT OR IGNORE INTO \"Configurations\" (\"Id\", \"Key\", \"Value\", \"Description\", \"CreatedAt\", \"UpdatedAt\") VALUES (3, 'FirstRun', 'true', '是否首次运行', '{now}', '{now}');",
                $"INSERT OR IGNORE INTO \"Configurations\" (\"Id\", \"Key\", \"Value\", \"Description\", \"CreatedAt\", \"UpdatedAt\") VALUES (4, 'CloudSyncEnabled', 'false', '云同步是否启用', '{now}', '{now}');",
                $"INSERT OR IGNORE INTO \"Configurations\" (\"Id\", \"Key\", \"Value\", \"Description\", \"CreatedAt\", \"UpdatedAt\") VALUES (5, 'LastCloudSyncTime', '1970-01-01T00:00:00Z', '最后云同步时间', '{now}', '{now}');",
            };

            foreach (var statement in statements)
            {
                await ExecuteNonQueryAsync(connection, statement);
            }
        }

        private static async Task<bool> TableExistsAsync(DbConnection connection, string tableName)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$name";
            parameter.Value = tableName;
            command.Parameters.Add(parameter);

            var result = await command.ExecuteScalarAsync();
            return Convert.ToInt64(result) > 0;
        }

        private static async Task<HashSet<string>> GetColumnNamesAsync(DbConnection connection, string tableName)
        {
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{tableName}\");";

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(1));
            }

            return columns;
        }

        private static async Task ExecuteNonQueryAsync(DbConnection connection, string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
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

