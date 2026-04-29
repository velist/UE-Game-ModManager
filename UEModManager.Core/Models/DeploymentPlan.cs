using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace UEModManager.Models
{
    /// <summary>
    /// 部署计划。
    /// 由 DeploymentPlanner 生成，描述从当前状态到目标状态的全部文件操作。
    /// 计划一旦生成即不可变，通过 DeploymentService 执行。
    /// </summary>
    public class DeploymentPlan
    {
        /// <summary>唯一标识。</summary>
        public Guid Id { get; init; } = Guid.NewGuid();

        /// <summary>关联的 Profile ID。</summary>
        public Guid ProfileId { get; init; }

        /// <summary>关联的游戏名称。</summary>
        public string HostGameName { get; init; } = default!;

        /// <summary>操作列表。</summary>
        public List<DeploymentOperation> Operations { get; init; } = [];

        /// <summary>生成时间。</summary>
        public DateTime CreatedAt { get; init; } = DateTime.Now;

        /// <summary>部署后端类型。</summary>
        public DeploymentBackendType BackendType { get; init; }

        // ─── 计算属性（UI 用） ───

        /// <summary>新增操作数。</summary>
        [JsonIgnore]
        public int AddCount => Operations.Count(o => o.Type == DeploymentOperationType.Add);

        /// <summary>删除操作数。</summary>
        [JsonIgnore]
        public int RemoveCount => Operations.Count(o => o.Type == DeploymentOperationType.Remove);

        /// <summary>替换操作数。</summary>
        [JsonIgnore]
        public int ReplaceCount => Operations.Count(o => o.Type == DeploymentOperationType.Replace);

        /// <summary>总操作数。</summary>
        [JsonIgnore]
        public int TotalCount => Operations.Count;

        /// <summary>是否有变更。</summary>
        [JsonIgnore]
        public bool HasChanges => Operations.Count > 0;

        /// <summary>涉及的包列表（去重）。</summary>
        [JsonIgnore]
        public List<string> AffectedPackages =>
            Operations.Select(o => o.PackageKey).Distinct().ToList();

        /// <summary>总文件大小（新增 + 替换的源文件大小）。</summary>
        [JsonIgnore]
        public long TotalSize => Operations
            .Where(o => o.Type != DeploymentOperationType.Remove)
            .Sum(o => o.FileSize);
    }
}
