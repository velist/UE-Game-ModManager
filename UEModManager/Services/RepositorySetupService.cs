using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using UEModManager.Infrastructure;
using UEModManager.Models;
using UEModManager.Services.Paths;

namespace UEModManager.Services
{
    /// <summary>
    /// 首次运行引导要读写的那一小部分用户偏好。抽出接口的理由与
    /// <see cref="IDataMigrationPreferences"/> 完全一致：把 <see cref="UiPreferences"/>
    /// 这个静态全局挡在服务外面，否则任何一条测试用例都会真的改掉开发者本机的仓库位置。
    /// </summary>
    internal interface IRepositorySetupPreferences
    {
        /// <summary>读取用户自定义的包仓库位置；未自定义返回 <c>null</c>。</summary>
        string? LoadRepositoryRoot();

        /// <summary>写入包仓库位置。<b>失败上抛</b>——用户必须知道这次选择没生效。</summary>
        void SaveRepositoryRoot(string? path);

        /// <summary>首次运行引导是否已经问过。</summary>
        bool LoadPrompted();

        /// <summary>记下"已经问过"。静默，失败只留痕。</summary>
        void SavePrompted();
    }

    /// <summary>
    /// 引导判定要看的全部位置。与 <see cref="DataMigrationPaths"/> 同构，
    /// 目的也一样：让测试能把整套判定指到临时目录上，不碰真实的
    /// <c>%LOCALAPPDATA%</c> / <c>%APPDATA%</c> / 安装目录。
    /// </summary>
    /// <param name="CurrentRepositoryRoot">当前生效的仓库位置（含用户覆盖）。</param>
    /// <param name="LegacyRepositoryRoot">仓库的旧默认位置。</param>
    /// <param name="ConfigFile">主配置的新位置。</param>
    /// <param name="LegacyConfigFile">主配置的旧位置（安装目录）。</param>
    /// <param name="LegacyDataDirectory">旧的 JSON 索引目录（安装目录）。</param>
    /// <param name="LegacyModBackupsDirectory">旧的 MOD 备份目录（安装目录）。</param>
    /// <param name="InstallDirectory">安装目录本身，用于判断用户是否把仓库选进了这里。</param>
    internal sealed record RepositorySetupPaths(
        string CurrentRepositoryRoot,
        string LegacyRepositoryRoot,
        string ConfigFile,
        string LegacyConfigFile,
        string LegacyDataDirectory,
        string LegacyModBackupsDirectory,
        string InstallDirectory);

    /// <summary>引导服务的运行环境。</summary>
    internal sealed record RepositorySetupEnvironment(
        RepositorySetupPaths Paths,
        IRepositorySetupPreferences Preferences);

    /// <summary>
    /// 首次运行时引导用户挑一个包仓库位置。本类只做 IO 与编排，
    /// 判定全部在 Core 的 <see cref="RepositorySetupPrompt"/>、
    /// <see cref="RepositoryLocationValidator"/> 与 <see cref="RepositoryDriveAdvisor"/> 里。
    ///
    /// <para><b>为什么需要这个引导</b></para>
    /// 包仓库存的是 MOD 包实体，重度用户能攒到几十 GB，而默认位置在系统盘。
    /// 设置里一直能改，问题只是用户不知道有这回事。不改默认值而改成问一次，
    /// 是因为改默认值会影响所有新安装、又照顾不到已有习惯。
    ///
    /// <para><b>只管仓库，不管生成物与备份</b></para>
    /// 三个可自定义的数据根里只有仓库可能到几十 GB（生成物是部署快照、备份是 MOD 原文件，
    /// 都是 MB~GB 量级）。另外两项刻意不问，三条理由：
    /// <list type="number">
    /// <item>部署事务备份在备份根下，<b>崩溃回滚依赖它</b>。把它引到可移动盘上，
    /// U 盘一拔就等于回滚能力消失，而用户完全看不出来。</item>
    /// <item>这两项目前<b>在设置界面上没有入口</b>，只有搬迁器会写。
    /// 引导里问了、用户选错了，他连改回来的地方都找不到。</item>
    /// <item>首次启动弹一个框问三个目录，普通玩家的反应是直接关掉 ——
    /// 结果是三项都没设，比只问一项更糟。</item>
    /// </list>
    ///
    /// <para><b>时序</b></para>
    /// 必须跑在 <see cref="DataLocationMigrator"/> <b>之后</b>（它的原地登记会把老用户的
    /// 旧仓库位置写进配置，那是"不打扰老用户"最主要的一条判据），
    /// 且必须跑在 <see cref="ObjectStore"/> 被首次解析<b>之前</b>——那个单例在构造时读一次
    /// <see cref="AppPaths.RepositoryRoot"/>，之后本次会话不再回头看配置。
    /// 两头都由 <c>App.ShowAuthenticationWindow</c> 保证，并有源码守卫测试钉住。
    /// </summary>
    public sealed class RepositorySetupService
    {
        private readonly ILogger<RepositorySetupService> _logger;

