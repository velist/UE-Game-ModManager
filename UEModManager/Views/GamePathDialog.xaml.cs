using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Microsoft.Extensions.DependencyInjection;
using System.Windows.Media;
using System.Windows.Controls;
using UEModManager.Models;
using UEModManager.Services;

namespace UEModManager.Views
{
    public partial class GamePathDialog : Window, INotifyPropertyChanged
    {
        private string _gameName = "";
        private string _gamePath = "";
        private string _modPath = "";
        private string _backupPath = "";
        private string _executableName = "";
        private string _gameIconPath = "";
        private bool _isPathsValid;

        public string GameName
        {
            get => _gameName;
            set
            {
                _gameName = value;
                OnPropertyChanged(nameof(GameName));
            }
        }

        // 标题栏按钮命令处理（Qt6风格自绘）
        private void OnMinimizeWindow(object sender, ExecutedRoutedEventArgs e)
        {
            try { SystemCommands.MinimizeWindow(this); } catch { }
        }
        private void OnMaximizeWindow(object sender, ExecutedRoutedEventArgs e)
        {
            try { SystemCommands.MaximizeWindow(this); } catch { }
        }
        private void OnRestoreWindow(object sender, ExecutedRoutedEventArgs e)
        {
            try { SystemCommands.RestoreWindow(this); } catch { }
        }
        private void OnCloseWindow(object sender, ExecutedRoutedEventArgs e)
        {
            try { SystemCommands.CloseWindow(this); } catch { }
        }

        public string GamePath
        {
            get => _gamePath;
            set
            {
                _gamePath = value;
                OnPropertyChanged(nameof(GamePath));
                ValidatePaths();
            }
        }

        public string ModPath
        {
            get => _modPath;
            set
            {
                _modPath = value;
                OnPropertyChanged(nameof(ModPath));
                ValidatePaths();
            }
        }

        public string BackupPath
        {
            get => _backupPath;
            set
            {
                _backupPath = value;
                OnPropertyChanged(nameof(BackupPath));
                ValidatePaths();
            }
        }

        /// <summary>
        /// 游戏执行程序名称
        /// </summary>
        public string ExecutableName
        {
            get => _executableName;
            set
            {
                _executableName = value;
                OnPropertyChanged(nameof(ExecutableName));
            }
        }

        public bool IsPathsValid
        {
            get => _isPathsValid;
            set
            {
                _isPathsValid = value;
                OnPropertyChanged(nameof(IsPathsValid));
            }
        }

        /// <summary>
        /// 游戏图标路径（非必填）。
        /// </summary>
        public string GameIconPath
        {
            get => _gameIconPath;
            set
            {
                _gameIconPath = value;
                OnPropertyChanged(nameof(GameIconPath));
            }
        }

        public GamePathDialog(string gameName)
        {
            InitializeComponent();
            DataContext = this;
            GameName = gameName;

            // 加载已有的游戏图标
            LoadExistingGameIcon(gameName);

            // 自动搜索路径
            _ = AutoSearchPaths();
        }

        private void LoadExistingGameIcon(string gameName)
        {
            try
            {
                var sp = (Application.Current as App)?.ServiceProvider;
                var gameConfig = sp?.GetService(typeof(UEModManager.Services.GameConfigService)) as UEModManager.Services.GameConfigService;
                var iconPath = gameConfig?.GetGameIconPath(gameName);
                if (!string.IsNullOrEmpty(iconPath) && System.IO.File.Exists(iconPath))
                {
                    GameIconPath = iconPath;
                    ShowIconPreview(iconPath);
                }
            }
            catch { }
        }

        private void GameIcon_Click(object sender, RoutedEventArgs e)
        {
            BrowseGameIcon();
        }

        private void GameIcon_Click(object sender, MouseButtonEventArgs e)
        {
            BrowseGameIcon();
        }

