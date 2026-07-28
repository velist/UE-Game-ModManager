using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Infrastructure;
using UEModManager.Services.Paths;
using UEModManager.Services.Persistence;

namespace UEModManager.Services
{
    /// <summary>
    /// 迁移执行结果。
    ///
    /// <para>
    /// <b>UI 侧请只看 <see cref="Status"/> / <see cref="ShouldNotifyUser"/> / <see cref="UserMessage"/></b>，
    /// 不要自己拿 <see cref="Completed"/> 或三个计数去判断。<see cref="Completed"/> 的语义是
    /// "本布局版本可以打版本标记了"，推迟项也会让它变 false——而推迟是搬移开关关闭时的
    /// 正常状态，照着它提示会让全体用户每次启动都收到一条毫无意义的告警。
    /// 判定规则连同理由都在 Core 的 <see cref="DataMigrationStatusClassifier"/> 里，有单测。
    /// </para>
    /// </summary>
    /// <param name="Completed">本次是否既无失败也无推迟（决定是否打版本标记）。</param>
    /// <param name="Executed">成功执行的项数。</param>
    /// <param name="Skipped">规划为无需动作的项数。</param>
    /// <param name="Deferred">因搬移开关关闭而推迟的项数。</param>
    /// <param name="Failed">失败的项数。</param>
    /// <param name="Summary">供日志使用的一行摘要。</param>
    public sealed record DataMigrationOutcome(
        bool Completed,
        int Executed,
        int Skipped,
        int Deferred,
        int Failed,
        string Summary)
    {
        /// <summary>整体状态。</summary>
        public DataMigrationStatus Status
            => DataMigrationStatusClassifier.Classify(Executed, Deferred, Failed);

        /// <summary>是否该给用户一条非模态提示。</summary>
        public bool ShouldNotifyUser => DataMigrationStatusClassifier.ShouldNotifyUser(Status);

        /// <summary>用户可见文案；不需要提示时为 <c>null</c>。</summary>
        public string? UserMessage => DataMigrationStatusClassifier.BuildUserMessage(Status);
    }

