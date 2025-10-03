using System;
using System.IO;
using System.Text.Json;

namespace UEModManager.Services
{
    public static class UiPreferences
    {
        private class UiConfig { public string? Language { get; set; } }

        private static string GetConfigPath()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UEModManager");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return Path.Combine(dir, "ui_config.json");
        }

        public static bool TryLoadEnglish(out bool isEnglish)
        {
            isEnglish = false;
            try
            {
                var path = GetConfigPath();
                if (!File.Exists(path)) return false;
                var json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<UiConfig>(json);
                var lang = cfg?.Language?.Trim();
                if (string.IsNullOrEmpty(lang)) return false;
                isEnglish = string.Equals(lang, "en-US", StringComparison.OrdinalIgnoreCase) || string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);
                return true;
            }
            catch { return false; }
        }

        public static void SaveEnglish(bool isEnglish)
        {
            try
            {
                var path = GetConfigPath();
                var cfg = new UiConfig { Language = isEnglish ? "en-US" : "zh-CN" };
                var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch { }
        }
    }
}
