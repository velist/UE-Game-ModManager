using UEModManager.Models;
using UEModManager.Services.Deployment;

namespace UEModManager.Core.Tests.Services.Deployment;

public class RollbackActionPlannerTests
{
    [Fact]
    public void Add_PlansDeleteAdded()
    {
        var op = new DeploymentOperation
        {
            Type = DeploymentOperationType.Add,
            TargetPath = "/g/Mods/a.pak",
            RelativeTargetPath = "a.pak",
            BackupPath = "/should/be/ignored", // Add 不需要备份
        };

        var action = RollbackActionPlanner.PlanRollback(op);

        Assert.Equal(RollbackActionType.DeleteAdded, action.Type);
        Assert.Equal("/g/Mods/a.pak", action.TargetPath);
        Assert.Null(action.BackupPath); // Add 即使有备份也不应使用
    }

    [Fact]
    public void RemoveWithBackup_PlansRestore()
    {
        var op = new DeploymentOperation
        {
            Type = DeploymentOperationType.Remove,
            TargetPath = "/g/Mods/x.pak",
            RelativeTargetPath = "x.pak",
            BackupPath = "/backup/x.pak",
        };

        var action = RollbackActionPlanner.PlanRollback(op);

        Assert.Equal(RollbackActionType.RestoreFromBackup, action.Type);
        Assert.Equal("/g/Mods/x.pak", action.TargetPath);
        Assert.Equal("/backup/x.pak", action.BackupPath);
    }

    [Fact]
    public void ReplaceWithBackup_PlansRestore()
    {
        var op = new DeploymentOperation
        {
            Type = DeploymentOperationType.Replace,
            TargetPath = "/g/Mods/x.pak",
            RelativeTargetPath = "x.pak",
            BackupPath = "/backup/x.pak",
        };

        var action = RollbackActionPlanner.PlanRollback(op);

        Assert.Equal(RollbackActionType.RestoreFromBackup, action.Type);
        Assert.Equal("/backup/x.pak", action.BackupPath);
    }

    [Fact]
    public void RemoveWithoutBackup_PlansNoBackupRecorded()
    {
        var op = new DeploymentOperation
        {
            Type = DeploymentOperationType.Remove,
            TargetPath = "/g/Mods/x.pak",
            RelativeTargetPath = "x.pak",
            BackupPath = null,
        };

        var action = RollbackActionPlanner.PlanRollback(op);

        Assert.Equal(RollbackActionType.NoBackupRecorded, action.Type);
        Assert.Null(action.BackupPath);
    }

    [Fact]
    public void ReplaceWithEmptyBackupPath_PlansNoBackupRecorded()
    {
        var op = new DeploymentOperation
        {
            Type = DeploymentOperationType.Replace,
            TargetPath = "/g/Mods/x.pak",
            RelativeTargetPath = "x.pak",
            BackupPath = "",
        };

        var action = RollbackActionPlanner.PlanRollback(op);

        Assert.Equal(RollbackActionType.NoBackupRecorded, action.Type);
    }

    [Fact]
    public void ReplaceWithBackupPath_BackupFileMissing_PlansBackupMissing()
    {
        var op = new DeploymentOperation
        {
            Type = DeploymentOperationType.Replace,
            TargetPath = "/g/Mods/x.pak",
            RelativeTargetPath = "x.pak",
            BackupPath = "/backup/x.pak",
        };

        // 备份判定函数永远返回 false 模拟"备份记录有但文件丢了"
        var action = RollbackActionPlanner.PlanRollback(op, backupExists: _ => false);

        Assert.Equal(RollbackActionType.BackupMissing, action.Type);
        Assert.Equal("/backup/x.pak", action.BackupPath);
    }

    [Fact]
    public void ReplaceWithBackupPath_BackupExists_PlansRestoreFromBackup()
    {
        var op = new DeploymentOperation
        {
            Type = DeploymentOperationType.Replace,
            TargetPath = "/g/Mods/x.pak",
            RelativeTargetPath = "x.pak",
            BackupPath = "/backup/x.pak",
        };

        var action = RollbackActionPlanner.PlanRollback(op, backupExists: _ => true);

        Assert.Equal(RollbackActionType.RestoreFromBackup, action.Type);
        Assert.Equal("/backup/x.pak", action.BackupPath);
    }

    [Fact]
    public void RemoveWithBackupPath_BackupFileMissing_PlansBackupMissing()
    {
        var op = new DeploymentOperation
        {
            Type = DeploymentOperationType.Remove,
            TargetPath = "/g/Mods/x.pak",
            RelativeTargetPath = "x.pak",
            BackupPath = "/backup/x.pak",
        };

        var action = RollbackActionPlanner.PlanRollback(op, backupExists: _ => false);

        Assert.Equal(RollbackActionType.BackupMissing, action.Type);
    }

    [Fact]
    public void Add_IgnoresBackupExistsCheck()
    {
        // Add 操作不需要备份，即使 backupExists 始终返回 false 也仍是 DeleteAdded
        var op = new DeploymentOperation
        {
            Type = DeploymentOperationType.Add,
            TargetPath = "/g/Mods/a.pak",
            RelativeTargetPath = "a.pak",
        };

        var action = RollbackActionPlanner.PlanRollback(op, backupExists: _ => false);

        Assert.Equal(RollbackActionType.DeleteAdded, action.Type);
    }

    [Fact]
    public void NullOperation_Throws()
        => Assert.Throws<ArgumentNullException>(() =>
            RollbackActionPlanner.PlanRollback(null!));
}
