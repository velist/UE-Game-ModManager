using UEModManager.Models;
using UEModManager.Services.DeploymentPlanning;

namespace UEModManager.Core.Tests.Services.DeploymentPlanning;

public class DeploymentDiffComputerTests
{
    private static DesiredFile Want(string key, string target, string? hash = "h", long size = 100)
        => new(
            PackageKey: key,
            PackageDisplayName: key.ToUpperInvariant(),
            SourcePath: $"/repo/{key}/files/x",
            TargetPath: target,
            RelativeTargetPath: Path.GetFileName(target),
            FileHash: hash,
            FileSize: size,
            Kind: PackageKind.Mod);

    private static DeployedFile Have(string key, string? hash = "h", bool known = true, long size = 100)
        => new(
            PackageKey: key,
            PackageDisplayName: key.ToUpperInvariant(),
            RelativePath: "x",
            Hash: hash,
            FileSize: size,
            Kind: PackageKind.Mod,
            BelongsToKnownPackage: known);

    [Fact]
    public void EmptyInputs_NoOps()
    {
        var ops = DeploymentDiffComputer.ComputeDiff(
            new Dictionary<string, DesiredFile>(),
            new Dictionary<string, DeployedFile>());

        Assert.Empty(ops);
    }

    [Fact]
    public void DesiredOnly_ProducesAdd()
    {
        var desired = new Dictionary<string, DesiredFile> { ["/g/a"] = Want("a", "/g/a") };
        var actual = new Dictionary<string, DeployedFile>();

        var ops = DeploymentDiffComputer.ComputeDiff(desired, actual);

        var op = Assert.Single(ops);
        Assert.Equal(DeploymentOperationType.Add, op.Type);
        Assert.Equal("a", op.PackageKey);
    }

    [Fact]
    public void HashMatches_NoOp()
    {
        var desired = new Dictionary<string, DesiredFile> { ["/g/a"] = Want("a", "/g/a", "same") };
        var actual = new Dictionary<string, DeployedFile> { ["/g/a"] = Have("a", "same") };

        var ops = DeploymentDiffComputer.ComputeDiff(desired, actual);

        Assert.Empty(ops);
    }

    [Fact]
    public void HashDiffers_ProducesReplace()
    {
        var desired = new Dictionary<string, DesiredFile> { ["/g/a"] = Want("a", "/g/a", "new") };
        var actual = new Dictionary<string, DeployedFile> { ["/g/a"] = Have("a", "old") };

        var ops = DeploymentDiffComputer.ComputeDiff(desired, actual);

        var op = Assert.Single(ops);
        Assert.Equal(DeploymentOperationType.Replace, op.Type);
    }

    [Fact]
    public void ActualOnlyKnownPackage_ProducesRemove()
    {
        var desired = new Dictionary<string, DesiredFile>();
        var actual = new Dictionary<string, DeployedFile> { ["/g/x"] = Have("x", known: true) };

        var ops = DeploymentDiffComputer.ComputeDiff(desired, actual);

        var op = Assert.Single(ops);
        Assert.Equal(DeploymentOperationType.Remove, op.Type);
    }

    [Fact]
    public void ActualOnlyUnknownPackage_NoOp()
    {
        // 不属于已知包（如外部用户自己放的文件）→ 不动
        var desired = new Dictionary<string, DesiredFile>();
        var actual = new Dictionary<string, DeployedFile> { ["/g/x"] = Have("x", known: false) };

        var ops = DeploymentDiffComputer.ComputeDiff(desired, actual);

        Assert.Empty(ops);
    }

    [Fact]
    public void MissingDesiredHash_NoReplaceEvenIfDifferent()
    {
        var desired = new Dictionary<string, DesiredFile> { ["/g/a"] = Want("a", "/g/a", hash: null) };
        var actual = new Dictionary<string, DeployedFile> { ["/g/a"] = Have("a", "anything") };

        var ops = DeploymentDiffComputer.ComputeDiff(desired, actual);

        Assert.Empty(ops);  // 无哈希 → 跳过比较
    }

    [Fact]
    public void MixedScenario_AllThreeOpTypes()
    {
        var desired = new Dictionary<string, DesiredFile>
        {
            ["/g/keep"]    = Want("keep", "/g/keep", "h-keep"),
            ["/g/new"]     = Want("new", "/g/new", "h-new"),
            ["/g/changed"] = Want("changed", "/g/changed", "h-new"),
        };
        var actual = new Dictionary<string, DeployedFile>
        {
            ["/g/keep"]    = Have("keep", "h-keep"),
            ["/g/changed"] = Have("changed", "h-old"),
            ["/g/orphan"]  = Have("orphan", "h", known: true),
        };

        var ops = DeploymentDiffComputer.ComputeDiff(desired, actual);

        Assert.Equal(3, ops.Count);
        Assert.Contains(ops, o => o.Type == DeploymentOperationType.Add && o.PackageKey == "new");
        Assert.Contains(ops, o => o.Type == DeploymentOperationType.Replace && o.PackageKey == "changed");
        Assert.Contains(ops, o => o.Type == DeploymentOperationType.Remove && o.PackageKey == "orphan");
    }

    [Fact]
    public void NullInputs_Throw()
    {
        Assert.Throws<ArgumentNullException>(() =>
            DeploymentDiffComputer.ComputeDiff(null!, new Dictionary<string, DeployedFile>()));
        Assert.Throws<ArgumentNullException>(() =>
            DeploymentDiffComputer.ComputeDiff(new Dictionary<string, DesiredFile>(), null!));
    }

    [Fact]
    public void Diff_DoesNotMutateInputs()
    {
        var desired = new Dictionary<string, DesiredFile> { ["/g/a"] = Want("a", "/g/a") };
        var actual = new Dictionary<string, DeployedFile>
        {
            ["/g/a"] = Have("a"),
            ["/g/x"] = Have("x", known: true),
        };

        DeploymentDiffComputer.ComputeDiff(desired, actual);

        // ComputeDiff 不应修改输入字典
        Assert.Single(desired);
        Assert.Equal(2, actual.Count);
    }
}
