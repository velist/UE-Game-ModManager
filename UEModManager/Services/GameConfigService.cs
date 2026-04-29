using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Models;
using UEModManager.Services.Detection;
using AppConfig = UEModManager.Models.AppConfig;

namespace UEModManager.Services
{
    /// <summary>
    /// 游戏配置服务。
    /// 从 MainWindow.xaml.cs 提取的配置加载/保存和游戏管理逻辑。
    /// </summary>
    public class GameConfigService
    {
        private readonly ILogger<GameConfigService> _logger;
        private readonly string _configFilePath;

        public AppConfig Config { get; private set; } = new();

        // ─── 便捷属性 ───

        public string CurrentGameName => Config.GameName ?? string.Empty;
        public string CurrentGamePath => Config.GamePath ?? string.Empty;
        public string CurrentModPath => Config.ModPath ?? string.Empty;
        public string CurrentBackupPath => Config.BackupPath ?? string.Empty;
        public string CurrentExecutableName => Config.ExecutableName ?? string.Empty;

        /// <summary>
        /// 获取指定游戏的图标路径。
        /// </summary>
        public string? GetGameIconPath(string gameName)
        {
            Config.GameIcons ??= new Dictionary<string, string>();
            return Config.GameIcons.TryGetValue(gameName, out var path) ? path : null;
        }

        /// <summary>
        /// 设置指定游戏的图标路径。
        /// </summary>
        public async Task SetGameIconAsync(string gameName, string? iconPath)
        {
            Config.GameIcons ??= new Dictionary<string, string>();
            if (string.IsNullOrEmpty(iconPath))
                Config.GameIcons.Remove(gameName);
            else
                Config.GameIcons[gameName] = iconPath;
            await SaveConfigAsync();
        }

        /// <summary>
        /// 当前游戏类型（由 GameName 推导）。
        /// </summary>
        public GameType CurrentGameType => DetermineGameType(CurrentGameName);

        /// <summary>
        /// 当前游戏的引擎类型。
        /// </summary>
        public EngineType CurrentEngineType => GetEngineType(CurrentGameName);

        /// <summary>
        /// 当前游戏的引擎配置档案。
        /// </summary>
        public EngineProfile CurrentEngineProfile => EngineProfile.Get(CurrentEngineType);

        /// <summary>
        /// 配置变更事件。
        /// </summary>
        public event Action? ConfigChanged;

        public GameConfigService(ILogger<GameConfigService> logger)
        {
            _logger = logger;
            _configFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        }

        // ─── 配置加载/保存 ───

        /// <summary>
        /// 从 config.json 加载配置。
        /// </summary>
        public Task LoadConfigAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    if (!File.Exists(_configFilePath))
                    {
                        _logger.LogInformation("配置文件不存在，使用默认设置");
                        return;
                    }

                    var json = File.ReadAllText(_configFilePath);
                    var config = JsonSerializer.Deserialize<AppConfig>(json);
                    if (config == null) return;

                    Config = config;

                    // 修复旧版本备份路径
                    if (!string.IsNullOrEmpty(Config.BackupPath) && Config.BackupPath.Contains("net6.0-windows"))
                    {
                        Config.BackupPath = Config.BackupPath.Replace("net6.0-windows", "net8.0-windows");
                        SaveConfigSync();
                        _logger.LogInformation("已自动修正备份路径");
                    }

