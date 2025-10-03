using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UEModManager.Data;
using UEModManager.Services;

namespace UEModManager.Tests
{
    /// <summary>
    /// 本地存储系统测试类
    /// </summary>
    public static class LocalStorageTest
    {
        /// <summary>
        /// 运行所有本地存储测试
        /// </summary>
        public static async Task RunAllTestsAsync(IServiceProvider serviceProvider)
        {
            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger("LocalStorageTest");
            
            logger.LogInformation("开始本地存储系统测试...");
            
            try
            {
                // 测试数据库连接和创建
                await TestDatabaseConnectionAsync(serviceProvider);
                
                // 测试本地认证服务
                await TestLocalAuthServiceAsync(serviceProvider);
                
                // 测试本地缓存服务
                await TestLocalCacheServiceAsync(serviceProvider);
                
                // 测试离线模式服务
                await TestOfflineModeServiceAsync(serviceProvider);
                
                logger.LogInformation("✅ 所有本地存储系统测试通过！");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "❌ 本地存储系统测试失败");
                throw;
            }
        }

        /// <summary>
        /// 测试数据库连接和创建
        /// </summary>
        private static async Task TestDatabaseConnectionAsync(IServiceProvider serviceProvider)
        {
            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger("LocalStorageTest.Database");
            var dbContext = serviceProvider.GetRequiredService<LocalDbContext>();
            
            logger.LogInformation("测试数据库连接...");
            
            // 确保数据库创建
            var created = await dbContext.EnsureDatabaseCreatedAsync();
            logger.LogInformation($"数据库创建状态: {(created ? "新建" : "已存在")}");
            
            // 获取数据库信息
            var dbInfo = await dbContext.GetDatabaseInfoAsync();
            logger.LogInformation($"数据库路径: {dbInfo.Path}");
            logger.LogInformation($"数据库大小: {dbInfo.Size} bytes");
            logger.LogInformation($"用户数量: {dbInfo.UserCount}");
            logger.LogInformation($"MOD缓存数量: {dbInfo.ModCacheCount}");
            logger.LogInformation($"配置数量: {dbInfo.ConfigCount}");
            
            logger.LogInformation("✅ 数据库连接测试通过");
        }

        /// <summary>
        /// 测试本地认证服务
        /// </summary>
        private static async Task TestLocalAuthServiceAsync(IServiceProvider serviceProvider)
        {
            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger("LocalStorageTest.Auth");
            var authService = serviceProvider.GetRequiredService<LocalAuthService>();
            
            logger.LogInformation("测试本地认证服务...");
            
            var testEmail = "test@example.com";
            var testPassword = "TestPassword123";
            var testUsername = "TestUser";
            
            // 测试用户注册
            logger.LogInformation("测试用户注册...");
            var registerResult = await authService.RegisterAsync(testEmail, testPassword, testUsername);
            if (registerResult.IsSuccess)
            {
                logger.LogInformation($"✅ 用户注册成功: {registerResult.Message}");
            }
            else
            {
                logger.LogError($"❌ 用户注册失败: {registerResult.Message}");
                return;
            }
            
            // 测试用户登录
            logger.LogInformation("测试用户登录...");
            var loginResult = await authService.LoginAsync(testEmail, testPassword);
            if (loginResult.IsSuccess)
            {
                logger.LogInformation($"✅ 用户登录成功: {loginResult.Message}");
            }
            else
            {
                logger.LogError($"❌ 用户登录失败: {loginResult.Message}");
                return;
            }
            
            // 测试会话恢复
            logger.LogInformation("测试会话恢复...");
            var sessionRestored = await authService.RestoreSessionAsync();
            if (sessionRestored)
            {
                logger.LogInformation("✅ 会话恢复成功");
            }
            else
            {
                logger.LogWarning("⚠️ 会话恢复失败或无有效会话");
            }
            
            // 测试用户注销
            logger.LogInformation("测试用户注销...");
            await authService.LogoutAsync();
            logger.LogInformation("✅ 用户注销完成");
            
            logger.LogInformation("✅ 本地认证服务测试通过");
        }

