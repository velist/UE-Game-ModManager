using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace UEModManager.Services
{
    /// <summary>
    /// 通过 Supabase PostgREST 读取云端数据的轻量服务。
    /// </summary>
    public class SupabaseRestService
    {
        private readonly ILogger<SupabaseRestService> _logger;
        private readonly HttpClient _httpClient;
        private readonly string _restBase;
        private readonly string _apiKey;

        public SupabaseRestService(HttpClient httpClient, ILogger<SupabaseRestService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;

            var baseUrl = SupabaseConfig.SupabaseUrl?.TrimEnd('/') ?? string.Empty;
            _restBase = string.IsNullOrWhiteSpace(baseUrl) ? string.Empty : $"{baseUrl}/rest/v1/";
            _apiKey = ResolveApiKey();

            if (!string.IsNullOrWhiteSpace(_restBase))
            {
                _httpClient.BaseAddress = new Uri(_restBase);
            }

            if (!string.IsNullOrWhiteSpace(_apiKey))
            {
                _httpClient.DefaultRequestHeaders.Remove("apikey");
                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("apikey", _apiKey);
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            }

            _httpClient.DefaultRequestHeaders.Accept.Clear();
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            _httpClient.DefaultRequestHeaders.Remove("Prefer");
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Prefer", "count=exact");
        }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(_restBase) && !string.IsNullOrWhiteSpace(_apiKey);

        private static string ResolveApiKey()
        {
            try
            {
                var currentDirectory = Environment.CurrentDirectory;
                var envPath = Path.Combine(currentDirectory, "supabase.env");

                if (File.Exists(envPath))
                {
                    var lines = File.ReadAllLines(envPath)
                                     .Select(l => l.Trim())
                                     .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#"))
                                     .ToList();

                    foreach (var line in lines)
                    {
                        var kv = line.Split('=', 2, StringSplitOptions.RemoveEmptyEntries);
                        if (kv.Length == 2)
                        {
                            var key = kv[0].Trim();
                            var value = kv[1].Trim();

                            if (string.Equals(key, "SUPABASE_SERVICE_KEY", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(key, "SUPABASE_REST_KEY", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(key, "SUPABASE_ANON_KEY", StringComparison.OrdinalIgnoreCase))
                            {
                                return value;
                            }
                        }
                    }

                    if (lines.Count == 1 && !lines[0].Contains(' ') && !lines[0].Contains('='))
                    {
                        return lines[0];
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"读取 supabase.env 失败: {ex.Message}");
            }

            if (!string.IsNullOrWhiteSpace(SupabaseConfig.SupabaseServiceKey))
            {
                return SupabaseConfig.SupabaseServiceKey;
            }

            return SupabaseConfig.SupabaseKey;
        }

        public async Task<(bool ok, string message, long latencyMs)> TestAsync()
        {
            if (!IsConfigured)
            {
                return (false, "未配置 Supabase REST 访问参数", 0);
            }

            try
            {
                var start = DateTime.UtcNow;
                var response = await _httpClient.GetAsync("users?select=id&limit=1");
                var latency = (long)(DateTime.UtcNow - start).TotalMilliseconds;

                if (response.IsSuccessStatusCode)
                {
                    return (true, "连接成功", latency);
                }

                var error = await response.Content.ReadAsStringAsync();
                return (false, $"HTTP {(int)response.StatusCode}: {error}", 0);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Supabase REST 测试失败");
                return (false, ex.Message, 0);
            }
        }

        public async Task<int> GetUsersCountAsync(bool onlyActive)
        {
            if (!IsConfigured)
            {
                return 0;
            }

            try
            {
                var path = onlyActive
                    ? "users?select=id&is_active=eq.true&limit=1"
                    : "users?select=id&limit=1";

                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                request.Headers.TryAddWithoutValidation("Prefer", "count=exact");

                var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("获取云端用户数量失败: {StatusCode}", response.StatusCode);
                    return 0;
                }

                if (response.Headers.TryGetValues("Content-Range", out var ranges))
                {
                    var range = ranges.FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(range))
                    {
                        var parts = range.Split('/');
                        if (parts.Length == 2 && int.TryParse(parts[1], out var total))
                        {
                            return total;
                        }
                    }
                }

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "获取云端用户数量异常");
                return 0;
            }
        }

        public async Task<List<CloudUserInfo>> GetUsersAsync(int limit, int offset)
        {
            var users = new List<CloudUserInfo>();
            if (!IsConfigured)
            {
                return users;
            }

            try
            {
                var path = $"users?select=id,email,display_name,created_at,last_login_at,is_active&order=created_at.desc&limit={limit}&offset={offset}";
                var response = await _httpClient.GetAsync(path);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("获取云端用户列表失败: {StatusCode}", response.StatusCode);
                    return users;
                }

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return users;
                }

                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var info = new CloudUserInfo
                    {
                        Id = item.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0,
                        Email = item.TryGetProperty("email", out var emailEl) ? emailEl.GetString() ?? string.Empty : string.Empty,
                        DisplayName = item.TryGetProperty("display_name", out var displayEl) && displayEl.ValueKind != JsonValueKind.Null
                            ? displayEl.GetString() ?? string.Empty
                            : string.Empty,
                        CreatedAt = item.TryGetProperty("created_at", out var createdEl) && createdEl.ValueKind == JsonValueKind.String && DateTime.TryParse(createdEl.GetString(), out var created)
                            ? created
                            : DateTime.MinValue,
                        LastLoginAt = item.TryGetProperty("last_login_at", out var loginEl) && loginEl.ValueKind == JsonValueKind.String && DateTime.TryParse(loginEl.GetString(), out var login)
                            ? login
                            : (DateTime?)null,
                        IsActive = item.TryGetProperty("is_active", out var activeEl) && activeEl.ValueKind == JsonValueKind.True
                    };

                    users.Add(info);
                }

                return users;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "获取云端用户列表异常");
                return users;
            }
        }
    }
}