    /// <summary>
    /// 数据目录搬迁器。职责只有编排：探测磁盘 → 交 Core 规划 → 交执行器落地 →
    /// 改写配置里指向旧位置的绝对路径 → 打版本标记。
    ///
    /// <para>
    /// 判定逻辑在 Core 的 <see cref="DataRelocationPlanner"/>（纯函数，74 个单测），
    /// 文件操作在 <see cref="DataRelocationExecutor"/>（可在临时目录上测），
    /// 本类负责把两者接到真实的路径与偏好上。
    /// </para>
    ///
    /// <para>
    /// 本类<b>自己不碰任何静态全局状态</b>：<see cref="AppPaths"/> 与
    /// <see cref="UiPreferences"/> 只在 <see cref="CreateProductionEnvironment"/> 这一处出现，
    /// 其余代码一律走构造时传入的 <see cref="DataMigrationEnvironment"/>。
    /// 这样测试能把整个迁移器指到临时目录上驱动，而不会碰开发者本机真实的
    /// <c>%LOCALAPPDATA%</c> / <c>%APPDATA%</c>——本类会<b>复制并删除</b>这些位置下的文件，
    /// 跑错一次就没有后悔药。<see cref="DataRelocationExecutor"/> 当初从本类里拆出去
    /// 正是因为这层全局状态没法在测试里安全驱动，现在本类自己也补上了这一刀。
    /// </para>
    ///
    /// <para>
    /// 本类的任何失败都不得阻断启动：数据留在旧位置尚可用，启动不了则彻底不可用。
    /// </para>
    /// </summary>
    public sealed class DataLocationMigrator
    {
        /// <summary>
        /// 搬移类动作（Copy / PurgeTargetThenCopy / ResumeCleanup）的<b>生产</b>执行开关。
        ///
        /// <para>
        /// <b>这是全项目唯一决定"正式版要不要真的搬数据"的地方，改它之前先读完下面几段。</b>
        /// 它只喂给 <see cref="CreateProductionEnvironment"/>；测试通过自己构造
        /// <see cref="DataMigrationEnvironment"/> 把开关打开，不会也不能改到这个值。
        /// </para>
        ///
        /// <para>
        /// <b>代码侧的前置条件已全部满足（核实于 2026-07-28），此开关之所以还是 false，
        /// 只差真机验证。</b>此前这里写着"<c>{安装目录}\Data</c> 的 12 个读取方中
        /// <c>ProfileService</c> / <c>PackageRepository</c> / <c>OverwriteStore</c> /
        /// <c>DeploymentService</c> 尚未切到 <see cref="AppPaths"/>"——那四个已在
        /// <c>c6523fc</c> 与 <c>7c58a43</c> 里切完，注释是旧的，别再按它判断。
        /// </para>
        ///
        /// <para>
        /// 已满足的部分（可用 <c>grep -rn "BaseDirectory" --include=*.cs</c> 自行复核，
        /// 运行期写安装目录的调用点已归零）：
        /// <list type="number">
        /// <item>全部 12 个 <c>Data</c> 读取方走 <see cref="AppPaths"/>；</item>
        /// <item>两个 MOD 备份根合一，主窗口与两个路径选择器都取
        /// <see cref="AppPaths.ModBackupsDirectory"/>；</item>
        /// <item>日志、XAML 错误日志改写 <see cref="AppPaths.LogsDirectory"/>
        /// （新目录建不出来时才退回安装目录）；</item>
        /// <item>搬完后的绝对路径改写已就位（<see cref="RewriteConfigPaths"/>）；</item>
        /// <item>诊断包同时采集新旧两处，搬迁失败时仍有证据；</item>
        /// <item>安装脚本不再删 <c>{app}\Data\Backups</c>，清理脚本与 INFO_AFTER 指向新位置；</item>
        /// <item>保存类操作的失败语义统一为 log + throw，不再有静默丢数据的通道。</item>
        /// </list>
        /// 唯一还在写安装目录的是头像（<c>Views/AccountSettingsWindow.xaml.cs</c>），
        /// 它<b>不在本迁移器的探测项里</b>，原因见
        /// <see cref="AppPaths.Legacy.AvatarsDirectory"/> 的注释——与本开关无关。
        /// </para>
        ///
        /// <para>
        /// <b>新旧两处都有数据时怎么合，已经定了（2026-07-28）</b>，不再是翻开开关的阻塞项。
        /// 策略是<b>新位置赢、旧位置原样保留不删</b>，落在
        /// <see cref="RelocationAction.AdoptTargetKeepLegacy"/>：
        /// 目标有实质内容、无墓碑、且<b>无搬迁进行中标记</b>时，判定那是应用一直在读写的真实数据，
        /// 于是不复制、不删源，只在旧位置写一张"已跳过"标记（<c>superseded-by.txt</c>）。
        /// 这条路径算<b>正常完成</b>，不会再让老用户每次启动都收到"迁移未完成"。
        /// <see cref="RelocationAction.PurgeTargetThenCopy"/> 则收窄成"目标带进行中标记"这一种情形，
        /// 即真正的"上次中断的半份数据"，两者判据不再重叠。
        /// </para>
        ///
        /// <para>
        /// <b>翻开之前还差的事：</b>
        /// <list type="number">
        /// <item><b>D0–D5 测试矩阵尚未在真机跑过。</b>见迁移方案 §七。文件系统层面的部分
        /// 已在 <c>DataLocationMigratorTests</c> 里自动化（D0/D1/D2/D5、新旧冲突合并、幂等、
        /// 两项备份解耦、失败时不改配置），真机只剩 GUI 相关的几项：应用能启动到主界面、
        /// 界面显示正确、D3（安装目录不可写）、D4（目标盘空间不足）。</item>
        /// <item><b>没有磁盘空间预检。</b>方案 §三② 要求空间不足时降级为原地登记，
        /// 目前没实现。几十 GB 的仓库/生成物已经是 <c>RegisterInPlace</c>、根本不复制，
        /// 所以风险比方案设想的小得多；但备份两项是游戏文件的副本，体积仍可观，
        /// 目标盘满时会走到"该项失败"分支——数据完整留在旧位置不会丢，
        /// 只是每次启动重试一遍。可接受，但真机 D4 要确认它确实只是失败而不是留下半份。</item>
        /// <item><b>失败提示尚未接到界面。</b>本类已把状态做成 UI 可消费的形式
        /// （<see cref="LastOutcome"/> + <see cref="DataMigrationOutcome.ShouldNotifyUser"/>），
        /// 但还没有任何 UI 读它。开关翻开后失败对用户仍是静默的，接线要一并完成。</item>
        /// </list>
        /// </para>
        ///
        /// <para>
        /// 开关关着时本类只做两件事：把计划完整算出来<b>写进日志</b>（可在真实用户机器上
        /// 验证探测与判定是否符合预期），以及<b>执行原地登记</b>——后者只写配置值，
        /// 要么与当前行为完全等价，要么写的是还没有读取方的新键，零风险。
        /// 配置路径改写（<see cref="RewriteConfigPaths"/>）以墓碑为判据，
        /// 本开关关着时不会有任何墓碑，因而同样是彻底的空操作。
        /// </para>
        /// </summary>
        private const bool ProductionRelocationExecutionEnabled = false;

