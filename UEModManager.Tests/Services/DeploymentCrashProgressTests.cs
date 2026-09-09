using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Models;
using UEModManager.Services;
using UEModManager.Services.Backends;

namespace UEModManager.Tests.Services;

public sealed class DeploymentCrashProgressTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "UEModManager.Tests", "crash-progress-" + Guid.NewGuid().ToString("N"));
    private readonly DeploymentStateStore _states;
    private DeploymentService? _service;
    private string GameRoot => Path.Combine(_root, "Game");
    private string ModRoot => Path.Combine(GameRoot, "Mods");

    public DeploymentCrashProgressTests()
    {
        Directory.CreateDirectory(ModRoot);
        _states = new DeploymentStateStore(NullLogger<DeploymentStateStore>.Instance, Path.Combine(_root, "State"));
    }

    [Theory]
    [InlineData(26)]
    [InlineData(30)]
    public async Task ReloadedCrashAfterProgressFlush_RollsBackUnloggedTailBeforeRestoringOwnership(int appliedOperations)
    {
        var (recoveredService, recovered) = await ReplayCrashAsync(appliedOperations);

        var outcome = await recoveredService.RollbackAsync(recovered);
        Assert.True(outcome.Succeeded);
        Assert.Empty(Directory.GetFiles(ModRoot, "*", SearchOption.AllDirectories));
        Assert.Empty((await _states.ReadAsync("Test", GameRoot, ModRoot)).Files);
        Assert.Equal(DeploymentStatus.RolledBack, Assert.Single(await recoveredService.GetTransactionHistoryAsync()).Status);
    }

    [Fact]
    public async Task PartiallyRolledBackCrash_ReloadedRetryStillCoversUnloggedTail()
    {
        var (recoveredService, recovered) = await ReplayCrashAsync(26);
        var tail = recovered.PlannedOperations[25].TargetPath;
        await using (var lockedTarget = new FileStream(tail, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var partial = await recoveredService.RollbackAsync(recovered);
            Assert.False(partial.Succeeded);
            Assert.Equal(DeploymentStatus.PartiallyRolledBack, recovered.Status);
            Assert.Equal(tail, Assert.Single(partial.Failures).TargetPath);
        }

        Assert.Equal(tail, Assert.Single(Directory.GetFiles(ModRoot, "*", SearchOption.AllDirectories)));
        var retryService = NewService(new CopyBackend(NullLogger<CopyBackend>.Instance));
        var retry = Assert.Single(await retryService.GetTransactionHistoryAsync());
        Assert.Equal(DeploymentStatus.PartiallyRolledBack, retry.Status);
        Assert.Equal(25, retry.ExecutedOperations.Count);

        Assert.True((await retryService.RollbackAsync(retry)).Succeeded);
        Assert.Empty(Directory.GetFiles(ModRoot, "*", SearchOption.AllDirectories));
        Assert.Empty((await _states.ReadAsync("Test", GameRoot, ModRoot)).Files);
    }

    [Fact]
    public async Task UnloggedPlannedAddContainingExternalBytes_IsPreservedAndReportedAsPartialRecovery()
    {
        var (recoveredService, recovered) = await ReplayCrashAsync(25);
        var unloggedTarget = recovered.PlannedOperations[25].TargetPath;
        await File.WriteAllTextAsync(unloggedTarget, "EXTERNAL");

        var outcome = await recoveredService.RollbackAsync(recovered);

        Assert.True(File.Exists(unloggedTarget));
        Assert.Equal("EXTERNAL", await File.ReadAllTextAsync(unloggedTarget));
        Assert.False(outcome.Succeeded);
        Assert.Equal(DeploymentStatus.PartiallyRolledBack, recovered.Status);
        Assert.Equal(unloggedTarget, Assert.Single(outcome.Failures).TargetPath);
        Assert.Equal(unloggedTarget, Assert.Single(Directory.GetFiles(ModRoot, "*", SearchOption.AllDirectories)));
        Assert.Equal(DeploymentStatus.PartiallyRolledBack, Assert.Single(await recoveredService.GetTransactionHistoryAsync()).Status);
    }

    private async Task<(DeploymentService Service, DeploymentTransaction Transaction)> ReplayCrashAsync(int appliedOperations)
    {
        var plan = await MakePlanAsync(30);
        string? checkpoint = null;
        var backend = new HookBackend(async (count, _) =>
        {
            if (count == 26)
            {
                // This is the real log written by DeploymentService after operation 25, while operation 26 is on disk.
                checkpoint = await File.ReadAllTextAsync(Path.Combine(_service!.LastTransaction!.BackupDirectory, "transaction.json"));
            }
        });
        _service = NewService(backend);
        var finished = await _service.ExecuteAsync(plan);
        Assert.Equal(DeploymentStatus.Committed, finished.Status);
        Assert.NotNull(checkpoint);
        await _service.PendingBackupCleanup;

        // Replay the disk image at the crash boundary: 26/30 file writes, only 25 logged, no ownership commit yet.
        foreach (var operation in plan.Operations.Skip(appliedOperations)) File.Delete(operation.TargetPath);
        await _states.WriteAsync(plan.StateBefore!);
        await File.WriteAllTextAsync(Path.Combine(finished.BackupDirectory, "transaction.json"), checkpoint);
        var recoveredService = NewService(new CopyBackend(NullLogger<CopyBackend>.Instance));
        var recovered = Assert.Single(await recoveredService.GetTransactionHistoryAsync());
        Assert.Equal(DeploymentStatus.InProgress, recovered.Status);
        Assert.Equal(25, recovered.ExecutedOperations.Count);
        Assert.Equal(30, recovered.PlannedOperations.Count);
        Assert.Equal(appliedOperations, Directory.GetFiles(ModRoot, "*", SearchOption.AllDirectories).Length);

        return (recoveredService, recovered);
    }

    [Fact]
    public async Task InProcessFailure_UsesExactExecutionRecord_AndPreservesUnattemptedExternalFile()
    {
        var plan = await MakePlanAsync(2);
        var unattemptedTarget = plan.Operations[1].TargetPath;
        _service = NewService(new HookBackend(async (count, _) =>
        {
            if (count != 1) return;
            // Simulate an external file appearing at the next planned destination while the first write fails.
            await File.WriteAllTextAsync(unattemptedTarget, "EXTERNAL");
            throw new IOException("Injected failure after the first real write");
        }));

        var result = await _service.ExecuteAsync(plan);
        Assert.Equal(DeploymentStatus.RolledBack, result.Status);
        Assert.Single(result.ExecutedOperations);
        Assert.False(File.Exists(plan.Operations[0].TargetPath));
        Assert.Equal("EXTERNAL", await File.ReadAllTextAsync(unattemptedTarget));
        Assert.Empty((await _states.ReadAsync("Test", GameRoot, ModRoot)).Files);
    }

    private async Task<DeploymentPlan> MakePlanAsync(int count)
    {
        var before = await _states.ReadAsync("Test", GameRoot, ModRoot);
        var operations = new List<DeploymentOperation>();
        var files = new List<ManagedDeploymentFile>();
        var sourceDirectory = Path.Combine(_root, "Repository", "batch", "files");
        Directory.CreateDirectory(sourceDirectory);
        for (var i = 1; i <= count; i++)
        {
            var name = $"{i:D2}.pak";
            var source = Path.Combine(sourceDirectory, name);
            await File.WriteAllTextAsync(source, $"CONTENT-{i:D2}");
            var relative = Path.Combine("batch", name);
            var target = Path.Combine(ModRoot, relative);
            var hash = await ObjectStore.ComputeFileHashAsync(source);
            var size = new FileInfo(source).Length;
            operations.Add(new DeploymentOperation
            {
                Type = DeploymentOperationType.Add, SourcePath = source, TargetPath = target,
                RelativeTargetPath = relative, FileHash = hash, FileSize = size,
                PackageKey = "batch", PackageDisplayName = "Batch", ExpectedTargetExists = false
            });
            files.Add(new ManagedDeploymentFile
            {
                TargetPath = target, RelativeTargetPath = relative, FileHash = hash, FileSize = size,
                PackageKey = "batch", PackageDisplayName = "Batch"
            });
        }
        return new DeploymentPlan
        {
            HostGameName = "Test", ProfileId = Guid.NewGuid(), BackendType = DeploymentBackendType.Copy,
            Operations = operations, StateBefore = before,
            StateAfter = new DeploymentState
            {
                HostGameName = "Test", GameRootPath = GameRoot, ModRootPath = ModRoot, Revision = Guid.NewGuid(), Files = files
            }
        };
    }

    private DeploymentService NewService(IDeploymentBackend backend)
        => new(NullLogger<DeploymentService>.Instance, [backend], null!, _states, Path.Combine(_root, "Backups"));

    private sealed class HookBackend(Func<int, string, Task> afterWrite) : IDeploymentBackend
    {
        private readonly CopyBackend _copy = new(NullLogger<CopyBackend>.Instance);
        private int _writes;
        public DeploymentBackendType Type => DeploymentBackendType.Copy;
        public string DisplayName => "Copy with test hook";
        public Task<bool> CanUseAsync() => Task.FromResult(true);
        public async Task DeployFileAsync(string sourcePath, string targetPath)
        {
            await _copy.DeployFileAsync(sourcePath, targetPath);
            await afterWrite(++_writes, targetPath);
        }
        public Task RemoveFileAsync(string targetPath) => _copy.RemoveFileAsync(targetPath);
    }

    public void Dispose()
    {
        _service?.PendingBackupCleanup.GetAwaiter().GetResult();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
