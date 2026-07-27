using System;
using System.IO;
using System.Text.Json;
using UEModManager.Models;
using UEModManager.Services.Persistence;

namespace UEModManager.Services
{
    /// <summary>
    /// UI 偏好设置（%AppData%\UEModManager\ui_config.json）。
    ///
    /// 该类被 App 启动、设置窗口、ObjectStore、部署计划等多处静态调用，因此约定：
    /// - 配置只从磁盘加载一次，之后以内存单例为准。旧实现每个 setter 都是
    ///   "读一次 → 改一个字段 → 写全量"，两处并发保存不同偏好时后写的会把先写的
    ///   覆盖回默认值（其中 RepositoryRoot 一旦被重置，ObjectStore 会回落到默认仓库，
    ///   用户自定义仓库里的 MOD 在界面上直接"消失"）；
    /// - 所有读写都在同一把锁内完成，消除上述竞态；
    /// - 写盘走 AtomicFileWriter，崩溃/断电不会留下截断的 json；
    /// - 任何失败都必须留痕。本类没有注入 ILogger，而 Console 已被 App.SetupFileLogging
    ///   重定向到结构化日志文件，故统一用 Console 取证（与 Infrastructure/SafeEvent 一致）。
    /// </summary>
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
            public string? RepositoryRoot { get; set; }
        }

        private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

        /// <summary>保护内存单例与落盘的同一把锁（本类全部入口都是静态的，可能来自任意线程）。</summary>
        private static readonly object Gate = new();

        /// <summary>内存单例；null 表示尚未成功确定磁盘状态。</summary>
        private static UiConfig? _cached;

        /// <summary>
        /// 磁盘状态未知：文件存在但读取失败（被占用/权限/杀软扫描）。
        /// 此时磁盘上的配置很可能仍然完好，写回前先备份，绝不让内存里的默认值直接盖掉它。
        /// </summary>
        private static bool _diskStateUnknown;

        private static string GetConfigPath()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UEModManager");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return Path.Combine(dir, "ui_config.json");
        }

        // ── 加载 / 保存（全部在 Gate 内调用）──

        /// <summary>取内存单例；首次访问时从磁盘加载一次。</summary>
        private static UiConfig GetOrLoadConfig()
        {
            if (_cached != null) return _cached;

            var cfg = LoadFromDisk(out var diskStateKnown);
            if (diskStateKnown)
            {
                _cached = cfg;
                _diskStateUnknown = false;
            }
            else
            {
                // 读盘失败可能是暂时的：本次不缓存（下次访问重试），并标记磁盘状态未知。
                _diskStateUnknown = true;
            }
            return cfg;
        }

        /// <param name="diskStateKnown">
        /// true 表示返回值确实代表磁盘上的状态（文件不存在 / 解析成功 / 解析失败且已备份），可以缓存并覆盖写回；
        /// false 表示只是读不到文件而临时回落的默认值，不可缓存。
        /// </param>
        private static UiConfig LoadFromDisk(out bool diskStateKnown)
        {
            diskStateKnown = false;

            string path;
            try
            {
                path = GetConfigPath();
            }
            catch (Exception ex)
            {
                LogFailure("无法定位 ui_config.json，本次使用默认设置", ex);
                return new UiConfig();
            }

            if (!File.Exists(path))
            {
                // 首次运行：默认配置就是磁盘的真实状态。
                diskStateKnown = true;
                return new UiConfig();
            }

            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                LogFailure("读取 ui_config.json 失败，本次使用默认设置且不缓存", ex);
                return new UiConfig();
            }

            try
            {
                var cfg = JsonSerializer.Deserialize<UiConfig>(json);
                if (cfg != null)
                {
                    diskStateKnown = true;
                    return cfg;
                }

                BackupBrokenConfigFile(path, "ui_config.json 反序列化结果为空", null);
            }
            catch (Exception ex)
            {
                BackupBrokenConfigFile(path, "解析 ui_config.json 失败", ex);
            }

            // 原文件已备份，按默认配置继续（与 ProfileService.LoadProfilesAsync 的语义一致）。
            diskStateKnown = true;
            return new UiConfig();
        }

        private static void SaveConfig(UiConfig cfg)
        {
            var path = GetConfigPath();

            if (_diskStateUnknown)
            {
                BackupBrokenConfigFile(path, "即将用内存配置覆盖一个此前读取失败的 ui_config.json", null);
                _diskStateUnknown = false;
            }

            var json = JsonSerializer.Serialize(cfg, SerializerOptions);
            AtomicFileWriter.WriteAllText(path, json);
            _cached = cfg;
        }

        /// <summary>备份不可用（损坏或读不出）的配置文件，命名与 ProfileService.BackupCorruptProfileFile 对齐。</summary>
        private static void BackupBrokenConfigFile(string filePath, string reason, Exception? cause)
        {
            if (!File.Exists(filePath)) return;

            try
            {
                var backupPath = $"{filePath}.corrupt-{DateTime.Now:yyyyMMddHHmmss}.bak";
                File.Copy(filePath, backupPath, overwrite: false);
                LogFailure($"{reason}，已备份原文件: {backupPath}", cause);
            }
            catch (Exception backupException)
            {
                LogFailure($"{reason}，且原文件备份失败: {filePath}", cause);
                LogFailure("损坏的 ui_config.json 备份失败", backupException);
            }
        }

        private static void LogFailure(string message, Exception? ex = null)
        {
            try
            {
                Console.WriteLine(ex == null
                    ? $"[UiPreferences] {message}"
                    : $"[UiPreferences] {message}: {ex}");
            }
            catch
            {
                // 日志通道本身失败时无处可去，只能忽略。
            }
        }

        // ── 读写入口：偏好读取不允许抛异常（调用点多在窗口构造与启动路径上），失败一律留痕后回落默认值 ──

        private static T Read<T>(Func<UiConfig, T> selector, T fallback, string operation)
        {
            lock (Gate)
            {
                try
                {
                    return selector(GetOrLoadConfig());
                }
                catch (Exception ex)
                {
                    LogFailure($"{operation} 失败，回落默认值", ex);
                    return fallback;
                }
            }
        }

        private static void Write(Action<UiConfig> mutate, string operation)
        {
            lock (Gate)
            {
                try
                {
                    var cfg = GetOrLoadConfig();
                    mutate(cfg);
                    SaveConfig(cfg);
                }
                catch (Exception ex)
                {
                    LogFailure($"{operation} 失败，本次设置未能写入磁盘", ex);
                }
            }
        }

        // ── 语言 ──

        public static bool TryLoadEnglish(out bool isEnglish)
        {
            var result = Read(cfg =>
            {
                var lang = cfg.Language?.Trim();
                if (string.IsNullOrEmpty(lang)) return (Found: false, IsEnglish: false);
                var english = string.Equals(lang, "en-US", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);
                return (Found: true, IsEnglish: english);
            }, (Found: false, IsEnglish: false), "读取语言设置");

            isEnglish = result.IsEnglish;
            return result.Found;
        }

        public static void SaveEnglish(bool isEnglish)
        {
            Write(cfg => cfg.Language = isEnglish ? "en-US" : "zh-CN", "保存语言设置");
        }

        // ── 背景设置 ──

        public static BackgroundSettings LoadBackground()
        {
            return Read(cfg => new BackgroundSettings
            {
                Mode = (BackgroundMode)cfg.BackgroundMode,
                ImagePath = cfg.BackgroundImagePath,
                SolidColor = cfg.BackgroundSolidColor ?? "#030303",
                Opacity = cfg.BackgroundOpacity,
                BlurRadius = cfg.BackgroundBlurRadius,
                ApplyToDialogs = cfg.ApplyToDialogs
            }, new BackgroundSettings(), "读取背景设置");
        }

        public static void SaveBackground(BackgroundSettings bg)
        {
            Write(cfg =>
            {
                cfg.BackgroundMode = (int)bg.Mode;
                cfg.BackgroundImagePath = bg.ImagePath;
                cfg.BackgroundSolidColor = bg.SolidColor;
                cfg.BackgroundOpacity = bg.Opacity;
                cfg.BackgroundBlurRadius = bg.BlurRadius;
                cfg.ApplyToDialogs = bg.ApplyToDialogs;
            }, "保存背景设置");
        }

        // ── 关闭行为 ──

        public static CloseAction LoadCloseAction()
        {
            return Read(cfg => (CloseAction)cfg.CloseActionValue, CloseAction.Ask, "读取关闭行为");
        }

        public static void SaveCloseAction(CloseAction action)
        {
            Write(cfg => cfg.CloseActionValue = (int)action, "保存关闭行为");
        }

        public static bool LoadPluginEnabled()
        {
            return Read(cfg => cfg.PluginSystemEnabled, false, "读取插件系统开关");
        }

        public static void SavePluginEnabled(bool enabled)
        {
            Write(cfg => cfg.PluginSystemEnabled = enabled, "保存插件系统开关");
        }

        // ── 部署设置 ──

        public static DeploymentBackendType LoadDeployBackend()
        {
            return Read(cfg =>
            {
                var backend = (DeploymentBackendType)cfg.DeployBackendType;
                // Symlink 已下线（普通用户需开发者模式/管理员权限，实际不可用），旧配置回落 Copy
                return backend is DeploymentBackendType.Copy or DeploymentBackendType.HardLink
                    ? backend
                    : DeploymentBackendType.Copy;
            }, DeploymentBackendType.Copy, "读取部署后端");
        }

        public static void SaveDeployBackend(DeploymentBackendType backend)
        {
            Write(cfg => cfg.DeployBackendType = (int)backend, "保存部署后端");
        }

        public static bool LoadDeployConfirm()
        {
            return Read(cfg => cfg.DeployConfirm, true, "读取部署确认设置");
        }

        public static void SaveDeployConfirm(bool confirm)
        {
            Write(cfg => cfg.DeployConfirm = confirm, "保存部署确认设置");
        }

        public static bool LoadAutoDeploy()
        {
            return Read(cfg => cfg.AutoDeploy, true, "读取自动部署设置");
        }

        public static void SaveAutoDeploy(bool auto)
        {
            Write(cfg => cfg.AutoDeploy = auto, "保存自动部署设置");
        }

        public static string? LoadRepositoryRoot()
        {
            return Read<string?>(cfg =>
            {
                var path = cfg.RepositoryRoot?.Trim();
                return string.IsNullOrWhiteSpace(path) ? null : path;
            }, null, "读取仓库根目录");
        }

        public static void SaveRepositoryRoot(string? path)
        {
            Write(cfg => cfg.RepositoryRoot = string.IsNullOrWhiteSpace(path) ? null : path.Trim(), "保存仓库根目录");
        }
    }
}
