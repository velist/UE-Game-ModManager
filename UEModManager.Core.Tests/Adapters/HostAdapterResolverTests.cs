using UEModManager.Adapters;
using UEModManager.Models;

namespace UEModManager.Core.Tests.Adapters;

public class HostAdapterResolverTests
{
    private sealed class FakeAdapter : IHostAdapter
    {
        public string AdapterKey { get; init; } = "fake";
        public string DisplayName { get; init; } = "Fake";
        public AdapterCapabilities Capabilities { get; init; } = new();
        public EngineType EngineType { get; init; } = EngineType.Unknown;
        public IReadOnlySet<string> ModFileExtensions { get; init; } = new HashSet<string>();
        public IReadOnlySet<string> DirectImportExtensions { get; init; } = new HashSet<string>();
        public string FileDialogFilter { get; init; } = "";
        public IReadOnlyList<string> DefaultModPathPatterns { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> GroupPriorityExtensions { get; init; } = Array.Empty<string>();
        public DeploymentBackendType RecommendedBackend { get; init; } = DeploymentBackendType.Copy;

        public Func<string, EngineType?, bool>? CanHandleFn { get; init; }
        public bool CanHandle(string gameName, EngineType? engineType = null)
            => CanHandleFn?.Invoke(gameName, engineType) ?? false;

        public IReadOnlyList<string> GetSearchKeywords(string gameName) => Array.Empty<string>();
        public IReadOnlyList<string> GetExecutableKeywords(string gameName) => Array.Empty<string>();
        public string GetDeployTargetPath(string gameRootPath, string gameName) => gameRootPath;
        public string GetBackupPath(string gameRootPath, string gameName) => gameRootPath;
        public bool ShouldSkipDirectory(string directoryName, string gameName) => false;
    }

    private static readonly IHostAdapter Fallback = new FakeAdapter
    {
        AdapterKey = "generic-overlay",
        DisplayName = "Generic Fallback",
    };

    [Fact]
    public void CanHandleHits_ReturnsThatAdapter()
    {
        var unreal = new FakeAdapter
        {
            AdapterKey = "unreal",
            EngineType = EngineType.UnrealEngine,
            CanHandleFn = (game, _) => game == "剑星",
        };

        var resolved = HostAdapterResolver.Resolve(
            new[] { unreal, Fallback }, Fallback, "剑星");

        Assert.Same(unreal, resolved);
    }

    [Fact]
    public void NoCanHandleHit_EngineMatch_ReturnsByEngine()
    {
        var unreal = new FakeAdapter
        {
            AdapterKey = "unreal",
            EngineType = EngineType.UnrealEngine,
            CanHandleFn = (_, _) => false,
        };

        var resolved = HostAdapterResolver.Resolve(
            new[] { unreal, Fallback }, Fallback, "未知游戏", EngineType.UnrealEngine);

        Assert.Same(unreal, resolved);
    }

    [Fact]
    public void NoMatchAtAll_ReturnsFallback()
    {
        var unreal = new FakeAdapter
        {
            AdapterKey = "unreal",
            EngineType = EngineType.UnrealEngine,
            CanHandleFn = (_, _) => false,
        };

        var resolved = HostAdapterResolver.Resolve(
            new[] { unreal, Fallback }, Fallback, "未知", EngineType.Unity);

        Assert.Same(Fallback, resolved);
    }

    [Fact]
    public void Fallback_InAdaptersList_IsSkippedDuringMatching()
    {
        // fallback 自己在 adapters 列表里，但不应被 CanHandle 阶段选中
        var fallbackTrap = new FakeAdapter
        {
            AdapterKey = "generic-overlay", // 与 Fallback 同 key
            CanHandleFn = (_, _) => true, // 永远说能处理
        };

        var resolved = HostAdapterResolver.Resolve(
            new[] { fallbackTrap, Fallback }, Fallback, "any");

        // 因为 AdapterKey 相同，被跳过 → 没有 CanHandle 命中 → 返回 fallback
        Assert.Same(Fallback, resolved);
    }

    [Fact]
    public void EngineUnknown_DoesNotTriggerEngineMatch()
    {
        var unreal = new FakeAdapter
        {
            AdapterKey = "unreal",
            EngineType = EngineType.UnrealEngine,
            CanHandleFn = (_, _) => false,
        };

        var resolved = HostAdapterResolver.Resolve(
            new[] { unreal, Fallback }, Fallback, "x", EngineType.Unknown);

        // engineType=Unknown 不算条件，且 CanHandle 不命中 → 走 fallback
        Assert.Same(Fallback, resolved);
    }

    [Fact]
    public void EngineNull_DoesNotTriggerEngineMatch()
    {
        var unreal = new FakeAdapter
        {
            AdapterKey = "unreal",
            EngineType = EngineType.UnrealEngine,
            CanHandleFn = (_, _) => false,
        };

        var resolved = HostAdapterResolver.Resolve(
            new[] { unreal, Fallback }, Fallback, "x", null);

        Assert.Same(Fallback, resolved);
    }

    [Fact]
    public void CanHandleWinsOverEngineMatch()
    {
        // 两个 adapter 都标识 Unreal，但只有 godot 的 CanHandle 命中
        var unreal = new FakeAdapter
        {
            AdapterKey = "unreal",
            EngineType = EngineType.UnrealEngine,
            CanHandleFn = (_, _) => false,
        };
        var godot = new FakeAdapter
        {
            AdapterKey = "godot",
            EngineType = EngineType.Unknown,
            CanHandleFn = (game, _) => game == "Brotato",
        };

        var resolved = HostAdapterResolver.Resolve(
            new[] { unreal, godot, Fallback }, Fallback, "Brotato", EngineType.UnrealEngine);

        // CanHandle 阶段先命中 godot，引擎阶段不会再走
        Assert.Same(godot, resolved);
    }

    [Fact]
    public void FirstCanHandleHit_Wins()
    {
        var first = new FakeAdapter { AdapterKey = "first", CanHandleFn = (_, _) => true };
        var second = new FakeAdapter { AdapterKey = "second", CanHandleFn = (_, _) => true };

        var resolved = HostAdapterResolver.Resolve(
            new[] { first, second, Fallback }, Fallback, "any");

        Assert.Same(first, resolved);
    }

    [Fact]
    public void NullAdapters_Throws()
        => Assert.Throws<ArgumentNullException>(() =>
            HostAdapterResolver.Resolve(null!, Fallback, "x"));

    [Fact]
    public void NullFallback_Throws()
        => Assert.Throws<ArgumentNullException>(() =>
            HostAdapterResolver.Resolve(Array.Empty<IHostAdapter>(), null!, "x"));
}
