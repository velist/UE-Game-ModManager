using System;
using System.Collections.Generic;
using UEModManager.Models;

namespace UEModManager.Services
{
    /// <summary>
    /// Profile 服务的"只读契约"。
    /// Domain 服务通过此接口访问当前活跃方案和方案列表，不允许越权创建/删除/切换。
    /// </summary>
    public interface IProfileQuery
    {
        /// <summary>当前活跃方案（可空 — 启动初期或无方案时）。</summary>
        InstanceProfile? CurrentProfile { get; }

        /// <summary>当前游戏的所有方案。</summary>
        IReadOnlyList<InstanceProfile> GetProfiles();

        /// <summary>按 ID 查找方案。</summary>
        InstanceProfile? FindProfile(Guid profileId);
    }
}
