namespace UEModManager.Services.Migration
{
    /// <summary>
    /// v1.8 → v2.0 数据迁移的 5 个步骤标识。
    /// 数字与 <see cref="MigrationProgress.CurrentStep"/> 一致，方便 UI 直接绑定。
    /// </summary>
    public enum MigrationStep
    {
        /// <summary>读取 {game}_mods.json。</summary>
        ScanOldData = 1,

        /// <summary>初始化对象仓库 / 默认方案由 ProfileService 处理。</summary>
        PrepareRepository = 2,

        /// <summary>遍历旧 MOD 写入 ObjectStore + PackageRepository。</summary>
        MigrateToRepository = 3,

        /// <summary>生成 manifest（已在 RegisterPackageAsync 内完成，此处仅作步骤标记）。</summary>
        GenerateManifest = 4,

        /// <summary>调用 PackageRepository.CheckIntegrityAsync。</summary>
        VerifyIntegrity = 5,
    }
}