        private void BrowseGameIcon()
        {
            try
            {
                var destPath = UEModManager.Infrastructure.GameIconPicker.BrowseAndCopy(this, GameName);
                if (string.IsNullOrEmpty(destPath)) return;

                GameIconPath = destPath;
                ShowIconPreview(destPath);
            }
            catch (Exception ex)
            {
                CyberMessageBox.Show(this, $"\u8bbe\u7f6e\u56fe\u6807\u5931\u8d25: {ex.Message}", "\u9519\u8bef",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ClearGameIcon_Click(object sender, RoutedEventArgs e)
        {
            GameIconPath = "";
            GameIconPreview.Source = null;
            GameIconPreview.Visibility = Visibility.Collapsed;
            GameIconPlaceholder.Visibility = Visibility.Visible;
            ClearIconBtn.Visibility = Visibility.Collapsed;
        }

        private void ShowIconPreview(string path)
        {
            try
            {
                var bitmap = UEModManager.Infrastructure.ImageLoader.LoadFrozen(path, decodePixelWidth: 128);
                if (bitmap == null) return;

                GameIconPreview.Source = bitmap;
                GameIconPreview.Visibility = Visibility.Visible;
                GameIconPlaceholder.Visibility = Visibility.Collapsed;
                ClearIconBtn.Visibility = Visibility.Visible;
            }
            catch { }
        }
        private sealed class AutoSearchResult
        {
            public string GamePath { get; init; } = string.Empty;
            public string ModPath { get; init; } = string.Empty;
            public string BackupPath { get; init; } = string.Empty;
            public string ExecutableName { get; init; } = string.Empty;
            public string StatusText { get; init; } = string.Empty;
        }

        private async System.Threading.Tasks.Task AutoSearchPaths()
        {
            var gameName = GameName;
            var engineType = GetConfiguredEngineType();

            try
            {
                var result = await System.Threading.Tasks.Task.Run(() => BuildAutoSearchResult(gameName, engineType));
                await Dispatcher.InvokeAsync(() => ApplyAutoSearchResult(result));
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    SearchStatusText.Text = $"\u641c\u7d22\u5931\u8d25: {ex.Message}";
                });
            }
        }

        private EngineType GetConfiguredEngineType()
        {
            try
            {
                var gameConfig = (App.Current as App)?.ServiceProvider?.GetService<GameConfigService>();
                return gameConfig?.GetEngineType(GameName) ?? EngineType.UnrealEngine;
            }
            catch
            {
                return EngineType.UnrealEngine;
            }
        }

        private AutoSearchResult BuildAutoSearchResult(string gameName, EngineType engineType)
        {
            var gameSearchPaths = GetGameSearchPaths(gameName);
            var foundGamePath = string.Empty;
            var foundExePath = string.Empty;
            var modPath = string.Empty;

            foreach (var searchPath in gameSearchPaths)
            {
                if (!Directory.Exists(searchPath))
                {
                    continue;
                }

                var exeFiles = EnumerateExecutableFiles(searchPath);
                if (exeFiles.Length == 0)
                {
                    continue;
                }

                var mainExe = FindMainGameExecutable(exeFiles, gameName);
                if (!string.IsNullOrEmpty(mainExe))
                {
                    foundExePath = mainExe;
                    foundGamePath = Path.GetDirectoryName(mainExe) ?? string.Empty;
                    break;
                }
            }

            string statusText;
            if (!string.IsNullOrEmpty(foundGamePath) && !string.IsNullOrEmpty(foundExePath))
            {
                modPath = DeduceModPathFromExe(foundExePath, gameName);
                statusText = $"\u2713 \u627e\u5230\u6e38\u620f: {Path.GetFileName(foundExePath)}";
            }
            else
            {
                var fallbackGamePath = gameSearchPaths.FirstOrDefault(Directory.Exists) ?? string.Empty;
                if (!string.IsNullOrEmpty(fallbackGamePath))
                {
                    foundGamePath = fallbackGamePath;
                    var modSearchPaths = GetModSearchPaths(fallbackGamePath);
                    modPath = modSearchPaths.FirstOrDefault(Directory.Exists) ?? string.Empty;

                    if (string.IsNullOrEmpty(modPath))
                    {
                        var exeInFallback = FindGameExecutableInPath(fallbackGamePath);
                        if (!string.IsNullOrEmpty(exeInFallback))
                        {
                            modPath = DeduceModPathFromExe(exeInFallback, gameName);
                        }
                    }

                    if (string.IsNullOrEmpty(modPath))
                    {
                        modPath = ResolveCommonModPath(fallbackGamePath, engineType);
                    }

                    EnsureDirectoryIfPossible(modPath);
                    statusText = "\u26a0 \u627e\u5230\u6e38\u620f\u76ee\u5f55\uff0c\u4f46\u672a\u5b9a\u4f4d\u5230exe\u6587\u4ef6";
                }
                else
                {
                    statusText = "\u2717 \u672a\u627e\u5230\u6e38\u620f\uff0c\u8bf7\u624b\u52a8\u9009\u62e9";
                }
            }

            var backupPath = ResolveBackupPath(gameName);
            return new AutoSearchResult
            {
                GamePath = foundGamePath,
                ModPath = modPath,
                BackupPath = backupPath,
                ExecutableName = string.IsNullOrEmpty(foundExePath) ? string.Empty : Path.GetFileName(foundExePath),
                StatusText = statusText
            };
        }

        private void ApplyAutoSearchResult(AutoSearchResult result)
        {
            if (!string.IsNullOrEmpty(result.GamePath))
            {
                GamePath = result.GamePath;
                GamePathTextBox.Text = result.GamePath;
            }

            if (!string.IsNullOrEmpty(result.ExecutableName))
            {
                ExecutableName = result.ExecutableName;
            }

            if (!string.IsNullOrEmpty(result.ModPath))
            {
                ModPath = result.ModPath;
                ModPathTextBox.Text = result.ModPath;
            }

            if (!string.IsNullOrEmpty(result.BackupPath))
            {
                BackupPath = result.BackupPath;
                BackupPathTextBox.Text = result.BackupPath;
            }

            SearchStatusText.Text = result.StatusText;
        }

        private string ResolveBackupPath(string gameName)
        {
            var currentDir = AppDomain.CurrentDomain.BaseDirectory;
            var backupDir = Path.Combine(currentDir, "Backups", $"{gameName}_\u5907\u4efd");

            if (EnsureDirectoryIfPossible(backupDir))
            {
                return backupDir;
            }

            var fallbackDir = Path.Combine(currentDir, "Backups");
            if (EnsureDirectoryIfPossible(fallbackDir))
            {
                return fallbackDir;
            }

            return currentDir;
        }

        private string ResolveCommonModPath(string gameBasePath, EngineType engineType)
        {
            var commonModPaths = engineType == EngineType.UnrealEngine
                ? new List<string>
                {
                    Path.Combine(gameBasePath, "Content", "Paks", "~mods"),
                    Path.Combine(gameBasePath, "Content", "Paks", "Mods"),
                    Path.Combine(gameBasePath, "Content", "Paks", "mods"),
                    Path.Combine(gameBasePath, "OakGame", "Content", "Paks", "~mods"),
                    Path.Combine(gameBasePath, "OakGame", "Content", "Paks", "Mods"),
                    Path.Combine(gameBasePath, "OakGame", "Content", "Paks", "mods"),
                    Path.Combine(gameBasePath, "Content", "Paks")
                }
                : EngineProfile.Get(engineType).DefaultModPathPatterns
                    .Select(p => Path.Combine(gameBasePath, p.Replace('/', Path.DirectorySeparatorChar)))
                    .ToList();

            var existingModPath = commonModPaths.FirstOrDefault(Directory.Exists);
            if (!string.IsNullOrEmpty(existingModPath))
            {
                return existingModPath;
            }

            if (engineType == EngineType.UnrealEngine)
            {
                var oakGameDir = Path.Combine(gameBasePath, "OakGame");
                return Directory.Exists(oakGameDir)
                    ? Path.Combine(oakGameDir, "Content", "Paks", "~mods")
                    : Path.Combine(gameBasePath, "Content", "Paks", "~mods");
            }

            return commonModPaths.FirstOrDefault() ?? Path.Combine(gameBasePath, "Mods");
        }

        private bool EnsureDirectoryIfPossible(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                Directory.CreateDirectory(path);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARNING] Create directory failed: {ex.Message}");
                return false;
            }
        }

