using UEModManager.Services.Steam;

namespace UEModManager.Core.Tests.Services.Steam;

public class SteamInstallLocatorTests
{
    [Theory]
    [InlineData(@"E:\SteamLibrary\steamapps\common\Game", @"E:\SteamLibrary\steamapps", "Game")]
    [InlineData(@"E:\SteamLibrary\steamapps\common\Game\", @"E:\SteamLibrary\steamapps", "Game")]
    [InlineData(@"E:\SteamLibrary\steamapps\common\Game\sub\Game.exe", @"E:\SteamLibrary\steamapps", "Game")]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\My Game\Binaries\Win64\G.exe",
        @"C:\Program Files (x86)\Steam\steamapps", "My Game")]
    [InlineData("d:/lib/SteamApps/Common/Game/bin", @"d:\lib\SteamApps", "Game")]
    [InlineData(@"\\nas\games\steamapps\common\Game", @"\\nas\games\steamapps", "Game")]
    public void Locate_PathInsideSteamLibrary_ReturnsSteamAppsAndInstallDir(
        string path, string steamApps, string installDir)
    {
        var location = SteamInstallLocator.Locate(path);

        Assert.NotNull(location);
        Assert.Equal(steamApps, location!.SteamAppsDirectory);
        Assert.Equal(installDir, location.InstallDir);
        Assert.Equal(steamApps + @"\common\" + installDir, location.InstallPath, ignoreCase: true);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"D:\Games\Game")]
    [InlineData(@"D:\Epic Games\Game\Binaries\Win64")]
    [InlineData(@"E:\SteamLibrary\steamapps")]
    [InlineData(@"E:\SteamLibrary\steamapps\common")]
    [InlineData(@"E:\SteamLibrary\steamapps\common\")]
    [InlineData(@"E:\SteamLibrary\steamapps\workshop\content\123")]
    [InlineData(@"E:\SteamLibrary\steamapps\common\..\x")]
    [InlineData(@"steamapps\common\Game")]
    public void Locate_PathOutsideSteamLibrary_ReturnsNull(string? path)
    {
        Assert.Null(SteamInstallLocator.Locate(path));
    }

    [Fact]
    public void Locate_LibraryItselfUnderAFolderNamedSteamApps_UsesTheInnermostMatch()
    {
        var location = SteamInstallLocator.Locate(@"D:\steamapps\common\Lib\steamapps\common\Game\bin");

        Assert.NotNull(location);
        Assert.Equal(@"D:\steamapps\common\Lib\steamapps", location!.SteamAppsDirectory);
        Assert.Equal("Game", location.InstallDir);
    }

    [Fact]
    public void FindOwner_MatchesInstallDirIgnoringCase()
    {
        var location = SteamInstallLocator.Locate(@"E:\Lib\steamapps\common\GameDir\x")!;
        var manifests = new[]
        {
            new SteamAppManifest(10, "Other", "OtherDir"),
            new SteamAppManifest(20, "The Game", "gamedir"),
        };

        var owner = SteamInstallLocator.FindOwner(location, manifests);

        Assert.Equal(20u, owner!.AppId);
    }

    [Fact]
    public void FindOwner_DoesNotMatchByPrefix()
    {
        // "Game" 是 "Game Demo" 的前缀：整段相等才算，否则会让 Steam 去启动试玩版
        var location = SteamInstallLocator.Locate(@"E:\Lib\steamapps\common\Game")!;

        Assert.Null(SteamInstallLocator.FindOwner(location, new[] { new SteamAppManifest(30, "Demo", "Game Demo") }));
    }

    [Fact]
    public void FindOwner_SkipsManifestsWithoutAppIdAndNullEntries()
    {
        var location = SteamInstallLocator.Locate(@"E:\Lib\steamapps\common\Game")!;
        var manifests = new SteamAppManifest?[]
        {
            null,
            new SteamAppManifest(0, "Broken", "Game"),
            new SteamAppManifest(40, "Game", "Game"),
        };

        Assert.Equal(40u, SteamInstallLocator.FindOwner(location, manifests)!.AppId);
    }

    [Fact]
    public void FindOwner_SameDirClaimedByDifferentApps_ReturnsNull()
    {
        var location = SteamInstallLocator.Locate(@"E:\Lib\steamapps\common\Game")!;
        var manifests = new[]
        {
            new SteamAppManifest(50, "Game", "Game"),
            new SteamAppManifest(51, "Game Beta", "Game"),
        };

        Assert.Null(SteamInstallLocator.FindOwner(location, manifests));
    }

    [Fact]
    public void FindOwner_SameAppListedTwice_StillMatches()
    {
        var location = SteamInstallLocator.Locate(@"E:\Lib\steamapps\common\Game")!;
        var manifests = new[]
        {
            new SteamAppManifest(60, "Game", "Game"),
            new SteamAppManifest(60, "Game", "Game"),
        };

        Assert.Equal(60u, SteamInstallLocator.FindOwner(location, manifests)!.AppId);
    }

    [Fact]
    public void FindOwner_NoManifests_ReturnsNull()
    {
        var location = SteamInstallLocator.Locate(@"E:\Lib\steamapps\common\Game")!;

        Assert.Null(SteamInstallLocator.FindOwner(location, Array.Empty<SteamAppManifest>()));
    }

    [Fact]
    public void FindOwner_NullArguments_Throw()
    {
        var location = SteamInstallLocator.Locate(@"E:\Lib\steamapps\common\Game")!;

        Assert.Throws<ArgumentNullException>(() => SteamInstallLocator.FindOwner(null!, Array.Empty<SteamAppManifest>()));
        Assert.Throws<ArgumentNullException>(() => SteamInstallLocator.FindOwner(location, null!));
    }

    [Fact]
    public void LocateThenFindOwner_WithRealManifestText_YieldsAppId()
    {
        const string acf = "\"AppState\"\n{\n\t\"appid\"\t\t\"123450\"\n\t\"name\"\t\t\"Sample\"\n\t\"installdir\"\t\t\"Sample\"\n}\n";
        var manifest = SteamAppManifest.Parse(acf, "appmanifest_123450.acf");
        var location = SteamInstallLocator.Locate(@"E:\SteamLibrary\steamapps\common\Sample\steam\Sample.exe")!;

        Assert.Equal(123450u, SteamInstallLocator.FindOwner(location, new[] { manifest })!.AppId);
    }
}
