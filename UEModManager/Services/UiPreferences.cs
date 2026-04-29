using System;
using System.IO;
using System.Text.Json;
using UEModManager.Models;

namespace UEModManager.Services
{
    public static class UiPreferences
    {
        /// <summary>
        /// 关闭行为：0=每次询问, 1=直接退出, 2=最小化到任务栏
        /// </summary>
        public enum CloseAction { Ask = 0, Exit = 1, Minimize = 2 }

        private class UiConfig
        {
            public string? Language { get; set; }
            public int BackgroundMode { get; set; }
            public string? BackgroundImagePath { get; set; }
            public string? BackgroundSolidColor { get; set; }
            public double BackgroundOpacity { get; set; } = 0.7;
            public double BackgroundBlurRadius { get; set; }
            public bool ApplyToDialogs { get; set; } = true;
            public int CloseActionValue { get; set; } = 0;
            public bool PluginSystemEnabled { get; set; } = false;
            public int DeployBackendType { get; set; } = 0;
            public bool DeployConfirm { get; set; } = true;
            public bool AutoDeploy { get; set; } = true;
        }

        private static string GetConfigPath()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UEModManager");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return Path.Combine(dir, "ui_config.json");
        }

        private static UiConfig LoadConfig()
        {
            try
            {
                var path = GetConfigPath();
                if (!File.Exists(path)) return new UiConfig();
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<UiConfig>(json) ?? new UiConfig();
            }
            catch { return new UiConfig(); }
        }

        private static void SaveConfig(UiConfig cfg)
        {
            try
            {
                var path = GetConfigPath();
                var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch { }
        }

        // ── 语言 ──

        public static bool TryLoadEnglish(out bool isEnglish)
        {
            isEnglish = false;
            try
            {
                var cfg = LoadConfig();
                var lang = cfg.Language?.Trim();
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
                var cfg = LoadConfig();
                cfg.Language = isEnglish ? "en-US" : "zh-CN";
                SaveConfig(cfg);
            }
            catch { }
        }

        // ── 背景设置 ──

        public static BackgroundSettings LoadBackground()
        {
            try
            {
                var cfg = LoadConfig();
                return new BackgroundSettings
                {
                    Mode = (BackgroundMode)cfg.BackgroundMode,
                    ImagePath = cfg.BackgroundImagePath,
                    SolidColor = cfg.BackgroundSolidColor ?? "#030303",
                    Opacity = cfg.BackgroundOpacity,
                    BlurRadius = cfg.BackgroundBlurRadius,
                    ApplyToDialogs = cfg.ApplyToDialogs
                };
            }
            catch { return new BackgroundSettings(); }
        }

        public static void SaveBackground(BackgroundSettings bg)
        {
            try
            {
                var cfg = LoadConfig();
                cfg.BackgroundMode = (int)bg.Mode;
                cfg.BackgroundImagePath = bg.ImagePath;
                cfg.BackgroundSolidColor = bg.SolidColor;
                cfg.BackgroundOpacity = bg.Opacity;
                cfg.BackgroundBlurRadius = bg.BlurRadius;
                cfg.ApplyToDialogs = bg.ApplyToDialogs;
                SaveConfig(cfg);
            }
            catch { }
        }

        // ── 关闭行为 ──

        public static CloseAction LoadCloseAction()
        {
            try
            {
                var cfg = LoadConfig();
                return (CloseAction)cfg.CloseActionValue;
            }
            catch { return CloseAction.Ask; }
        }

        public static void SaveCloseAction(CloseAction action)
        {
            try
            {
                var cfg = LoadConfig();
                cfg.CloseActionValue = (int)action;
                SaveConfig(cfg);
            }
            catch { }
        }

        public static bool LoadPluginEnabled()
        {
            try { return LoadConfig().PluginSystemEnabled; }
            catch { return false; }
        }

        public static void SavePluginEnabled(bool enabled)
        {
            try
            {
                var cfg = LoadConfig();
                cfg.PluginSystemEnabled = enabled;
                SaveConfig(cfg);
            }
            catch { }
        }

        // ── 部署设置 ──

        public static DeploymentBackendType LoadDeployBackend()
        {
            try { return (DeploymentBackendType)LoadConfig().DeployBackendType; }
            catch { return DeploymentBackendType.Copy; }
        }

        public static void SaveDeployBackend(DeploymentBackendType backend)
        {
            try
            {
                var cfg = LoadConfig();
                cfg.DeployBackendType = (int)backend;
                SaveConfig(cfg);
            }
            catch { }
        }

        public static bool LoadDeployConfirm()
        {
            try { return LoadConfig().DeployConfirm; }
            catch { return true; }
        }

        public static void SaveDeployConfirm(bool confirm)
        {
            try
            {
                var cfg = LoadConfig();
                cfg.DeployConfirm = confirm;
                SaveConfig(cfg);
            }
            catch { }
        }

        public static bool LoadAutoDeploy()
        {
            try { return LoadConfig().AutoDeploy; }
            catch { return true; }
        }

        public static void SaveAutoDeploy(bool auto)
        {
            try
            {
                var cfg = LoadConfig();
                cfg.AutoDeploy = auto;
                SaveConfig(cfg);
            }
            catch { }
        }
    }
}
