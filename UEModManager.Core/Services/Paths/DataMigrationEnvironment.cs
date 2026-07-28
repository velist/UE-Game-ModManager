using System;

namespace UEModManager.Services.Paths;

/// <summary>
/// 迁移器要读写的那一小部分用户偏好。
///
/// <para>
/// 抽出接口的唯一目的是<b>把静态全局状态挡在迁移器外面</b>。生产实现转调
/// <c>UiPreferences</c>——它是静态类、有进程级内存单例、写的是真实的
/// <c>%APPDATA%\UEModManager\ui_config.json</c>。测试若直接驱动它，
/// 一是会污染开发者本机的真实配置（本项目已经踩过一次：<c>OverwriteStore</c>
/// 的测试原先靠"测试宿主进程目录"的隐式隔离，路径归口之后就开始写真实
/// <c>%LOCALAPPDATA%</c>），二是内存单例让用例之间互相串味、并行跑必然打架。
/// </para>
///
/// <para>
/// 只列迁移器实际用到的六个方法，不做成"UiPreferences 的完整镜像"：
/// 接口越窄，测试替身越不容易和真实实现产生语义偏差。
/// </para>
/// </summary>
public interface IDataMigrationPreferences
{
    /// <summary>读取已迁移到的数据目录布局版本；从未迁移过返回 0。</summary>
    int LoadDataLayoutVersion();

    /// <summary>写入已迁移到的数据目录布局版本。</summary>
    void SaveDataLayoutVersion(int version);

    /// <summary>读取用户自定义的包仓库位置；未自定义返回 <c>null</c>。</summary>
    string? LoadRepositoryRoot();

    /// <summary>登记包仓库位置。</summary>
    void SaveRepositoryRoot(string? path);

    /// <summary>读取用户自定义的生成物存储位置；未自定义返回 <c>null</c>。</summary>
    string? LoadOverwritesRoot();

    /// <summary>登记生成物存储位置。</summary>
    void SaveOverwritesRoot(string? path);
}

/// <summary>
/// 迁移器要用到的全部路径，新旧成对。
///
/// <para>
/// 存在的理由与上面的偏好接口相同：主项目的 <c>AppPaths</c> 是静态类，
/// 属性直接指向真实的 <c>%LOCALAPPDATA%</c> / <c>%APPDATA%</c> 与
/// <c>AppDomain.CurrentDomain.BaseDirectory</c>。迁移器会<b>复制并删除</b>这些位置下的
/// 文件——让它在测试里对着开发者的真实数据目录跑，第一次跑错就没有后悔药。
/// 把路径收成一个不可变的入参，测试就能整体指到临时目录上。
/// </para>
///
/// <para>
/// 字段按"旧位置, 新位置"成对排列，顺序与
/// <c>DataLocationMigrator.BuildProbes()</c> 的探测顺序一致，便于对读。
/// 仓库与生成物只有旧位置——它们是原地登记项，新位置就是旧位置本身。
/// </para>
/// </summary>
/// <param name="LegacyConfigFile">旧的主配置文件。</param>
/// <param name="ConfigFile">新的主配置文件。</param>
/// <param name="LegacyDeploymentBackupsDirectory">旧的部署事务备份目录（物理上是旧数据索引的子目录）。</param>
/// <param name="DeploymentBackupsDirectory">新的部署事务备份目录。</param>
/// <param name="LegacyDataDirectory">旧的 JSON 索引目录。</param>
/// <param name="DataDirectory">新的 JSON 索引目录。</param>
/// <param name="LegacyModBackupsDirectory">旧的 MOD 备份目录。</param>
/// <param name="ModBackupsDirectory">新的 MOD 备份目录。</param>
/// <param name="LegacyRepositoryRoot">包仓库的旧默认位置（原地登记，不搬移）。</param>
/// <param name="LegacyOverwritesRoot">生成物存储的旧默认位置（原地登记，不搬移）。</param>
public sealed record DataMigrationPaths(
    string LegacyConfigFile,
    string ConfigFile,
    string LegacyDeploymentBackupsDirectory,
    string DeploymentBackupsDirectory,
    string LegacyDataDirectory,
    string DataDirectory,
    string LegacyModBackupsDirectory,
    string ModBackupsDirectory,
    string LegacyRepositoryRoot,
    string LegacyOverwritesRoot);

/// <summary>
/// 迁移器的运行环境：路径、偏好读写、搬移执行开关。
///
/// <para>
/// <b>为什么是"每个实例一份的不可变值"，而不是可写的静态开关。</b>
/// 搬移开关原本是 <c>DataLocationMigrator</c> 里的 <c>private const bool</c>，
/// 后果有两条：测试打不开它，于是 Copy / PurgeTargetThenCopy / ResumeCleanup
/// 三种搬移动作的编排逻辑一行都跑不到；而编译期常量还会让
/// <c>if (!开关)</c> 之后的分支被判定为不可达，覆盖率和静态分析一起失真。
/// </para>
///
/// <para>
/// 换成 <c>public static bool</c> 一样不行——那是全局可变状态，任何一处忘了复位
/// 就污染整个进程，并行跑测试时更是必然互相打架。做成构造时传入、之后只读的值，
/// 既让测试能各测各的，也让"生产用哪个值"这件事仍然只由
/// <c>DataLocationMigrator</c> 里那一个常量决定。
/// </para>
/// </summary>
/// <param name="Paths">新旧位置。</param>
/// <param name="Preferences">偏好读写。</param>
/// <param name="RelocationExecutionEnabled">
/// 是否真的执行搬移类动作。<b>默认 false，且必须保持 false</b>——生产侧的取值由
/// <c>DataLocationMigrator</c> 内的常量给出，翻开它是一次需要真机验收背书的决定。
/// </param>
public sealed record DataMigrationEnvironment(
    DataMigrationPaths Paths,
    IDataMigrationPreferences Preferences,
    bool RelocationExecutionEnabled = false)
{
    /// <summary>路径。</summary>
    public DataMigrationPaths Paths { get; } =
        Paths ?? throw new ArgumentNullException(nameof(Paths));

    /// <summary>偏好读写。</summary>
    public IDataMigrationPreferences Preferences { get; } =
        Preferences ?? throw new ArgumentNullException(nameof(Preferences));
}
