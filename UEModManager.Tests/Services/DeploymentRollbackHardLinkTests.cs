using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Models;
using UEModManager.Services;
using UEModManager.Services.Backends;

namespace UEModManager.Tests.Services;

/// <summary>
/// 回滚不得穿透硬链接污染仓库源文件。
///
/// <para>
/// 事故形态：硬链接模式下部署一个要替换游戏原有文件的 MOD，游戏原文件先被备份走，
/// 目标位置变成指向仓库 MOD 文件的硬链接；此时部署失败触发回滚，若回滚直接
/// <c>File.Copy(backup, target, overwrite: true)</c>，Win32 的 <c>CREATE_ALWAYS</c>
/// 会**原地截断并写入既有文件**，而硬链接的两端是同一份数据 ——
/// 仓库里那个 MOD 文件的内容就被游戏原版文件覆盖掉了。用户以为仓库是干净备份。
/// </para>
///
/// <para>
/// 这里刻意建**真实的硬链接**并跑**真实的 <see cref="DeploymentService.RollbackAsync"/></c>：
/// 把文件系统 mock 掉，要防的那部分正好就测不到了。
/// </para>
/// </summary>
public sealed class DeploymentRollbackHardLinkTests : IDisposable
{
    private readonly string _root;
    private readonly string _repoDir;
    private readonly string _gameDir;
    private readonly string _backupDir;

    private const string ModContent = "这是仓库里的 MOD 文件内容";
    private const string OriginalGameContent = "这是游戏原版文件内容";

    public DeploymentRollbackHardLinkTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "uemm-rollback-" + Guid.NewGuid().ToString("N"));
        _repoDir = Path.Combine(_root, "Repository");
        _gameDir = Path.Combine(_root, "Game", "Content", "Paks", "~mods");
        _backupDir = Path.Combine(_root, "Backups");
        Directory.CreateDirectory(_repoDir);
        Directory.CreateDirectory(_gameDir);
        Directory.CreateDirectory(_backupDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 临时目录清理失败无所谓 */ }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    private static DeploymentService NewService()
    {
        // OverwriteStore 只在 ExecuteAsync 里用到（DeploymentService.cs:219），
        // RollbackAsync 全程不碰它，所以这里传 null! 以避开它那条很长的依赖链。
        var backends = new List<IDeploymentBackend>
        {
            new CopyBackend(NullLogger<CopyBackend>.Instance),
            new HardLinkBackend(NullLogger<HardLinkBackend>.Instance),
        };
        return new DeploymentService(NullLogger<DeploymentService>.Instance, backends, null!);
    }

    private DeploymentTransaction BuildFailedReplaceTransaction(string targetPath, string backupPath) => new()
    {
        HostGameName = "测试游戏",
        BackupDirectory = _backupDir,
        BackendType = DeploymentBackendType.HardLink,
        Status = DeploymentStatus.Failed,
        TotalOperations = 1,
        ExecutedOperations =
        [
            new DeploymentOperation
            {
                Type = DeploymentOperationType.Replace,
                PackageKey = "test-pkg",
                PackageDisplayName = "测试 MOD",
                TargetPath = targetPath,
                BackupPath = backupPath,
            }
        ],
    };

    [Fact]
    public async Task 硬链接目标回滚后_仓库源文件内容不变()
    {
        // 仓库里的 MOD 文件
        var repoSource = Path.Combine(_repoDir, "mod.pak");
        File.WriteAllText(repoSource, ModContent);

        // 游戏目录里的目标 = 指向仓库源文件的硬链接（这是 HardLinkBackend 部署后的状态）
        var target = Path.Combine(_gameDir, "mod.pak");
        var linked = CreateHardLink(target, repoSource, IntPtr.Zero);
        Assert.True(linked,
            $"建立硬链接失败（Win32Error={Marshal.GetLastWin32Error()}）——" +
            "本测试必须在真实硬链接上运行，否则它测不到要防的那件事");

        // 部署前备份下来的游戏原版文件
        var backup = Path.Combine(_backupDir, "mod.pak.bak");
        File.WriteAllText(backup, OriginalGameContent);

        var outcome = await NewService().RollbackAsync(BuildFailedReplaceTransaction(target, backup));

        // 回滚该做的事：把游戏目录恢复成原版
        Assert.Equal(OriginalGameContent, File.ReadAllText(target));

        // 回滚绝不该做的事：动到仓库里的 MOD 文件
        Assert.Equal(ModContent, File.ReadAllText(repoSource));
        Assert.NotNull(outcome);
    }

    /// <summary>目标是普通文件（Copy 模式部署）时，回滚行为不应受修复影响。</summary>
    [Fact]
    public async Task 普通文件目标_回滚照常恢复()
    {
        var target = Path.Combine(_gameDir, "plain.pak");
        File.WriteAllText(target, "被 MOD 覆盖后的内容");

        var backup = Path.Combine(_backupDir, "plain.pak.bak");
        File.WriteAllText(backup, OriginalGameContent);

        await NewService().RollbackAsync(BuildFailedReplaceTransaction(target, backup));

        Assert.Equal(OriginalGameContent, File.ReadAllText(target));
    }

    /// <summary>
    /// 硬链接的两端在回滚后必须**断开**：目标已是独立的普通文件，
    /// 之后再写目标也不会波及仓库。
    /// </summary>
    [Fact]
    public async Task 回滚后目标与仓库源文件已断开()
    {
        var repoSource = Path.Combine(_repoDir, "mod2.pak");
        File.WriteAllText(repoSource, ModContent);

        var target = Path.Combine(_gameDir, "mod2.pak");
        Assert.True(CreateHardLink(target, repoSource, IntPtr.Zero), "建立硬链接失败");

        var backup = Path.Combine(_backupDir, "mod2.pak.bak");
        File.WriteAllText(backup, OriginalGameContent);

        await NewService().RollbackAsync(BuildFailedReplaceTransaction(target, backup));

        // 回滚之后再改目标，仓库源文件仍不受影响
        File.WriteAllText(target, "回滚之后又被改了");
        Assert.Equal(ModContent, File.ReadAllText(repoSource));
    }
}