        /// <summary>
        /// 注入的环境；<c>null</c> 表示走生产环境。刻意<b>不在构造时求值</b>：
        /// <see cref="AppPaths"/> 不缓存布局（用户改了位置要立即生效），
        /// 在构造函数里把路径拍死会悄悄改掉这个语义。
        /// </summary>
        private readonly RepositorySetupEnvironment? _environment;

        public RepositorySetupService(ILogger<RepositorySetupService> logger)
            : this(logger, environment: null)
        {
        }

        /// <summary>
        /// 测试用构造：把路径与偏好整体换成可控的实现。标 <c>internal</c>，
        /// DI 只看得到上面那个公开构造。
        /// </summary>
        internal RepositorySetupService(
            ILogger<RepositorySetupService> logger, RepositorySetupEnvironment? environment)
        {
            _logger = logger;
            _environment = environment;
        }

        /// <summary>生产环境。<b>全类只有这一个方法碰静态全局状态。</b></summary>
        private static RepositorySetupEnvironment CreateProductionEnvironment() => new(
            new RepositorySetupPaths(
                CurrentRepositoryRoot: AppPaths.RepositoryRoot,
                LegacyRepositoryRoot: AppPaths.Legacy.RepositoryRoot,
                ConfigFile: AppPaths.ConfigFile,
                LegacyConfigFile: AppPaths.Legacy.ConfigFile,
                LegacyDataDirectory: AppPaths.Legacy.DataDirectory,
                LegacyModBackupsDirectory: AppPaths.Legacy.ModBackupsDirectory,
                InstallDirectory: AppPaths.Legacy.InstallDirectory),
            UiPreferencesRepositorySetupAdapter.Instance);

        /// <summary>
        /// 生产环境每次现算：<see cref="AppPaths"/> 不缓存布局，
        /// 搬迁器刚刚写下的仓库位置必须能被本次判定读到。
        /// </summary>
        private RepositorySetupEnvironment ResolveEnvironment()
            => _environment ?? CreateProductionEnvironment();

        internal string DefaultRepositoryRoot => ResolveEnvironment().Paths.CurrentRepositoryRoot;

        // ─── 该不该弹 ───

        /// <summary>
        /// 判定这次启动要不要弹引导。<b>绝不抛异常</b>：引导跑在启动早期，
        /// 一个判定失败把应用挡在主界面之前是完全不成比例的代价，出错就当作"不弹"。
        /// </summary>
        public RepositorySetupDecision Decide()
        {
            try
            {
                var decision = RepositorySetupPrompt.Decide(BuildProbe(ResolveEnvironment()));
                _logger.LogInformation("[RepoSetup] {Decision}", decision);
                return decision;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RepoSetup] 判定是否需要引导时出错，本次不弹");
                return new RepositorySetupDecision(false,
                    RepositorySetupSkipReason.ProbeFailed, "判定过程出错，保守起见不打扰用户");
            }
        }

