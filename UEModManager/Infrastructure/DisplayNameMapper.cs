using UEModManager.Localization;
using UEModManager.Models;

namespace UEModManager.Infrastructure;

public static class DisplayNameMapper
{
    public static string DeploymentStatus(DeploymentStatus status) => status switch
    {
        UEModManager.Models.DeploymentStatus.Pending => UiText.Get("待处理"),
        UEModManager.Models.DeploymentStatus.InProgress => UiText.Get("未完成"),
        UEModManager.Models.DeploymentStatus.Committed => UiText.Get("已完成"),
        UEModManager.Models.DeploymentStatus.RolledBack => UiText.Get("已回滚"),
        UEModManager.Models.DeploymentStatus.Failed => UiText.Get("失败"),
        UEModManager.Models.DeploymentStatus.PartiallyRolledBack => UiText.Get("部分回滚"),
        UEModManager.Models.DeploymentStatus.LogPersistenceFailed => UiText.Get("日志保存失败"),
        UEModManager.Models.DeploymentStatus.Dismissed => UiText.Get("已忽略"),
        _ => status.ToString()
    };

    public static string DeploymentBackend(DeploymentBackendType backend) => backend switch
    {
        DeploymentBackendType.Copy => UiText.Get("文件复制"),
        DeploymentBackendType.HardLink => UiText.Get("硬链接"),
        DeploymentBackendType.Symlink => UiText.Get("符号链接"),
        _ => backend.ToString()
    };
}