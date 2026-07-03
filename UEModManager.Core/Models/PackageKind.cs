namespace UEModManager.Models
{
    /// <summary>
    /// 包类型枚举。
    /// 对应 UI 中的三色标：MOD(青) / Plugin(紫) / Config(橙)。
    /// </summary>
    public enum PackageKind
    {
        /// <summary>MOD 文件（.pak/.ucas/.utoc 等）。</summary>
        Mod,

        /// <summary>插件/DLL。</summary>
        Plugin,

        /// <summary>配置文件（.ini/.json/.cfg 等）。</summary>
        Config
    }

    /// <summary>
    /// 部署后端类型。
    /// </summary>
    public enum DeploymentBackendType
    {
        /// <summary>文件复制（默认，最安全）。</summary>
        Copy,

        /// <summary>硬链接（节省空间，同卷限制）。</summary>
        HardLink,

        /// <summary>
        /// 符号链接。已于 v2.0.5 下线（普通用户需开发者模式/管理员权限，实际不可用）。
        /// 枚举值保留用于反序列化旧事务/配置，运行时自动降级为 Copy。
        /// </summary>
        Symlink
    }

    /// <summary>
    /// 部署操作类型。
    /// </summary>
    public enum DeploymentOperationType
    {
        Add,
        Remove,
        Replace
    }

    /// <summary>
    /// 部署事务状态。
    /// </summary>
    public enum DeploymentStatus
    {
        Pending,
        InProgress,
        Committed,
        RolledBack,
        Failed,

        /// <summary>
        /// 回滚执行了，但中途至少一个操作失败 → 文件状态可能不一致。
        /// 由 CrashRecoveryScanner 强制再次扫描，等待用户决策。
        /// </summary>
        PartiallyRolledBack,

        /// <summary>
        /// 事务执行完毕，但写入 transaction.json 失败（磁盘满 / 权限）。
        /// 内存状态已是 Committed，但持久化可能丢失，需要用户确认。
        /// </summary>
        LogPersistenceFailed,

        /// <summary>
        /// 用户曾在崩溃恢复对话框中"忽略此事务"。
        /// 由 CrashRecoveryScanner 跳过；可通过管理操作重置。
        /// </summary>
        Dismissed
    }

    /// <summary>
    /// 冲突类型。
    /// </summary>
    public enum ConflictType
    {
        /// <summary>文件路径冲突（多个包提供同一路径）。</summary>
        Path,

        /// <summary>配置键冲突。</summary>
        ConfigKey,

        /// <summary>加载顺序冲突。</summary>
        LoadOrder,

        /// <summary>依赖缺失。</summary>
        DependencyMissing,

        /// <summary>目录遮蔽。</summary>
        DirectoryShadow
    }

    /// <summary>生成物类型。</summary>
    public enum GeneratedArtifactType
    {
        /// <summary>部署快照 — 部署事务执行后的状态记录。</summary>
        DeploymentSnapshot,
        /// <summary>合并配置 — 多个配置文件合并后的产物（Phase 7 扩展）。</summary>
        MergedConfig,
        /// <summary>工具输出 — 外部工具或脚本生成的文件。</summary>
        ToolOutput,
        /// <summary>缓存 — 运行时缓存文件（如缩略图缓存）。</summary>
        Cache,
        /// <summary>用户修复 — 用户手动放入的临时修复文件。</summary>
        UserFix,
        /// <summary>其他 — 未分类的生成物。</summary>
        Other
    }

    /// <summary>生成物状态。</summary>
    public enum GeneratedArtifactStatus
    {
        /// <summary>活跃 — 当前部署中生效。</summary>
        Active,
        /// <summary>过期 — 已被更新的部署覆盖。</summary>
        Stale,
        /// <summary>已晋升 — 已转为正式 Package。</summary>
        Promoted
    }
}
