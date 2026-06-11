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

    private static Package MakePlugin(string key = "plugA", string? targetRootPath = "Engine/Plugins")
        => new()
        {
            PackageKey = key,
            DisplayName = key.ToUpperInvariant(),
            Kind = PackageKind.Plugin,
            HostGameName = "demo",
            TargetRootPath = targetRootPath,
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
        Assert.Equal("/g/game/Saved/Config/WindowsNoEditor/cfgA/Engine.ini", normalized);
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
