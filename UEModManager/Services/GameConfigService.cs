using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Infrastructure;
using UEModManager.Models;
using UEModManager.Services.Detection;
using UEModManager.Services.Persistence;
using UEModManager.Services.Security;
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

        /// <summary>
        /// 磁盘状态未知：config.json 存在但读取失败（被占用/权限），内存里的 Config 只是默认值。
        /// 此时任何一次保存都会全量覆盖，故覆盖前先备份原文件。
        /// </summary>
        private bool _diskStateUnknown;

        public AppConfig Config { get; private set; } = new();

        // ─── 便捷属性 ───

        public string CurrentGameName => Config.GameName ?? string.Empty;
        public string CurrentGamePath => Config.GamePath ?? string.Empty;
        public string CurrentModPath => Config.ModPath ?? string.Empty;
        public string CurrentBackupPath => Config.BackupPath ?? string.Empty;
        public string CurrentExecutableName => Config.ExecutableName ?? string.Empty;

        /// <summary>
        /// 获取游戏图标：用户自选图片优先，未设置或文件丢失时使用随程序分发的默认图标。
        /// </summary>
        public string? GetGameIconPath(string gameName)
        {
            if (string.IsNullOrWhiteSpace(gameName)) return null;
            if (Config.GameIcons?.TryGetValue(gameName, out var path) == true && File.Exists(path))
                return path;

            return GetBuiltInGameIconPath(gameName);
        }

        private static string? GetBuiltInGameIconPath(string gameName)
        {
            var fileName = NormalizeGameName(gameName) switch
            {
                "黑神话悟空" => "black-myth-wukong.png",
                "剑星" or "剑星 (CNS)" or "剑星(CNS)" or "剑星(CNS模式)" => "stellar-blade.png",
                "光与影:33号远征队" => "clair-obscur-expedition-33.png",
                "明末渊虚之羽" => "wuchang-fallen-feathers.png",
                "暗黑破坏神4" => "diablo-iv.png",
                "生化危机9" => "resident-evil-requiem.png",
                "识质存在" => "pragmata.png",
                "无主之地4" => "borderlands-4.png",
                "死亡搁浅2" => "death-stranding-2.png",
                "杀戮尖塔2" => "slay-the-spire-2.png",
                _ => null
            };
            if (fileName == null) return null;

            // 使用安装目录下的只读资源，首次启动和离线模式不依赖配置写入或网络请求。
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "GameIcons", fileName);
            return File.Exists(path) ? path : null;
        }

        /// <summary>
        /// 设置指定游戏的图标路径。
        /// </summary>
        public async Task SetGameIconAsync(string gameName, string? iconPath)
        {
            Config.GameIcons ??= new Dictionary<string, string>();
            // 默认图标随程序更新，不把本次安装的绝对路径保存成用户覆盖项。
            if (string.IsNullOrEmpty(iconPath)
                || string.Equals(iconPath, GetBuiltInGameIconPath(gameName), StringComparison.OrdinalIgnoreCase))
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
            : this(logger, AppPaths.ConfigFile)
        {
        }

        /// <summary>
        /// 指定配置文件路径的构造函数（测试用）。DI 走上面的单参数构造函数——
        /// 容器无法解析 string，不会误选此重载。
        /// </summary>
        public GameConfigService(ILogger<GameConfigService> logger, string configFilePath)
        {
            _logger = logger;
            _configFilePath = configFilePath;
        }

        // ─── 配置加载/保存 ───

        /// <summary>
        /// 从 config.json 加载配置。
        /// </summary>
        public Task LoadConfigAsync()
        {
            return Task.Run(() =>
            {
                string json;
                try
                {
                    if (!File.Exists(_configFilePath))
                    {
                        _logger.LogInformation("配置文件不存在，使用默认设置");
                        return;
                    }

                    json = File.ReadAllText(_configFilePath);
                }
                catch (Exception ex)
                {
                    // 读盘失败时磁盘上的配置很可能仍然完好：标记状态未知，
                    // 由 SaveConfigSync 在覆盖前补一次备份，避免游戏路径被默认值抹掉。
                    _diskStateUnknown = true;
                    _logger.LogError(ex, "读取配置文件失败，本次使用默认设置");
                    return;
                }

                AppConfig? config;
                try
                {
                    config = JsonSerializer.Deserialize<AppConfig>(json);
                }
                catch (Exception ex)
                {
                    // 解析失败若只是"保持默认值"，之后任意一次保存（切换游戏、设图标……）
                    // 都会用空配置覆盖原文件，游戏路径/MOD 路径/自定义游戏列表全部丢失。
                    // 与 ProfileService.LoadProfilesAsync 对齐：先备份，再按默认配置继续。
                    BackupBrokenConfigFile("解析配置失败", ex);
                    return;
                }

                if (config == null)
                {
                    BackupBrokenConfigFile("配置文件反序列化结果为空", null);
                    return;
                }

                Config = config;

                // 配置文件可能来自旧版本、手工编辑或外部导入，不能假设其中的
                // GameName 已经满足文件名规则。清空非法当前游戏，让用户重新选择，
                // 避免启动初始化时把它拼进 Profile/索引路径。
                if (!string.IsNullOrEmpty(Config.GameName))
                {
                    try
                    {
                        Config.GameName = ValidateGameName(Config.GameName);
                    }
                    catch (ArgumentException ex)
                    {
                        _logger.LogWarning(ex, "配置中的当前游戏名非法，已要求重新选择: {Name}", Config.GameName);
                        Config.GameName = null;
                        Config.ExecutableName = null;
                    }
                }

                // 修复旧版本备份路径
                if (!string.IsNullOrEmpty(Config.BackupPath) && Config.BackupPath.Contains("net6.0-windows"))
                {
                    Config.BackupPath = Config.BackupPath.Replace("net6.0-windows", "net8.0-windows");
                    // 这是加载路径上的顺带修正，用户没有发起任何操作，没有 UI 能承接错误；
                    // 抛出去只会让 LoadConfigAsync 整体失败、主界面连游戏名都拿不到。
                    TrySaveConfigQuietly("修正旧版本备份路径");
                    _logger.LogInformation("已自动修正备份路径");
                }

                _logger.LogInformation("配置加载成功: 游戏={Game}, 路径={Path}",
                    Config.GameName, Config.GamePath);
            });
        }

        /// <summary>
        /// 保存当前配置到 config.json。写失败会以异常形式上抛给调用方。
        /// </summary>
        public Task SaveConfigAsync()
        {
            return Task.Run(() => SaveConfigSync());
        }

        /// <summary>
        /// 落盘配置。
        ///
        /// 写失败必须上抛：config.json 里装的是游戏安装路径、MOD 路径、自定义游戏列表。
        /// 此前这里把异常吞掉，用户设完游戏路径看到界面正常刷新，重启后路径没了——
        /// 而同一个根因（目标目录不可写）下，导入 MOD 和改方案却会弹错误框
        /// （PackageRepository / ProfileService 一直是 log + throw）。分裂的失败语义比
        /// 全都静默更糟：用户会因为"别处会报错"而信任这里的沉默。现统一为 log + throw。
        /// </summary>
        private void SaveConfigSync()
        {
            try
            {
                if (_diskStateUnknown)
                {
                    BackupBrokenConfigFile("即将用内存配置覆盖一个此前读取失败的 config.json", null);
                    _diskStateUnknown = false;
                }

                var json = JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true });
                AtomicFileWriter.WriteAllText(_configFilePath, json);
                _logger.LogInformation("配置已保存");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存配置失败: {Path}", _configFilePath);
                throw;
            }
        }

        /// <summary>
        /// 顺带保存：只记日志、不上抛。
        ///
        /// 仅用于"用户没有发起、失败也不该中断当前动作"的隐式落盘（加载时的路径修正、
        /// 启动游戏时回写自动检测到的可执行文件名）。用户显式发起的保存一律走
        /// <see cref="SaveConfigSync"/>，失败要看得见。
        /// </summary>
        private void TrySaveConfigQuietly(string reason)
        {
            try
            {
                SaveConfigSync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Reason}后保存配置失败，本次改动仅存在于内存", reason);
            }
        }

        /// <summary>
        /// 备份不可用（损坏或读不出）的 config.json，命名与 ProfileService.BackupCorruptProfileFile 对齐。
        /// </summary>
        private void BackupBrokenConfigFile(string reason, Exception? cause)
        {
            if (!File.Exists(_configFilePath)) return;

            try
            {
                var backupPath = $"{_configFilePath}.corrupt-{DateTime.Now:yyyyMMddHHmmss}.bak";
                File.Copy(_configFilePath, backupPath, overwrite: false);
                _logger.LogError(cause, "{Reason}，已备份原文件: {BackupPath}", reason, backupPath);
            }
            catch (Exception backupException)
            {
                _logger.LogError(cause, "{Reason}，且原文件备份失败: {Path}", reason, _configFilePath);
                _logger.LogError(backupException, "损坏的 config.json 备份失败");
            }
        }

        // ─── 游戏切换 ───

        /// <summary>
        /// 切换当前游戏，更新配置。
        /// </summary>
        public async Task SwitchGameAsync(string gameName, string gamePath, string modPath, string backupPath)
        {
            var safeGameName = ValidateGameName(gameName);
            var oldGameName = Config.GameName;
            var oldGamePath = Config.GamePath;
            var oldModPath = Config.ModPath;
            var oldBackupPath = Config.BackupPath;
            var oldExecutableName = Config.ExecutableName;

            try
            {
                Config.GameName = safeGameName;
                Config.GamePath = gamePath?.Trim();
                Config.ModPath = modPath?.Trim();
                Config.BackupPath = backupPath?.Trim();
                Config.ExecutableName = null; // 重置，让自动检测重新查找

                // 确保备份目录存在
                if (!string.IsNullOrEmpty(Config.BackupPath) && !Directory.Exists(Config.BackupPath))
                    Directory.CreateDirectory(Config.BackupPath);

                await SaveConfigAsync();
                ConfigChanged?.Invoke();

                _logger.LogInformation("已切换到游戏: {Game}", safeGameName);
            }
            catch
            {
                // 配置写失败时不能让当前进程误以为已经切换成功，否则随后加载的
                // Profile/仓库会落到新游戏名，而重启后又回到旧游戏，形成两套状态。
                Config.GameName = oldGameName;
                Config.GamePath = oldGamePath;
                Config.ModPath = oldModPath;
                Config.BackupPath = oldBackupPath;
                Config.ExecutableName = oldExecutableName;
                throw;
            }
        }

        /// <summary>
        /// 获取可用的游戏列表（内置 + 自定义）。
        /// </summary>
        public List<string> GetAvailableGames()
        {
            // 官网与管理器共享的常驻游戏列表，按社区热度 + 官网展示顺序排列。
            var builtIn = new List<string>
            {
                "黑神话·悟空",
                "剑星",
                "剑星 (CNS)",
                "光与影：33号远征队",
                "明末·渊虚之羽",
                "暗黑破坏神4",
                "生化危机9",
                "识质存在",
                "无主之地4",
                "死亡搁浅2",
                "杀戮尖塔2"
            };

            foreach (var custom in GetCustomGames())
            {
                if (!builtIn.Contains(custom, StringComparer.OrdinalIgnoreCase))
                    builtIn.Add(custom);
            }

            return builtIn;
        }

        /// <summary>获取配置中有效的自定义游戏名称，不返回内置游戏或重复项。</summary>
        public IReadOnlyList<string> GetCustomGames()
        {
            if (Config.CustomGames == null || Config.CustomGames.Count == 0)
                return Array.Empty<string>();

            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawName in Config.CustomGames)
            {
                try
                {
                    var name = ValidateGameName(rawName);
                    if (IsBuiltInGame(name) || !seen.Add(name)) continue;
                    result.Add(name);
                }
                catch (ArgumentException ex)
                {
                    _logger.LogWarning(ex, "忽略配置中的非法自定义游戏名: {Name}", rawName);
                }
            }

            return result;
        }

        /// <summary>判断游戏是否来自用户自定义列表。</summary>
        public bool IsCustomGame(string gameName)
            => GetCustomGames().Any(name =>
                string.Equals(name, gameName, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// 添加自定义游戏。
        /// </summary>
        public async Task<string> AddCustomGameAsync(string name)
        {
            var safeName = ValidateGameName(name);
            if (IsBuiltInGame(safeName))
                throw new ArgumentException("不能把内置游戏重复添加为自定义游戏", nameof(name));

            var oldGames = Config.CustomGames == null
                ? null
                : new List<string>(Config.CustomGames);
            Config.CustomGames ??= new List<string>();
            var existing = Config.CustomGames.FirstOrDefault(existingName =>
                string.Equals(existingName?.Trim(), safeName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                return existing.Trim();

            try
            {
                Config.CustomGames.Add(safeName);
                await SaveConfigAsync();
                ConfigChanged?.Invoke();
                _logger.LogInformation("已添加自定义游戏: {Name}", safeName);
                return safeName;
            }
            catch
            {
                Config.CustomGames = oldGames ?? new List<string>();
                throw;
            }
        }

        /// <summary>
        /// 移除自定义游戏。
        /// </summary>
        public async Task<bool> RemoveCustomGameAsync(string name)
        {
            var safeName = ValidateGameName(name);
            var existing = Config.CustomGames?.FirstOrDefault(existingName =>
                string.Equals(existingName?.Trim(), safeName, StringComparison.OrdinalIgnoreCase));
            if (existing == null || IsBuiltInGame(safeName))
                return false;

            if (string.Equals(CurrentGameName.Trim(), existing.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("不能删除当前正在使用的游戏，请先切换到其他游戏");

            var oldGames = new List<string>(Config.CustomGames!);
            var oldIcons = Config.GameIcons == null
                ? null
                : new Dictionary<string, string>(Config.GameIcons);
            var oldEngines = Config.GameEngines == null
                ? null
                : new Dictionary<string, string>(Config.GameEngines);
            var oldPluginPaths = Config.PluginPaths == null
                ? null
                : new Dictionary<string, string>(Config.PluginPaths);

            try
            {
                Config.CustomGames.RemoveAll(existingName =>
                    string.Equals(existingName?.Trim(), existing, StringComparison.OrdinalIgnoreCase));
                RemoveGameMetadata(Config.GameIcons, existing);
                RemoveGameMetadata(Config.GameEngines, existing);
                RemoveGameMetadata(Config.PluginPaths, existing);
                await SaveConfigAsync();
                ConfigChanged?.Invoke();
                _logger.LogInformation("已移除自定义游戏: {Name}", existing);
                return true;
            }
            catch
            {
                Config.CustomGames = oldGames;
                Config.GameIcons = oldIcons ?? new Dictionary<string, string>();
                Config.GameEngines = oldEngines ?? new Dictionary<string, string>();
                Config.PluginPaths = oldPluginPaths ?? new Dictionary<string, string>();
                throw;
            }
        }

        /// <summary>
        /// 自动检测游戏可执行文件，返回文件名（不含目录）。
        /// </summary>
        public string? AutoDetectExecutable(string gamePath, string gameName)
            => Path.GetFileName(AutoDetectExecutablePath(gamePath, gameName));

        /// <summary>
        /// 自动检测游戏可执行文件，返回完整路径；找不到返回 null。
        ///
        /// 先按目录约定探测（游戏根目录顶层、UE 的 {模块}/Binaries/Win64 等），命中即返回；
        /// 只有约定路径给不出确定答案时才退回全盘递归。3A 游戏目录动辄十万级文件，
        /// 冷缓存下一次 AllDirectories 枚举是数秒级操作，而这条路径在"启动游戏"时是同步的。
        /// </summary>
        public string? AutoDetectExecutablePath(string gamePath, string gameName)
        {
            try
            {
                if (string.IsNullOrEmpty(gamePath) || !Directory.Exists(gamePath))
                    return null;

                // 1) 约定路径。只在"名称匹配命中"或"只有唯一候选"时采信：
                //    候选集不完整，此时用体积回退去猜的代价是启动错误的程序。
                var probed = EnumerateConventionalExecutables(gamePath);
                var fromProbe = SelectExecutable(probed, gameName, allowLargestFallback: false);
                if (fromProbe != null)
                {
                    _logger.LogDebug("按目录约定检测到可执行文件: {Path}", fromProbe);
                    return fromProbe;
                }

                // 2) 兜底全盘递归：惰性枚举 + 数量上限，不再一次性物化整棵目录树；
                //    IgnoreInaccessible 让个别无权限子目录不再使整次检测失败。
                var all = Directory
                    .EnumerateFiles(gamePath, "*.exe", RecursiveExeScanOptions)
                    .Take(MaxScannedExecutables);
                return SelectExecutable(all, gameName, allowLargestFallback: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "自动检测游戏可执行文件失败");
                return null;
            }
        }

        /// <summary>兜底全盘扫描的候选数量上限，避免超大目录把内存和耗时拖到无界。</summary>
        private const int MaxScannedExecutables = 4000;

        private static readonly EnumerationOptions RecursiveExeScanOptions = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            // 默认会跳过隐藏/系统文件，而旧实现（SearchOption 重载）不跳过，保持一致
            AttributesToSkip = FileAttributes.None
        };

        private static readonly string[] AuxiliaryExecutableKeywords =
            { "unins", "setup", "launcher", "updater", "installer", "redist", "vcredist", "directx" };

        /// <summary>
        /// 按目录约定收集候选 exe：游戏根目录顶层 + `{根}/Binaries/Win*`
        /// + 下探一层的 `{模块}/Binaries/Win*`（UE 布局）+ `{模块}/bin`（部分非 UE 游戏）。
        /// 只枚举这些目录的顶层，代价与游戏目录规模无关。
        /// </summary>
        private List<string> EnumerateConventionalExecutables(string gamePath)
        {
            var directories = new List<string> { gamePath };
            AddBinariesDirectories(gamePath, directories);

            foreach (var sub in EnumerateDirectoriesSafe(gamePath))
            {
                AddBinariesDirectories(sub, directories);

                var bin = Path.Combine(sub, "bin");
                if (Directory.Exists(bin)) directories.Add(bin);
            }

            var executables = new List<string>();
            foreach (var dir in directories)
            {
                try
                {
                    executables.AddRange(Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "读取候选目录失败，跳过: {Path}", dir);
                }
            }

            return executables;
        }

        private void AddBinariesDirectories(string root, List<string> directories)
        {
            var binaries = Path.Combine(root, "Binaries");
            if (!Directory.Exists(binaries)) return;

            // Win64 / Win32 / WinGDK
            directories.AddRange(EnumerateDirectoriesSafe(binaries, "Win*"));
        }

        private string[] EnumerateDirectoriesSafe(string path, string pattern = "*")
        {
            try
            {
                return Directory.GetDirectories(path, pattern, SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "枚举子目录失败，跳过: {Path}", path);
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// 从候选 exe 中挑出游戏主程序：排除安装/更新/卸载等辅助工具 → 按游戏名匹配 →
        /// （允许时）回退到体积最大的一个。纯逻辑，可单测。
        /// </summary>
        /// <param name="candidates">候选 exe 的完整路径。</param>
        /// <param name="gameName">游戏名，用于按已知命名规则匹配。</param>
        /// <param name="allowLargestFallback">名称匹配不中时是否允许"取体积最大者"的回退。</param>
        /// <param name="fileSizeProvider">取文件大小的方式，默认读磁盘（测试可注入）。</param>
        /// <returns>选中的 exe 完整路径；没有合适候选时返回 null。</returns>
        public static string? SelectExecutable(
            IEnumerable<string> candidates,
            string? gameName,
            bool allowLargestFallback,
            Func<string, long>? fileSizeProvider = null)
        {
            var validExes = candidates
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(exe => !IsAuxiliaryExecutable(exe, gameName))
                .ToArray();

            if (validExes.Length == 0) return null;
            if (validExes.Length == 1) return validExes[0];

            var match = MatchByKnownGame(validExes, gameName)
                ?? MatchByNormalizedGameName(validExes, gameName);
            if (match != null) return match;

            if (!allowLargestFallback) return null;

            var sizeOf = fileSizeProvider ?? (p => new FileInfo(p).Length);
            return validExes.OrderByDescending(sizeOf).First();
        }

        /// <summary>是否是安装/卸载/更新器一类的辅助程序（不是游戏本体）。</summary>
        private static bool IsAuxiliaryExecutable(string exePath, string? gameName)
        {
            var fileName = Path.GetFileName(exePath);

            // 无主之地的主程序本身带 CrashReporter 字样，单独放行
            if (fileName.Contains("crashreporter", StringComparison.OrdinalIgnoreCase)
                && !IsBorderlands(gameName))
            {
                return true;
            }

            return AuxiliaryExecutableKeywords.Any(kw => fileName.Contains(kw, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsBorderlands(string? gameName)
            => gameName != null
                && (gameName.Contains("无主之地")
                    || gameName.Contains("Borderlands", StringComparison.OrdinalIgnoreCase));

        /// <summary>按已知游戏的可执行文件命名规则匹配，token 按优先级排列。</summary>
        private static string? MatchByKnownGame(IReadOnlyList<string> validExes, string? gameName)
        {
            if (string.IsNullOrEmpty(gameName)) return null;

            if (gameName.StartsWith("剑星"))
                return FindByFileNameToken(validExes, "sb-win64-shipping", "stellarblade");

            if (gameName.StartsWith("黑神话"))
                return FindByFileNameToken(validExes, "b1-win64-shipping", "wukong");

            if (gameName == "光与影：33号远征队")
                return FindByFileNameToken(validExes,
                    "expedition33steam-win64-shipping", "expedition33", "sandfall-win64-shipping", "sandfall");

            if (gameName.Contains("明末") || gameName.Contains("渊虚之羽"))
                return FindByFileNameToken(validExes, "project_plague-win64-shipping", "wuchang");

            if (IsBorderlands(gameName))
                return FindByFileNameToken(validExes, "borderlands");

            return null;
        }

        private static string? FindByFileNameToken(IReadOnlyList<string> validExes, params string[] tokens)
        {
            foreach (var token in tokens)
            {
                var hit = validExes.FirstOrDefault(e =>
                    Path.GetFileName(e).Contains(token, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
            }
            return null;
        }

        /// <summary>通用回退匹配：把游戏名归一化后与文件名互相包含比对。</summary>
        private static string? MatchByNormalizedGameName(IReadOnlyList<string> validExes, string? gameName)
        {
            var normalized = (gameName ?? string.Empty).Split('(')[0].Trim()
                .Replace("：", "").Replace("·", "").Replace(" ", "")
                .ToLowerInvariant();

            // 游戏名为空时不能走"互相包含"：任何文件名都 Contains("")，
            // 那等于按目录枚举顺序随便挑一个。
            if (normalized.Length == 0) return null;

            return validExes.FirstOrDefault(e =>
            {
                var fn = Path.GetFileNameWithoutExtension(e).ToLowerInvariant();
                return fn.Contains(normalized) || normalized.Contains(fn);
            });
        }

        // ─── 引擎类型 ───

        /// <summary>
        /// 内置游戏名称集合（全部为 UE 引擎）。
        /// </summary>
        private static readonly HashSet<string> BuiltInGames = new()
        {
            "黑神话·悟空", "剑星", "剑星 (CNS)", "光与影：33号远征队", "明末·渊虚之羽",
            "暗黑破坏神4", "生化危机9", "识质存在", "无主之地4", "死亡搁浅2", "杀戮尖塔2"
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
            if (gameName == "生化危机9" || gameName == "识质存在") return EngineType.REEngine;
            if (gameName == "暗黑破坏神4") return EngineType.Diablo4Engine;

            if (IsBuiltInGame(gameName)) return EngineType.UnrealEngine;

            Config.GameEngines ??= new Dictionary<string, string>();
            var engineEntry = Config.GameEngines.FirstOrDefault(entry =>
                string.Equals(entry.Key, gameName, StringComparison.OrdinalIgnoreCase));
            return !string.IsNullOrEmpty(engineEntry.Key)
                ? EngineProfile.Parse(engineEntry.Value)
                : EngineType.UnrealEngine;
        }

        /// <summary>
        /// 保存自定义游戏的引擎类型到配置。
        /// </summary>
        public async Task SetGameEngineAsync(string gameName, EngineType engine)
        {
            var safeGameName = ValidateGameName(gameName);
            var oldEngines = Config.GameEngines == null
                ? null
                : new Dictionary<string, string>(Config.GameEngines);
            Config.GameEngines ??= new Dictionary<string, string>();
            try
            {
                // 旧配置可能保留了不同大小写的同名键；先清理等价键，避免
                // "My Game" 与 "my game" 在读取和保存时出现两套引擎设置。
                var equivalentKeys = Config.GameEngines.Keys
                    .Where(key => string.Equals(key, safeGameName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var key in equivalentKeys)
                    Config.GameEngines.Remove(key);
                Config.GameEngines[safeGameName] = engine.ToString();
                await SaveConfigAsync();
                _logger.LogInformation("已设置游戏 '{Name}' 的引擎类型为 {Engine}", safeGameName, engine);
            }
            catch
            {
                Config.GameEngines = oldEngines ?? new Dictionary<string, string>();
                throw;
            }
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
            if (gameName.Contains("光与影")
                || gameName.Contains("33号远征队")
                || gameName.Contains("Expedition 33", StringComparison.OrdinalIgnoreCase)
                || gameName.Contains("Expedition33", StringComparison.OrdinalIgnoreCase)
                || gameName.Contains("Clair Obscur", StringComparison.OrdinalIgnoreCase)
                || gameName.Contains("Sandfall", StringComparison.OrdinalIgnoreCase))
            {
                return GameType.Expedition33;
            }
            if (gameName.Contains("明末") || gameName.Contains("渊虚之羽")) return GameType.WuchangFallenFeathers;
            if (gameName.Contains("无主之地") || gameName.Contains("Borderlands")) return GameType.Borderlands4;
            return GameType.Other;
        }

        /// <summary>
        /// 规范化游戏名称。委托 Core 的 GameNameNormalizer。
        /// </summary>
        public static string NormalizeGameName(string name)
            => GameNameNormalizer.Normalize(name);

        public static bool IsBuiltInGameName(string name)
            => !string.IsNullOrWhiteSpace(name) && IsBuiltInGame(name.Trim());

        private static bool IsBuiltInGame(string name)
            => BuiltInGames.Contains(name, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 校验会参与配置/方案/包索引文件名拼接的游戏名。
        /// </summary>
        public static string ValidateGameName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("游戏名称不能为空", nameof(name));

            var trimmed = name.Trim();
            if (trimmed.Length < 2)
                throw new ArgumentException("游戏名称至少需要 2 个字符", nameof(name));
            if (trimmed.Length > 100)
                throw new ArgumentException("游戏名称不能超过 100 个字符", nameof(name));
            if (trimmed.Any(char.IsControl))
                throw new ArgumentException("游戏名称不能包含控制字符", nameof(name));

            // 游戏名会参与 profile/index 文件名拼接，必须限制为单段安全名称。
            return PathSanitizer.SanitizeSegment(trimmed, nameof(name));
        }

        private static void RemoveGameMetadata<T>(IDictionary<string, T>? values, string gameName)
        {
            if (values == null) return;

            var keys = values.Keys
                .Where(key => string.Equals(key?.Trim(), gameName.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var key in keys)
                values.Remove(key);
        }
    }
}
