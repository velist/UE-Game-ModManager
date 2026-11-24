using UEModManager.Services.Steam;

namespace UEModManager.Core.Tests.Services.Steam;

public class SteamManifestParsingTests
{
    // ─── ValveKeyValues ───

    [Fact]
    public void Parse_NestedObjects_KeepsOrderAndValues()
    {
        var root = ValveKeyValues.Parse("\"a\"\n{\n\t\"x\"\t\t\"1\"\n\t\"b\"\n\t{\n\t\t\"y\"\t\"2\"\n\t}\n\t\"z\" \"3\"\n}\n");

        var a = root.Child("a");
        Assert.NotNull(a);
        Assert.True(a!.IsObject);
        Assert.Equal(new[] { "x", "b", "z" }, a.Children.Select(c => c.Key));
        Assert.Equal("1", a.GetString("x"));
        Assert.Equal("2", a.Child("b")!.GetString("y"));
        Assert.Equal("3", a.GetString("z"));
    }

    [Fact]
    public void Parse_KeyLookupIgnoresCase()
    {
        var root = ValveKeyValues.Parse("\"LibraryFolders\" { \"Path\" \"x\" }");

        Assert.Equal("x", root.Child("libraryfolders")!.GetString("path"));
    }

    [Fact]
    public void Parse_Escapes_AreUnescapedInsideQuotes()
    {
        var root = ValveKeyValues.Parse("\"k\" \"D:\\\\Steam Library\\\\a \\\"b\\\"\\tc\"");

        Assert.Equal("D:\\Steam Library\\a \"b\"\tc", root.GetString("k"));
    }

    [Fact]
    public void Parse_UnknownEscape_KeepsBackslash()
    {
        // 手改过的文件里会有没转义的路径：D:\Games 的 \G 不是合法转义，不能把反斜杠吞掉
        var root = ValveKeyValues.Parse("\"k\" \"D:\\Games\\Steam\"");

        Assert.Equal("D:\\Games\\Steam", root.GetString("k"));
    }

    [Fact]
    public void Parse_CommentsUnquotedTokensAndConditionals()
    {
        var root = ValveKeyValues.Parse("// 头注释\r\nAppState // 行尾注释\r\n{\r\n appid 42\r\n \"name\" \"N\" [$WIN32]\r\n \"last\" \"v\"\r\n}");

        var state = root.Child("AppState")!;
        Assert.Equal("42", state.GetString("appid"));
        Assert.Equal("N", state.GetString("name"));
        Assert.Equal("v", state.GetString("last"));
    }

