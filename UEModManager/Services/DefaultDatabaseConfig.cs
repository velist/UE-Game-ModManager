using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Models;

namespace UEModManager.Services
{
    /// <summary>
    /// 默认数据库配置管理器 - 为用户提供预配置的免费云端数据库
    /// </summary>
    public class DefaultDatabaseConfig
    {
        private readonly ILogger<DefaultDatabaseConfig> _logger;
        private readonly PostgreSQLConfig? _injectedConfig;

        /// <summary>
        /// 预配置的数据库服务列表 - 使用AIgame项目的Supabase
        /// </summary>
        private static readonly List<PostgreSQLConfig> _defaultConfigs = new()
        {
            // 使用AIgame项目的Supabase数据库 - 免费500MB
            new PostgreSQLConfig
            {
                Provider = DatabaseProvider.Supabase,
                Host = "db.oiatqeymovnyubrnlmlu.supabase.co",
                Port = 5432,
                Database = "postgres",
                Username = "postgres",
                Password = "", // 需要数据库密码
                UseSsl = true,
                TrustServerCertificate = true,
                IncludeErrorDetail = true,
                ConnectionTimeout = 30
            }
        };

        public DefaultDatabaseConfig(ILogger<DefaultDatabaseConfig> logger, PostgreSQLConfig? injectedConfig = null)
        {
            _logger = logger;
            _injectedConfig = injectedConfig;
        }

