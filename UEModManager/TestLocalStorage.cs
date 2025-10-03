using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using UEModManager.Tests;

namespace UEModManager
{
    /// <summary>
    /// 测试本地存储系统的入口点
    /// </summary>
    public static class TestLocalStorage
    {
        /// <summary>
        /// 运行本地存储系统测试
        /// </summary>
        public static async Task<bool> RunTestAsync()
        {
            try
            {
                // 创建服务提供程序（模拟App.xaml.cs中的配置）
                var serviceCollection = new ServiceCollection();
                
                // 配置日志
                serviceCollection.AddLogging(builder =>
                {
                    builder.AddConsole();
                    builder.SetMinimumLevel(LogLevel.Information);
                });

                // 配置数据库上下文
                serviceCollection.AddDbContext<Data.LocalDbContext>(options =>
                {
                    var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    var dbPath = System.IO.Path.Combine(appDataPath, "UEModManager", "local_test.db");
                    options.UseSqlite($"Data Source={dbPath}");
                });

                // 添加服务
                serviceCollection.AddScoped<Services.LocalAuthService>();
                serviceCollection.AddScoped<Services.LocalCacheService>();
                serviceCollection.AddScoped<Services.OfflineModeService>();

                var serviceProvider = serviceCollection.BuildServiceProvider();

                // 运行测试
                await LocalStorageTest.RunAllTestsAsync(serviceProvider);
                
                Console.WriteLine("\n✅ 本地存储系统测试完成！所有功能正常工作。");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n❌ 本地存储系统测试失败: {ex.Message}");
                Console.WriteLine($"详细错误: {ex}");
                return false;
            }
        }
    }
}