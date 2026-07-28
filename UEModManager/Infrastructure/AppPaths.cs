using System;
using System.Collections.Generic;
using System.IO;
using UEModManager.Services;
using UEModManager.Services.Paths;

namespace UEModManager.Infrastructure
{
    /// <summary>
    /// 应用路径归口。全项目**唯一**允许拼接数据目录的地方。
    ///
    /// <para>
    /// 背景：此前 24 处代码各自用 <c>AppDomain.CurrentDomain.BaseDirectory</c> 或
    /// <c>SpecialFolder.ApplicationData</c> 拼路径，结果是同一类数据散落在三个位置
    /// （包实体在漫游目录、包索引在 exe 旁、两个互不相干的备份根），
    /// 且写在安装目录里的数据会被卸载/覆盖安装抹掉、还会被打进安装包分发。
    /// </para>
    ///
    /// <para>
    /// 分层判据与完整清单见
    /// <c>.claude/audit_reports/2026-07-27-data-location-migration-plan.md</c>。
    /// 纯计算部分在 Core 的 <see cref="AppDataLayout"/>，本类只负责把它绑定到
    /// 真实的环境目录与用户偏好。
    /// </para>
    ///
    /// <para>
    /// 本类不缓存 <see cref="AppDataLayout"/>：<see cref="UiPreferences"/> 自身已有
    /// 内存单例，每次构造布局只是几个字符串拼接，代价可忽略；换来的是"用户改了仓库
    /// 位置后立即生效"，不必再维护一套失效逻辑。调用点都在服务构造与零星文件操作上，
    /// 不在热循环里。
    /// </para>
    /// </summary>
    public static class AppPaths
    {
        private const string AppFolderName = "UEModManager";

        /// <summary>本机数据根：<c>%LOCALAPPDATA%\UEModManager</c>。</summary>
        public static string LocalRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

        /// <summary>漫游数据根：<c>%APPDATA%\UEModManager</c>。</summary>
        public static string RoamingRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);

        private static AppDataLayout Layout => new(LocalRoot, RoamingRoot, LoadOverrides());

        /// <summary>
        /// 不带用户覆盖的布局。**定位 <see cref="UiConfigFile"/> 必须走这里**：
        /// 用户覆盖本身就是从 ui_config.json 里读出来的，若定位这个文件时再去读一遍覆盖，
        /// <see cref="LoadOverrides"/> → <see cref="UiPreferences"/> → 定位配置文件
        /// 会无限递归，直接 StackOverflow 把进程带走（这类异常 catch 不住）。
        /// 三个可覆盖根之外的路径都不依赖覆盖，走这里只是少读一次配置。
        /// </summary>
        private static AppDataLayout PlainLayout => new(LocalRoot, RoamingRoot);