        private RepositorySetupProbe BuildProbe(RepositorySetupEnvironment env)
        {
            var paths = env.Paths;
            return new RepositorySetupProbe(
                AlreadyPrompted: env.Preferences.LoadPrompted(),
                RepositoryRootConfigured: !string.IsNullOrWhiteSpace(env.Preferences.LoadRepositoryRoot()),
                CurrentRepositoryHasContent: HasContent(paths.CurrentRepositoryRoot),
                LegacyRepositoryHasContent: HasContent(paths.LegacyRepositoryRoot),
                AppConfigExists: Exists(paths.ConfigFile) || Exists(paths.LegacyConfigFile),
                LegacyInstallDataExists:
                    HasContent(paths.LegacyDataDirectory) || HasContent(paths.LegacyModBackupsDirectory));
        }

        /// <summary>
        /// 目录里有没有东西。
        ///
        /// <para>
        /// <b>枚举失败按"有内容"处理</b>，与 <c>DataRelocationExecutor.DirectoryHasContent</c>
        /// 的方向刚好相反，是刻意的：那边错判的后果是"本次不迁移"，这边错判的后果是
        /// "对着一个装满 MOD 的老用户弹出换位置的框"。两边都取各自更保守的一侧。
        /// </para>
        ///
        /// <para>
        /// 检查候选位置时复用同一个方法，那里的保守方向恰好也是这一侧：
        /// 错判成"有内容"只会让落点多一层专用子目录（安全且在界面上写明了），
        /// 错判成"空"则可能把用户自己的文件夹直接变成仓库根。
        /// </para>
        /// </summary>
        private bool HasContent(string path)
        {
            try
            {
                return Directory.Exists(path)
                    && Directory.EnumerateFileSystemEntries(path).Any();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[RepoSetup] 无法确认目录是否为空，按有内容处理: {Path}", path);
                return true;
            }
        }

        private static bool Exists(string path)
        {
            try { return File.Exists(path); }
            catch { return true; }
        }

        // ─── 盘位列表 ───

        /// <summary>
        /// 列出可供选择的盘，已按 <see cref="RepositoryDriveAdvisor.Rank"/> 排好序。
        /// 查不到任何盘时返回空列表（界面退化成"只有一个手动选文件夹的按钮"，仍然可用）。
        /// </summary>
        public IReadOnlyList<RepositoryDriveOption> ListDrives()
        {
            var paths = ResolveEnvironment().Paths;
            var currentRoot = TryGetPathRoot(paths.CurrentRepositoryRoot);

            // 游戏所在的盘。拿不到是**常态**而不是异常：引导跑在启动早期，而
            // "config.json 不存在"本身就是引导会弹出来的前提之一（见 RepositorySetupPrompt），
            // 所以首次运行时这里几乎必然是 null。此时全部盘的 HostsCurrentGame 都是 false，
            // 界面上那条"与游戏同一个盘"的提示整体不出现——不显示任何"未知"或错误。
            var gameRoot = VolumePaths.TryGetVolumeRoot(TryGetConfiguredGamePath(paths));

            var options = new List<RepositoryDriveOption>();

            DriveInfo[] drives;
            try
            {
                drives = DriveInfo.GetDrives();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[RepoSetup] 枚举磁盘失败，引导界面只提供手动选择");
                return Array.Empty<RepositoryDriveOption>();
            }

            foreach (var drive in drives)
            {
                try
                {
                    // 未就绪的卷（空光驱、拔掉的读卡器）连盘符都是假的，列出来只会误导
                    if (!drive.IsReady) continue;

                    var label = TryGetVolumeLabel(drive);
                    var root = drive.RootDirectory.FullName;
                    options.Add(new RepositoryDriveOption(
                        RootPath: root,
                        DisplayName: string.IsNullOrWhiteSpace(label)
                            ? drive.Name.TrimEnd('\\')
                            : $"{drive.Name.TrimEnd('\\')} {label}",
                        Kind: MapKind(drive.DriveType),
                        AvailableBytes: drive.AvailableFreeSpace,
                        TotalBytes: drive.TotalSize,
                        IsCurrentDefault: currentRoot != null && string.Equals(
                            currentRoot, root, StringComparison.OrdinalIgnoreCase),
                        HostsCurrentGame: gameRoot != null && string.Equals(
                            gameRoot, root, StringComparison.OrdinalIgnoreCase)));
                }
                catch (Exception ex)
                {
                    // 单个盘查不动不该让整张列表消失（域环境里的映射盘最容易在这里抛）
                    _logger.LogDebug(ex, "[RepoSetup] 跳过读不出信息的盘");
                }
            }

            return RepositoryDriveAdvisor.Rank(options);
        }