        private const string RepositoryItemName = "包仓库";
        private const string OverwritesItemName = "生成物存储";
        private const string DataIndexItemName = "数据索引";
        private const string DeploymentBackupsItemName = "部署事务备份";

        private readonly ILogger<DataLocationMigrator> _logger;
        private readonly DataRelocationExecutor _executor;

        /// <summary>
        /// 注入的运行环境；<c>null</c> 表示走生产环境。
        ///
        /// <para>
        /// 生产环境刻意<b>不在构造时求值</b>，而是每次 <see cref="Run"/> 现算一次：
        /// <see cref="AppPaths"/> 本身就不缓存布局（用户改了仓库位置要立即生效），
        /// 在构造函数里把路径拍死会悄悄改掉这个语义。
        /// </para>
        /// </summary>
        private readonly DataMigrationEnvironment? _environment;

        public DataLocationMigrator(ILogger<DataLocationMigrator> logger)
            : this(logger, environment: null, executor: null)
        {
        }

        /// <summary>
        /// 测试用构造：把路径、偏好与搬移开关整体换成可控的实现。
        ///
        /// <para>
        /// 刻意标 <c>internal</c>（配合 <c>AssemblyInfo.cs</c> 里的
        /// <c>InternalsVisibleTo("UEModManager.Tests")</c>）。DI 只看得到上面那个公开构造，
        /// 也就不存在"某处顺手 new 一个开着搬移开关的迁移器"这种事——生产行为仍然只由
        /// <see cref="ProductionRelocationExecutionEnabled"/> 一个常量决定。
        /// </para>
        /// </summary>
        /// <param name="executor">
        /// 传入自定义执行器是为了在四步搬移的<b>指定阶段</b>注入失败
        /// （复制中 / 校验后写墓碑前 / 删源中），验证中断后重启能自愈。
        /// 这三种中断没法靠真实文件操作稳定复现。
        /// </param>
        internal DataLocationMigrator(
            ILogger<DataLocationMigrator> logger,
            DataMigrationEnvironment? environment,
            DataRelocationExecutor? executor = null)
        {
            _logger = logger;
            _environment = environment;
            _executor = executor ?? new DataRelocationExecutor(logger);
        }

        /// <summary>
        /// 生产环境：把 <see cref="AppPaths"/> 的真实路径与 <see cref="UiPreferences"/>
        /// 的真实偏好绑上，搬移开关取
        /// <see cref="ProductionRelocationExecutionEnabled"/>。
        /// <b>全类只有这一个方法碰静态全局状态。</b>
        /// </summary>
        private static DataMigrationEnvironment CreateProductionEnvironment() => new(
            new DataMigrationPaths(
                LegacyConfigFile: AppPaths.Legacy.ConfigFile,
                ConfigFile: AppPaths.ConfigFile,
                LegacyDeploymentBackupsDirectory: AppPaths.Legacy.DeploymentBackupsDirectory,
                DeploymentBackupsDirectory: AppPaths.DeploymentBackupsDirectory,
                LegacyDataDirectory: AppPaths.Legacy.DataDirectory,
                DataDirectory: AppPaths.DataDirectory,
                LegacyModBackupsDirectory: AppPaths.Legacy.ModBackupsDirectory,
                ModBackupsDirectory: AppPaths.ModBackupsDirectory,
                LegacyRepositoryRoot: AppPaths.Legacy.RepositoryRoot,
                LegacyOverwritesRoot: AppPaths.Legacy.OverwritesRoot),
            UiPreferencesDataMigrationAdapter.Instance,
            ProductionRelocationExecutionEnabled);