        /// <summary>
        /// 测试本地缓存服务
        /// </summary>
        private static async Task TestLocalCacheServiceAsync(IServiceProvider serviceProvider)
        {
            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger("LocalStorageTest.Cache");
            var cacheService = serviceProvider.GetRequiredService<LocalCacheService>();
            
            logger.LogInformation("测试本地缓存服务...");
            
            var testModId = "test-mod-001";
            var testModName = "测试MOD";
            var testGameName = "测试游戏";
            
            // 测试MOD缓存
            logger.LogInformation("测试MOD缓存...");
            var cacheResult = await cacheService.CacheModAsync(
                testModId, testModName, testGameName, 
                "1.0.0", "这是一个测试MOD", "测试作者", 
                "http://example.com/download", 1024 * 1024, "/path/to/test-mod.zip"
            );
            
            if (cacheResult)
            {
                logger.LogInformation("✅ MOD缓存成功");
            }
            else
            {
                logger.LogError("❌ MOD缓存失败");
                return;
            }
            
            // 测试获取缓存的MOD
            logger.LogInformation("测试获取缓存的MOD...");
            var cachedMod = await cacheService.GetCachedModAsync(testModId);
            if (cachedMod != null)
            {
                logger.LogInformation($"✅ 获取缓存MOD成功: {cachedMod.ModName}");
            }
            else
            {
                logger.LogError("❌ 获取缓存MOD失败");
                return;
            }
            
            // 测试搜索缓存MOD
            logger.LogInformation("测试搜索缓存MOD...");
            var searchResults = await cacheService.SearchCachedModsAsync("测试", testGameName);
            logger.LogInformation($"✅ 搜索到 {searchResults.Count} 个MOD");
            
            // 测试获取缓存统计
            logger.LogInformation("测试缓存统计...");
            var cacheStats = await cacheService.GetCacheStatisticsAsync();
            logger.LogInformation($"缓存统计 - 总MOD数: {cacheStats.TotalMods}, 总大小: {cacheStats.TotalSizeFormatted}");
            
            logger.LogInformation("✅ 本地缓存服务测试通过");
        }

        /// <summary>
        /// 测试离线模式服务
        /// </summary>
        private static async Task TestOfflineModeServiceAsync(IServiceProvider serviceProvider)
        {
            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger("LocalStorageTest.Offline");
            var offlineService = serviceProvider.GetRequiredService<OfflineModeService>();
            
            logger.LogInformation("测试离线模式服务...");
            
            // 初始化离线模式
            logger.LogInformation("初始化离线模式...");
            await offlineService.InitializeAsync();
            logger.LogInformation($"当前离线模式状态: {(offlineService.IsOfflineMode ? "启用" : "禁用")}");
            
            // 测试启用离线模式
            logger.LogInformation("测试启用离线模式...");
            var enableResult = await offlineService.EnableOfflineModeAsync();
            if (enableResult)
            {
                logger.LogInformation($"✅ 离线模式启用成功，当前状态: {offlineService.IsOfflineMode}");
            }
            else
            {
                logger.LogError("❌ 离线模式启用失败");
                return;
            }
            
            // 测试预加载常用MOD
            logger.LogInformation("测试预加载常用MOD...");
            var preloadResult = await offlineService.PreloadCommonModsAsync("测试游戏", 5);
            if (preloadResult)
            {
                logger.LogInformation("✅ 常用MOD预加载成功");
            }
            else
            {
                logger.LogWarning("⚠️ 常用MOD预加载失败");
            }
            
            // 测试文件完整性检查
            logger.LogInformation("测试文件完整性检查...");
            var fileStatus = await offlineService.CheckLocalFileIntegrityAsync();
            logger.LogInformation($"文件完整性 - 总计: {fileStatus.TotalFiles}, 有效: {fileStatus.ValidFiles}, 损坏: {fileStatus.CorruptedFiles}, 丢失: {fileStatus.MissingFiles}");
            logger.LogInformation($"文件完整性百分比: {fileStatus.IntegrityPercentage:F2}%");
            
            // 测试存储信息
            logger.LogInformation("测试获取存储信息...");
            var storageInfo = await offlineService.GetOfflineStorageInfoAsync();
            logger.LogInformation($"存储信息 - 数据库大小: {storageInfo.DatabaseSizeFormatted}, 缓存文件大小: {storageInfo.CachedFilesSizeFormatted}");
            logger.LogInformation($"存储信息 - 用户数: {storageInfo.UserCount}, MOD数: {storageInfo.CachedModsCount}");
            
            // 测试禁用离线模式
            logger.LogInformation("测试禁用离线模式...");
            var disableResult = await offlineService.DisableOfflineModeAsync();
            if (disableResult)
            {
                logger.LogInformation($"✅ 离线模式禁用成功，当前状态: {offlineService.IsOfflineMode}");
            }
            else
            {
                logger.LogError("❌ 离线模式禁用失败");
                return;
            }
            
            logger.LogInformation("✅ 离线模式服务测试通过");
        }
    }
}