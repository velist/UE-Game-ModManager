using System;
using System.IO;
using System.Text.Json;
using UEModManager.Infrastructure;
using UEModManager.Models;
using UEModManager.Services.Paths;
using UEModManager.Services.Persistence;
using UEModManager.Services.Telemetry;

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
    ///   重定向到结构化日志文件，故统一用 Console 取证（与 Infrastructure/SafeEvent 一致）；
    /// - <b>失败语义分两档</b>：用户显式发起的设置变更走 <see cref="Write"/>（log + throw），
    ///   隐式的顺带落盘走 <see cref="WriteQuietly"/>（只 log）。读取一律不抛，回落默认值后留痕。
    ///
    /// <para>
    /// 为什么写失败必须上抛：这里装的是仓库位置、背景图、语言、部署后端。写失败此前被
    /// 一个空 catch 吞掉，用户在设置界面改完看到界面正常响应，重启后全部还原——而同一个
    /// 根因（目标不可写：磁盘满 / 权限 / 杀软锁定 / 同步盘占用）下，改游戏路径、导入 MOD、
    /// 改方案却会弹错误框（GameConfigService / PackageRepository / ProfileService 都是
    /// log + throw）。分裂的失败语义比全都静默更糟：用户会因为"别处会报错"而信任这里的沉默。
    /// 本类是最后一处例外，现已并入同一语义。
    /// </para>
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
            public int DeployBackendType { get; set; } = 0;
            public bool AutoDeploy { get; set; } = true;

            /// <summary>
            /// 已经就"这次没能按你选的方式部署"告知过用户的情形签名
            /// （见 <c>DeploymentDegradationNotice.BuildSignature</c>）。
            ///
            /// <para>
            /// <b>存签名而不是存一个"已提示过"的布尔</b>：用户把包仓库搬到别的盘、
            /// 或者换一个装在别的盘上的游戏之后，结论真的变了，那时值得再说一次；
            /// 而同一组合下每次部署都弹一遍只是骚扰，最终结果是用户开始无视所有提示。
            /// </para>
            /// </summary>
            public string? DeployDegradationNotice { get; set; }
            public string? RepositoryRoot { get; set; }
            public string? OverwritesRoot { get; set; }
            public string? BackupsRoot { get; set; }

            /// <summary>
            /// 在途的仓库搬移日记：阶段 + 从哪搬 + 搬到哪
            /// （见 <c>RepositoryRelocationJournal</c>）。
            ///
            /// <para>
            /// <b>刻意和 <see cref="RepositoryRoot"/> 放在同一个文件里。</b>
            /// 搬移落定的那一刻要同时做两件事：把存放位置改成新位置、把日记推进到"只剩清尾"。
            /// 两件事分处两个文件就必然存在一个"改了一个没改另一个"的窗口，
            /// 而那个窗口正好是断电恢复唯一读不懂的状态。同一个文件意味着它们由
            /// <see cref="SaveConfig"/> 的一次原子写一起落盘，要么都生效、要么都不生效。
            /// </para>
            /// </summary>
            public int RepositoryRelocationPhase { get; set; }

            public string? RepositoryRelocationSource { get; set; }

            public string? RepositoryRelocationTarget { get; set; }

            /// <summary>
            /// 首次运行的仓库位置引导是否已经问过。
            /// 独立于 <see cref="RepositoryRoot"/>：跳过引导的人这里为 true 而 RepositoryRoot 仍为 null，
            /// 拿"有没有位置"当判据会让他每次启动都被问一次。
            /// </summary>
            public bool RepositoryLocationPrompted { get; set; }

            /// <summary>
            /// 已迁移到的数据目录布局版本（见 DataRelocationPlanner.CurrentLayoutVersion）。
            /// 0 表示尚未迁移。用版本号而非布尔，是为了将来再次调整目录结构时能做增量迁移。
            /// </summary>
            public int DataLayoutVersion { get; set; }

            /// <summary>
            /// 是否已就"匿名统计"告知过用户。<b>与 <see cref="TelemetryEnabled"/> 正交，
            /// 不能用后者是不是默认值来推断。</b>
            ///
            /// <para>
            /// 一个主动选了"参与"的用户与一个从没被问过的用户，<see cref="TelemetryEnabled"/>
            /// 都是 true，长得一模一样。合并成一个字段的后果只有两种：要么每次启动再问一遍，
            /// 要么在还没告知的时候就开始上报——后者是合规意义上最严重的一种错误。
            /// </para>
            /// </summary>
            public bool TelemetryAsked { get; set; }

            /// <summary>
            /// 匿名统计开关。<b>默认 true</b>。
            ///
            /// <para>
            /// 默认关会让样本只剩下"愿意主动去设置里翻出这一项并打开"的人，那不是典型用户，
            /// 数字会失真到没有参考价值——那还不如不做。所以取的是
            /// "默认开 + 首次运行明确告知一次 + 设置里随时能关"，
            /// 把代价放在"必须真的告知到位"上，而不是放在"数字不可信"上。
            /// </para>
            ///
            /// <para>
            /// 默认值为 true 不等于可以先斩后奏：<c>TelemetryConsent.Decide</c> 里
            /// <see cref="TelemetryAsked"/> 优先级最高，没问过时这个 true 一个字节也发不出去。
            /// </para>
            /// </summary>
            public bool TelemetryEnabled { get; set; } = true;

            /// <summary>
            /// 浅拷贝。全部属性都是值类型或 string（不可变），浅拷贝即完整快照。
            /// <see cref="Write"/> 靠它做到"写盘失败时内存单例保持原值"。
            /// 将来若加入集合或可变对象属性，这里必须跟着做深拷贝，否则回滚会失效。
            /// </summary>
            public UiConfig Clone() => (UiConfig)MemberwiseClone();
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

        /// <summary>
        /// 配置文件位置的测试覆盖。<b>生产恒为 null</b>，取值走 <see cref="AppPaths.UiConfigFile"/>。
        /// 只由 <see cref="OverrideConfigPathForTests"/> 写，且始终在 <see cref="Gate"/> 内。
        /// </summary>
        private static string? _configPathOverride;

        /// <summary>
        /// 把配置文件重定向到 <paramref name="configFilePath"/> 并清空内存单例，
        /// 返回的对象 Dispose 时复原（含再清一次缓存，不把临时目录的值留给后续用例）。
        ///
        /// <para>
        /// 没有这个口子，任何一条测试用例都会真的读写开发者本机的
        /// <c>%APPDATA%\UEModManager\ui_config.json</c>：里面装着仓库根、背景图、语言，
        /// 跑一次测试就改掉开发者的真实偏好，而"改仓库根"恰恰是本类最危险的一项——
        /// <c>ObjectStore</c> 会跟着换目录，界面上的 MOD 直接消失。
        /// 其他服务是靠"指定路径的测试用构造函数"做到隔离的
        /// （<c>ObjectStore</c> / <c>GameConfigService</c> / <c>OverwriteStore</c>），
        /// 静态类没有构造函数可用，只能显式开一个作用域式覆盖。
        /// </para>
        ///
        /// <para>
        /// <b>静态状态没法并行。</b>覆盖期间任何线程读本类都会看到临时路径，因此消费本口子的
        /// 测试类必须与其它会碰 <see cref="UiPreferences"/> 的测试类放进同一个 xUnit
        /// collection 串行执行（见 <c>UiPreferencesStaticStateCollection</c>）。
        /// </para>
        /// </summary>
        internal static IDisposable OverrideConfigPathForTests(string configFilePath)
        {
            lock (Gate)
            {
                var scope = new TestOverrideScope(_configPathOverride);
                _configPathOverride = configFilePath;
                ResetStateInsideGate();
                return scope;
            }
        }

        /// <summary>丢弃内存单例，让下一次访问重新读盘。调用方必须已持有 <see cref="Gate"/>。</summary>
        private static void ResetStateInsideGate()
        {
            _cached = null;
            _diskStateUnknown = false;
        }

        private sealed class TestOverrideScope : IDisposable
        {
            private readonly string? _previous;

            internal TestOverrideScope(string? previous) => _previous = previous;

            public void Dispose()
            {
                lock (Gate)
                {
                    _configPathOverride = _previous;
                    ResetStateInsideGate();
                }
            }
        }

        /// <summary>
        /// 配置文件位置。路径归口 <see cref="AppPaths.UiConfigFile"/>，与本类此前自己拼的
        /// <c>%APPDATA%\UEModManager\ui_config.json</c> 是同一个文件；双源时改一处漏一处，
        /// 应用会静默读写两个不同的配置。
        ///
        /// <para>
        /// 注意这里存在一条受控的环：<see cref="AppPaths"/> 要读本类拿用户自定义的数据根。
        /// 环之所以不闭合，是因为 <see cref="AppPaths.UiConfigFile"/> 用的是不含用户覆盖的
        /// 布局，取值只依赖 <c>%APPDATA%</c>，不会再回头读配置。若哪天把它改成读覆盖的版本，
        /// 这里会立刻变成无限递归。
        /// </para>
        /// </summary>
        private static string GetConfigPath()
        {
            var path = _configPathOverride ?? AppPaths.UiConfigFile;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return path;
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

        // ── 读写入口 ──
        //
        // 读：不允许抛异常（调用点多在窗口构造与启动路径上，AppPaths 解析数据根也要读这里），
        //     失败一律留痕后回落默认值——启动不了比读到默认配置更糟。
        // 写：默认 log + throw（Write）。除 SaveDataLayoutVersion 之外的每一项都是用户在
        //     设置界面上显式改出来的，失败必须让他看见。
        //
        // 新增设置项时先问一句"这次保存是谁发起的"：用户点了才发生 → Write；
        // 加载/迁移路径上顺带回写 → WriteQuietly，并把理由写清楚。

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
                    // 改在副本上，只有 SaveConfig 成功之后才把副本提升为内存单例
                    // （SaveConfig 末尾那句 _cached = cfg）。
                    //
                    // 不能直接改 _cached 指向的那个实例：那样写盘失败时内存里已经是新值，
                    // 用户看到错误框，但本次会话仍按一个没落盘的值继续跑（仓库根就是这么
                    // "半生效"的——ObjectStore 换了目录，MOD 消失，重启后又换回来），
                    // 而且下一次任何设置保存成功时会把这个失败的改动一起写进去。
                    // "写失败"的语义必须是"这次改动没生效"。
                    var draft = GetOrLoadConfig().Clone();
                    mutate(draft);
                    SaveConfig(draft);
                }
                catch (Exception ex)
                {
                    LogFailure($"{operation} 失败，本次设置未写入磁盘", ex);
                    throw;
                }
            }
        }

        /// <summary>
        /// 顺带保存：记日志、不上抛。
        ///
        /// <para>
        /// 只给"用户没有发起、也没有 UI 能承接错误"的隐式落盘用，语义与
        /// <c>GameConfigService.TrySaveConfigQuietly</c> 对齐。用户显式发起的保存一律走
        /// <see cref="Write"/>，失败要看得见。
        /// </para>
        /// </summary>
        private static void WriteQuietly(Action<UiConfig> mutate, string operation)
        {
            try
            {
                Write(mutate, operation);
            }
            catch (Exception ex)
            {
                LogFailure($"{operation} 失败，已忽略以免中断当前动作", ex);
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

        public static bool LoadAutoDeploy()
        {
            return Read(cfg => cfg.AutoDeploy, true, "读取自动部署设置");
        }

        public static void SaveAutoDeploy(bool auto)
        {
            Write(cfg => cfg.AutoDeploy = auto, "保存自动部署设置");
        }

        /// <summary>读取"已经告知过的部署降级情形签名"；从没告知过返回 <c>null</c>。</summary>
        public static string? LoadDeployDegradationNotice()
        {
            return Read<string?>(cfg =>
            {
                var signature = cfg.DeployDegradationNotice?.Trim();
                return string.IsNullOrWhiteSpace(signature) ? null : signature;
            }, null, "读取部署降级告知记录");
        }

        /// <summary>
        /// 记下"这种情形已经告知过了"。
        /// <b>走 WriteQuietly</b>：这不是用户发起的设置变更，而是弹完提示之后的顺带记账；
        /// 写失败的唯一后果是下次同样的情形再提示一次，为它弹一个错误框只会让人莫名其妙。
        /// </summary>
        public static void SaveDeployDegradationNotice(string? signature)
        {
            WriteQuietly(cfg => cfg.DeployDegradationNotice = signature, "保存部署降级告知记录");
        }

        public static string? LoadRepositoryRoot()
        {
            return Read<string?>(cfg =>
            {
                var path = cfg.RepositoryRoot?.Trim();
                return string.IsNullOrWhiteSpace(path) ? null : path;
            }, null, "读取仓库根目录");
        }

        /// <summary>
        /// 写入用户自定义的仓库根。<b>失败上抛。</b>
        ///
        /// <para>
        /// 这一项是全部偏好里后果最重的：<c>ObjectStore</c> 与 <c>AppPaths.RepositoryRoot</c>
        /// 都以它为准，值丢了包实体就"消失"了。两个调用方都接得住异常——
        /// <c>SettingsWindow.Save_Click</c> 有 try/catch + CyberMessageBox，
        /// 搬迁器的原地登记跑在 <c>DataLocationMigrator.TryExecute</c> 里（单项失败只计一次
        /// failed，数据仍完整留在旧位置，启动不受影响）。
        /// </para>
        /// </summary>
        public static void SaveRepositoryRoot(string? path)
        {
            Write(cfg => cfg.RepositoryRoot = string.IsNullOrWhiteSpace(path) ? null : path.Trim(), "保存仓库根目录");
        }

        // ── 在途的仓库搬移日记 ──

        /// <summary>读出在途搬移日记；没有在途搬移时返回 <see cref="RepositoryRelocationJournal.None"/>。</summary>
        public static RepositoryRelocationJournal LoadRepositoryRelocationJournal()
        {
            return Read(cfg =>
            {
                var phase = cfg.RepositoryRelocationPhase;
                // 认不出的阶段值（配置被手改过、或将来降级安装到旧版本）一律当作没有在途搬移：
                // 恢复动作里有"清空目标目录"这种不可逆的一步，判据说不清时唯一安全的做法是什么都不做。
                if (!Enum.IsDefined(typeof(RepositoryRelocationPhase), phase))
                {
                    return RepositoryRelocationJournal.None;
                }

                return new RepositoryRelocationJournal(
                    (RepositoryRelocationPhase)phase,
                    string.IsNullOrWhiteSpace(cfg.RepositoryRelocationSource)
                        ? null : cfg.RepositoryRelocationSource.Trim(),
                    string.IsNullOrWhiteSpace(cfg.RepositoryRelocationTarget)
                        ? null : cfg.RepositoryRelocationTarget.Trim());
            }, RepositoryRelocationJournal.None, "读取仓库搬移日记");
        }

        /// <summary>
        /// 写下在途搬移日记。<b>失败上抛</b>：日记是断电之后唯一能认出"那两个路径"的东西，
        /// 写不下去就说明这次搬移不该开始——真断电了没人收拾得了。
        /// </summary>
        public static void SaveRepositoryRelocationJournal(RepositoryRelocationJournal journal)
        {
            if (journal is null) throw new ArgumentNullException(nameof(journal));

            Write(cfg =>
            {
                cfg.RepositoryRelocationPhase = (int)journal.Phase;
                cfg.RepositoryRelocationSource = journal.SourceRoot;
                cfg.RepositoryRelocationTarget = journal.TargetRoot;
            }, "保存仓库搬移日记");
        }

        /// <summary>
        /// 落定：把存放位置改成新位置、同时把日记推进到"只剩清尾"。
        ///
        /// <para>
        /// <b>这两件事必须在同一次写里完成</b>，这也是把日记塞进 <c>ui_config.json</c>
        /// 的全部理由。分成两次写就必然存在一个中间态：存放位置已经指向新位置、
        /// 而日记还停在"搬移中"。那一刻断电，下次启动的恢复判定会看到
        /// "指针在新位置 + 旧位置有墓碑"——这一格恰好还判得对（继续清尾），
        /// 但反过来的顺序（先推进日记再改指针）就会在同一格里判成"往前推"，
        /// 对一个已经写好的指针再写一次。判据能被一次断电改变结论，这种代码不该存在。
        /// </para>
        /// </summary>
        public static void CommitRepositoryRelocation(string targetRoot, string sourceRoot)
        {
            if (string.IsNullOrWhiteSpace(targetRoot))
                throw new ArgumentException("新位置不能为空", nameof(targetRoot));

            Write(cfg =>
            {
                cfg.RepositoryRoot = targetRoot.Trim();
                cfg.RepositoryRelocationPhase = (int)RepositoryRelocationPhase.Cleanup;
                cfg.RepositoryRelocationSource = sourceRoot?.Trim();
                cfg.RepositoryRelocationTarget = targetRoot.Trim();
            }, "落定仓库搬移（改存放位置 + 推进日记）");
        }

        /// <summary>
        /// 划掉日记。<b>走 WriteQuietly</b>：搬移已经彻底做完了，此刻再弹一个
        /// "保存失败"只会让用户以为刚搬好的 MOD 出了问题。失败的唯一后果是下次启动
        /// 多做一次判定，而那次判定会得出 <c>ClearJournalOnly</c>，再划一次。
        /// </summary>
        public static void ClearRepositoryRelocationJournal()
        {
            WriteQuietly(cfg =>
            {
                cfg.RepositoryRelocationPhase = (int)RepositoryRelocationPhase.Idle;
                cfg.RepositoryRelocationSource = null;
                cfg.RepositoryRelocationTarget = null;
            }, "清除仓库搬移日记");
        }

        // ── 其余可自定义的数据根 ──
        //
        // 与 RepositoryRoot 同构：非空即表示"用户/迁移器已显式指定过位置"，
        // 数据搬迁规划器据此判定"一步都不能动"。空白值一律归一为 null，
        // 避免配置里留下空字符串把数据根指到当前工作目录。
        //
        // 失败语义同样与 RepositoryRoot 一致：写不进去就抛。搬迁器那条路径
        // （RegisterInPlace ← TryExecute）接得住，只计一次 failed 不阻断启动。

        public static string? LoadOverwritesRoot()
        {
            return Read<string?>(cfg =>
            {
                var path = cfg.OverwritesRoot?.Trim();
                return string.IsNullOrWhiteSpace(path) ? null : path;
            }, null, "读取生成物根目录");
        }

        public static void SaveOverwritesRoot(string? path)
        {
            Write(cfg => cfg.OverwritesRoot = string.IsNullOrWhiteSpace(path) ? null : path.Trim(), "保存生成物根目录");
        }

        public static string? LoadBackupsRoot()
        {
            return Read<string?>(cfg =>
            {
                var path = cfg.BackupsRoot?.Trim();
                return string.IsNullOrWhiteSpace(path) ? null : path;
            }, null, "读取备份根目录");
        }

        public static void SaveBackupsRoot(string? path)
        {
            Write(cfg => cfg.BackupsRoot = string.IsNullOrWhiteSpace(path) ? null : path.Trim(), "保存备份根目录");
        }

        // ── 首次运行的仓库位置引导 ──

        /// <summary>首次运行的仓库位置引导是否已经问过；从未问过返回 false。</summary>
        public static bool LoadRepositoryLocationPrompted()
        {
            return Read(cfg => cfg.RepositoryLocationPrompted, false, "读取仓库位置引导标记");
        }

        /// <summary>
        /// 记下"已经问过仓库位置"。
        ///
        /// <para>
        /// <b>静默写入</b>（本类第二个，也是最后一个）。这不是设置界面上的一项，用户看不见它，
        /// 弹一个"保存标记失败"的框只会让人莫名其妙；而它写不上的唯一后果是下次启动再问一次，
        /// 与 <see cref="SaveDataLayoutVersion"/> 同类，属于可承受的失败。
        /// </para>
        ///
        /// <para>
        /// <b>调用顺序是硬要求：先存位置，成功之后才记标记。</b>反过来的话，
        /// 位置落盘失败时用户既没设置成功、下次也不会再被问，那个选择就永久丢了。
        /// </para>
        /// </summary>
        public static void SaveRepositoryLocationPrompted()
        {
            WriteQuietly(cfg => cfg.RepositoryLocationPrompted = true, "保存仓库位置引导标记");
        }

        // ── 数据目录布局版本 ──

        /// <summary>读取已迁移到的数据目录布局版本；从未迁移过返回 0。</summary>
        public static int LoadDataLayoutVersion()
        {
            return Read(cfg => cfg.DataLayoutVersion, 0, "读取数据布局版本");
        }

        /// <summary>
        /// 写入已迁移到的数据目录布局版本。
        ///
        /// <para>
        /// 与 <see cref="SaveRepositoryLocationPrompted"/> 并列为本类仅有的两处静默写入，三条理由：
        /// <list type="number">
        /// <item>唯一调用方是 <c>DataLocationMigrator.Run</c>，用户没发起任何操作，
        /// 迁移跑在主窗口创建之前，压根没有 UI 能承接这个错误。</item>
        /// <item><c>Run</c> 里这一句没有局部 try/catch，抛出去会让所有搬迁步骤都成功之后
        /// 半途中断，外层 <c>RunAsync</c> 兜住并报成"迁移异常，已沿用旧数据位置"——
        /// 一条彻底的误报，而搬迁器的铁律是任何失败都不得阻断启动。</item>
        /// <item>失败的后果本身可承受：版本标记只增不减，没写上就是下次启动重新探测一遍磁盘，
        /// 不会造成数据损坏（规划器以墓碑为准）。</item>
        /// </list>
        /// </para>
        /// </summary>
        public static void SaveDataLayoutVersion(int version)
        {
            WriteQuietly(cfg => cfg.DataLayoutVersion = version, "保存数据布局版本");
        }

        // ── 匿名统计 ──

        /// <summary>
        /// 读出用户对匿名统计的当前状态。读失败时回落到"没问过 + 开"——
        /// 而"没问过"意味着不上报，所以读不出配置的最坏结果是<b>少统计</b>，不是偷偷上报。
        /// </summary>
        public static TelemetryConsentState LoadTelemetryConsent()
        {
            return Read(cfg => new TelemetryConsentState(cfg.TelemetryAsked, cfg.TelemetryEnabled),
                new TelemetryConsentState(Asked: false, Enabled: true), "读取匿名统计设置");
        }

        /// <summary>
        /// 记下用户在首次运行的告知框里做的选择。
        ///
        /// <para>
        /// <b>走 WriteQuietly</b>：这是弹完提示之后的顺带记账，用户刚点完一个"知道了"，
        /// 紧接着甩他一个"保存失败"只会让人莫名其妙。写失败的唯一后果是下次启动再问一次
        /// ——与 <see cref="SaveRepositoryLocationPrompted"/> 同类，属于可承受的失败。
        /// </para>
        ///
        /// <para>
        /// <b>两个字段必须在同一次写里落盘。</b>分两次写就存在一个中间态：已标记问过、
        /// 而选择还没写上。那一刻断电，用户明明选了"不参与"，下次启动却读到
        /// "问过 + 默认开"，于是开始上报——恰好是这套设计最不能出的那个错。
        /// </para>
        /// </summary>
        public static void SaveTelemetryConsent(bool enabled)
        {
            WriteQuietly(cfg =>
            {
                cfg.TelemetryAsked = true;
                cfg.TelemetryEnabled = enabled;
            }, "保存匿名统计选择");
        }

        /// <summary>
        /// 用户在设置界面里显式改这个开关。<b>失败上抛</b>：与本类其余"用户点了才发生"的
        /// 设置项同一语义——关掉统计却没关成，用户必须看得见。
        /// </summary>
        public static void SaveTelemetryEnabled(bool enabled)
        {
            Write(cfg =>
            {
                // 在设置里主动改过，等同于知情，顺带把"问过"钉上。
                // 否则一个先在设置里关掉、又从没被弹窗问过的用户，下次启动会被弹一次
                // "我们要开始统计了"——而他刚刚才明确表示过不要。
                cfg.TelemetryAsked = true;
                cfg.TelemetryEnabled = enabled;
            }, "保存匿名统计开关");
        }

    }
}