        /// <summary>
        /// 最近一次 <see cref="RunAsync"/> 的结果；从未跑过时为 <c>null</c>。
        ///
        /// <para>
        /// 存在的理由只有一个：<b>让"迁移没做完"这件事对 UI 可见</b>。此前迁移结果只进
        /// <c>Console.WriteLine</c>，失败对用户是彻底静默的——数据分处新旧两地、每次启动
        /// 都在重试，用户界面上什么都看不出来，直到某天他去翻日志。这正是
        /// <c>355589a</c> 那轮修掉的"UI 静默失败通道"的同类。
        /// </para>
        ///
        /// <para>
        /// 本类是 DI 单例，迁移在主窗口创建之前就跑完了（见 <c>App.ShowAuthenticationWindow</c>），
        /// 因此 UI 侧任何时候读到的都是最终值，不存在竞态。用实例属性而不是静态字段，
        /// 是为了让它跟着容器的生命周期走，测试里也能各测各的。
        /// </para>
        ///
        /// <para>
        /// 消费方式：取 <see cref="DataMigrationOutcome.ShouldNotifyUser"/> 决定要不要提示，
        /// 取 <see cref="DataMigrationOutcome.UserMessage"/> 拿文案。<b>不要</b>用
        /// <see cref="DataMigrationOutcome.Completed"/> 当判据，理由见该记录的注释。
        /// </para>
        /// </summary>
        public DataMigrationOutcome? LastOutcome { get; private set; }

        /// <summary>执行一次迁移。绝不抛异常——失败时应用沿用旧位置继续运行。</summary>
        public Task<DataMigrationOutcome> RunAsync()
        {
            DataMigrationOutcome outcome;
            try
            {
                outcome = Run();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DataMigration] 迁移过程异常，沿用旧数据位置继续运行");
                outcome = new DataMigrationOutcome(false, 0, 0, 0, 1, "迁移异常，已沿用旧位置");
            }

            LastOutcome = outcome;
            return Task.FromResult(outcome);
        }

        private DataMigrationOutcome Run()
        {
            // 生产环境每次现算：AppPaths 不缓存布局，用户改过的仓库位置要立即生效。
            var env = _environment ?? CreateProductionEnvironment();

            var migratedVersion = env.Preferences.LoadDataLayoutVersion();
            var plan = DataRelocationPlanner.Plan(migratedVersion, BuildProbes(env));

            int executed = 0, skipped = 0, deferred = 0, failed = 0;

            foreach (var step in plan.Steps)
            {
                if (step.Action == RelocationAction.None)
                {
                    skipped++;
                    _logger.LogInformation("[DataMigration] 跳过 {Name}：{Reason}", step.Name, step.Reason);
                    continue;
                }

                if (step.Action != RelocationAction.RegisterInPlace && !env.RelocationExecutionEnabled)
                {
                    deferred++;
                    _logger.LogInformation(
                        "[DataMigration] 计划（本阶段不执行）{Name}: {Action} — {Reason}｜{From} → {To}",
                        step.Name, step.Action, step.Reason, step.LegacyPath, step.TargetPath);
                    continue;
                }

                if (TryExecute(env, step)) executed++;
                else failed++;
            }

            // 目录搬完了，config.json 里指向旧位置的绝对路径必须跟着改。
            // 放在整个循环之后是必须的：config.json 自己就是"主配置"那一项，
            // 得等它落到新位置，才能读新位置的那份来改写。
            if (!RewriteConfigPaths(env)) failed++;

            // 只有既无失败、也无推迟项时，本布局版本才算彻底完成。
            // 推迟项在开关打开后仍要处理，提前打版本标记会让它们被永久跳过。
            var completed = failed == 0 && deferred == 0;
            if (completed && plan.ShouldStampVersion)
            {
                env.Preferences.SaveDataLayoutVersion(plan.ToVersion);
                _logger.LogInformation("[DataMigration] 数据布局版本标记为 v{Version}", plan.ToVersion);
            }

            var summary = $"执行 {executed}，跳过 {skipped}，推迟 {deferred}，失败 {failed}";
            var outcome = new DataMigrationOutcome(completed, executed, skipped, deferred, failed, summary);

            // 状态一并进日志：排障时最先要区分的就是"推迟"（开关没开，正常）
            // 与"失败"（真出事了），只看四个计数每次都得重新推一遍。
            if (outcome.ShouldNotifyUser)
            {
                _logger.LogWarning("[DataMigration] {Status}｜{Summary}", outcome.Status, summary);
            }
            else
            {
                _logger.LogInformation("[DataMigration] {Status}｜{Summary}", outcome.Status, summary);
            }

            return outcome;
        }

