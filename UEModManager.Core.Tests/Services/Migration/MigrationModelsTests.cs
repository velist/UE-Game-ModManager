using UEModManager.Services.Migration;

namespace UEModManager.Core.Tests.Services.Migration;

public class MigrationModelsTests
{
    [Fact]
    public void MigrationProgress_Percentage_ComputesStepRatio()
    {
        var progress = new MigrationProgress
        {
            CurrentStep = 2,
            TotalSteps = 5,
        };

        Assert.Equal(40, progress.Percentage);
    }

    [Fact]
    public void MigrationProgress_Percentage_ZeroTotal_ReturnsZero()
    {
        var progress = new MigrationProgress
        {
            CurrentStep = 2,
            TotalSteps = 0,
        };

        Assert.Equal(0, progress.Percentage);
    }

    [Fact]
    public void MigrationProgress_DefaultStrings_AreEmpty()
    {
        var progress = new MigrationProgress();

        Assert.Equal(string.Empty, progress.StepName);
        Assert.Equal(string.Empty, progress.Detail);
    }

    [Fact]
    public void MigrationResult_DefaultWarnings_IsEmptyList()
    {
        var result = new MigrationResult();

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void MigrationResult_CanCarryFailureErrorMessage()
    {
        var result = new MigrationResult
        {
            Success = false,
            ErrorMessage = "failed",
        };

        Assert.False(result.Success);
        Assert.Equal("failed", result.ErrorMessage);
    }
}
