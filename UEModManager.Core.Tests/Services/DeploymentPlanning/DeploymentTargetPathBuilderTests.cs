using UEModManager.Models;
using UEModManager.Services.DeploymentPlanning;

namespace UEModManager.Core.Tests.Services.DeploymentPlanning;

public class DeploymentTargetPathBuilderTests
{
    private static PackageArtifact MakeArtifact(string relTarget, ArtifactType type = ArtifactType.ModFile)
        => new()
        {
            RelativeSourcePath = $"{relTarget}.src",
            RelativeTargetPath = relTarget,
            FileName = relTarget,
            ArtifactType = type,
            FileSize = 100,
        };

    private static Package MakeMod(string key = "modA")
        => new()
        {
            PackageKey = key,
            DisplayName = key.ToUpperInvariant(),
            Kind = PackageKind.Mod,
            HostGameName = "demo",
        };

    private static Package MakePlugin(
        string key = "plugA", string? targetRootPath = "Engine/Plugins", bool preservesInstallLayout = false)
        => new()
        {
            PackageKey = key,
            DisplayName = key.ToUpperInvariant(),
            Kind = PackageKind.Plugin,
            HostGameName = "demo",
            TargetRootPath = targetRootPath,
            PreservesInstallLayout = preservesInstallLayout,
        };

    private static Package MakeConfig(string key = "cfgA", string? targetRootPath = "Saved/Config/WindowsNoEditor")
        => new()
        {
            PackageKey = key,
            DisplayName = key.ToUpperInvariant(),
            Kind = PackageKind.Config,
            HostGameName = "demo",
            TargetRootPath = targetRootPath,
        };

    [Fact]
    public void ComputeTargetPath_Mod_UsesModPathAndPackageKey()
    {
        var artifact = MakeArtifact("file.pak");
        var package = MakeMod("modA");

        var path = DeploymentTargetPathBuilder.ComputeTargetPath(
            artifact, package, entry: null,
            modPath: "/g/mods", gamePath: "/g/game");

        var normalized = path.Replace('\\', '/');
        Assert.Equal("/g/mods/modA/file.pak", normalized);
    }

    [Fact]
    public void ComputeTargetPath_PluginWithBareFile_KeepsPackageDirectory()
    {
        // 裸文件名没有方位信息：同名 dll 必须靠包目录隔离，否则互相覆盖。
        var artifact = MakeArtifact("dll.dll", ArtifactType.PluginFile);
        var package = MakePlugin("plugA", "Engine/Plugins");
        var entry = new ProfilePackageEntry { PackageKey = "plugA", TargetRootPath = null };

        var path = DeploymentTargetPathBuilder.ComputeTargetPath(
            artifact, package, entry, modPath: "/g/mods", gamePath: "/g/game");

        Assert.Equal("/g/game/Engine/Plugins/plugA/dll.dll", path.Replace('\\', '/'));
    }

    [Fact]
    public void ComputeTargetPath_PluginWithNestedPath_OmitsPackageDirectory()
    {
        // 目录导入的插件：相对路径自带安装位置（ue4ss/Mods/...），
        // 再套一层包目录会让 UE4SS 找不到文件。
        var artifact = MakeArtifact("ue4ss/Mods/mods.json", ArtifactType.PluginFile);
        var package = MakePlugin("plugA", targetRootPath: null);

        var path = DeploymentTargetPathBuilder.ComputeTargetPath(
            artifact, package, entry: null, modPath: "/g/mods", gamePath: "/g/game/Binaries/Win64");

        Assert.Equal("/g/game/Binaries/Win64/ue4ss/Mods/mods.json", path.Replace('\\', '/'));
    }

    [Fact]
    public void ComputeTargetPath_LayoutPreservingPlugin_BareFileStaysAtGameRoot()
    {
        // 目录 / 压缩包整体导入的插件包（PreservesInstallLayout = true）：
        // RelativeTargetPath 就是"相对游戏根的安装位置"，裸文件名即"必须在游戏根"。
        // 现场症状：UE4SS 的 dwmapi.dll 与 CSharpLoader 的 version.dll 被套进包目录，
        // 落到 .../Win64/超级筋斗云/dwmapi.dll，Windows 加载器按约定只认 exe 同目录，
        // 三个插件 MOD 因此一个都没挂上。
        var artifact = MakeArtifact("version.dll", ArtifactType.PluginFile);
        var package = MakePlugin("plugA", targetRootPath: "", preservesInstallLayout: true);

        var path = DeploymentTargetPathBuilder.ComputeTargetPath(
            artifact, package, entry: null, modPath: "/g/mods", gamePath: "/g/game/Binaries/Win64");

        Assert.Equal("/g/game/Binaries/Win64/version.dll", path.Replace('\\', '/'));
    }

    [Fact]
    public void ComputeTargetPath_LayoutPreservingPlugin_NestedPathStaysAtGameRoot()
    {
        // 同一个包的带层级文件（ue4ss/Mods/mods.json）也走"不套包目录"这条路。
        var artifact = MakeArtifact("ue4ss/Mods/mods.json", ArtifactType.PluginFile);
        var package = MakePlugin("超级筋斗云", targetRootPath: "", preservesInstallLayout: true);

        var path = DeploymentTargetPathBuilder.ComputeTargetPath(
            artifact, package, entry: null, modPath: "/g/mods", gamePath: "/g/game/Binaries/Win64");

        Assert.Equal("/g/game/Binaries/Win64/ue4ss/Mods/mods.json", path.Replace('\\', '/'));
    }