                    _logger.LogInformation("配置加载成功: 游戏={Game}, 路径={Path}",
                        Config.GameName, Config.GamePath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "加载配置失败");
                }
            });
        }

        /// <summary>
        /// 保存当前配置到 config.json。
        /// </summary>
        public Task SaveConfigAsync()
        {
            return Task.Run(() => SaveConfigSync());
        }

        private void SaveConfigSync()
        {
            try
            {
                var json = JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_configFilePath, json);
                _logger.LogInformation("配置已保存");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存配置失败");
            }
        }

        // ─── 游戏切换 ───

        /// <summary>
        /// 切换当前游戏，更新配置。
        /// </summary>
        public async Task SwitchGameAsync(string gameName, string gamePath, string modPath, string backupPath)
        {
            Config.GameName = gameName;
            Config.GamePath = gamePath;
            Config.ModPath = modPath;
            Config.BackupPath = backupPath;
            Config.ExecutableName = null; // 重置，让自动检测重新查找

            // 确保备份目录存在
            if (!string.IsNullOrEmpty(backupPath) && !Directory.Exists(backupPath))
                Directory.CreateDirectory(backupPath);

            await SaveConfigAsync();
            ConfigChanged?.Invoke();

            _logger.LogInformation("已切换到游戏: {Game}", gameName);
        }

        /// <summary>
        /// 获取可用的游戏列表（内置 + 自定义）。
        /// </summary>
        public List<string> GetAvailableGames()
        {
            var builtIn = new List<string>
            {
                "剑星",
                "剑星 (CNS)",
                "黑神话·悟空",
                "光与影：33号远征队",
                "明末·渊虚之羽",
                "无主之地4",
                "杀戮尖塔2",
                "死亡搁浅2"
            };

            if (Config.CustomGames?.Count > 0)
                builtIn.AddRange(Config.CustomGames);

            return builtIn;
        }

        /// <summary>
        /// 添加自定义游戏。
        /// </summary>
        public async Task AddCustomGameAsync(string name)
        {
            Config.CustomGames ??= new List<string>();
            if (!Config.CustomGames.Contains(name))
            {
                Config.CustomGames.Add(name);
                await SaveConfigAsync();
                _logger.LogInformation("已添加自定义游戏: {Name}", name);
            }
        }

        /// <summary>
        /// 移除自定义游戏。
        /// </summary>
        public async Task RemoveCustomGameAsync(string name)
        {
            if (Config.CustomGames?.Remove(name) == true)
            {
                await SaveConfigAsync();
                _logger.LogInformation("已移除自定义游戏: {Name}", name);
            }
        }

        // ─── 游戏启动 ───

        /// <summary>
        /// 启动当前游戏。返回是否成功。
        /// </summary>
        public bool LaunchGame()
        {
            try
            {
                if (string.IsNullOrEmpty(Config.GamePath) || !Directory.Exists(Config.GamePath))
                {
                    _logger.LogWarning("游戏路径无效");
                    return false;
                }

                string? exePath = null;

                // 1. 使用保存的可执行文件
                if (!string.IsNullOrEmpty(Config.ExecutableName))
                {
                    var path = Path.Combine(Config.GamePath, Config.ExecutableName);
                    if (File.Exists(path))
                        exePath = path;
                }

                // 2. 自动检测
                if (string.IsNullOrEmpty(exePath))
                {
                    var detected = AutoDetectExecutable(Config.GamePath, Config.GameName ?? "");
                    if (!string.IsNullOrEmpty(detected))
                    {
                        exePath = Path.Combine(Config.GamePath, detected);
                        Config.ExecutableName = detected;
                        SaveConfigSync();
                    }
                }

                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                {
                    _logger.LogWarning("无法找到游戏可执行文件");
                    return false;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = Config.GamePath,
                    UseShellExecute = true
                });

                _logger.LogInformation("游戏已启动: {Path}", exePath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "启动游戏失败");
                return false;
            }
        }

        /// <summary>
        /// 自动检测游戏可执行文件。
        /// </summary>
        public string? AutoDetectExecutable(string gamePath, string gameName)
        {
            try
            {
                if (string.IsNullOrEmpty(gamePath) || !Directory.Exists(gamePath))
                    return null;

                var exeFiles = Directory.GetFiles(gamePath, "*.exe", SearchOption.AllDirectories);
                if (exeFiles.Length == 0)
                    return null;

                // 排除辅助工具
                var excludeKeywords = new[] { "unins", "setup", "launcher", "updater", "installer", "redist", "vcredist", "directx" };
                var validExes = exeFiles.Where(exe =>
                {
                    var fn = Path.GetFileName(exe).ToLower();
                    if (fn.Contains("crashreporter") && !gameName.Contains("无主之地") && !gameName.Contains("Borderlands"))
                        return false;
                    return !excludeKeywords.Any(kw => fn.Contains(kw));
                }).ToArray();

                if (validExes.Length == 0) return null;
                if (validExes.Length == 1) return Path.GetFileName(validExes[0]);

                // 按游戏名称匹配
                string? match = null;
                if (gameName != null && gameName.StartsWith("剑星"))
                {
                    match = validExes.FirstOrDefault(e => Path.GetFileName(e).ToLower().Contains("sb-win64-shipping"))
                        ?? validExes.FirstOrDefault(e => Path.GetFileName(e).ToLower().Contains("stellarblade"));
                }
                else if (gameName != null && gameName.StartsWith("黑神话"))
                {
                    match = validExes.FirstOrDefault(e => Path.GetFileName(e).ToLower().Contains("b1-win64-shipping"))
                        ?? validExes.FirstOrDefault(e => Path.GetFileName(e).ToLower().Contains("wukong"));
                }
                else if (gameName == "光与影：33号远征队")
                {
                    match = validExes.FirstOrDefault(e => Path.GetFileName(e).ToLower().Contains("enshrouded"));
                }
                else if (gameName != null && (gameName.Contains("明末") || gameName.Contains("渊虚之羽")))
                {
                    match = validExes.FirstOrDefault(e => Path.GetFileName(e).ToLower().Contains("project_plague-win64-shipping"))
                        ?? validExes.FirstOrDefault(e => Path.GetFileName(e).ToLower().Contains("wuchang"));
                }
                else if (gameName != null && (gameName.Contains("无主之地") || gameName.Contains("Borderlands")))
                {
                    match = validExes.FirstOrDefault(e => Path.GetFileName(e).ToLower().Contains("borderlands"));
                }

                if (match == null)
                {
                    match = validExes.FirstOrDefault(e =>
                    {
                        var fn = Path.GetFileNameWithoutExtension(e).ToLower();
                        var gn = (gameName ?? "").Split('(')[0].Trim().ToLower()
                            .Replace("：", "").Replace("·", "").Replace(" ", "");
                        return fn.Contains(gn) || gn.Contains(fn);
                    });
                }

                if (match != null) return Path.GetFileName(match);

                // 回退：选择最大的 exe
                return Path.GetFileName(validExes.OrderByDescending(e => new FileInfo(e).Length).First());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "自动检测游戏可执行文件失败");
                return null;
            }
        }

        // ─── 引擎类型 ───

        /// <summary>
        /// 内置游戏名称集合（全部为 UE 引擎）。
        /// </summary>
        private static readonly HashSet<string> BuiltInGames = new()
        {
            "剑星", "剑星 (CNS)", "黑神话·悟空", "光与影：33号远征队", "明末·渊虚之羽", "无主之地4",
            "杀戮尖塔2", "死亡搁浅2"
        };

        /// <summary>
        /// 获取指定游戏的引擎类型。内置游戏返回 UE，自定义游戏查配置。
        /// </summary>
        public EngineType GetEngineType(string gameName)
        {
            if (string.IsNullOrEmpty(gameName)) return EngineType.UnrealEngine;

            // 非 UE 引擎的内置游戏
            if (gameName == "杀戮尖塔2") return EngineType.Godot;
            if (gameName == "死亡搁浅2") return EngineType.Decima;

            if (BuiltInGames.Contains(gameName)) return EngineType.UnrealEngine;

            Config.GameEngines ??= new Dictionary<string, string>();
            return Config.GameEngines.TryGetValue(gameName, out var engineStr)
                ? EngineProfile.Parse(engineStr)
                : EngineType.UnrealEngine;
        }

        /// <summary>
        /// 保存自定义游戏的引擎类型到配置。
        /// </summary>
        public async Task SetGameEngineAsync(string gameName, EngineType engine)
        {
            Config.GameEngines ??= new Dictionary<string, string>();
            Config.GameEngines[gameName] = engine.ToString();
            await SaveConfigAsync();
            _logger.LogInformation("已设置游戏 '{Name}' 的引擎类型为 {Engine}", gameName, engine);
        }

        /// <summary>
        /// 根据目录特征自动识别游戏引擎类型。
        /// 决策树委托 Core 的 EngineDetector，本方法只负责注入"路径探测"IO。
        /// </summary>
        public static EngineType AutoDetectEngine(string gamePath)
        {
            if (string.IsNullOrEmpty(gamePath) || !Directory.Exists(gamePath))
                return EngineType.Unknown;

            return EngineDetector.Detect(
                directoryExists: rel => Directory.Exists(Path.Combine(gamePath, rel)),
                hasFileMatching: pattern => Directory.GetFiles(gamePath, pattern, SearchOption.TopDirectoryOnly).Length > 0,
                hasDirectoryMatching: pattern => Directory.GetDirectories(gamePath, pattern, SearchOption.TopDirectoryOnly).Length > 0);
        }

        // ─── 插件路径 ───

        /// <summary>
        /// 获取指定游戏的默认插件路径。
        /// </summary>
        public string GetPluginPath(string gameName)
        {
            Config.PluginPaths ??= new Dictionary<string, string>();
            return Config.PluginPaths.TryGetValue(gameName, out var path) ? path : string.Empty;
        }

        /// <summary>
        /// 保存指定游戏的默认插件路径。
        /// </summary>
        public async Task SetPluginPathAsync(string gameName, string path)
        {
            Config.PluginPaths ??= new Dictionary<string, string>();
            Config.PluginPaths[gameName] = path;
            await SaveConfigAsync();
        }

        // ─── 工具方法 ───

        /// <summary>
        /// 根据游戏名称推导 GameType。
        /// </summary>
        public static GameType DetermineGameType(string gameName)
        {
            if (string.IsNullOrEmpty(gameName)) return GameType.Other;
            if (gameName.Contains("CNS")) return GameType.StellarBladeCNS;
            if (gameName.StartsWith("剑星")) return GameType.StellarBlade;
            if (gameName.Contains("黑神话") || gameName.Contains("悟空")) return GameType.BlackMythWukong;
            if (gameName.Contains("光与影") || gameName.Contains("Enshrouded")) return GameType.Enshrouded;
            if (gameName.Contains("明末") || gameName.Contains("渊虚之羽")) return GameType.WuchangFallenFeathers;
            if (gameName.Contains("无主之地") || gameName.Contains("Borderlands")) return GameType.Borderlands4;
            return GameType.Other;
        }

        /// <summary>
        /// 规范化游戏名称。委托 Core 的 GameNameNormalizer。
        /// </summary>
        public static string NormalizeGameName(string name)
            => GameNameNormalizer.Normalize(name);
    }
}
