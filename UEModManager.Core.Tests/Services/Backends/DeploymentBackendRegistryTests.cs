using UEModManager.Models;
using UEModManager.Services.Backends;

namespace UEModManager.Core.Tests.Services.Backends;

public class DeploymentBackendRegistryTests
{
    private sealed class FakeBackend : IDeploymentBackend
    {
        public FakeBackend(DeploymentBackendType type, string name = "fake")
        {
            Type = type;
            DisplayName = name;
        }

        public DeploymentBackendType Type { get; }
        public string DisplayName { get; }
        public Task<bool> CanUseAsync() => Task.FromResult(true);
        public Task DeployFileAsync(string sourcePath, string targetPath) => Task.CompletedTask;
        public Task RemoveFileAsync(string targetPath) => Task.CompletedTask;
    }

    private static FakeBackend Copy(string name = "copy")
        => new(DeploymentBackendType.Copy, name);

    [Fact]
    public void Build_IndexesBackendsByType()
    {
        var copy = Copy();
        var hardLink = new FakeBackend(DeploymentBackendType.HardLink);

        var registry = DeploymentBackendRegistry.Build(new IDeploymentBackend[] { copy, hardLink });

        Assert.Equal(2, registry.Count);
        Assert.Same(copy, registry[DeploymentBackendType.Copy]);
        Assert.Same(hardLink, registry[DeploymentBackendType.HardLink]);
    }

    [Fact]
    public void Build_LaterRegistrationWins()
    {
        // 内置后端在 App.xaml.cs 里先注册，自定义实现注册在后面即可替换掉同类型的内置实现。
        // samples 里的 SampleMirrorBackend 声明的正是 Copy 类型，靠的就是这条规则。
        var builtIn = Copy("内置");
        var custom = Copy("自定义");

        var registry = DeploymentBackendRegistry.Build(new IDeploymentBackend[] { builtIn, custom });

        Assert.Single(registry);
        Assert.Same(custom, registry[DeploymentBackendType.Copy]);
    }

    [Fact]
    public void Build_DuplicateType_ReportsWhoOverridesWhom()
    {
        // 两个实现完全可能类名相同（不同程序集里的 MirrorBackend），
        // 消息必须靠 DisplayName 才能说清谁覆盖了谁
        var messages = new List<string>();

        DeploymentBackendRegistry.Build(
            new IDeploymentBackend[] { Copy("内置"), Copy("自定义") },
            messages.Add);

        var message = Assert.Single(messages);
        Assert.Contains("内置", message);
        Assert.Contains("自定义", message);
        Assert.True(
            message.IndexOf("内置", StringComparison.Ordinal) < message.IndexOf("自定义", StringComparison.Ordinal),
            "被覆盖者应出现在覆盖者之前，否则消息读起来是反的");
    }

    [Fact]
    public void Build_NoDuplicates_ReportsNothing()
    {
        var messages = new List<string>();

        DeploymentBackendRegistry.Build(
            new IDeploymentBackend[] { Copy(), new FakeBackend(DeploymentBackendType.HardLink) },
            messages.Add);

        Assert.Empty(messages);
    }

    [Fact]
    public void Build_WithoutCopyBackend_Throws()
    {
        // Copy 是 GetBackend 的兜底目标，缺了它每次降级都会变成 KeyNotFoundException。
        // 这只可能来自 DI 装配失误，应该在装配阶段就炸掉而不是等到用户点部署。
        var ex = Assert.Throws<InvalidOperationException>(
            () => DeploymentBackendRegistry.Build(
                new IDeploymentBackend[] { new FakeBackend(DeploymentBackendType.HardLink) }));

        Assert.Contains("Copy", ex.Message);
    }

    [Fact]
    public void Build_Empty_Throws()
        => Assert.Throws<InvalidOperationException>(
            () => DeploymentBackendRegistry.Build(Array.Empty<IDeploymentBackend>()));

    [Fact]
    public void Build_Null_Throws()
        => Assert.Throws<ArgumentNullException>(() => DeploymentBackendRegistry.Build(null!));

    [Fact]
    public void Build_SkipsNullEntries()
    {
        var copy = Copy();

        var registry = DeploymentBackendRegistry.Build(new IDeploymentBackend?[] { null, copy, null }!);

        Assert.Same(copy, registry[DeploymentBackendType.Copy]);
    }

    [Fact]
    public void Build_UnknownBackendType_IsAccepted()
    {
        // 新增一种部署方式时只加枚举值与实现，注册表不该需要跟着改
        var copy = Copy();
        var symlink = new FakeBackend(DeploymentBackendType.Symlink);

        var registry = DeploymentBackendRegistry.Build(new IDeploymentBackend[] { copy, symlink });

        Assert.True(registry.ContainsKey(DeploymentBackendType.Symlink));
    }
}