        /// <summary>
        /// 读出当前配置的游戏安装路径；读不到返回 <c>null</c>。
        ///
        /// <para>
        /// <b>直接读 config.json，不注入 <c>GameConfigService</c></b>：那个服务在引导跑的时候
        /// 还没被解析过，为了一条提示把它提前构造出来，就等于把"第一次读主配置"的时机
        /// 挪进了引导内部——本服务的时序约束本来就已经夹在搬迁器和 ObjectStore 之间，
        /// 不该再多绑一个。这里只要一个字段，读一次文件就够，而且路径来自
        /// <see cref="RepositorySetupPaths"/>，测试能整体指到临时目录。
        /// </para>
        ///
        /// <para>
        /// <b>任何失败都当作"没有游戏路径"</b>：这条信息只驱动一句可有可无的提示，
        /// 为它把启动早期的引导带崩是完全不成比例的。
        /// </para>
        /// </summary>
        private string? TryGetConfiguredGamePath(RepositorySetupPaths paths)
        {
            foreach (var configFile in new[] { paths.ConfigFile, paths.LegacyConfigFile })
            {
                try
                {
                    if (!File.Exists(configFile)) continue;

                    var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(configFile));
                    var gamePath = config?.GamePath?.Trim();
                    if (!string.IsNullOrWhiteSpace(gamePath)) return gamePath;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[RepoSetup] 读取游戏路径失败，本次不标注同盘提示: {Path}", configFile);
                }
            }

