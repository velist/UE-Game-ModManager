using System;
using Supabase;
using Supabase.Gotrue;
using Supabase.Gotrue.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace UEModManager.Services
{
    public static class SupabaseConfig
    {
        // AIgame项目的Supabase配置
        public static readonly string SupabaseUrl = "https://oiatqeymovnyubrnlmlu.supabase.co";
        public static readonly string SupabaseServiceKey = ""; // 服务密钥（如果需要）
        public static readonly string SupabaseKey = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Im9pYXRxZXltb3ZueXVicm5sbWx1Iiwicm9sZSI6ImFub24iLCJpYXQiOjE3NTQzMjM0MzYsImV4cCI6MjA2OTg5OTQzNn0.U-3p0SEVNOQUV4lYFWRiOfVmxgNSbMRWx0mE0DXZYuM";

        public static SupabaseOptions GetOptions()
        {
            return new SupabaseOptions
            {
                AutoConnectRealtime = true,
                SessionHandler = new SupabaseSessionHandler()
            };
        }

        public static void ConfigureServices(IServiceCollection services)
        {
            // 注册Supabase Client
            services.AddSingleton<Supabase.Client>(provider =>
            {
                var logger = provider.GetService<ILogger<Supabase.Client>>();
                var client = new Supabase.Client(SupabaseUrl, SupabaseKey, GetOptions());
                
                // 不在这里初始化客户端，让它在第一次使用时初始化
                logger?.LogInformation("Supabase客户端已创建，将在首次使用时初始化");
                
                return client;
            });

            // 注册认证服务
            services.AddSingleton<AuthenticationService>();
        }
    }

    public class SupabaseSessionHandler : IGotrueSessionPersistence<Session>
    {
        private const string SessionKey = "supabase.session";

        public void SaveSession(Session session)
        {
            try
            {
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(session);
                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var configDir = System.IO.Path.Combine(appDataPath, "UEModManager");
                
                if (!System.IO.Directory.Exists(configDir))
                    System.IO.Directory.CreateDirectory(configDir);
                
                var sessionFile = System.IO.Path.Combine(configDir, "session.json");
                System.IO.File.WriteAllText(sessionFile, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存会话失败: {ex.Message}");
            }
        }

        public void DestroySession()
        {
            try
            {
                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var sessionFile = System.IO.Path.Combine(appDataPath, "UEModManager", "session.json");
                
                if (System.IO.File.Exists(sessionFile))
                    System.IO.File.Delete(sessionFile);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"删除会话失败: {ex.Message}");
            }
        }

        public Session? LoadSession()
        {
            try
            {
                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var sessionFile = System.IO.Path.Combine(appDataPath, "UEModManager", "session.json");
                
                if (!System.IO.File.Exists(sessionFile))
                    return null;
                
                var json = System.IO.File.ReadAllText(sessionFile);
                return Newtonsoft.Json.JsonConvert.DeserializeObject<Session>(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载会话失败: {ex.Message}");
                return null;
            }
        }
    }
}