        // ─── 探测 ───

        private static IReadOnlyList<RelocationProbe> BuildProbes(DataMigrationEnvironment env)
        {
            var paths = env.Paths;
            return new[]
            {
                // 搬移类：体积恒定在 MB 级
                FileProbe("主配置", paths.LegacyConfigFile, paths.ConfigFile),

                // 部署事务备份必须早于"数据索引"：它物理上是 {安装目录}\Data\Backups，
                // 即数据索引那一项的子目录，但目标位置完全不同
                // （{LOCALAPPDATA}\Backups\Deployments，而非 {LOCALAPPDATA}\Data\Backups）。
                // 顺序只是让日志更好读——真正保证两项不打架的是数据索引那一步的排除清单。
                DirectoryProbe(DeploymentBackupsItemName,
                    paths.LegacyDeploymentBackupsDirectory, paths.DeploymentBackupsDirectory),
                DirectoryProbe(DataIndexItemName, paths.LegacyDataDirectory, paths.DataDirectory),

                DirectoryProbe("MOD 备份", paths.LegacyModBackupsDirectory, paths.ModBackupsDirectory),

                // 原地登记类：可能几十 GB，且已不在安装目录，卸载不会丢
                RegisterInPlaceProbe(RepositoryItemName, paths.LegacyRepositoryRoot,
                    env.Preferences.LoadRepositoryRoot()),
                RegisterInPlaceProbe(OverwritesItemName, paths.LegacyOverwritesRoot,
                    env.Preferences.LoadOverwritesRoot()),
            };
        }

        /// <summary>
        /// "数据索引"搬移时必须原样留下的子目录。
        ///
        /// <para>
        /// <c>Data\Backups</c> 是上面那个独立探测项的地盘。不排除的话，数据索引这一步
        /// 会把它复制到 <c>{LOCALAPPDATA}\Data\Backups</c> 再删源——而 DeploymentService
        /// 读的是 <c>{LOCALAPPDATA}\Backups\Deployments</c>，部署备份会当场消失，
        /// 崩溃回滚随之失效。排除后两项彻底解耦：谁先执行都一样，一项失败也不波及另一项。
        /// </para>
        /// </summary>
        private static IReadOnlyCollection<string> ExcludedChildrenOf(
            DataMigrationEnvironment env, string itemName)
            => itemName == DataIndexItemName
                ? new[] { Path.GetFileName(env.Paths.LegacyDeploymentBackupsDirectory) }
                : Array.Empty<string>();

        private static RelocationProbe FileProbe(string name, string legacy, string target)
            => new(name, RelocationKind.Relocate, legacy, target,
                LegacyExists: DataRelocationExecutor.FileExists(legacy),
                TargetExists: DataRelocationExecutor.FileExists(target),
                TombstoneExists: DataRelocationExecutor.FileExists(
                    DataRelocationExecutor.GetTombstonePath(legacy, isFile: true)),
                SupersededMarkerExists: DataRelocationExecutor.FileExists(
                    DataRelocationExecutor.GetSupersededMarkerPath(legacy, isFile: true)),
                TargetMigrationInProgress: DataRelocationExecutor.FileExists(
                    DataRelocationExecutor.GetInProgressMarkerPath(target, isFile: true)));

        private static RelocationProbe DirectoryProbe(string name, string legacy, string target)
            => new(name, RelocationKind.Relocate, legacy, target,
                LegacyExists: DataRelocationExecutor.DirectoryHasContent(legacy),
                TargetExists: DataRelocationExecutor.DirectoryHasContent(target),
                TombstoneExists: DataRelocationExecutor.FileExists(
                    DataRelocationExecutor.GetTombstonePath(legacy, isFile: false)),
                SupersededMarkerExists: DataRelocationExecutor.FileExists(
                    DataRelocationExecutor.GetSupersededMarkerPath(legacy, isFile: false)),
                TargetMigrationInProgress: DataRelocationExecutor.FileExists(
                    DataRelocationExecutor.GetInProgressMarkerPath(target, isFile: false)));

