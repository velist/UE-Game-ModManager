using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Text.Json;
using System.Collections.Generic;

namespace UEModManager.Services
{
    /// <summary>
    /// PostgreSQL数据库配置
    /// </summary>
    public class PostgreSQLConfig
    {
        public string Host { get; set; } = "localhost";
        public int Port { get; set; } = 5432;
        public string Database { get; set; } = "uemodmanager";
        public string Username { get; set; } = "postgres";
        public string Password { get; set; } = string.Empty;
        public bool UseSsl { get; set; } = false;
        public bool TrustServerCertificate { get; set; } = false;
        public bool IncludeErrorDetail { get; set; } = true;
        public int ConnectionTimeout { get; set; } = 30;
        public int CommandTimeout { get; set; } = 60;
        public DatabaseProvider Provider { get; set; } = DatabaseProvider.Local;
    }

    /// <summary>
    /// 数据库提供商枚举
    /// </summary>
    public enum DatabaseProvider
    {
        Local,          // 本地PostgreSQL
        ElephantSQL,    // ElephantSQL (免费层)
        Heroku,         // Heroku Postgres
        Supabase,       // Supabase PostgreSQL
        Neon,           // Neon (免费层)
        Railway,        // Railway (免费层)
        Custom          // 自定义
    }

    /// <summary>
    /// PostgreSQL云端认证服务
    /// </summary>
    public class PostgreSQLAuthService
    {
        private readonly PostgreSQLConfig _config;
        private readonly ILogger<PostgreSQLAuthService> _logger;
        private string _connectionString;

        // 免费PostgreSQL服务商的预设配置
        private static readonly Dictionary<DatabaseProvider, (string host, int port, bool ssl)> PresetConfigs = new()
        {
            [DatabaseProvider.ElephantSQL] = ("", 5432, true), // 需要用户填写具体的host
            [DatabaseProvider.Heroku] = ("", 5432, true),
            [DatabaseProvider.Supabase] = ("db.{project_id}.supabase.co", 5432, true),
            [DatabaseProvider.Neon] = ("", 5432, true),
            [DatabaseProvider.Railway] = ("", 5432, true),
            [DatabaseProvider.Local] = ("localhost", 5432, false)
        };

        public PostgreSQLAuthService(PostgreSQLConfig config, ILogger<PostgreSQLAuthService> logger)
        {
            _config = config;
            _logger = logger;
            _connectionString = BuildConnectionString();
        }

        /// <summary>
        /// 构建连接字符串
        /// </summary>
        private string BuildConnectionString()
        {
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = _config.Host,
                Port = _config.Port,
                Database = _config.Database,
                Username = _config.Username,
                Password = _config.Password,
                SslMode = _config.UseSsl ? SslMode.Require : SslMode.Prefer,
                TrustServerCertificate = _config.TrustServerCertificate,
                IncludeErrorDetail = _config.IncludeErrorDetail,
                Timeout = _config.ConnectionTimeout,
                CommandTimeout = _config.CommandTimeout,
                ApplicationName = "UEModManager",
                // 优化连接参数
                Pooling = true,
                MaxPoolSize = 20,
                MinPoolSize = 0
            };

            return builder.ToString();
        }