            return null;
        }

        private static string? TryGetVolumeLabel(DriveInfo drive)
        {
            try { return drive.VolumeLabel; }
            catch { return null; }
        }

        private static RepositoryVolumeKind MapKind(DriveType type) => type switch
        {
            DriveType.Fixed => RepositoryVolumeKind.Fixed,
            DriveType.Removable => RepositoryVolumeKind.Removable,
            DriveType.Network => RepositoryVolumeKind.Network,
            _ => RepositoryVolumeKind.Unknown,
        };

        // ─── 候选位置检查 ───

        /// <summary>
        /// 检查用户选中的位置。会真的往目标写一个探针文件再删掉——
        /// "有没有写入权限"没有别的可靠判法（ACL 推演在网络位置与容器化目录上一律不准），
        /// 而这一步失败正是引导必须提前拦住的头号情形：
        /// <see cref="UiPreferences.SaveRepositoryRoot"/> 的写失败会上抛，
        /// 而引导跑在启动早期，不拦就是一次启动崩溃。
        /// </summary>
        public RepositoryLocationVerdict Inspect(string? selectedPath)
        {
            // 路径本身不成立时一个 IO 都不做：拿一个相对路径去建目录、试写、查卷，
            // 得到的失败原因会指向错误的方向。判据与 Core 共用同一份，不在这里再写一遍。
            if (!RepositoryLocationValidator.IsPathAcceptable(selectedPath, out _, out var trimmed))
            {
                return RepositoryLocationValidator.Validate(new RepositoryLocationProbe(
                    SelectedPath: selectedPath,
                    DirectoryHasContent: false,
                    IsWritable: false,
                    WriteFailureMessage: null,
                    VolumeKind: RepositoryVolumeKind.Unknown,
                    AvailableBytes: null,
                    IsInsideInstallDirectory: false));
            }

            var hasContent = HasContent(trimmed);
            var resolved = RepositoryLocationValidator.ResolveRepositoryPath(trimmed, hasContent);
            var writable = TryWriteProbe(resolved, out var failure);

            var verdict = RepositoryLocationValidator.Validate(new RepositoryLocationProbe(
                SelectedPath: trimmed,
                DirectoryHasContent: hasContent,
                IsWritable: writable,
                WriteFailureMessage: failure,
                VolumeKind: ProbeVolumeKind(trimmed),
                AvailableBytes: DriveFreeSpaceProbe.Instance.TryGetAvailableFreeBytes(trimmed),
                IsInsideInstallDirectory: IsInside(ResolveEnvironment().Paths.InstallDirectory, trimmed)));

            _logger.LogInformation("[RepoSetup] 候选位置 {Path} → {Severity}（落点 {Resolved}）",
                trimmed, verdict.Severity, verdict.ResolvedPath);
            return verdict;
        }

        private static RepositoryVolumeKind ProbeVolumeKind(string path)
        {
            try
            {
                var full = Path.GetFullPath(path);
                // UNC 路径拿不到盘符，DriveInfo 直接抛；它一定是网络位置
                if (full.StartsWith(@"\\", StringComparison.Ordinal)) return RepositoryVolumeKind.Network;

                var root = Path.GetPathRoot(full);
                if (string.IsNullOrEmpty(root)) return RepositoryVolumeKind.Unknown;

                var drive = new DriveInfo(root);
                return drive.IsReady ? MapKind(drive.DriveType) : RepositoryVolumeKind.Unknown;
            }
            catch
            {
                return RepositoryVolumeKind.Unknown;
            }
        }

        /// <summary>
        /// 真写一次再删掉。探针文件名带 Guid，避免撞上用户自己的文件。
        ///
        /// <para>
        /// <b>检查完把目录原样退回去。</b>目录是这一步顺带建出来的
        /// （"建不出目录"与"目录里写不进文件"对用户是同一件事，必须一起验），
        /// 但候选位置终究只是候选：界面上每换一个盘就检查一次，不退回的话用户点过的
        /// 每个盘上都会躺着一对空目录 —— 而且是他从没确认过的位置。
        /// 只删我们自己建的那几层，且只在仍然为空时删。
        /// </para>
        /// </summary>
        private bool TryWriteProbe(string resolvedPath, out string? failureMessage)
        {
            var createdByUs = CollectMissingAncestors(resolvedPath);
            var probeFile = Path.Combine(resolvedPath, $".uemm-write-probe-{Guid.NewGuid():N}.tmp");
            try
            {
                Directory.CreateDirectory(resolvedPath);
                File.WriteAllText(probeFile, string.Empty);
                failureMessage = null;
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[RepoSetup] 候选位置不可写: {Path}", resolvedPath);
                failureMessage = ex.Message;
                return false;
            }
            finally
            {
                try { if (File.Exists(probeFile)) File.Delete(probeFile); }
                catch { /* 探针删不掉只是留个 0 字节文件，不值得让检查失败 */ }

                // CollectMissingAncestors 是自底向上收集的，正好按"先删最深一层"的顺序退回
                foreach (var dir in createdByUs)
                {
                    try
                    {
                        if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                            Directory.Delete(dir);
                    }
                    catch { /* 删不掉就留着，空目录不影响任何功能 */ }
                }
            }
        }

        /// <summary>
        /// 从 <paramref name="path"/> 一路向上，列出此刻还不存在的每一层（自深向浅）。
        /// 这就是"接下来的 CreateDirectory 会新建哪几层"，也是检查完要退回的那几层。
        /// </summary>
        private static List<string> CollectMissingAncestors(string path)
        {
            var missing = new List<string>();
            try
            {
                for (var dir = path;
                     !string.IsNullOrEmpty(dir) && !Directory.Exists(dir);
                     dir = Path.GetDirectoryName(dir))
                {
                    missing.Add(dir);
                }
            }
            catch
            {
                // 算不出来就一层都不退（宁可留下空目录，也不能删到不属于我们的东西）
                missing.Clear();
            }
            return missing;
        }

        private static bool IsInside(string parent, string candidate)
        {
            try
            {
                var normalizedParent = Path.GetFullPath(parent)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var normalizedCandidate = Path.GetFullPath(candidate)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (string.Equals(normalizedParent, normalizedCandidate, StringComparison.OrdinalIgnoreCase))
                    return true;

                // 必须带上分隔符再比前缀，否则 C:\App 会把 C:\AppData 也算成自己的子目录
                return normalizedCandidate.StartsWith(
                    normalizedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string? TryGetPathRoot(string path)
        {
            try { return Path.GetPathRoot(Path.GetFullPath(path)); }
            catch { return null; }
        }

        // ─── 落盘 ───

        /// <summary>
        /// 采纳用户选中的位置。<b>失败上抛</b>，由对话框弹给用户并保持打开让他重选
        /// （与 <c>SettingsWindow.Save_Click</c> 同一模式）。
        ///
        /// <para>
        /// 顺序是<b>先存位置、成功之后才记"已问过"</b>：反过来的话，位置落盘失败时
        /// 用户既没设置成功、下次也不会再被问，那个选择就永久丢了。
        /// </para>
        /// </summary>
        public void Apply(string resolvedPath)
        {
            if (string.IsNullOrWhiteSpace(resolvedPath))
                throw new ArgumentException("仓库位置不能为空", nameof(resolvedPath));

            var env = ResolveEnvironment();
            Directory.CreateDirectory(resolvedPath);
            env.Preferences.SaveRepositoryRoot(resolvedPath);
            env.Preferences.SavePrompted();
            _logger.LogInformation("[RepoSetup] 仓库位置已设置为 {Path}", resolvedPath);
        }

        /// <summary>
        /// 用户跳过（点"以后再说"或直接关掉窗口）：只记"已问过"，位置保持默认。
        /// <b>不抛</b>——跳过路径上没有任何 UI 能承接这个错误，而且跳过本来就是"什么都别做"，
        /// 为一个内部标记弹框只会让人莫名其妙；失败的唯一后果是下次启动再问一次。
        /// </summary>
        public void Skip()
        {
            try
            {
                ResolveEnvironment().Preferences.SavePrompted();
                _logger.LogInformation("[RepoSetup] 用户跳过了仓库位置引导，沿用默认位置");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[RepoSetup] 记录引导标记失败，下次启动会再问一次");
            }
        }
    }

    /// <summary>
    /// <see cref="IRepositorySetupPreferences"/> 的生产实现，转调静态的 <see cref="UiPreferences"/>。
    /// 只是一层直通转发，没有自己的状态，故用单例（与
    /// <see cref="UiPreferencesDataMigrationAdapter"/> 同一形态）。
    /// </summary>
    internal sealed class UiPreferencesRepositorySetupAdapter : IRepositorySetupPreferences
    {
        internal static readonly UiPreferencesRepositorySetupAdapter Instance = new();

        private UiPreferencesRepositorySetupAdapter() { }

        public string? LoadRepositoryRoot() => UiPreferences.LoadRepositoryRoot();

        public void SaveRepositoryRoot(string? path) => UiPreferences.SaveRepositoryRoot(path);

        public bool LoadPrompted() => UiPreferences.LoadRepositoryLocationPrompted();

        public void SavePrompted() => UiPreferences.SaveRepositoryLocationPrompted();
    }
}