        /// <summary>
        /// 获取可用的默认数据库配置
        /// </summary>
        public async Task<PostgreSQLConfig?> GetAvailableDatabaseConfigAsync()
        {
            _logger.LogInformation("开始检测可用的数据库配置");

            // 优先测试DI注入的配置
            if (_injectedConfig != null)
            {
                try
                {
                    _logger.LogInformation($"测试DI注入的数据库连接: {_injectedConfig.Provider} - {_injectedConfig.Host}");

                    var testLogger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<PostgreSQLAuthService>();
                    var postgreSqlService = new PostgreSQLAuthService(_injectedConfig, testLogger);
                    var (isConnected, message) = await postgreSqlService.TestConnectionAsync();

                    if (isConnected)
                    {
                        _logger.LogInformation($"DI注入的数据库连接成功: {_injectedConfig.Provider}");
                        return _injectedConfig;
                    }
                    else
                    {
                        _logger.LogWarning($"DI注入的数据库连接失败: {_injectedConfig.Provider} - {message}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"测试DI注入的数据库连接时出错: {_injectedConfig.Provider}");
                }
            }

            // 回退到预配置的默认配置
            _logger.LogInformation("回退到预配置的默认数据库配置");
            foreach (var config in _defaultConfigs)
            {
                try
                {
                    _logger.LogInformation($"测试预配置数据库连接: {config.Provider} - {config.Host}");

                    // 测试数据库连接
                    var testLogger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<PostgreSQLAuthService>();
                    var postgreSqlService = new PostgreSQLAuthService(config, testLogger);
                    var (isConnected, message) = await postgreSqlService.TestConnectionAsync();

                    if (isConnected)
                    {
                        _logger.LogInformation($"预配置数据库连接成功: {config.Provider}");
                        return config;
                    }
                    else
                    {
                        _logger.LogWarning($"预配置数据库连接失败: {config.Provider} - {message}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"测试预配置数据库连接时出错: {config.Provider}");
                }
            }

            _logger.LogError("所有数据库配置都不可用");
            return null;
        }

        /// <summary>
        /// 获取主要数据库配置（Neon）
        /// </summary>
        public PostgreSQLConfig GetPrimaryDatabaseConfig()
        {
            return _defaultConfigs[0];
        }

        /// <summary>
        /// 获取所有预配置的数据库
        /// </summary>
        public List<PostgreSQLConfig> GetAllDefaultConfigs()
        {
            return new List<PostgreSQLConfig>(_defaultConfigs);
        }

        /// <summary>
        /// 初始化默认数据库
        /// </summary>
        public async Task<bool> InitializeDefaultDatabaseAsync()
        {
            var config = await GetAvailableDatabaseConfigAsync();
            if (config == null)
            {
                _logger.LogError("无法获取可用的数据库配置");
                return false;
            }

            try
            {
                var testLogger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<PostgreSQLAuthService>();
                var postgreSqlService = new PostgreSQLAuthService(config, testLogger);
                var success = await postgreSqlService.InitializeDatabaseAsync();
                
                if (success)
                {
                    _logger.LogInformation($"数据库初始化成功: {config.Provider}");
                    // 保存当前使用的配置
                    await SaveCurrentDatabaseConfigAsync(config);
                    return true;
                }
                else
                {
                    _logger.LogError("数据库初始化失败");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "数据库初始化时出错");
                return false;
            }
        }

        /// <summary>
        /// 保存当前使用的数据库配置
        /// </summary>
        private async Task SaveCurrentDatabaseConfigAsync(PostgreSQLConfig config)
        {
            try
            {
                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var configDir = System.IO.Path.Combine(appDataPath, "UEModManager");
                
                if (!System.IO.Directory.Exists(configDir))
                    System.IO.Directory.CreateDirectory(configDir);
                
                var configFile = System.IO.Path.Combine(configDir, "current_database.json");
                
                // 不保存敏感信息，只保存连接配置标识
                var saveConfig = new 
                {
                    Provider = config.Provider.ToString(),
                    Host = config.Host,
                    Port = config.Port,
                    Database = config.Database,
                    LastUsed = DateTime.UtcNow
                };
                
                var json = System.Text.Json.JsonSerializer.Serialize(saveConfig, new System.Text.Json.JsonSerializerOptions 
                { 
                    WriteIndented = true 
                });
                
                await System.IO.File.WriteAllTextAsync(configFile, json);
                _logger.LogInformation($"已保存当前数据库配置: {config.Provider}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "保存数据库配置失败");
            }
        }

        /// <summary>
        /// 检查数据库健康状态并自动切换
        /// </summary>
        public async Task<PostgreSQLConfig?> GetHealthyDatabaseConfigAsync()
        {
            // 优先使用DI注入的配置
            if (_injectedConfig != null)
            {
                try
                {
                    _logger.LogInformation($"检查DI注入的数据库健康状态: {_injectedConfig.Provider}");
                    var postgreSqlService = new PostgreSQLAuthService(_injectedConfig, new Microsoft.Extensions.Logging.Abstractions.NullLogger<PostgreSQLAuthService>());
                    var (isConnected, _) = await postgreSqlService.TestConnectionAsync();
                    if (isConnected)
                    {
                        _logger.LogInformation($"DI注入的数据库健康: {_injectedConfig.Provider}");
                        return _injectedConfig;
                    }
                    else
                    {
                        _logger.LogWarning($"DI注入的数据库连接失败: {_injectedConfig.Provider}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"DI注入的数据库健康检查失败: {_injectedConfig.Provider}");
                }
            }

            // 回退：首先尝试获取上次使用的配置
            var lastUsedConfig = await GetLastUsedConfigAsync();
            if (lastUsedConfig != null)
            {
                try
                {
                    var postgreSqlService = new PostgreSQLAuthService(lastUsedConfig, new Microsoft.Extensions.Logging.Abstractions.NullLogger<PostgreSQLAuthService>());
                    var (isConnected, _) = await postgreSqlService.TestConnectionAsync();
                    if (isConnected)
                    {
                        _logger.LogInformation($"继续使用上次的数据库: {lastUsedConfig.Provider}");
                        return lastUsedConfig;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"上次使用的数据库连接失败: {lastUsedConfig.Provider}");
                }
            }

            // 如果所有配置都不可用，自动选择可用的配置
            _logger.LogInformation("自动切换到可用的数据库配置");
            return await GetAvailableDatabaseConfigAsync();
        }

        /// <summary>
        /// 获取上次使用的数据库配置
        /// </summary>
        private async Task<PostgreSQLConfig?> GetLastUsedConfigAsync()
        {
            try
            {
                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var configFile = System.IO.Path.Combine(appDataPath, "UEModManager", "current_database.json");
                
                if (!System.IO.File.Exists(configFile))
                    return null;
                
                var json = await System.IO.File.ReadAllTextAsync(configFile);
                var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                
                if (!root.TryGetProperty("Provider", out var providerElement) ||
                    !root.TryGetProperty("Host", out var hostElement))
                    return null;

                var providerString = providerElement.GetString();
                var host = hostElement.GetString();
                
                if (Enum.TryParse<DatabaseProvider>(providerString, out var provider))
                {
                    return _defaultConfigs.FirstOrDefault(c => 
                        c.Provider == provider && c.Host == host);
                }
                
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取上次使用的数据库配置失败");
                return null;
            }
        }
    }
}
