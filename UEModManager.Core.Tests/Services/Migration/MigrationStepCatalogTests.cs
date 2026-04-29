using System;
using UEModManager.Services.Migration;

namespace UEModManager.Core.Tests.Services.Migration;

public class MigrationStepCatalogTests
{
    [Fact]
    public void TotalSteps_Is5()
    {
        Assert.Equal(5, MigrationStepCatalog.TotalSteps);
    }

    [Fact]
    public void All_HasFiveStepsInOrder()
    {
        var all = MigrationStepCatalog.All;

        Assert.Equal(5, all.Count);
        Assert.Equal(MigrationStep.ScanOldData,         all[0].Step);
        Assert.Equal(MigrationStep.PrepareRepository,   all[1].Step);
        Assert.Equal(MigrationStep.MigrateToRepository, all[2].Step);
        Assert.Equal(MigrationStep.GenerateManifest,    all[3].Step);
        Assert.Equal(MigrationStep.VerifyIntegrity,     all[4].Step);
    }

    [Fact]
    public void All_NumbersAreSequential1To5()
    {
        var all = MigrationStepCatalog.All;
        for (int i = 0; i < all.Count; i++)
            Assert.Equal(i + 1, all[i].Number);
    }

    [Fact]
    public void All_NamesAreNotEmpty()
    {
        foreach (var d in MigrationStepCatalog.All)
            Assert.False(string.IsNullOrWhiteSpace(d.Name));
    }

    [Theory]
    [InlineData(MigrationStep.ScanOldData,         1, "扫描旧数据")]
    [InlineData(MigrationStep.PrepareRepository,   2, "创建默认方案")]
    [InlineData(MigrationStep.MigrateToRepository, 3, "迁移到仓库")]
    [InlineData(MigrationStep.GenerateManifest,    4, "生成 Manifest")]
    [InlineData(MigrationStep.VerifyIntegrity,     5, "验证完整性")]
    public void Get_ByEnum_ReturnsExpectedDescriptor(MigrationStep step, int expectedNumber, string expectedName)
    {
        var d = MigrationStepCatalog.Get(step);

        Assert.Equal(step, d.Step);
        Assert.Equal(expectedNumber, d.Number);
        Assert.Equal(expectedName, d.Name);
    }

    [Theory]
    [InlineData(1, MigrationStep.ScanOldData)]
    [InlineData(2, MigrationStep.PrepareRepository)]
    [InlineData(3, MigrationStep.MigrateToRepository)]
    [InlineData(4, MigrationStep.GenerateManifest)]
    [InlineData(5, MigrationStep.VerifyIntegrity)]
    public void Get_ByNumber_ReturnsExpectedDescriptor(int stepNumber, MigrationStep expectedStep)
    {
        var d = MigrationStepCatalog.Get(stepNumber);

        Assert.Equal(expectedStep, d.Step);
        Assert.Equal(stepNumber, d.Number);
    }

    [Fact]
    public void Get_UnknownEnum_Throws()
    {
        var unknown = (MigrationStep)999;
        Assert.Throws<ArgumentOutOfRangeException>(() => MigrationStepCatalog.Get(unknown));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(6)]
    [InlineData(100)]
    public void Get_NumberOutOfRange_Throws(int stepNumber)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MigrationStepCatalog.Get(stepNumber));
    }

    [Fact]
    public void All_ReturnsSameInstance()
    {
        var first = MigrationStepCatalog.All;
        var second = MigrationStepCatalog.All;
        Assert.Same(first, second);
    }

    [Fact]
    public void Steps_AreUnique()
    {
        var distinct = new System.Collections.Generic.HashSet<MigrationStep>();
        foreach (var d in MigrationStepCatalog.All)
            Assert.True(distinct.Add(d.Step), $"重复的步骤: {d.Step}");
    }
}