    [Fact]
    public void ComputeTargetPath_LegacyPluginWithoutLayoutFlag_BareFileStillIsolated()
    {
        // 旧数据兼容：PreservesInstallLayout 缺省为 false，手动导入的单文件插件包
        // 行为不变，仍靠包目录隔离避免两个同名 dll 互相覆盖。
        var artifact = MakeArtifact("dll.dll", ArtifactType.PluginFile);
        var package = MakePlugin("plugA", targetRootPath: "");

        var path = DeploymentTargetPathBuilder.ComputeTargetPath(
            artifact, package, entry: null, modPath: "/g/mods", gamePath: "/g/game/Binaries/Win64");

        Assert.Equal("/g/game/Binaries/Win64/plugA/dll.dll", path.Replace('\\', '/'));
    }

    [Fact]
    public void ComputeTargetPath_PluginWithBareDwmapiAndGameRoot_StaysAtGameRoot()
    {
        // T1 的直接回归：gamePath 就是 .../b1/Binaries/Win64，裸 dwmapi.dll 必须落在这里。
        var artifact = MakeArtifact("dwmapi.dll", ArtifactType.PluginFile);
        var package = MakePlugin("超级识破", targetRootPath: "", preservesInstallLayout: true);

        var path = DeploymentTargetPathBuilder.ComputeTargetPath(
            artifact, package, entry: null, modPath: "/g/mods", gamePath: "/g/game/Binaries/Win64");

        Assert.Equal("/g/game/Binaries/Win64/dwmapi.dll", path.Replace('\\', '/'));
        Assert.DoesNotContain("超级识破", path);
    }

    [Fact]
    public void ComputeTargetPath_Plugin_PreferEntryPluginPath()
    {
        var artifact = MakeArtifact("dll.dll", ArtifactType.PluginFile);
        var package = MakePlugin("plugA", "Engine/Plugins");
        var entry = new ProfilePackageEntry
        {
            PackageKey = "plugA",
            TargetRootPath = "GameMods/Plugins",
        };

        var path = DeploymentTargetPathBuilder.ComputeTargetPath(
            artifact, package, entry,
            modPath: "/g/mods", gamePath: "/g/game");

        var normalized = path.Replace('\\', '/');
        Assert.Equal("/g/game/GameMods/Plugins/plugA/dll.dll", normalized);
    }

    [Fact]
    public void ComputeTargetPath_Plugin_FallsBackToPackagePluginPath()
    {
        var artifact = MakeArtifact("dll.dll", ArtifactType.PluginFile);
        var package = MakePlugin("plugA", "Engine/Plugins");
        var entry = new ProfilePackageEntry { PackageKey = "plugA", TargetRootPath = null };

        var path = DeploymentTargetPathBuilder.ComputeTargetPath(
            artifact, package, entry,
            modPath: "/g/mods", gamePath: "/g/game");

        var normalized = path.Replace('\\', '/');
        Assert.Equal("/g/game/Engine/Plugins/plugA/dll.dll", normalized);
    }

    [Fact]
    public void ComputeTargetPath_Plugin_BothPluginPathsNull_UsesEmptySegment()
    {
        var artifact = MakeArtifact("dll.dll", ArtifactType.PluginFile);
        var package = MakePlugin("plugA", null);

        var path = DeploymentTargetPathBuilder.ComputeTargetPath(
            artifact, package, entry: null,
            modPath: "/g/mods", gamePath: "/g/game");

        var normalized = path.Replace('\\', '/');
        Assert.EndsWith("/plugA/dll.dll", normalized);
        Assert.StartsWith("/g/game", normalized);
    }

    [Fact]
    public void ComputeTargetPath_Config_UsesTargetRootPath()
    {
        var artifact = MakeArtifact("Engine.ini", ArtifactType.ConfigFile);
        var package = MakeConfig("cfgA", "Saved/Config/WindowsNoEditor");

        var path = DeploymentTargetPathBuilder.ComputeTargetPath(
            artifact, package, entry: null,
            modPath: "/g/mods", gamePath: "/g/game");

        var normalized = path.Replace('\\', '/');
        Assert.Equal("/g/game/Saved/Config/WindowsNoEditor/Engine.ini", normalized);
    }

    [Fact]
    public void ComputeRelativeTargetPath_Mod_ReturnsPathRelativeToModPath()
    {
        var artifact = MakeArtifact("sub/file.pak");
        var package = MakeMod("modA");
        var modPath = "/g/mods";

        var abs = DeploymentTargetPathBuilder.ComputeTargetPath(
            artifact, package, null, modPath, "/g/game");
        var rel = DeploymentTargetPathBuilder.ComputeRelativeTargetPath(
            artifact, package, null, modPath, "/g/game", abs);

        var normalized = rel.Replace('\\', '/');
        Assert.Equal("modA/sub/file.pak", normalized);
    }

    [Fact]
    public void ComputeRelativeTargetPath_Plugin_RelativeToGamePathPlusPluginPath()
    {
        var artifact = MakeArtifact("a.dll", ArtifactType.PluginFile);
        var package = MakePlugin("plugA", "Engine/Plugins");
        var gamePath = "/g/game";

        var abs = DeploymentTargetPathBuilder.ComputeTargetPath(
            artifact, package, null, "/g/mods", gamePath);
        var rel = DeploymentTargetPathBuilder.ComputeRelativeTargetPath(
            artifact, package, null, "/g/mods", gamePath, abs);

        var normalized = rel.Replace('\\', '/');
        Assert.Equal("plugA/a.dll", normalized);
    }
}