        private static IReadOnlyDictionary<DataRoot, string> LoadOverrides()
        {
            var overrides = new Dictionary<DataRoot, string>();
            try
            {
                Add(DataRoot.Repository, UiPreferences.LoadRepositoryRoot());
                Add(DataRoot.Overwrites, UiPreferences.LoadOverwritesRoot());
                Add(DataRoot.Backups, UiPreferences.LoadBackupsRoot());
            }
            catch (Exception ex)
            {
                // 读偏好失败不能让路径解析崩掉——回落默认位置总比启动不了好。
                LogFailure("读取自定义数据位置失败，本次使用默认位置", ex);
            }
            return overrides;

            void Add(DataRoot root, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value)) overrides[root] = value!;
            }
        }

        // ─── 本机数据 ───

        /// <summary>主配置（游戏安装路径等）。</summary>
        public static string ConfigFile => Layout.ConfigFile;

        /// <summary>各游戏的 JSON 索引目录。</summary>
        public static string DataDirectory => Layout.DataDirectory;

        /// <summary>用户自选的游戏图标。</summary>
        public static string GameIconsDirectory => Layout.GameIconsDirectory;

        /// <summary>启动会话记录。</summary>
        public static string LaunchSessionsDirectory => Layout.LaunchSessionsDirectory;

        /// <summary>日志目录。</summary>
        public static string LogsDirectory => Layout.LogsDirectory;

        /// <summary>包实体仓库（可自定义）。</summary>
        public static string RepositoryRoot => Layout.RepositoryRoot;

        /// <summary>生成物存储（可自定义）。</summary>
        public static string OverwritesRoot => Layout.OverwritesRoot;

        /// <summary>备份根（可自定义）。</summary>
        public static string BackupsRoot => Layout.BackupsRoot;

        /// <summary>部署事务备份。崩溃回滚依赖此目录。</summary>
        public static string DeploymentBackupsDirectory => Layout.DeploymentBackupsDirectory;

        /// <summary>MOD 备份。</summary>
        public static string ModBackupsDirectory => Layout.ModBackupsDirectory;

        // ─── 漫游数据 ───

        /// <summary>
        /// UI 偏好文件。<see cref="UiPreferences"/> 反过来消费本属性定位自己的配置文件，
        /// 故这里只能用 <see cref="PlainLayout"/>，不能用 <see cref="Layout"/>。
        /// </summary>
        public static string UiConfigFile => PlainLayout.UiConfigFile;

        /// <summary>用户头像目录。</summary>
        public static string AvatarsDirectory => Layout.AvatarsDirectory;

        /// <summary>用户自选背景图的副本目录。</summary>
        public static string BackgroundsDirectory => Layout.BackgroundsDirectory;

        /// <summary>DPAPI 加密的密钥文件目录（磁盘上仍叫 <c>config</c>，原因见布局类注释）。</summary>
        public static string SecretsDirectory => Layout.SecretsDirectory;

        /// <summary>本地 SQLite 库。认证体系删除时会随之调整，暂留在漫游根以免与在途改动冲突。</summary>
        public static string LocalDatabaseFile => Path.Combine(RoamingRoot, "local.db");

        // ─── 旧位置（仅供迁移器使用） ───

        /// <summary>
        /// 迁移前的旧位置。除 <c>DataLocationMigrator</c> 外不应有任何消费者——
        /// 新代码一律走上面的属性。
        /// </summary>
        public static class Legacy
        {
            /// <summary>安装目录。</summary>
            public static string InstallDirectory => AppDomain.CurrentDomain.BaseDirectory;

            /// <summary>旧的 JSON 索引目录：<c>{安装目录}\Data</c>。</summary>
            public static string DataDirectory => Path.Combine(InstallDirectory, "Data");

            /// <summary>旧的主配置：<c>{安装目录}\config.json</c>。</summary>
            public static string ConfigFile => Path.Combine(InstallDirectory, "config.json");

            /// <summary>旧的 MOD 备份根：<c>{安装目录}\Backups</c>。</summary>
            public static string ModBackupsDirectory => Path.Combine(InstallDirectory, "Backups");

            /// <summary>旧的部署事务备份：<c>{安装目录}\Data\Backups</c>。</summary>
            public static string DeploymentBackupsDirectory => Path.Combine(DataDirectory, "Backups");

            /// <summary>旧的头像目录：<c>{安装目录}\UserData\Avatars</c>。</summary>
            public static string AvatarsDirectory => Path.Combine(InstallDirectory, "UserData", "Avatars");

            /// <summary>旧的包实体仓库默认位置：<c>%APPDATA%\UEModManager\Repository</c>。</summary>
            public static string RepositoryRoot => Path.Combine(RoamingRoot, "Repository");

            /// <summary>旧的生成物存储：<c>%APPDATA%\UEModManager\Overwrites</c>。</summary>
            public static string OverwritesRoot => Path.Combine(RoamingRoot, "Overwrites");
        }

        /// <summary>
        /// 确保目录存在。失败只留痕不抛——调用点多在服务构造函数里，
        /// 让一个目录创建失败把整个应用带崩没有意义。
        /// </summary>
        public static bool TryEnsureDirectory(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path)) Directory.CreateDirectory(path);
                return true;
            }
            catch (Exception ex)
            {
                LogFailure($"创建目录失败: {path}", ex);
                return false;
            }
        }

        private static void LogFailure(string message, Exception? ex = null)
        {
            try
            {
                // Console 已被 App.SetupFileLogging 重定向到结构化日志文件
                Console.WriteLine(ex == null
                    ? $"[AppPaths] {message}"
                    : $"[AppPaths] {message}: {ex}");
            }
            catch
            {
                // 日志通道本身失败时无处可去
            }
        }
    }
}
