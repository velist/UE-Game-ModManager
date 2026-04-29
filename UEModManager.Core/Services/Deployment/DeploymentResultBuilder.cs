using System;
using UEModManager.Models;

namespace UEModManager.Services.Deployment
{
    /// <summary>
    /// 部署事务结果构造（纯函数）。
    ///
    /// 把"无变更直接提交"的早期返回路径分离为可独立单测的纯映射。
    /// </summary>
    public static class DeploymentResultBuilder
    {
        /// <summary>
        /// 为"无变更"的部署计划构造一个直接提交的空事务记录。
        /// 用于 ExecuteAsync 的早期返回，避免创建备份目录和真实事务。
        /// </summary>
        public static DeploymentTransaction BuildEmptyCommittedTransaction(DeploymentPlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));

            return new DeploymentTransaction
            {
                PlanId = plan.Id,
                ProfileId = plan.ProfileId,
                HostGameName = plan.HostGameName,
                BackendType = plan.BackendType,
                BackupDirectory = "",
                TotalOperations = 0,
                Status = DeploymentStatus.Committed,
                CompletedAt = DateTime.Now,
            };
        }
    }
}
