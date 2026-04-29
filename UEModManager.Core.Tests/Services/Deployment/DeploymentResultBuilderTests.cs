using UEModManager.Models;
using UEModManager.Services.Deployment;

namespace UEModManager.Core.Tests.Services.Deployment;

public class DeploymentResultBuilderTests
{
    [Fact]
    public void EmptyTransaction_PreservesPlanIdentity()
    {
        var planId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var plan = new DeploymentPlan
        {
            Id = planId,
            ProfileId = profileId,
            HostGameName = "demo",
            BackendType = DeploymentBackendType.HardLink,
        };

        var tx = DeploymentResultBuilder.BuildEmptyCommittedTransaction(plan);

        Assert.Equal(planId, tx.PlanId);
        Assert.Equal(profileId, tx.ProfileId);
        Assert.Equal("demo", tx.HostGameName);
        Assert.Equal(DeploymentBackendType.HardLink, tx.BackendType);
    }

    [Fact]
    public void EmptyTransaction_StatusIsCommittedZeroOperations()
    {
        var plan = new DeploymentPlan { HostGameName = "demo" };

        var tx = DeploymentResultBuilder.BuildEmptyCommittedTransaction(plan);

        Assert.Equal(DeploymentStatus.Committed, tx.Status);
        Assert.Equal(0, tx.TotalOperations);
        Assert.Equal(0, tx.CompletedOperations);
        Assert.Empty(tx.ExecutedOperations);
    }

    [Fact]
    public void EmptyTransaction_BackupDirectoryIsEmpty()
    {
        var plan = new DeploymentPlan { HostGameName = "demo" };

        var tx = DeploymentResultBuilder.BuildEmptyCommittedTransaction(plan);

        Assert.Equal("", tx.BackupDirectory);
    }

    [Fact]
    public void EmptyTransaction_CompletedAtSet()
    {
        var plan = new DeploymentPlan { HostGameName = "demo" };

        var tx = DeploymentResultBuilder.BuildEmptyCommittedTransaction(plan);

        Assert.NotNull(tx.CompletedAt);
    }

    [Fact]
    public void EmptyTransaction_HasNewIdNotPlanId()
    {
        var plan = new DeploymentPlan { Id = Guid.NewGuid(), HostGameName = "demo" };

        var tx = DeploymentResultBuilder.BuildEmptyCommittedTransaction(plan);

        Assert.NotEqual(plan.Id, tx.Id);
        Assert.NotEqual(Guid.Empty, tx.Id);
    }

    [Fact]
    public void NullPlan_Throws()
        => Assert.Throws<ArgumentNullException>(() =>
            DeploymentResultBuilder.BuildEmptyCommittedTransaction(null!));
}
