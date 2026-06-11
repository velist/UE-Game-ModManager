using UEModManager.Models;

namespace UEModManager.Infrastructure;

public static class DisplayNameMapper
{
    public static string DeploymentStatus(DeploymentStatus status) => status switch
    {
        UEModManager.Models.DeploymentStatus.Pending => "待处理",
        UEModManager.Models.DeploymentStatus.InProgress => "未完成",
        UEModManager.Models.DeploymentStatus.Committed => "已完成",
        UEModManager.Models.DeploymentStatus.RolledBack => "已回滚",
        UEModManager.Models.DeploymentStatus.Failed => "失败",
        UEModManager.Models.DeploymentStatus.PartiallyRolledBack => "部分回滚",
        UEModManager.Models.DeploymentStatus.LogPersistenceFailed => "日志保存失败",
        UEModManager.Models.DeploymentStatus.Dismissed => "已忽略",
        _ => status.ToString()
    };

    public static string DeploymentBackend(DeploymentBackendType backend) => backend switch
    {
        DeploymentBackendType.Copy => "文件复制",
        DeploymentBackendType.HardLink => "硬链接",
        DeploymentBackendType.Symlink => "符号链接",
        _ => backend.ToString()
    };
}