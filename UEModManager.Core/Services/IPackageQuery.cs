using System;
using System.Collections.Generic;
using UEModManager.Models;

namespace UEModManager.Services
{
    /// <summary>
    /// 包仓库的"只读契约"。
    /// Domain 服务（ConflictResolver/未来下沉的 ResolvedViewBuilder/DeploymentPlanner 等）通过此接口访问包数据，
    /// 不允许越权修改仓库状态——写操作仍由具体 PackageRepository 提供，且应通过 Application UseCase 触发。
    /// </summary>
    public interface IPackageQuery
    {
        /// <summary>获取所有包。</summary>
        IReadOnlyList<Package> GetAllPackages();

        /// <summary>按 PackageKey 获取包（大小写不敏感）。</summary>
        Package? GetByKey(string packageKey);

        /// <summary>按 ID 获取包。</summary>
        Package? GetById(Guid id);

        /// <summary>按类型过滤。</summary>
        IReadOnlyList<Package> GetByKind(PackageKind kind);

        /// <summary>搜索包（按名称/Key/标签匹配）。</summary>
        IReadOnlyList<Package> Search(string query);

        /// <summary>包是否存在。</summary>
        bool Exists(string packageKey);

        /// <summary>包总数。</summary>
        int GetTotalCount();

        /// <summary>仓库总占用字节。</summary>
        long GetTotalSize();
    }
}
