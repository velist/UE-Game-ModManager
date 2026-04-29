using UEModManager.Models;
using UEModManager.Services.Profile;

namespace UEModManager.Core.Tests.Services.Profile;

public class LegacyProfileMigratorTests
{
    // ─── DetermineKind ───

    [Fact]
    public void DetermineKind_IsPluginTrue_ReturnsPluginRegardlessOfName()
    {
        var entry = new LegacyModEntry("anything.pak", IsEnabled: true, IsPlugin: true, null);
        Assert.Equal(PackageKind.Plugin, LegacyProfileMigrator.DetermineKind(entry));
    }

    [Theory]
    [InlineData("conf.ini")]
    [InlineData("CONF.INI")]
    [InlineData("conf.cfg")]
    [InlineData("conf.json")]   // 注意：保留 v1.8 行为，迁移时 .json 都视为 Config
    [InlineData("conf.yaml")]
    public void DetermineKind_ConfigExtensions(string name)
    {
        var entry = new LegacyModEntry(name, false, false, null);
        Assert.Equal(PackageKind.Config, LegacyProfileMigrator.DetermineKind(entry));
    }

    [Theory]
    [InlineData("file.pak")]
    [InlineData("ModName")] // 文件夹形式（最常见的 v1.8 RealName）
    [InlineData("data.unknown")]
    public void DetermineKind_DefaultIsMod(string name)
    {
        var entry = new LegacyModEntry(name, false, false, null);
        Assert.Equal(PackageKind.Mod, LegacyProfileMigrator.DetermineKind(entry));
    }

    [Fact]
    public void DetermineKind_NullThrows()
        => Assert.Throws<ArgumentNullException>(() => LegacyProfileMigrator.DetermineKind(null!));

    // ─── ToProfileEntry ───

    [Fact]
    public void ToProfileEntry_BasicFieldsCarry()
    {
        var entry = new LegacyModEntry("ModA", IsEnabled: true, IsPlugin: false, null);

        var p = LegacyProfileMigrator.ToProfileEntry(entry, priority: 7);

        Assert.Equal("ModA", p.PackageKey);
        Assert.True(p.IsEnabled);
        Assert.Equal(7, p.Priority);
        Assert.Equal(PackageKind.Mod, p.Kind);
        Assert.Null(p.PluginTargetPath); // 非 Plugin 不带 path
    }

    [Fact]
    public void ToProfileEntry_Plugin_PreservesTargetPath()
    {
        var entry = new LegacyModEntry("PlugA", true, IsPlugin: true, "Engine/Plugins");

        var p = LegacyProfileMigrator.ToProfileEntry(entry, priority: 0);

        Assert.Equal(PackageKind.Plugin, p.Kind);
        Assert.Equal("Engine/Plugins", p.PluginTargetPath);
    }

    [Fact]
    public void ToProfileEntry_NonPluginIgnoresTargetPath()
    {
        // 即便传入 PluginTargetPath，非插件也应丢弃
        var entry = new LegacyModEntry("ModA", true, IsPlugin: false, "Engine/Plugins");

        var p = LegacyProfileMigrator.ToProfileEntry(entry, 0);

        Assert.Null(p.PluginTargetPath);
    }

    [Fact]
    public void ToProfileEntry_NullThrows()
        => Assert.Throws<ArgumentNullException>(() => LegacyProfileMigrator.ToProfileEntry(null!, 0));

    // ─── BuildPackagesFromLegacyMods ───

    [Fact]
    public void Build_Empty_ReturnsEmpty()
    {
        var result = LegacyProfileMigrator.BuildPackagesFromLegacyMods(Array.Empty<LegacyModEntry>());
        Assert.Empty(result);
    }

    [Fact]
    public void Build_PrioritiesAreSequentialFromZero()
    {
        var mods = new[]
        {
            new LegacyModEntry("a", true, false, null),
            new LegacyModEntry("b", false, false, null),
            new LegacyModEntry("c", true, true, "Plugins"),
        };

        var result = LegacyProfileMigrator.BuildPackagesFromLegacyMods(mods);

        Assert.Equal(new[] { 0, 1, 2 }, result.Select(p => p.Priority));
        Assert.Equal(new[] { "a", "b", "c" }, result.Select(p => p.PackageKey));
    }

    [Fact]
    public void Build_PreservesEnabledAndKind()
    {
        var mods = new[]
        {
            new LegacyModEntry("settings.ini", true, false, null),
            new LegacyModEntry("plug.dll", false, true, "Plugins"),
        };

        var result = LegacyProfileMigrator.BuildPackagesFromLegacyMods(mods);

        Assert.Equal(PackageKind.Config, result[0].Kind);
        Assert.True(result[0].IsEnabled);
        Assert.Equal(PackageKind.Plugin, result[1].Kind);
        Assert.False(result[1].IsEnabled);
    }

    [Fact]
    public void Build_NullThrows()
        => Assert.Throws<ArgumentNullException>(
            () => LegacyProfileMigrator.BuildPackagesFromLegacyMods(null!));
}
