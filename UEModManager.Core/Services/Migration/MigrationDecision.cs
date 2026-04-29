namespace UEModManager.Services.Migration
{
    /// <summary>
    /// v1.8 → v2.0 迁移需求判定（纯函数）。
    ///
    /// 把 <c>DataMigrationService.NeedsMigration</c> 中关于"看到这些文件状态时是否需要迁移"
    /// 的决策抽离，让 IO（File.Exists / 反序列化）留主项目，纯逻辑下沉。
    ///
    /// 决策规则：
    /// - 旧 _mods.json 不存在 → false（无源数据可迁）
    /// - 旧存在 + 新 _packages.json 不存在 → true（首次迁移）
    /// - 旧存在 + 新存在但 packages 数为 0 或反序列化失败 → true（迁移结果空，重做）
    /// - 旧存在 + 新存在且非空 → false（已迁移过）
    /// </summary>
    public static class MigrationDecision
    {
        /// <summary>
        /// 判定是否需要迁移。
        /// </summary>
        /// <param name="oldDataExists">旧 <c>{game}_mods.json</c> 是否存在。</param>
        /// <param name="newDataExists">新 <c>{game}_packages.json</c> 是否存在。</param>
        /// <param name="newPackagesCount">新数据反序列化后的包数量。null 表示反序列化失败 / 文件不可读。</param>
        public static bool NeedsMigration(bool oldDataExists, bool newDataExists, int? newPackagesCount)
        {
            if (!oldDataExists) return false;
            if (!newDataExists) return true;
            if (newPackagesCount is null or 0) return true;
            return false;
        }
    }
}
