using System;
using UEModManager.Services.Migration;

namespace UEModManager.Core.Tests.Services.Migration;

public class MigrationProgressTrackerTests
{
    [Fact]
    public void Build_ScanOldData_UsesStep1Defaults()
    {
        var p = MigrationProgressTracker.Build(MigrationStep.ScanOldData, "读取 abc_mods.json");

        Assert.Equal(1, p.CurrentStep);
        Assert.Equal(5, p.TotalSteps);
        Assert.Equal("扫描旧数据", p.StepName);
        Assert.Equal("读取 abc_mods.json", p.Detail);
    }

    [Fact]
    public void Build_VerifyIntegrity_UsesStep5Defaults()
    {
        var p = MigrationProgressTracker.Build(MigrationStep.VerifyIntegrity, "检查仓库一致性");

        Assert.Equal(5, p.CurrentStep);
        Assert.Equal(5, p.TotalSteps);
        Assert.Equal("验证完整性", p.StepName);
        Assert.Equal("检查仓库一致性", p.Detail);
    }

    [Fact]
    public void Build_AllSteps_PercentagesGoUpInTwentyIncrements()
    {
        Assert.Equal(20, MigrationProgressTracker.Build(MigrationStep.ScanOldData,         "").Percentage);
        Assert.Equal(40, MigrationProgressTracker.Build(MigrationStep.PrepareRepository,   "").Percentage);
        Assert.Equal(60, MigrationProgressTracker.Build(MigrationStep.MigrateToRepository, "").Percentage);
        Assert.Equal(80, MigrationProgressTracker.Build(MigrationStep.GenerateManifest,    "").Percentage);
        Assert.Equal(100, MigrationProgressTracker.Build(MigrationStep.VerifyIntegrity,    "").Percentage);
    }

    [Fact]
    public void Build_NullDetail_NormalizedToEmpty()
    {
        var p = MigrationProgressTracker.Build(MigrationStep.ScanOldData, null!);

        Assert.Equal(string.Empty, p.Detail);
    }

    [Fact]
    public void Build_WithOverrideName_UsesProvidedName()
    {
        var p = MigrationProgressTracker.Build(MigrationStep.MigrateToRepository, "处理 5 个", "Custom Step Name");

        Assert.Equal(3, p.CurrentStep);
        Assert.Equal("Custom Step Name", p.StepName);
        Assert.Equal("处理 5 个", p.Detail);
    }

    [Fact]
    public void Build_WithNullOverrideName_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            MigrationProgressTracker.Build(MigrationStep.ScanOldData, "x", null!));
    }

    [Fact]
    public void Build_UnknownStep_Throws()
    {
        var unknown = (MigrationStep)42;
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MigrationProgressTracker.Build(unknown, "detail"));
    }

    [Theory]
    [InlineData(0, 5, 0)]
    [InlineData(1, 5, 20)]
    [InlineData(2, 5, 40)]
    [InlineData(3, 5, 60)]
    [InlineData(5, 5, 100)]
    [InlineData(10, 5, 200)] // 上界不夹紧；调用方约束
    public void ComputePercentage_NormalCases(int current, int total, double expected)
    {
        Assert.Equal(expected, MigrationProgressTracker.ComputePercentage(current, total));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    [InlineData(0, -10)]
    public void ComputePercentage_NonPositiveTotal_ReturnsZero(int current, int total)
    {
        Assert.Equal(0, MigrationProgressTracker.ComputePercentage(current, total));
    }
}