        private static RelocationProbe RegisterInPlaceProbe(string name, string legacy, string? userOverride)
            => new(name, RelocationKind.RegisterInPlace, legacy, legacy,
                LegacyExists: DataRelocationExecutor.DirectoryHasContent(legacy),
                TargetExists: false,
                TombstoneExists: false,
                IsUserOverridden: !string.IsNullOrWhiteSpace(userOverride));

        // ─── 执行 ───

        private bool TryExecute(DataMigrationEnvironment env, RelocationStep step)
        {
            try
            {
                if (step.Action == RelocationAction.RegisterInPlace)
                {
                    RegisterInPlace(env, step);
                    return true;
                }

                _executor.Execute(step, IsFileItem(step), ExcludedChildrenOf(env, step.Name));
                return true;
            }
            catch (Exception ex)
            {
                // 单项失败不影响其它项；该项数据仍完整留在旧位置
                _logger.LogError(ex, "[DataMigration] {Name} 迁移失败，数据保留在旧位置 {Path}",
                    step.Name, step.LegacyPath);
                return false;
            }
        }

        private void RegisterInPlace(DataMigrationEnvironment env, RelocationStep step)
        {
            switch (step.Name)
            {
                case RepositoryItemName:
                    env.Preferences.SaveRepositoryRoot(step.LegacyPath);
                    break;
                case OverwritesItemName:
                    env.Preferences.SaveOverwritesRoot(step.LegacyPath);
                    break;
                default:
                    _logger.LogWarning("[DataMigration] 未知的原地登记项：{Name}", step.Name);
                    return;
            }

            _logger.LogInformation("[DataMigration] {Name} 原地登记：{Path}", step.Name, step.LegacyPath);
        }

        private static bool IsFileItem(RelocationStep step)
            => Path.HasExtension(step.LegacyPath) && !Directory.Exists(step.LegacyPath);

        // ─── 配置里的绝对路径改写 ───

        /// <summary>
        /// 把 <c>config.json</c> 里指向旧位置的绝对路径改写到新位置。
        ///
        /// <para>
        /// 搬迁器只搬目录，不改配置——而 <c>BackupPath</c> 与 <c>GameIcons</c> 存的是绝对路径。
        /// 不改的话，老用户升级后备份继续写进已被清空、只剩墓碑的旧目录，
        /// 全部自定义游戏图标当场失效。
        /// </para>
        ///
        /// <para>
        /// <b>判据是墓碑，不是"本次执行成功"。</b>墓碑（<c>migrated-to.txt</c>）代表
        /// "复制与校验都已完成"（见 <see cref="DataRelocationPlanner"/>），
        /// 这正是"数据确实已经在新位置"的唯一证据：
        /// 本次搬成的、上次搬完只差删源的、上次搬完这次直接跳过的，三种情况一视同仁；
        /// 而搬迁失败、或 <see cref="DataMigrationEnvironment.RelocationExecutionEnabled"/>
        /// 仍为 false 根本没搬时，墓碑不存在，对应的路径一个字都不会动。
        /// </para>
        ///
        /// <para>
        /// <b>"已跳过"标记不是墓碑，这里也绝不能认它。</b>
        /// <see cref="RelocationAction.AdoptTargetKeepLegacy"/>（新位置赢）留下的是
        /// <c>superseded-by.txt</c>，它的含义恰恰是"数据<b>没有</b>搬过去"。若把它也当成改写依据：
        /// <list type="bullet">
        /// <item><c>GameIcons</c> 会被平移到新位置下的同名文件，而那些 png <b>还留在安装目录里</b>，
        /// 新位置压根没有 —— 用户的全部自定义游戏图标当场失效，且是那种"界面上图标空了、
        /// 日志里什么都没有"的静默故障；</item>
        /// <item><c>BackupPath</c> 会指向一个新位置下不存在的备份目录，用户此前的备份从界面上消失
        /// （文件其实还在旧位置）。</item>
        /// </list>
        /// 跳过时正确的做法是<b>一个字都不改</b>：数据仍在旧位置且完好，旧的绝对路径依然有效。
        /// 这也是"两种记号用两个文件名"的直接收益 —— 本方法的判据一行都不用改，
        /// 跳过项自然落不进 <c>migrated-to.txt</c> 的查询里，
        /// 不存在"忘了在这里加一个 if"这种失误空间。
        /// </para>
        ///
        /// <para>
        /// 返回 false 表示改写失败。失败只计入 failed（从而不打版本标记，下次启动重试），
        /// 绝不抛出：配置没改好顶多是备份路径不对，启动不了则彻底不可用。
        /// </para>
        /// </summary>
        private bool RewriteConfigPaths(DataMigrationEnvironment env)
        {
            try
            {
                var modBackupsRule = RuleIfRelocated(
                    env.Paths.LegacyModBackupsDirectory, env.Paths.ModBackupsDirectory);
                var dataRule = RuleIfRelocated(
                    env.Paths.LegacyDataDirectory, env.Paths.DataDirectory);

                if (modBackupsRule is null && dataRule is null) return true;

                var configFile = ResolveConfigFileToRewrite(env);
                if (configFile is null)
                {
                    _logger.LogInformation("[DataMigration] 未找到 config.json，无需改写配置中的路径");
                    return true;
                }

                var result = AppConfigPathRewriter.Rewrite(
                    File.ReadAllText(configFile), modBackupsRule, dataRule);

                // 无改动就不写盘：重复启动时 config.json 的修改时间都不该被扰动
                if (!result.Changed) return true;

                AtomicFileWriter.WriteAllText(configFile, result.Json);
                foreach (var entry in result.ChangedEntries)
                {
                    _logger.LogInformation("[DataMigration] 配置路径已改写 {Entry}", entry);
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DataMigration] 改写配置中的绝对路径失败，配置暂时仍指向旧位置");
                return false;
            }
        }

