using UEModManager.Services.Migration;

namespace UEModManager.Core.Tests.Services.Migration;

public class MigrationDecisionTests
{
    [Fact]
    public void NeedsMigration_NoOldData_ReturnsFalse()
    {
        var result = MigrationDecision.NeedsMigration(
            oldDataExists: false,
            newDataExists: false,
            newPackagesCount: null);

        Assert.False(result);
    }

    [Fact]
    public void NeedsMigration_OldDataWithoutNewData_ReturnsTrue()
    {
        var result = MigrationDecision.NeedsMigration(
            oldDataExists: true,
            newDataExists: false,
            newPackagesCount: null);

        Assert.True(result);
    }

    [Fact]
    public void NeedsMigration_NewDataUnreadable_ReturnsTrue()
    {
        var result = MigrationDecision.NeedsMigration(
            oldDataExists: true,
            newDataExists: true,
            newPackagesCount: null);

        Assert.True(result);
    }

    [Fact]
    public void NeedsMigration_NewDataEmpty_ReturnsTrue()
    {
        var result = MigrationDecision.NeedsMigration(
            oldDataExists: true,
            newDataExists: true,
            newPackagesCount: 0);

        Assert.True(result);
    }

    [Fact]
    public void NeedsMigration_NewDataHasPackages_ReturnsFalse()
    {
        var result = MigrationDecision.NeedsMigration(
            oldDataExists: true,
            newDataExists: true,
            newPackagesCount: 3);

        Assert.False(result);
    }
}