        private string[] EnumerateExecutableFiles(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            {
                return Array.Empty<string>();
            }

            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    ReturnSpecialDirectories = false
                };
                return Directory.EnumerateFiles(rootPath, "*.exe", options).ToArray();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARNING] Enumerate executables failed: {ex.Message}");
                return Array.Empty<string>();
            }
        }


        /// <summary>
        /// 找到主要的游戏可执行文件
        /// </summary>
        private string FindMainGameExecutable(string[] exeFiles, string gameName)
        {
            // 排除常见的辅助工具和安装程序
            var excludeKeywords = new[] { "unins", "setup", "launcher", "updater", "installer", "redist", "vcredist", "directx" };
            
            var validExes = exeFiles.Where(exe =>
            {
                var fileName = Path.GetFileName(exe).ToLower();
                return !excludeKeywords.Any(keyword => fileName.Contains(keyword));
            }).ToArray();
            
            if (validExes.Length == 0) return "";
            if (validExes.Length == 1) return validExes[0];
            
            // 提取游戏名称的核心部分（去掉括号内容）
            var coreGameName = gameName.Split('(')[0].Trim();
            
            // 根据游戏名称查找最匹配的exe
            var gameSpecificExe = coreGameName switch
            {
                "剑星" => validExes.FirstOrDefault(exe => Path.GetFileName(exe).ToLower().Contains("sb-win64-shipping")) ??
                         validExes.FirstOrDefault(exe => Path.GetFileName(exe).ToLower().Contains("stellarblade")) ??
                         validExes.FirstOrDefault(exe => Path.GetFileName(exe).ToLower().Contains("stellar")),
                
                "黑神话·悟空" => validExes.FirstOrDefault(exe => Path.GetFileName(exe).ToLower().Contains("b1-win64-shipping")) ??
                               validExes.FirstOrDefault(exe => Path.GetFileName(exe).ToLower().Contains("wukong")) ??
                               validExes.FirstOrDefault(exe => Path.GetFileName(exe).ToLower().Contains("blackmyth")),

                "明末·渊虚之羽" => validExes.FirstOrDefault(exe => Path.GetFileName(exe).ToLower().Contains("project_plague-win64-shipping")) ??
                                validExes.FirstOrDefault(exe => Path.GetFileName(exe).ToLower().Contains("project_plague")) ??
                                validExes.FirstOrDefault(exe => Path.GetFileName(exe).ToLower().Contains("wuchang")),
                
                _ => validExes.FirstOrDefault(exe => 
                     {
                         var fileName = Path.GetFileNameWithoutExtension(exe).ToLower();
                         var gameNameLower = coreGameName.ToLower();
                         return fileName.Contains(gameNameLower) || gameNameLower.Contains(fileName);
                     })
            };
            
            if (!string.IsNullOrEmpty(gameSpecificExe))
                return gameSpecificExe;
            
            // 选择最大的exe文件（通常是主程序）
            return validExes.OrderByDescending(exe => new FileInfo(exe).Length).First();
        }

        /// <summary>
        /// 从exe路径推导MOD路径
        /// </summary>
        private string DeduceModPathFromExe(string exePath, string gameName)
        {
            try
            {
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                    return "";

                var exeDir = Path.GetDirectoryName(exePath);
                var exeFileName = Path.GetFileName(exePath);
                
                Console.WriteLine($"[DEBUG] 推导MOD路径 - exe: {exeFileName}, 目录: {exeDir}");

                // 定义虚幻引擎游戏的MOD路径模式
                var modPathPatterns = new List<string>();

                // 检测exe是否在 Binaries\Win64 目录中
                if (exeDir.EndsWith("Binaries\\Win64", StringComparison.OrdinalIgnoreCase))
                {
                    string gameRootDir = null;

                    // 检查是否是Engine\Binaries\Win64结构（如无主之地4）
                    if (exeDir.EndsWith("Engine\\Binaries\\Win64", StringComparison.OrdinalIgnoreCase))
                    {
                        // 获取游戏根目录（往上三级：Engine\Binaries\Win64 -> 游戏根目录）
                        gameRootDir = Directory.GetParent(exeDir)?.Parent?.Parent?.FullName;

                        if (!string.IsNullOrEmpty(gameRootDir))
                        {
                            // 无主之地4特殊处理：查找OakGame目录
                            if (gameName.Contains("无主之地4") || gameName.Contains("Borderlands 4"))
                            {
                                var oakGameDir = Path.Combine(gameRootDir, "OakGame");
                                if (Directory.Exists(oakGameDir))
                                {
                                    modPathPatterns.AddRange(new[]
                                    {
                                        Path.Combine(oakGameDir, "Content", "Paks", "~mods"),
                                        Path.Combine(oakGameDir, "Content", "Paks", "Mods"),
                                        Path.Combine(oakGameDir, "Content", "Paks", "mods"),
                                        Path.Combine(oakGameDir, "Content", "Paks")
                                    });
                                }
                            }
                            else
                            {
                                // 其他游戏的Engine目录结构处理
                                modPathPatterns.AddRange(new[]
                                {
                                    Path.Combine(gameRootDir, "Content", "Paks", "~mods"),
                                    Path.Combine(gameRootDir, "Content", "Paks", "Mods"),
                                    Path.Combine(gameRootDir, "Content", "Paks", "mods"),
                                    Path.Combine(gameRootDir, "Content", "Paks")
                                });
                            }
                        }

                        Console.WriteLine($"[DEBUG] Engine结构检测到游戏根目录: {gameRootDir}");
                    }

                    // 获取游戏项目目录（往上两级：Binaries\Win64 -> 项目根目录）
                    var gameProjectDir = Directory.GetParent(exeDir)?.Parent?.FullName;

                    if (gameRootDir == null)
                    {
                        if (!string.IsNullOrEmpty(gameProjectDir))
                        {
                        // 根据游戏类型选择不同的MOD路径模式
                        if (gameName.Contains("CNS模式"))
                        {
                            // CNS模式专用路径
                            modPathPatterns.AddRange(new[]
                            {
                                Path.Combine(gameProjectDir, "Content", "Paks", "~mods", "CustomNanosuitSystem"),
                                Path.Combine(gameProjectDir, "Content", "Paks", "Mods", "CustomNanosuitSystem"),
                                Path.Combine(gameProjectDir, "Content", "Paks", "mods", "CustomNanosuitSystem"),
                                Path.Combine(gameProjectDir, "Content", "Paks", "CustomNanosuitSystem"),
                            });
                        }
                        else
                        {
                            // 标准UE MOD路径模式
                            modPathPatterns.AddRange(new[]
                            {
                                Path.Combine(gameProjectDir, "Content", "Paks", "~mods"),      // 标准MOD路径
                                Path.Combine(gameProjectDir, "Content", "Paks", "Mods"),       // 变体1
                                Path.Combine(gameProjectDir, "Content", "Paks", "mods"),       // 变体2
                                Path.Combine(gameProjectDir, "Content", "Paks"),               // 基础Paks目录
                            });
                        }

                        Console.WriteLine($"[DEBUG] UE结构检测到游戏项目目录: {gameProjectDir}");
                        }
                    }
                    
                    // 检查是否是子目录结构（如：StellarBlade\SB\Binaries\Win64）
                    var parentDir = Directory.GetParent(gameProjectDir)?.FullName;
                    if (!string.IsNullOrEmpty(parentDir))
                    {
                        // 查找兄弟目录或其他子目录的Content\Paks
                        try
                        {
                            var siblingDirs = Directory.GetDirectories(parentDir);
                            foreach (var siblingDir in siblingDirs)
                            {
                                var contentPaks = Path.Combine(siblingDir, "Content", "Paks");
                                if (Directory.Exists(contentPaks))
                                {
                                    modPathPatterns.AddRange(new[]
                                    {
                                        Path.Combine(contentPaks, "~mods"),
                                        Path.Combine(contentPaks, "Mods"),
                                        Path.Combine(contentPaks, "mods"),
                                        contentPaks
                                    });
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[DEBUG] 搜索兄弟目录时出错: {ex.Message}");
                        }
                    }
                }

                // 特定游戏的MOD路径规则
                var gameSpecificModPaths = new Dictionary<string, List<string>>
                {
                    ["剑星"] = new List<string>
                    {
                        "SB\\Content\\Paks\\~mods",
                        "StellarBlade\\SB\\Content\\Paks\\~mods",
                        "SB\\Content\\Paks\\Mods",
                        "SB\\Content\\Paks"
                    },
                    ["剑星（CNS模式）"] = new List<string>
                    {
                        "SB\\Content\\Paks\\~mods\\CustomNanosuitSystem",
                        "StellarBlade\\SB\\Content\\Paks\\~mods\\CustomNanosuitSystem",
                        "SB\\Content\\Paks\\Mods\\CustomNanosuitSystem",
                        "SB\\Content\\Paks\\CustomNanosuitSystem"
                    },
                    ["Stellar Blade"] = new List<string>
                    {
                        "SB\\Content\\Paks\\~mods",
                        "StellarBlade\\SB\\Content\\Paks\\~mods",
                        "SB\\Content\\Paks\\Mods",
                        "SB\\Content\\Paks"
                    },
                    ["黑神话悟空"] = new List<string>
                    {
                        "b1\\Content\\Paks\\~mods",
                        "BlackMythWukong\\b1\\Content\\Paks\\~mods",
                        "b1\\Content\\Paks\\Mods",
                        "b1\\Content\\Paks"
                    },
                    ["黑神话·悟空"] = new List<string>
                    {
                        "b1\\Content\\Paks\\~mods",
                        "BlackMythWukong\\b1\\Content\\Paks\\~mods",
                        "b1\\Content\\Paks\\Mods",
                        "b1\\Content\\Paks"
                    },
                    ["Black Myth Wukong"] = new List<string>
                    {
                        "b1\\Content\\Paks\\~mods",
                        "BlackMythWukong\\b1\\Content\\Paks\\~mods",
                        "b1\\Content\\Paks\\Mods",
                        "b1\\Content\\Paks"
                    },
                    ["明末·渊虚之羽"] = new List<string>
                    {
                        "Project_Plague\\Content\\Paks\\~mods",
                        "Wuchang Fallen Feathers\\Project_Plague\\Content\\Paks\\~mods",
                        "Project_Plague\\Content\\Paks\\Mods",
                        "Project_Plague\\Content\\Paks"
                    },
                    ["无主之地4"] = new List<string>
                    {
                        "OakGame\\Content\\Paks\\~mods",
                        "Borderlands 4\\OakGame\\Content\\Paks\\~mods",
                        "OakGame\\Content\\Paks\\Mods",
                        "OakGame\\Content\\Paks"
                    },
                    ["Borderlands 4"] = new List<string>
                    {
                        "OakGame\\Content\\Paks\\~mods",
                        "Borderlands 4\\OakGame\\Content\\Paks\\~mods",
                        "OakGame\\Content\\Paks\\Mods",
                        "OakGame\\Content\\Paks"
                    }
                };

                // 提取游戏名称的核心部分（去掉括号内容）
                var coreGameName = gameName.Split('(')[0].Trim();
                
                // 添加特定游戏的路径模式
                if (gameSpecificModPaths.ContainsKey(coreGameName) || gameSpecificModPaths.ContainsKey(gameName))
                {
                    var gameRootDir = FindGameRootDirectory(exePath);
                    if (!string.IsNullOrEmpty(gameRootDir))
                    {
                        foreach (var relativePath in gameSpecificModPaths[coreGameName] ?? gameSpecificModPaths[gameName])
                        {
                            var fullPath = Path.Combine(gameRootDir, relativePath);
                            modPathPatterns.Add(fullPath);
                        }
                    }
                }

                // 测试所有可能的MOD路径
                foreach (var modPath in modPathPatterns)
                {
                    try
                    {
                        var normalizedPath = Path.GetFullPath(modPath);
                        Console.WriteLine($"[DEBUG] 测试MOD路径: {normalizedPath}");
                        
                        // 如果目录存在，直接返回
                        if (Directory.Exists(normalizedPath))
                        {
                            Console.WriteLine($"[SUCCESS] 找到现有MOD目录: {normalizedPath}");
                            return normalizedPath;
                        }
                        
                        // 如果父目录存在且是Content/Paks，这个路径很可能是正确的
                        var parentDir = Path.GetDirectoryName(normalizedPath);
                        if (Directory.Exists(parentDir))
                        {
                            // 对于CNS模式，检查~mods目录是否存在
                            if (gameName.Contains("CNS模式") && normalizedPath.Contains("CustomNanosuitSystem"))
                            {
                                // parentDir 应该是 ~mods 目录
                                if (parentDir.EndsWith("~mods", StringComparison.OrdinalIgnoreCase) ||
                                    parentDir.EndsWith("Mods", StringComparison.OrdinalIgnoreCase))
                                {
                                    Console.WriteLine($"[SUCCESS] 推导出CNS模式MOD路径: {normalizedPath}");
                                    return normalizedPath;
                                }
                            }
                            // 标准模式检查
                            else if (parentDir.EndsWith("Paks", StringComparison.OrdinalIgnoreCase) ||
                                     parentDir.EndsWith("Content", StringComparison.OrdinalIgnoreCase))
                            {
                                Console.WriteLine($"[SUCCESS] 推导出可能的MOD路径: {normalizedPath}");
                                return normalizedPath;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[DEBUG] 测试路径 {modPath} 时出错: {ex.Message}");
                    }
                }

                Console.WriteLine($"[WARNING] 未能推导出MOD路径");
                return "";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] 推导MOD路径失败: {ex.Message}");
                return "";
            }
        }

        // 查找游戏根目录
        private string FindGameRootDirectory(string exePath)
        {
            try
            {
                var currentDir = Path.GetDirectoryName(exePath);
                
                // 向上查找，直到找到看起来像游戏根目录的地方
                while (!string.IsNullOrEmpty(currentDir))
                {
                    var dirName = Path.GetFileName(currentDir);
                    
                    // 如果是Steam游戏目录或其他游戏分发平台的特征
                    var parentDirName = Path.GetFileName(Path.GetDirectoryName(currentDir)) ?? "";
                    
                    if (parentDirName.Equals("steamapps", StringComparison.OrdinalIgnoreCase) ||
                        parentDirName.Equals("common", StringComparison.OrdinalIgnoreCase) ||
                        dirName.Contains("StellarBlade") ||
                        dirName.Contains("BlackMythWukong") ||
                        dirName.Contains("Wukong") ||
                        dirName.Contains("Wuchang") ||
                        dirName.Contains("Fallen Feathers") ||
                        dirName.Contains("Borderlands"))
                    {
                        return currentDir;
                    }

                    currentDir = Path.GetDirectoryName(currentDir);
                }
                
                // 如果没找到特殊标记，返回exe文件往上3级的目录 (Win64/Binaries/ProjectDir)
                var fallbackDir = Path.GetDirectoryName(exePath);
                for (int i = 0; i < 3 && !string.IsNullOrEmpty(fallbackDir); i++)
                {
                    fallbackDir = Path.GetDirectoryName(fallbackDir);
                }
                
                return fallbackDir ?? "";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] 查找游戏根目录失败: {ex.Message}");
                return "";
            }
        }

        private string[] GetGameSearchPaths(string gameName)
        {
            var searchPaths = new List<string>();
            
            // 1. Steam路径检测
            var steamPaths = GetSteamGamePaths(gameName);
            searchPaths.AddRange(steamPaths);
            
            // 2. Epic Games路径检测
            var epicPaths = GetEpicGamePaths(gameName);
            searchPaths.AddRange(epicPaths);
            
            // 3. 通用游戏路径
            var commonPaths = new[]
            {
                @"C:\Program Files (x86)\Steam\steamapps\common",
                @"C:\Program Files\Steam\steamapps\common",
                @"D:\Steam\steamapps\common",
                @"E:\Steam\steamapps\common",
                @"C:\Program Files\Epic Games",
                @"D:\Epic Games",
                @"E:\Epic Games",
                @"C:\Program Files",
                @"C:\Program Files (x86)",
                @"D:\Games",
                @"E:\Games"
            };

            var gameKeywords = gameName switch
            {
                "剑星" => new[] { "Stellar Blade", "StellarBlade", "剑星" },
                "剑星（CNS模式）" => new[] { "Stellar Blade", "StellarBlade", "剑星" },
                "黑神话·悟空" => new[] { "Black Myth Wukong", "BlackMythWukong", "Black Myth- Wukong", "Wukong", "黑神话", "悟空" },
                "明末·渊虚之羽" => new[] { "Wuchang Fallen Feathers", "WuchangFallenFeathers", "Wuchang", "明末", "渊虚之羽" },
                "光与影：33号远征队" => new[] { "Enshrouded", "光与影", "33号远征队" },
                "艾尔登法环" => new[] { "Elden Ring", "EldenRing", "艾尔登法环" },
                "赛博朋克2077" => new[] { "Cyberpunk 2077", "Cyberpunk2077", "赛博朋克" },
                "巫师3" => new[] { "The Witcher 3 Wild Hunt", "Witcher3", "巫师3" },
                "无主之地4" => new[] { "Borderlands 4", "Borderlands4", "BorderLands 4", "BorderLands4", "无主之地4", "无主之地 4" },
                _ => new[] { gameName, gameName.Replace(" ", ""), gameName.Replace(" ", "_") }
            };

            foreach (var basePath in commonPaths)
            {
                if (Directory.Exists(basePath))
                {
                    foreach (var keyword in gameKeywords)
                    {
                        try
                        {
                            var directories = Directory.GetDirectories(basePath, $"*{keyword}*", SearchOption.TopDirectoryOnly);
                            foreach (var dir in directories)
                            {
                                // 验证是否确实是游戏目录（包含exe文件）
                                if (HasGameExecutable(dir))
                                {
                                    searchPaths.Add(dir);
                                }
                            }
                        }
                        catch { }
                    }
                }
            }

            return searchPaths.Distinct().ToArray();
        }

        private string[] GetSteamGamePaths(string gameName)
        {
            var steamPaths = new List<string>();
            
            try
            {
                // 从注册表获取Steam路径
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                var steamPath = key?.GetValue("SteamPath")?.ToString();
                
                if (!string.IsNullOrEmpty(steamPath))
                {
                    // 检查主Steam库
                    var commonPath = Path.Combine(steamPath, "steamapps", "common");
                    if (Directory.Exists(commonPath))
                    {
                        var gameFolders = GetGameKeywords(gameName);
                        foreach (var folder in gameFolders)
                        {
                            var gamePath = Path.Combine(commonPath, folder);
                            if (Directory.Exists(gamePath) && HasGameExecutable(gamePath))
                            {
                                steamPaths.Add(gamePath);
                            }
                        }
                    }
                    
                    // 检查其他Steam库
                    var libraryFoldersFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                    if (File.Exists(libraryFoldersFile))
                    {
                        var content = File.ReadAllText(libraryFoldersFile);
                        var pathMatches = System.Text.RegularExpressions.Regex.Matches(content, @"""path""\s*""([^""]+)""");
                        
                        foreach (System.Text.RegularExpressions.Match match in pathMatches)
                        {
                            var libraryPath = match.Groups[1].Value.Replace(@"\\", @"\");
                            var libraryCommon = Path.Combine(libraryPath, "steamapps", "common");
                            
                            if (Directory.Exists(libraryCommon))
                            {
                                var gameFolders = GetGameKeywords(gameName);
                                foreach (var folder in gameFolders)
                                {
                                    var gamePath = Path.Combine(libraryCommon, folder);
                                    if (Directory.Exists(gamePath) && HasGameExecutable(gamePath))
                                    {
                                        steamPaths.Add(gamePath);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            
            return steamPaths.ToArray();
        }

        private string[] GetEpicGamePaths(string gameName)
        {
            var epicPaths = new List<string>();
            
            try
            {
                var epicDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
                
                if (Directory.Exists(epicDataPath))
                {
                    var manifestFiles = Directory.GetFiles(epicDataPath, "*.item");
                    var gameKeywords = GetGameKeywords(gameName);
                    
                    foreach (var manifestFile in manifestFiles)
                    {
                        try
                        {
                            var content = File.ReadAllText(manifestFile);
                            var manifest = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(content);
                            
                            if (manifest.TryGetValue("InstallLocation", out var installLocationObj) &&
                                manifest.TryGetValue("DisplayName", out var displayNameObj))
                            {
                                var installLocation = installLocationObj.ToString();
                                var displayName = displayNameObj.ToString();
                                
                                if (!string.IsNullOrEmpty(installLocation) && !string.IsNullOrEmpty(displayName))
                                {
                                    foreach (var keyword in gameKeywords)
                                    {
                                        if (displayName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                                            Path.GetFileName(installLocation).Contains(keyword, StringComparison.OrdinalIgnoreCase))
                                        {
                                            if (Directory.Exists(installLocation) && HasGameExecutable(installLocation))
                                            {
                                                epicPaths.Add(installLocation);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            
            return epicPaths.ToArray();
        }

        private string[] GetGameKeywords(string gameName)
        {
            // 提取游戏名称的核心部分（去掉括号内容）
            var coreGameName = gameName.Split('(')[0].Trim();
            
            return coreGameName switch
            {
                "剑星" => new[] { "Stellar Blade", "StellarBlade", "Stellarblade" },
                "黑神话·悟空" => new[] { "Black Myth Wukong", "BlackMythWukong", "Black Myth- Wukong", "b1-win64-shipping" },
                "明末·渊虚之羽" => new[] { "Wuchang Fallen Feathers", "WuchangFallenFeathers", "Wuchang", "Project_Plague" },
                "光与影：33号远征队" => new[] { "Enshrouded" },
                "艾尔登法环" => new[] { "Elden Ring", "EldenRing" },
                "赛博朋克2077" => new[] { "Cyberpunk 2077", "Cyberpunk2077" },
                "巫师3" => new[] { "The Witcher 3 Wild Hunt", "Witcher3" },
                _ => new[] { coreGameName, coreGameName.Replace(" ", ""), coreGameName.Replace(" ", "_"), coreGameName.Replace("·", " "), gameName }
            };
        }

        private bool HasGameExecutable(string gamePath)
        {
            try
            {
                var exeFiles = EnumerateExecutableFiles(gamePath);
                return exeFiles.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private string[] GetModSearchPaths(string gamePath)
        {
            return new[]
            {
                // 最常见的虚幻引擎MOD路径
                Path.Combine(gamePath, "Game", "Content", "Paks", "~mods"),
                Path.Combine(gamePath, "Game", "Content", "Paks", "Mods"),
                Path.Combine(gamePath, "Content", "Paks", "~mods"),
                Path.Combine(gamePath, "Content", "Paks", "Mods"),
                // 简单路径
                Path.Combine(gamePath, "Mods"),
                // 直接在Paks目录（备选）
                Path.Combine(gamePath, "Game", "Content", "Paks"),
                Path.Combine(gamePath, "Content", "Paks"),
                // 游戏根目录（最后选择）
                gamePath
            };
        }

        private void ValidatePaths()
        {
            IsPathsValid = !string.IsNullOrEmpty(GamePath) && 
                          !string.IsNullOrEmpty(ModPath) && 
                          !string.IsNullOrEmpty(BackupPath) &&
                          Directory.Exists(GamePath);
        }

        private void BrowseGamePath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择游戏安装目录",
                UseDescriptionForTitle = true,
                SelectedPath = GamePath
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                GamePath = dialog.SelectedPath;
                GamePathTextBox.Text = dialog.SelectedPath;
            }
        }

        private void BrowseModPath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择MOD目录",
                UseDescriptionForTitle = true,
                SelectedPath = ModPath
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                ModPath = dialog.SelectedPath;
                ModPathTextBox.Text = dialog.SelectedPath;
            }
        }

        private void BrowseBackupPath_Click(object sender, RoutedEventArgs e)
        {
            // 确保有一个有效的初始路径
            string initialPath = BackupPath;
            if (string.IsNullOrEmpty(initialPath))
            {
                // 如果备份路径为空，使用程序安装目录下的Backups作为初始路径
                initialPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Backups");
            }
            
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择备份目录",
                UseDescriptionForTitle = true,
                SelectedPath = initialPath
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                BackupPath = dialog.SelectedPath;
                BackupPathTextBox.Text = dialog.SelectedPath;
            }
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            if (IsPathsValid)
            {
                DialogResult = true;
                Close();
            }
            else
            {
                ShowCustomMessageBox("请确保所有路径都已正确设置", "路径无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        /// <summary>
        /// 自定义深色主题MessageBox
        /// </summary>
        private MessageBoxResult ShowCustomMessageBox(string message, string title, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None)
        {
            // 根据消息长度和类型决定窗口尺寸
            int width = 450;
            int height = 250;
            
            // 对于简短的成功/信息消息，使用更小的尺寸
            if (icon == MessageBoxImage.Information && message.Length < 50)
            {
                width = 350;
                height = 200;
            }
            // 对于较长的消息（如系统状态），使用更大的尺寸
            else if (message.Length > 200)
            {
                width = 550;
                height = 350;
            }
            
            var messageWindow = new Window
            {
                Title = title,
                Width = width,
                Height = height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0B1426")),
                WindowStyle = WindowStyle.None,
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1A2332")),
                BorderThickness = new Thickness(1)
            };

            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 标题栏
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 内容
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 按钮

            // 自定义标题栏
            var titleBar = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1A2332")),
                Padding = new Thickness(15),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2A3441")),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };

            var titleGrid = new Grid();
            var titleText = new TextBlock
            {
                Text = title,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F1F5F9")),
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center
            };

            var closeButton = new Button
            {
                Content = "✕",
                Width = 30,
                Height = 30,
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9CA3AF")),
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right,
                FontSize = 14,
                Cursor = Cursors.Hand
            };

            titleGrid.Children.Add(titleText);
            titleGrid.Children.Add(closeButton);
            titleBar.Child = titleGrid;
            Grid.SetRow(titleBar, 0);

            // 内容区域
            var contentGrid = new Grid();
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 图标
            string iconText = icon switch
            {
                MessageBoxImage.Information => "ℹ️",
                MessageBoxImage.Warning => "⚠️",
                MessageBoxImage.Error => "❌",
                MessageBoxImage.Question => "❓",
                _ => "💬"
            };

            var iconBlock = new TextBlock
            {
                Text = iconText,
                FontSize = 32,
                Margin = new Thickness(20, 20, 15, 20),
                VerticalAlignment = VerticalAlignment.Top
            };
            Grid.SetColumn(iconBlock, 0);

            // 消息文本
            var messageText = new TextBlock
            {
                Text = message,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F1F5F9")),
                FontSize = 14,
                Margin = new Thickness(0, 20, 20, 20),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(messageText, 1);

            contentGrid.Children.Add(iconBlock);
            contentGrid.Children.Add(messageText);
            Grid.SetRow(contentGrid, 1);

            // 按钮区域
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(20, 0, 20, 20),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0F1B2E"))
            };

            MessageBoxResult result = MessageBoxResult.None;

            // 根据按钮类型创建按钮
            switch (buttons)
            {
                case MessageBoxButton.OK:
                    var okBtn = CreateMessageBoxButton("确定", true);
                    okBtn.Click += (s, e) => { result = MessageBoxResult.OK; messageWindow.Close(); };
                    buttonPanel.Children.Add(okBtn);
                    break;

                case MessageBoxButton.OKCancel:
                    var cancelBtn1 = CreateMessageBoxButton("取消", false);
                    var okBtn1 = CreateMessageBoxButton("确定", true);
                    cancelBtn1.Click += (s, e) => { result = MessageBoxResult.Cancel; messageWindow.Close(); };
                    okBtn1.Click += (s, e) => { result = MessageBoxResult.OK; messageWindow.Close(); };
                    buttonPanel.Children.Add(cancelBtn1);
                    buttonPanel.Children.Add(okBtn1);
                    break;

                case MessageBoxButton.YesNo:
                    var noBtn = CreateMessageBoxButton("否", false);
                    var yesBtn = CreateMessageBoxButton("是", true);
                    noBtn.Click += (s, e) => { result = MessageBoxResult.No; messageWindow.Close(); };
                    yesBtn.Click += (s, e) => { result = MessageBoxResult.Yes; messageWindow.Close(); };
                    buttonPanel.Children.Add(noBtn);
                    buttonPanel.Children.Add(yesBtn);
                    break;

                case MessageBoxButton.YesNoCancel:
                    var cancelBtn2 = CreateMessageBoxButton("取消", false);
                    var noBtn2 = CreateMessageBoxButton("否", false);
                    var yesBtn2 = CreateMessageBoxButton("是", true);
                    cancelBtn2.Click += (s, e) => { result = MessageBoxResult.Cancel; messageWindow.Close(); };
                    noBtn2.Click += (s, e) => { result = MessageBoxResult.No; messageWindow.Close(); };
                    yesBtn2.Click += (s, e) => { result = MessageBoxResult.Yes; messageWindow.Close(); };
                    buttonPanel.Children.Add(cancelBtn2);
                    buttonPanel.Children.Add(noBtn2);
                    buttonPanel.Children.Add(yesBtn2);
                    break;
            }

            Grid.SetRow(buttonPanel, 2);

            // 关闭按钮事件
            closeButton.Click += (s, e) => { result = MessageBoxResult.Cancel; messageWindow.Close(); };

            // 添加键盘支持
            messageWindow.KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Escape)
                {
                    result = MessageBoxResult.Cancel;
                    messageWindow.Close();
                }
                else if (e.Key == System.Windows.Input.Key.Enter && buttons == MessageBoxButton.OK)
                {
                    result = MessageBoxResult.OK;
                    messageWindow.Close();
                }
            };

            mainGrid.Children.Add(titleBar);
            mainGrid.Children.Add(contentGrid);
            mainGrid.Children.Add(buttonPanel);

            messageWindow.Content = mainGrid;
            messageWindow.ShowDialog();

            return result;
        }

        /// <summary>
        /// 创建MessageBox按钮
        /// </summary>
        private Button CreateMessageBoxButton(string text, bool isPrimary)
        {
            var button = new Button
            {
                Content = text,
                Width = 80,
                Height = 32,
                Margin = new Thickness(10, 0, 0, 0),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                FontSize = 13
            };

            if (isPrimary)
            {
                button.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FBBF24"));
                button.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E2A3A"));
                button.FontWeight = FontWeights.Bold;
            }
            else
            {
                button.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4A5568"));
                button.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F1F5F9"));
            }

            // 添加鼠标悬停效果
            button.MouseEnter += (s, e) =>
            {
                if (isPrimary)
                {
                    button.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"));
                }
                else
                {
                    button.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6B7280"));
                }
            };

            button.MouseLeave += (s, e) =>
            {
                if (isPrimary)
                {
                    button.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FBBF24"));
                }
                else
                {
                    button.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4A5568"));
                }
            };

            return button;
        }

        /// <summary>
        /// 在指定目录中查找游戏可执行文件
        /// </summary>
        private string FindGameExecutableInPath(string gamePath)
        {
            try
            {
                if (!Directory.Exists(gamePath))
                    return "";

                var exeFiles = EnumerateExecutableFiles(gamePath);

                // 优先查找CrashReportClient.exe（无主之地4）
                var crashReportClient = exeFiles.FirstOrDefault(exe =>
                    Path.GetFileName(exe).ToLower().Contains("crashreportclient"));
                if (!string.IsNullOrEmpty(crashReportClient))
                    return crashReportClient;

                // 查找其他游戏exe
                var gameExe = exeFiles.FirstOrDefault(exe =>
                {
                    var fileName = Path.GetFileName(exe).ToLower();
                    return !fileName.Contains("unins") &&
                           !fileName.Contains("setup") &&
                           !fileName.Contains("launcher") &&
                           !fileName.Contains("updater") &&
                           !fileName.Contains("installer");
                });

                return gameExe ?? "";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] 查找exe文件失败: {ex.Message}");
                return "";
            }
        }

        /// <summary>
        /// 设置常见的MOD路径结构
        /// </summary>
        private void SetCommonModPaths(string gameBasePath)
        {
            try
            {
                // 获取当前游戏的引擎类型
                var gameConfig = (App.Current as App)?.ServiceProvider?.GetService<GameConfigService>();
                var engineType = gameConfig?.GetEngineType(GameName) ?? EngineType.UnrealEngine;
                var engineProfile = EngineProfile.Get(engineType);

                List<string> commonModPaths;

                if (engineType == EngineType.UnrealEngine)
                {
                    // UE 引擎保留原有逻辑
                    commonModPaths = new List<string>
                    {
                        Path.Combine(gameBasePath, "Content", "Paks", "~mods"),
                        Path.Combine(gameBasePath, "Content", "Paks", "Mods"),
                        Path.Combine(gameBasePath, "Content", "Paks", "mods"),
                        Path.Combine(gameBasePath, "OakGame", "Content", "Paks", "~mods"),
                        Path.Combine(gameBasePath, "OakGame", "Content", "Paks", "Mods"),
                        Path.Combine(gameBasePath, "OakGame", "Content", "Paks", "mods"),
                        Path.Combine(gameBasePath, "Content", "Paks")
                    };
                }
                else
                {
                    // 其他引擎使用 DefaultModPathPatterns
                    commonModPaths = engineProfile.DefaultModPathPatterns
                        .Select(p => Path.Combine(gameBasePath, p.Replace('/', Path.DirectorySeparatorChar)))
                        .ToList();
                }

                // 查找已存在的路径
                var existingModPath = commonModPaths.FirstOrDefault(Directory.Exists);
                if (!string.IsNullOrEmpty(existingModPath))
                {
                    ModPath = existingModPath;
                    ModPathTextBox.Text = existingModPath;
                    Console.WriteLine($"[DEBUG] 使用已存在的MOD路径: {existingModPath}");
                }
                else
                {
                    // 如果都不存在，使用第一个合适的路径
                    string defaultModPath;
                    if (engineType == EngineType.UnrealEngine)
                    {
                        if (GameName.Contains("无主之地4") || GameName.Contains("Borderlands 4"))
                            defaultModPath = Path.Combine(gameBasePath, "OakGame", "Content", "Paks", "~mods");
                        else
                            defaultModPath = Path.Combine(gameBasePath, "Content", "Paks", "~mods");
                    }
                    else
                    {
                        defaultModPath = commonModPaths.FirstOrDefault()
                            ?? Path.Combine(gameBasePath, "Mods");
                    }

                    ModPath = defaultModPath;
                    ModPathTextBox.Text = defaultModPath;
                    Console.WriteLine($"[DEBUG] 设置默认MOD路径: {defaultModPath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] 设置MOD路径失败: {ex.Message}");
                ModPath = "";
                ModPathTextBox.Text = "";
            }
        }

        public event PropertyChangedEventHandler PropertyChanged = delegate { };
        
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        private void ApplyLocalization()
        {
            var toEnglish = UEModManager.Services.LanguageManager.IsEnglish;
            var map = new System.Collections.Generic.Dictionary<string,string>
            {
                {"游戏路径配置","Game Path Configuration"},
                {"游戏路径","Game Path"},
                {"MOD路径","MOD Path"},
                {"MOD备份路径","MOD Backup Path"},
                {"浏览","Browse"},
                {"保存","Save"},
                {"取消","Cancel"}
            };
            UEModManager.Services.LocalizationHelper.Apply(this, toEnglish, map);
            this.Title = toEnglish ? "Game Path Configuration" : "游戏路径配置";
        }

    }
}