        /// <summary>
        /// 该项已留下<b>搬移</b>墓碑（数据确已在新位置）时给出改写规则，否则返回 null 表示"不许改"。
        /// 只查 <c>migrated-to.txt</c>：<c>superseded-by.txt</c>（新位置赢、旧数据原样留着）
        /// 表示数据<b>没有</b>搬过去，理由见 <see cref="RewriteConfigPaths"/> 的注释。
        /// </summary>
        private static PathRebaseRule? RuleIfRelocated(string legacyRoot, string targetRoot)
            => DataRelocationExecutor.FileExists(
                    DataRelocationExecutor.GetTombstonePath(legacyRoot, isFile: false))
                ? new PathRebaseRule(legacyRoot, targetRoot)
                : null;

        /// <summary>
        /// 定位要改写的 config.json：优先新位置——那是应用真正会去读的一份。
        /// 主配置那一项自己搬失败时它还在旧位置，就改旧的那份，等它搬过来时新值会一并带过去。
        /// </summary>
        private static string? ResolveConfigFileToRewrite(DataMigrationEnvironment env)
        {
            if (DataRelocationExecutor.FileExists(env.Paths.ConfigFile)) return env.Paths.ConfigFile;
            return DataRelocationExecutor.FileExists(env.Paths.LegacyConfigFile)
                ? env.Paths.LegacyConfigFile
                : null;
        }
    }

    /// <summary>
    /// <see cref="IDataMigrationPreferences"/> 的生产实现，转调静态的 <see cref="UiPreferences"/>。
    ///
    /// <para>
    /// 只是一层直通转发，没有任何自己的状态——因此用单例，避免每次迁移都 new 一个。
    /// 真正的隔离发生在接口那一侧：测试注入自己的实现，压根不会走到这里。
    /// </para>
    /// </summary>
    internal sealed class UiPreferencesDataMigrationAdapter : IDataMigrationPreferences
    {
        internal static readonly UiPreferencesDataMigrationAdapter Instance = new();

        private UiPreferencesDataMigrationAdapter() { }

        public int LoadDataLayoutVersion() => UiPreferences.LoadDataLayoutVersion();

        public void SaveDataLayoutVersion(int version) => UiPreferences.SaveDataLayoutVersion(version);

        public string? LoadRepositoryRoot() => UiPreferences.LoadRepositoryRoot();

        public void SaveRepositoryRoot(string? path) => UiPreferences.SaveRepositoryRoot(path);

        public string? LoadOverwritesRoot() => UiPreferences.LoadOverwritesRoot();

        public void SaveOverwritesRoot(string? path) => UiPreferences.SaveOverwritesRoot(path);
    }
}