    [Fact]
    public void Parse_LeadingByteOrderMark_IsIgnored()
    {
        var root = ValveKeyValues.Parse((char)0xFEFF + "\"k\" \"v\"");

        Assert.Equal("v", root.GetString("k"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n\t")]
    [InlineData("// 只有注释")]
    public void Parse_NothingToRead_ReturnsEmptyRoot(string? text)
    {
        var root = ValveKeyValues.Parse(text);

        Assert.True(root.IsObject);
        Assert.Empty(root.Children);
    }

    [Fact]
    public void Parse_TruncatedFile_ReturnsWhatWasRead()
    {
        // 写到一半断电：引号没闭合、括号没闭合、最后一个键没有值
        var root = ValveKeyValues.Parse("\"AppState\"\n{\n\t\"appid\"\t\"42\"\n\t\"installdir\"\t\"Gam");

        var state = root.Child("AppState")!;
        Assert.Equal("42", state.GetString("appid"));
        Assert.Equal("Gam", state.GetString("installdir"));

        var dangling = ValveKeyValues.Parse("\"a\" { \"x\" \"1\" \"y\"");
        Assert.Equal("1", dangling.Child("a")!.GetString("x"));
        Assert.Null(dangling.Child("a")!.Child("y"));
    }

    [Theory]
    [InlineData("}}}} \"k\" \"v\"")]
    [InlineData("{ \"k\" \"v\" }")]
    [InlineData("\"a\" { } } \"k\" \"v\" {")]
    [InlineData("\0\0\0garbage{{{\"\"\"")]
    public void Parse_UnbalancedOrGarbage_DoesNotThrow(string text)
    {
        var root = ValveKeyValues.Parse(text);

        Assert.True(root.IsObject);
    }

    [Fact]
    public void Parse_StrayClosingBraceAtTopLevel_IsIgnored()
    {
        var root = ValveKeyValues.Parse("} \"k\" \"v\"");

        Assert.Equal("v", root.GetString("k"));
    }

    [Fact]
    public void Parse_DeeperThanLimit_StopsInsteadOfOverflowing()
    {
        var text = string.Concat(Enumerable.Repeat("\"k\" { ", 5000));

        var root = ValveKeyValues.Parse(text);

        var depth = 0;
        for (var node = root.Child("k"); node != null; node = node.Child("k")) depth++;
        Assert.InRange(depth, 1, ValveKeyValues.MaxDepth);
    }

    [Fact]
    public void GetString_OnObjectChild_ReturnsNull()
    {
        var root = ValveKeyValues.Parse("\"a\" { \"x\" \"1\" }");

        Assert.Null(root.GetString("a"));
        Assert.Null(root.GetString("missing"));
    }

    // ─── libraryfolders.vdf ───

    private const string ModernLibraryFolders =
        "\"libraryfolders\"\n{\n" +
        "\t\"0\"\n\t{\n" +
        "\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n" +
        "\t\t\"label\"\t\t\"\"\n" +
        "\t\t\"contentid\"\t\t\"1234567890123456789\"\n" +
        "\t\t\"totalsize\"\t\t\"0\"\n" +
        "\t\t\"apps\"\n\t\t{\n\t\t\t\"228980\"\t\t\"421042311\"\n\t\t\t\"2358720\"\t\t\"139000000000\"\n\t\t}\n" +
        "\t}\n" +
        "\t\"1\"\n\t{\n" +
        "\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"\n" +
        "\t\t\"apps\"\n\t\t{\n\t\t\t\"3764200\"\t\t\"80000000000\"\n\t\t}\n" +
        "\t}\n" +
        "\t\"2\"\n\t{\n" +
        "\t\t\"path\"\t\t\"E:\\\\Games\\\\Steam 库\\\\\"\n" +
        "\t}\n" +
        "}\n";

    [Fact]
    public void ParseLibraryPaths_ModernFormat_ReturnsEveryLibraryUnescaped()
    {
        var paths = SteamLibraryFolders.ParseLibraryPaths(ModernLibraryFolders);

        Assert.Equal(
            new[] { @"C:\Program Files (x86)\Steam", @"D:\SteamLibrary", @"E:\Games\Steam 库" },
            paths);
    }

    [Fact]
    public void ParseLibraryPaths_LegacyFormat_ReadsStringEntriesAndSkipsOtherFields()
    {
        const string legacy =
            "\"LibraryFolders\"\n{\n" +
            "\t\"TimeNextStatsReport\"\t\t\"1600000000\"\n" +
            "\t\"ContentStatsID\"\t\t\"-123456789\"\n" +
            "\t\"1\"\t\t\"D:\\\\SteamLibrary\"\n" +
            "\t\"2\"\t\t\"F:/Other Library\"\n" +
            "}\n";

        var paths = SteamLibraryFolders.ParseLibraryPaths(legacy);

        Assert.Equal(new[] { @"D:\SteamLibrary", @"F:\Other Library" }, paths);
    }

    [Fact]
    public void ParseLibraryPaths_DuplicatesAndBlankPaths_AreDropped()
    {
        const string text =
            "\"libraryfolders\"\n{\n" +
            "\t\"0\" { \"path\" \"D:\\\\SteamLibrary\" }\n" +
            "\t\"1\" { \"path\" \"d:\\\\steamlibrary\\\\\" }\n" +
            "\t\"2\" { \"path\" \"\" }\n" +
            "\t\"3\" { \"label\" \"没有 path\" }\n" +
            "\t\"4\" { \"path\" \"D:\\\\\" }\n" +
            "}\n";

        var paths = SteamLibraryFolders.ParseLibraryPaths(text);

        Assert.Equal(new[] { @"D:\SteamLibrary", @"D:\" }, paths);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a vdf at all")]
    [InlineData("\"libraryfolders\" \"是个字符串不是对象\"")]
    [InlineData("\"somethingelse\" { \"0\" { \"path\" \"D:\\\\X\" } }")]
    public void ParseLibraryPaths_UnusableInput_ReturnsEmpty(string? text)
    {
        Assert.Empty(SteamLibraryFolders.ParseLibraryPaths(text));
    }

    [Fact]
    public void ParseLibraryPaths_TruncatedFile_KeepsLibrariesReadSoFar()
    {
        var truncated = ModernLibraryFolders.Substring(0, ModernLibraryFolders.IndexOf("\"2\"", StringComparison.Ordinal));

        var paths = SteamLibraryFolders.ParseLibraryPaths(truncated);

        Assert.Equal(new[] { @"C:\Program Files (x86)\Steam", @"D:\SteamLibrary" }, paths);
    }

    // ─── appmanifest_*.acf ───

    private static string Acf(string body) => "\"AppState\"\n{\n" + body + "\n}\n";

    [Fact]
    public void ParseManifest_ReadsAppIdNameAndInstallDir()
    {
        var manifest = SteamAppManifest.Parse(Acf(
            "\t\"appid\"\t\t\"3764200\"\n" +
            "\t\"universe\"\t\t\"1\"\n" +
            "\t\"name\"\t\t\"Resident Evil Requiem\"\n" +
            "\t\"StateFlags\"\t\t\"4\"\n" +
            "\t\"installdir\"\t\t\"RESIDENT EVIL requiem BIOHAZARD requiem\"\n" +
            "\t\"InstalledDepots\"\n\t{\n\t\t\"3764201\"\n\t\t{\n\t\t\t\"manifest\"\t\t\"1\"\n\t\t\t\"size\"\t\t\"2\"\n\t\t}\n\t}"));

        Assert.Equal(new SteamAppManifest(3764200, "Resident Evil Requiem", "RESIDENT EVIL requiem BIOHAZARD requiem"), manifest);
    }

    [Fact]
    public void ParseManifest_MissingName_GivesEmptyName()
    {
        var manifest = SteamAppManifest.Parse(Acf("\"appid\" \"7\"\n\"installdir\" \"Game\""));

        Assert.Equal(new SteamAppManifest(7, "", "Game"), manifest);
    }

    [Theory]
    [InlineData("\"installdir\" \"Game\"")]
    [InlineData("\"appid\" \"\"\n\"installdir\" \"Game\"")]
    [InlineData("\"appid\" \"abc\"\n\"installdir\" \"Game\"")]
    [InlineData("\"appid\" \"-5\"\n\"installdir\" \"Game\"")]
    public void ParseManifest_MissingOrBadAppId_FallsBackToFileName(string body)
    {
        Assert.Equal(123u, SteamAppManifest.Parse(Acf(body), @"D:\Lib\steamapps\appmanifest_123.acf")!.AppId);
        Assert.Equal(0u, SteamAppManifest.Parse(Acf(body))!.AppId);
        Assert.Equal(0u, SteamAppManifest.Parse(Acf(body), "whatever.acf")!.AppId);
    }

    [Theory]
    [InlineData("\"appid\" \"7\"")]
    [InlineData("\"appid\" \"7\"\n\"installdir\" \"\"")]
    [InlineData("\"appid\" \"7\"\n\"installdir\" \"   \"")]
    [InlineData("\"appid\" \"7\"\n\"installdir\" \"..\"")]
    [InlineData("\"appid\" \"7\"\n\"installdir\" \"..\\\\..\\\\Windows\"")]
    [InlineData("\"appid\" \"7\"\n\"installdir\" \"a/b\"")]
    [InlineData("\"appid\" \"7\"\n\"installdir\" \"C:\\\\Windows\"")]
    [InlineData("\"appid\" \"7\"\n\"installdir\" \"C:evil\"")]
    [InlineData("\"appid\" \"7\"\n\"installdir\" \"bad|name\"")]
    [InlineData("\"appid\" \"7\"\n\"installdir\" \"trailing.\"")]
    public void ParseManifest_MissingOrUnsafeInstallDir_ReturnsNull(string body)
    {
        Assert.Null(SteamAppManifest.Parse(Acf(body)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("乱码 \0\0 {{{")]
    [InlineData("\"NotAppState\" { \"appid\" \"7\" \"installdir\" \"Game\" }")]
    [InlineData("\"AppState\" \"是个字符串\"")]
    public void ParseManifest_UnusableInput_ReturnsNull(string? text)
    {
        Assert.Null(SteamAppManifest.Parse(text));
    }

    [Fact]
    public void ParseManifest_TruncatedAfterInstallDir_StillUsable()
    {
        var text = "\"AppState\"\n{\n\t\"appid\"\t\"7\"\n\t\"name\"\t\"N\"\n\t\"installdir\"\t\"Game\"\n\t\"InstalledDepots\"\n\t{\n\t\t\"8\"\n\t\t{\n\t\t\t\"mani";

        Assert.Equal(new SteamAppManifest(7, "N", "Game"), SteamAppManifest.Parse(text));
    }

    [Theory]
    [InlineData("appmanifest_271.acf", true, 271u)]
    [InlineData("APPMANIFEST_271.ACF", true, 271u)]
    [InlineData("C:/x/steamapps/appmanifest_9.acf", true, 9u)]
    [InlineData("appmanifest_.acf", false, 0u)]
    [InlineData("appmanifest_0.acf", false, 0u)]
    [InlineData("appmanifest_12x.acf", false, 0u)]
    [InlineData("appmanifest_12.acf.bak", false, 0u)]
    [InlineData("", false, 0u)]
    [InlineData(null, false, 0u)]
    public void TryParseAppIdFromFileName_Works(string? fileName, bool expected, uint expectedId)
    {
        Assert.Equal(expected, SteamAppManifest.TryParseAppIdFromFileName(fileName, out var id));
        Assert.Equal(expectedId, id);
    }
}
