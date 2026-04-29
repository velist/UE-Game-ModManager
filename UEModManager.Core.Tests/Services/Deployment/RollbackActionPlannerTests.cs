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
    public void RemoveWithoutBackup_PlansNone()
    {
        var op = new DeploymentOperation
        {
            Type = DeploymentOperationType.Remove,
            TargetPath = "/g/Mods/x.pak",
            RelativeTargetPath = "x.pak",
            BackupPath = null,
        };

        var action = RollbackActionPlanner.PlanRollback(op);

        Assert.Equal(RollbackActionType.None, action.Type);
        Assert.Null(action.BackupPath);
    }

    [Fact]
    public void ReplaceWithEmptyBackupPath_PlansNone()
    {
        var op = new DeploymentOperation
        {
            Type = DeploymentOperationType.Replace,
            TargetPath = "/g/Mods/x.pak",
            RelativeTargetPath = "x.pak",
            BackupPath = "",
        };

        var action = RollbackActionPlanner.PlanRollback(op);

        Assert.Equal(RollbackActionType.None, action.Type);
    }

    [Fact]
    public void NullOperation_Throws()
        => Assert.Throws<ArgumentNullException>(() =>
            RollbackActionPlanner.PlanRollback(null!));
}
