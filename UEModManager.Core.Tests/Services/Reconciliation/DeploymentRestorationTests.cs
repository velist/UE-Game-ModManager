using UEModManager.Models;
using UEModManager.Services.Reconciliation;

namespace UEModManager.Core.Tests.Services.Reconciliation;

/// <summary>
/// 从部署计划里认出"丢了又装回去"的文件。
/// 启动游戏时靠它告诉玩家：有 MOD 的文件在游戏目录里不见了，已经重新装好。
/// </summary>
public class DeploymentRestorationTests
{
    private static ManagedDeploymentFile Owned(string key, string target) => new()
    {
        PackageKey = key, PackageDisplayName = key + " 显示名", TargetPath = target,
        RelativeTargetPath = Path.GetFileName(target), FileHash = "h", FileSize = 1,
    };

    private static DeploymentOperation Op(DeploymentOperationType type, string key, string target) => new()
    {
        Type = type, PackageKey = key, PackageDisplayName = key + " 显示名", TargetPath = target,
        RelativeTargetPath = Path.GetFileName(target), SourcePath = @"R:\repo\" + Path.GetFileName(target),
    };

    private static DeploymentPlan Plan(
        IEnumerable<ManagedDeploymentFile> before, IEnumerable<ManagedDeploymentFile> after,
        params DeploymentOperation[] operations) => new()
    {
        HostGameName = "Test",
        Operations = [.. operations],
        StateBefore = new DeploymentState { Revision = Guid.NewGuid(), Files = [.. before] },
        StateAfter = new DeploymentState { Revision = Guid.NewGuid(), Files = [.. after] },
    };

    [Fact]
    public void FindRestoredFiles_OwnedFileAddedAgain_IsARestoration()
    {
        var files = new[] { Owned("a", @"G:\Mods\a\a.pak"), Owned("a", @"G:\Mods\a\a.ucas"), Owned("b", @"G:\Mods\b\b.pak") };
        var plan = Plan(files, files,
            Op(DeploymentOperationType.Add, "a", @"G:\Mods\a\a.pak"),
            Op(DeploymentOperationType.Add, "a", @"G:\Mods\a\a.ucas"));

        var restored = Assert.Single(DeploymentRestoration.FindRestoredFiles(plan));

        Assert.Equal("a", restored.PackageKey);
        Assert.Equal("a 显示名", restored.DisplayName);
        Assert.Equal(2, restored.FileCount);
    }

    [Fact]
    public void FindRestoredFiles_FirstTimeEnable_IsNotARestoration()
    {
        // 刚启用的 MOD：清单里本来就没有它，这是正常的第一次安装
        var installed = new[] { Owned("new", @"G:\Mods\new\n.pak") };
        var plan = Plan([], installed, Op(DeploymentOperationType.Add, "new", @"G:\Mods\new\n.pak"));

        Assert.Empty(DeploymentRestoration.FindRestoredFiles(plan));
    }

    [Fact]
    public void FindRestoredFiles_ReplaceOfChangedFile_IsNotARestoration()
    {
        // 文件还在、只是内容变了：那是"被改过"，不是"丢了"
        var files = new[] { Owned("a", @"G:\Mods\a\a.pak") };
        var plan = Plan(files, files, Op(DeploymentOperationType.Replace, "a", @"G:\Mods\a\a.pak"));

        Assert.Empty(DeploymentRestoration.FindRestoredFiles(plan));
    }

    [Fact]
    public void FindRestoredFiles_RestoringTheGamesOriginalFile_IsNotARestoration()
    {
        // 禁用配置包时把游戏原来的文件放回去：也是一条"新增"，但它不在这次要装的清单里
        var before = new[] { Owned("cfg", @"G:\Game\Engine.ini") };
        var plan = Plan(before, [], Op(DeploymentOperationType.Add, "cfg", @"G:\Game\Engine.ini"));

        Assert.Empty(DeploymentRestoration.FindRestoredFiles(plan));
    }

    [Fact]
    public void FindRestoredFiles_RemovalsAndUntouchedFiles_AreIgnored()
    {
        var before = new[] { Owned("a", @"G:\Mods\a\a.pak"), Owned("b", @"G:\Mods\b\b.pak") };
        var after = new[] { Owned("a", @"G:\Mods\a\a.pak") };
        var plan = Plan(before, after, Op(DeploymentOperationType.Remove, "b", @"G:\Mods\b\b.pak"));

        Assert.Empty(DeploymentRestoration.FindRestoredFiles(plan));
    }

    [Fact]
    public void FindRestoredFiles_SeveralPackages_AreListedByName()
    {
        var files = new[] { Owned("zeta", @"G:\Mods\zeta\z.pak"), Owned("alpha", @"G:\Mods\alpha\a.pak") };
        var plan = Plan(files, files,
            Op(DeploymentOperationType.Add, "zeta", @"G:\Mods\zeta\z.pak"),
            Op(DeploymentOperationType.Add, "alpha", @"g:\mods\ALPHA\a.pak"));

        var restored = DeploymentRestoration.FindRestoredFiles(plan);

        Assert.Equal(["alpha", "zeta"], restored.Select(r => r.PackageKey));
        Assert.All(restored, r => Assert.Equal(1, r.FileCount));
    }

    [Fact]
    public void FindRestoredFiles_PlanWithoutOwnershipSnapshots_HasNothingToSay()
    {
        // 旧格式的计划不带清单：不知道哪些是管理器装过的，不乱猜
        var plan = new DeploymentPlan
        {
            HostGameName = "Test",
            Operations = [Op(DeploymentOperationType.Add, "a", @"G:\Mods\a\a.pak")],
        };

        Assert.Empty(DeploymentRestoration.FindRestoredFiles(plan));
    }

    [Fact]
    public void FindRestoredFiles_NullPlan_Throws()
        => Assert.Throws<ArgumentNullException>(() => DeploymentRestoration.FindRestoredFiles(null!));
}