        /// <summary>
        /// 测试数据库连接
        /// </summary>
        public async Task<(bool success, string message)> TestConnectionAsync()
        {
            try
            {
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                
                // 执行简单查询测试
                using var command = new NpgsqlCommand("SELECT version();", connection);
                var version = await command.ExecuteScalarAsync();
                
                _logger.LogInformation("PostgreSQL连接测试成功");
                return (true, $"连接成功！PostgreSQL版本: {version}");
            }
            catch (NpgsqlException ex)
            {
                var errorMessage = ex.SqlState switch
                {
                    "28000" => "认证失败，请检查用户名和密码",
                    "3D000" => "数据库不存在",
                    "08006" => "连接失败，请检查主机和端口",
                    _ => $"PostgreSQL错误: {ex.Message}"
                };
                
                _logger.LogError(ex, "PostgreSQL连接测试失败");
                return (false, errorMessage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "数据库连接异常");
                return (false, $"连接异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 初始化数据库架构
        /// </summary>
        public async Task<bool> InitializeDatabaseAsync()
        {
            try
            {
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                // 创建用户表
                var createUsersTable = @"
                CREATE TABLE IF NOT EXISTS users (
                    id SERIAL PRIMARY KEY,
                    email VARCHAR(255) UNIQUE NOT NULL,
                    username VARCHAR(100),
                    display_name VARCHAR(100),
                    password_hash VARCHAR(255) NOT NULL,
                    salt VARCHAR(255) NOT NULL,
                    is_active BOOLEAN DEFAULT true,
                    is_verified BOOLEAN DEFAULT false,
                    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
                    updated_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
                    last_login_at TIMESTAMP WITH TIME ZONE
                );";

                // 创建用户首选项表
                var createPreferencesTable = @"
                CREATE TABLE IF NOT EXISTS user_preferences (
                    id SERIAL PRIMARY KEY,
                    user_id INTEGER REFERENCES users(id) ON DELETE CASCADE,
                    theme_mode VARCHAR(20) DEFAULT 'System',
                    language VARCHAR(10) DEFAULT 'zh-CN',
                    auto_start BOOLEAN DEFAULT false,
                    enable_notifications BOOLEAN DEFAULT true,
                    check_updates BOOLEAN DEFAULT true,
                    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
                    updated_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP
                );";

                // 创建MOD缓存表
                var createModCacheTable = @"
                CREATE TABLE IF NOT EXISTS mod_cache (
                    id SERIAL PRIMARY KEY,
                    user_id INTEGER REFERENCES users(id) ON DELETE CASCADE,
                    mod_name VARCHAR(255) NOT NULL,
                    mod_path TEXT NOT NULL,
                    game_type VARCHAR(50) NOT NULL,
                    is_enabled BOOLEAN DEFAULT true,
                    install_date TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
                    last_accessed TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
                    file_size BIGINT,
                    mod_version VARCHAR(50),
                    metadata JSONB
                );";

                // 创建用户会话表
                var createSessionsTable = @"
                CREATE TABLE IF NOT EXISTS user_sessions (
                    id SERIAL PRIMARY KEY,
                    user_id INTEGER REFERENCES users(id) ON DELETE CASCADE,
                    session_token VARCHAR(255) UNIQUE NOT NULL,
                    device_info TEXT,
                    ip_address INET,
                    expires_at TIMESTAMP WITH TIME ZONE NOT NULL,
                    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
                    last_activity TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP
                );";

                // 创建索引
                var createIndexes = @"
                CREATE INDEX IF NOT EXISTS idx_users_email ON users(email);
                CREATE INDEX IF NOT EXISTS idx_user_preferences_user_id ON user_preferences(user_id);
                CREATE INDEX IF NOT EXISTS idx_mod_cache_user_id ON mod_cache(user_id);
                CREATE INDEX IF NOT EXISTS idx_mod_cache_game_type ON mod_cache(game_type);
                CREATE INDEX IF NOT EXISTS idx_sessions_user_id ON user_sessions(user_id);
                CREATE INDEX IF NOT EXISTS idx_sessions_token ON user_sessions(session_token);
                CREATE INDEX IF NOT EXISTS idx_sessions_expires ON user_sessions(expires_at);";

                using var command = connection.CreateCommand();
                
                command.CommandText = createUsersTable;
                await command.ExecuteNonQueryAsync();
                
                command.CommandText = createPreferencesTable;
                await command.ExecuteNonQueryAsync();
                
                command.CommandText = createModCacheTable;
                await command.ExecuteNonQueryAsync();
                
                command.CommandText = createSessionsTable;
                await command.ExecuteNonQueryAsync();
                
                command.CommandText = createIndexes;
                await command.ExecuteNonQueryAsync();

                _logger.LogInformation("PostgreSQL数据库架构初始化完成");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "初始化PostgreSQL数据库失败");
                return false;
            }
        }

        /// <summary>
        /// 用户注册
        /// </summary>
        public async Task<(bool success, string message, int userId)> RegisterUserAsync(string email, string username, string passwordHash, string salt)
        {
            try
            {
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                var sql = @"
                INSERT INTO users (email, username, password_hash, salt) 
                VALUES (@email, @username, @passwordHash, @salt)
                RETURNING id;";

                using var command = new NpgsqlCommand(sql, connection);
                command.Parameters.AddWithValue("@email", email);
                command.Parameters.AddWithValue("@username", (object?)username ?? DBNull.Value);
                command.Parameters.AddWithValue("@passwordHash", passwordHash);
                command.Parameters.AddWithValue("@salt", salt);

                var userId = (int)(await command.ExecuteScalarAsync() ?? 0);

                // 创建默认首选项
                await CreateDefaultPreferencesAsync(connection, userId);

                _logger.LogInformation($"用户注册成功: {email}");
                return (true, "注册成功", userId);
            }
            catch (NpgsqlException ex) when (ex.SqlState == "23505") // 唯一约束违反
            {
                _logger.LogWarning($"用户注册失败，邮箱已存在: {email}");
                return (false, "该邮箱已被注册", 0);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"用户注册异常: {email}");
                return (false, $"注册失败: {ex.Message}", 0);
            }
        }

        /// <summary>
        /// 用户登录验证
        /// </summary>
        public async Task<(bool success, string message, int userId, string displayName)> ValidateUserAsync(string email, string passwordHash)
        {
            try
            {
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                var sql = @"
                SELECT id, display_name, password_hash, is_active 
                FROM users 
                WHERE email = @email;";

                using var command = new NpgsqlCommand(sql, connection);
                command.Parameters.AddWithValue("@email", email);

                using var reader = await command.ExecuteReaderAsync();
                
                if (!await reader.ReadAsync())
                {
                    return (false, "用户不存在", 0, "");
                }

                var userId = reader.GetInt32(0); // id column
                var displayName = reader.IsDBNull(1) ? email : reader.GetString(1); // display_name column
                var storedHash = reader.GetString(2); // password_hash column  
                var isActive = reader.GetBoolean(3); // is_active column

                if (!isActive)
                {
                    return (false, "账户已被禁用", 0, "");
                }

                if (storedHash != passwordHash)
                {
                    return (false, "密码错误", 0, "");
                }

                // 更新最后登录时间
                await UpdateLastLoginAsync(connection, userId);

                _logger.LogInformation($"用户登录成功: {email}");
                return (true, "登录成功", userId, displayName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"用户登录验证异常: {email}");
                return (false, $"登录验证失败: {ex.Message}", 0, "");
            }
        }

        /// <summary>
        /// 创建默认用户首选项
        /// </summary>
        private async Task CreateDefaultPreferencesAsync(NpgsqlConnection connection, int userId)
        {
            var sql = @"
            INSERT INTO user_preferences (user_id) 
            VALUES (@userId);";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("@userId", userId);
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// 更新最后登录时间
        /// </summary>
        private async Task UpdateLastLoginAsync(NpgsqlConnection connection, int userId)
        {
            var sql = @"
            UPDATE users 
            SET last_login_at = CURRENT_TIMESTAMP 
            WHERE id = @userId;";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("@userId", userId);
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// 获取云端用户总数
        /// </summary>
        public async Task<int> GetCloudUsersCountAsync()
        {
            try
            {
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                var sql = "SELECT COUNT(*) FROM users WHERE is_active = true;";
                using var command = new NpgsqlCommand(sql, connection);
                var result = await command.ExecuteScalarAsync();

                _logger.LogInformation($"云端用户总数: {result}");
                return Convert.ToInt32(result ?? 0);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取云端用户总数失败");
                return 0;
            }
        }

        /// <summary>
        /// 获取云端活跃用户数（30天内登录）
        /// </summary>
        public async Task<int> GetCloudActiveUsersCountAsync()
        {
            try
            {
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                var sql = @"
                    SELECT COUNT(*)
                    FROM users
                    WHERE is_active = true
                    AND last_login_at >= CURRENT_TIMESTAMP - INTERVAL '30 days';";

                using var command = new NpgsqlCommand(sql, connection);
                var result = await command.ExecuteScalarAsync();

                _logger.LogInformation($"云端活跃用户数: {result}");
                return Convert.ToInt32(result ?? 0);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取云端活跃用户数失败");
                return 0;
            }
        }

        /// <summary>
        /// 获取云端用户列表（分页）
        /// </summary>
        public async Task<List<CloudUserInfo>> GetCloudUsersAsync(int limit = 100, int offset = 0)
        {
            try
            {
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                var sql = @"
                    SELECT id, email, display_name, created_at, last_login_at, is_active
                    FROM users
                    ORDER BY created_at DESC
                    LIMIT @limit OFFSET @offset;";

                using var command = new NpgsqlCommand(sql, connection);
                command.Parameters.AddWithValue("@limit", limit);
                command.Parameters.AddWithValue("@offset", offset);

                var users = new List<CloudUserInfo>();
                using var reader = await command.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    users.Add(new CloudUserInfo
                    {
                        Id = reader.GetInt32(0), // id
                        Email = reader.GetString(1), // email
                        DisplayName = reader.IsDBNull(2) ? reader.GetString(1) : reader.GetString(2), // display_name
                        CreatedAt = reader.GetDateTime(3), // created_at
                        LastLoginAt = reader.IsDBNull(4) ? null : reader.GetDateTime(4), // last_login_at
                        IsActive = reader.GetBoolean(5) // is_active
                    });
                }

                _logger.LogInformation($"获取到 {users.Count} 个云端用户");
                return users;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取云端用户列表失败");
                return new List<CloudUserInfo>();
            }
        }

        /// <summary>
        /// 获取云端数据库连接延迟
        /// </summary>
        public async Task<long> GetConnectionLatencyAsync()
        {
            try
            {
                var startTime = DateTime.UtcNow;
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                using var command = new NpgsqlCommand("SELECT 1;", connection);
                await command.ExecuteScalarAsync();

                var latency = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;
                _logger.LogInformation($"云端数据库延迟: {latency}ms");
                return latency;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取云端数据库延迟失败");
                return -1;
            }
        }

        /// <summary>
        /// 获取免费PostgreSQL服务推荐
        /// </summary>
        public static List<(DatabaseProvider provider, string name, string description, string url)> GetFreeProviders()
        {
            return new List<(DatabaseProvider, string, string, string)>
            {
                (DatabaseProvider.ElephantSQL, "ElephantSQL", "免费层提供20MB存储，适合小项目", "https://www.elephantsql.com/"),
                (DatabaseProvider.Neon, "Neon", "现代化PostgreSQL，免费层3GB存储", "https://neon.tech/"),
                (DatabaseProvider.Railway, "Railway", "免费层提供512MB存储", "https://railway.app/"),
                (DatabaseProvider.Supabase, "Supabase", "开源Firebase替代，免费层500MB", "https://supabase.com/"),
                (DatabaseProvider.Heroku, "Heroku Postgres", "免费层已停止，但Hobby层便宜", "https://www.heroku.com/postgres")
            };
        }
    }

    /// <summary>
    /// 云端用户信息
    /// </summary>
    public class CloudUserInfo
    {
        public int Id { get; set; }
        public string Email { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime? LastLoginAt { get; set; }
        public bool IsActive { get; set; }
    }
}

