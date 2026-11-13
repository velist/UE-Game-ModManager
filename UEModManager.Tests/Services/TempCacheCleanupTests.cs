using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using UEModManager.Models;
using UEModManager.Services;
using UEModManager.Services.Backends;
using UEModManager.Services.Deployment;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace UEModManager.Tests.Services;

/// <summary>
/// 「临时数据把系统盘占满」的回归用例：部署备份按时间/总量清理且覆盖旧位置、
/// 老版本留下的部署快照被清掉、定时清理一轮能把三样都收走。
/// </summary>
public sealed class TempCacheCleanupTests : IDisposable
{
    private static readonly JsonSerializerOptions TxJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "UEModManager.Tests", "TempCache", Guid.NewGuid().ToString("N"));

    private string BackupRoot => Path.Combine(_root, "Repository", ".deploy-backups");
    private string LegacyBackupRoot => Path.Combine(_root, "Local", "Backups", "Deployments");
    private string OverwriteRoot => Path.Combine(_root, "Overwrites");
    private string DataDir => Path.Combine(_root, "Data");

    private DeploymentService? _deployment;

    public TempCacheCleanupTests() => Directory.CreateDirectory(DataDir);

    public void Dispose()
    {
        _deployment?.PendingBackupCleanup.GetAwaiter().GetResult();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ─── 部署事务备份 ───

    [Fact]
    public void Cleanup_RemovesStaleFinishedBackups_FromBothCurrentAndLegacyRoots()
    {
        var staleCurrent = SeedTransaction(BackupRoot, DeploymentStatus.Committed, daysAgo: 10);
        var staleLegacy = SeedTransaction(LegacyBackupRoot, DeploymentStatus.Committed, daysAgo: 10);
        var fresh = SeedTransaction(BackupRoot, DeploymentStatus.Committed, daysAgo: 0);

        var (directories, bytes) = NewDeployment().CleanupOldBackups();

        Assert.Equal(2, directories);
        Assert.True(bytes > 0);
        Assert.False(Directory.Exists(staleCurrent));
        Assert.False(Directory.Exists(staleLegacy));   // 老用户系统盘上的那一份
        Assert.True(Directory.Exists(fresh));
    }

    [Fact]
    public void Cleanup_NeverTouchesTransactionsThatMayNeedRecovery()
    {
        var crashed = SeedTransaction(LegacyBackupRoot, DeploymentStatus.InProgress, daysAgo: 30);
        var partial = SeedTransaction(BackupRoot, DeploymentStatus.PartiallyRolledBack, daysAgo: 30);
        var unreadable = Path.Combine(BackupRoot, "unreadable");
        Directory.CreateDirectory(unreadable);
        File.WriteAllText(Path.Combine(unreadable, "transaction.json"), "{");
        File.WriteAllText(Path.Combine(unreadable, "payload.pak"), "DATA");
        Directory.SetCreationTimeUtc(unreadable, DateTime.UtcNow.AddDays(-30));

        NewDeployment().CleanupOldBackups();

        Assert.True(Directory.Exists(crashed));
        Assert.True(Directory.Exists(partial));
        Assert.True(Directory.Exists(unreadable));   // 日志读不出来 ≠ 可以删
    }

    [Fact]
    public void Cleanup_KeepsOldPre210Transactions_UntilCountRuleEvictsThem()
    {
        // 不带部署文件清单 = 2.1.0 之前的事务：升级用户认领旧文件要用，不按时间清。
        var legacyProvenance = SeedTransaction(LegacyBackupRoot, DeploymentStatus.Committed, daysAgo: 60, withState: false);

        NewDeployment().CleanupOldBackups();

        Assert.True(Directory.Exists(legacyProvenance));
    }

    [Fact]
    public void Cleanup_RemovesAbandonedDirectoriesWithoutLog()
    {
        var abandoned = Path.Combine(BackupRoot, "abandoned");
        Directory.CreateDirectory(abandoned);
        File.WriteAllText(Path.Combine(abandoned, "x.pak"), "DATA");
        Directory.SetCreationTimeUtc(abandoned, DateTime.UtcNow.AddDays(-5));
        var justCreated = Path.Combine(BackupRoot, "just-created");
        Directory.CreateDirectory(justCreated);

        NewDeployment().CleanupOldBackups();

        Assert.False(Directory.Exists(abandoned));
        Assert.True(Directory.Exists(justCreated));
    }

    [Fact]
    public async Task History_IncludesTransactionsLeftAtLegacyRoot()
    {
        SeedTransaction(LegacyBackupRoot, DeploymentStatus.InProgress, daysAgo: 1);
        SeedTransaction(BackupRoot, DeploymentStatus.Committed, daysAgo: 0);

        var history = await NewDeployment().GetTransactionHistoryAsync();

        // 崩溃恢复读的就是这份历史：旧位置上没回滚完的事务必须还在里面。
        Assert.Equal(2, history.Count);
        Assert.Contains(history, tx => tx.Status == DeploymentStatus.InProgress);
    }

    // ─── 老版本留下的部署快照 ───

    [Fact]
    public async Task LoadingGame_DropsOldDeploymentSnapshots_AndKeepsRealArtifacts()
    {
        const string game = "Test";
        var snapshotFile = SeedOverwriteFile(game, "deploymentsnapshot", "big.pak");
        var toolFile = SeedOverwriteFile(game, "tooloutput", "merged.ini");
        SeedOverwriteIndex(game,
            Artifact(GeneratedArtifactType.DeploymentSnapshot, Path.Combine("deploymentsnapshot", "big.pak")),
            Artifact(GeneratedArtifactType.ToolOutput, Path.Combine("tooloutput", "merged.ini")));

        var store = NewOverwriteStore();
        await store.SetCurrentGameAsync(game);

        var remaining = Assert.Single(store.GetAll());
        Assert.Equal(GeneratedArtifactType.ToolOutput, remaining.Type);
        Assert.False(File.Exists(snapshotFile));
        Assert.True(File.Exists(toolFile));

        // 落盘了：再加载一次不该又冒出来。
        var reloaded = NewOverwriteStore();
        await reloaded.SetCurrentGameAsync(game);
        Assert.Single(reloaded.GetAll());
    }

    [Fact]
    public void PurgeSnapshotFolders_CoversGamesThatAreNotLoaded()
    {
        var a = SeedOverwriteFile("GameA", "deploymentsnapshot", "a.pak");
        var b = SeedOverwriteFile("GameB", "deploymentsnapshot", "b.pak");
        var keep = SeedOverwriteFile("GameB", "tooloutput", "keep.ini");

        var freed = NewOverwriteStore().PurgeDeploymentSnapshotFolders();

        Assert.True(freed > 0);
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
        Assert.True(File.Exists(keep));
    }

    // ─── 定时清理 ───

    [Fact]
    public async Task Janitor_OneSweep_ClearsSnapshotsBackupsAndStaleImportTemp()
    {
        var snapshot = SeedOverwriteFile("GameA", "deploymentsnapshot", "a.pak");
        var staleBackup = SeedTransaction(LegacyBackupRoot, DeploymentStatus.Committed, daysAgo: 10);
        var objectStore = new ObjectStore(NullLogger<ObjectStore>.Instance, Path.Combine(_root, "Repository"));
        var staleTemp = Path.Combine(objectStore.RepositoryRoot, ".import-tmp", "uemod_import_dead");
        Directory.CreateDirectory(staleTemp);
        File.WriteAllText(Path.Combine(staleTemp, "x.pak"), "DATA");
        Directory.SetLastWriteTimeUtc(staleTemp, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(Path.Combine(staleTemp, "x.pak"), DateTime.UtcNow.AddDays(-2));

        var overwrites = NewOverwriteStore();
        var deployment = NewDeployment();
        var reclaim = new RepositoryReclaimService(NullLogger<RepositoryReclaimService>.Instance, objectStore, DataDir);
        using var janitor = new TempCacheJanitor(NullLogger<TempCacheJanitor>.Instance,
            () => deployment, () => overwrites, () => reclaim, () => objectStore);

        var result = await janitor.RunOnceAsync();

        Assert.True(result.FreedBytes > 0);
        Assert.False(File.Exists(snapshot));
        Assert.False(Directory.Exists(staleBackup));
        Assert.False(Directory.Exists(staleTemp));
    }

    [Fact]
    public async Task Janitor_StepFailure_DoesNotStopOtherSteps()
    {
        var staleBackup = SeedTransaction(BackupRoot, DeploymentStatus.Committed, daysAgo: 10);
        var deployment = NewDeployment();
        using var janitor = new TempCacheJanitor(NullLogger<TempCacheJanitor>.Instance,
            () => deployment,
            () => throw new InvalidOperationException("生成物仓库起不来"),
            () => throw new InvalidOperationException("回收服务起不来"),
            () => throw new InvalidOperationException("对象仓库起不来"));

        await janitor.RunOnceAsync();

        Assert.False(Directory.Exists(staleBackup));
    }

    // ─── 辅助 ───

    private DeploymentService NewDeployment()
    {
        var states = new DeploymentStateStore(NullLogger<DeploymentStateStore>.Instance, Path.Combine(_root, "State"));
        return _deployment = new DeploymentService(NullLogger<DeploymentService>.Instance,
            [new CopyBackend(NullLogger<CopyBackend>.Instance)], null!, states, BackupRoot, [LegacyBackupRoot]);
    }

    private OverwriteStore NewOverwriteStore()
        => new(NullLogger<OverwriteStore>.Instance, null!, null!, DataDir, OverwriteRoot);

    private string SeedTransaction(string root, DeploymentStatus status, int daysAgo, bool withState = true)
    {
        var dir = Path.Combine(root, Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        var completed = DateTime.Now.AddDays(-daysAgo);
        var state = withState
            ? new DeploymentState
            {
                HostGameName = "Test",
                GameRootPath = Path.Combine(_root, "Game"),
                ModRootPath = Path.Combine(_root, "Game", "Mods"),
            }
            : null;
        var transaction = new DeploymentTransaction
        {
            HostGameName = "Test",
            BackupDirectory = dir,
            Status = status,
            CompletedAt = completed,
            StateAfter = state,
        };
        File.WriteAllText(Path.Combine(dir, "transaction.json"), JsonSerializer.Serialize(transaction, TxJson));
        File.WriteAllText(Path.Combine(dir, "payload.pak"), new string('x', 4096));
        Directory.SetCreationTimeUtc(dir, completed.ToUniversalTime());
        return dir;
    }

    private string SeedOverwriteFile(string game, string typeFolder, string fileName)
    {
        var dir = Path.Combine(OverwriteRoot, game, typeFolder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, new string('x', 4096));
        return path;
    }

    private void SeedOverwriteIndex(string game, params GeneratedArtifact[] artifacts)
        => File.WriteAllText(Path.Combine(DataDir, $"{game}_overwrites.json"), JsonConvert.SerializeObject(artifacts));

    private static GeneratedArtifact Artifact(GeneratedArtifactType type, string relativePath) => new()
    {
        RelativePath = relativePath,
        DisplayName = "artifact",
        Type = type,
        Status = GeneratedArtifactStatus.Active,
        FileSize = 4096,
    };
}
