using UEModManager.Models;
using UEModManager.Services.DeploymentPlanning;

namespace UEModManager.Core.Tests.Services.DeploymentPlanning;

public class TogglePlanBuilderTests
{
    private static Package MakeMod(string key, params (string rel, ArtifactType type)[] artifacts)
        => new()
        {
            PackageKey = key,
            DisplayName = key.ToUpperInvariant(),
            Kind = PackageKind.Mod,
            HostGameName = "demo",
            Artifacts = artifacts.Select(a => new PackageArtifact
            {
                RelativeSourcePath = $"{key}/{a.rel}",
                RelativeTargetPath = a.rel,
                FileName = a.rel,
                ArtifactType = a.type,
                FileHash = "h",
                FileSize = 100,
            }).ToList(),
        };

    private static Func<string, bool> ExistsSet(params string[] existingPaths)
    {
        var set = new HashSet<string>(existingPaths
            .Select(p => p.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
        return path => set.Contains(path.Replace('\\', '/'));
    }

    [Fact]
    public void Enable_TargetAbsent_ProducesAdd()
    {
        var package = MakeMod("modA", ("a.pak", ArtifactType.ModFile));

        var ops = TogglePlanBuilder.BuildToggleOperations(
            package, entry: null,
            modPath: "/g/mods", gamePath: "/g/game",
            repositoryRoot: "/repo",
            enable: true,
            targetExists: ExistsSet());

        var op = Assert.Single(ops);
        Assert.Equal(DeploymentOperationType.Add, op.Type);
        Assert.Equal("modA", op.PackageKey);
        Assert.Equal("h", op.FileHash);
    }

    [Fact]
    public void Enable_TargetExists_ProducesReplace()
    {
        var package = MakeMod("modA", ("a.pak", ArtifactType.ModFile));

        var ops = TogglePlanBuilder.BuildToggleOperations(
            package, entry: null,
            modPath: "/g/mods", gamePath: "/g/game",
            repositoryRoot: "/repo",
            enable: true,
            targetExists: ExistsSet("/g/mods/modA/a.pak"));

        var op = Assert.Single(ops);
        Assert.Equal(DeploymentOperationType.Replace, op.Type);
    }

    [Fact]
    public void Disable_TargetExists_ProducesRemove()
    {
        var package = MakeMod("modA", ("a.pak", ArtifactType.ModFile));

        var ops = TogglePlanBuilder.BuildToggleOperations(
            package, entry: null,
            modPath: "/g/mods", gamePath: "/g/game",
            repositoryRoot: "/repo",
            enable: false,
            targetExists: ExistsSet("/g/mods/modA/a.pak"));

        var op = Assert.Single(ops);
        Assert.Equal(DeploymentOperationType.Remove, op.Type);
        Assert.Null(op.SourcePath); // Remove 不需要源
    }

    [Fact]
    public void Disable_TargetAbsent_NoOperation()
    {
        var package = MakeMod("modA", ("a.pak", ArtifactType.ModFile));

        var ops = TogglePlanBuilder.BuildToggleOperations(
            package, entry: null,
            modPath: "/g/mods", gamePath: "/g/game",
            repositoryRoot: "/repo",
            enable: false,
            targetExists: ExistsSet());

        Assert.Empty(ops);
    }

    [Fact]
    public void PreviewImage_IsSkipped()
    {
        var package = MakeMod("modA",
            ("preview.png", ArtifactType.PreviewImage),
            ("real.pak", ArtifactType.ModFile));

        var ops = TogglePlanBuilder.BuildToggleOperations(
            package, entry: null,
            modPath: "/g/mods", gamePath: "/g/game",
            repositoryRoot: "/repo",
            enable: true,
            targetExists: ExistsSet());

        var op = Assert.Single(ops);
        Assert.EndsWith("real.pak", op.TargetPath.Replace('\\', '/'));
    }

    [Fact]
    public void Enable_MultipleArtifacts_OperationPerArtifact()
    {
        var package = MakeMod("modA",
            ("a.pak", ArtifactType.ModFile),
            ("b.pak", ArtifactType.ModFile),
            ("c.ini", ArtifactType.ConfigFile));

        var ops = TogglePlanBuilder.BuildToggleOperations(
            package, entry: null,
            modPath: "/g/mods", gamePath: "/g/game",
            repositoryRoot: "/repo",
            enable: true,
            targetExists: ExistsSet("/g/mods/modA/b.pak"));

        Assert.Equal(3, ops.Count);
        Assert.Equal(DeploymentOperationType.Add, ops.First(o => o.RelativeTargetPath.EndsWith("a.pak")).Type);
        Assert.Equal(DeploymentOperationType.Replace, ops.First(o => o.RelativeTargetPath.EndsWith("b.pak")).Type);
        Assert.Equal(DeploymentOperationType.Add, ops.First(o => o.RelativeTargetPath.EndsWith("c.ini")).Type);
    }

    [Fact]
    public void Enable_SourcePath_IsRepositoryRootJoinSourceRel()
    {
        var package = MakeMod("modA", ("a.pak", ArtifactType.ModFile));

        var ops = TogglePlanBuilder.BuildToggleOperations(
            package, entry: null,
            modPath: "/g/mods", gamePath: "/g/game",
            repositoryRoot: "/repo",
            enable: true,
            targetExists: ExistsSet());

        var op = Assert.Single(ops);
        var normalized = op.SourcePath!.Replace('\\', '/');
        Assert.Equal("/repo/modA/a.pak", normalized);
    }

    [Fact]
    public void NullPackage_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            TogglePlanBuilder.BuildToggleOperations(
                null!, null, "/m", "/g", "/r", true, _ => false));
    }

    [Fact]
    public void NullTargetExists_Throws()
    {
        var package = MakeMod("modA");
        Assert.Throws<ArgumentNullException>(() =>
            TogglePlanBuilder.BuildToggleOperations(
                package, null, "/m", "/g", "/r", true, null!));
    }
}
