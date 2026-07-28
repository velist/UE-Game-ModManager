using System.Text.Json;
using System.Text.Json.Nodes;
using UEModManager.Services.Paths;

namespace UEModManager.Core.Tests.Services.Paths;

/// <summary>
/// 路径平移规则的单测。这里的每一条都对应"老用户升级后数据失联"的一种具体走法，
/// 尤其是前缀相似（Backups2 vs Backups）与幂等两条：前者会把隔壁目录的备份改到不存在的位置，
/// 后者会在每次启动时把路径往下再套一层。
/// </summary>
public class PathRebaseRuleTests
{
    private const string LegacyRoot = @"C:\Program Files\UEModManager\Backups";
    private const string TargetRoot = @"C:\Users\u\AppData\Local\UEModManager\Backups\Mods";

    private static PathRebaseRule Rule(string legacy = LegacyRoot, string target = TargetRoot)
        => new(legacy, target);

    // ─── 基本平移 ───

    [Fact]
    public void PathUnderLegacyRoot_IsRebasedToSameRelativeLocation()
    {
        var result = Rule().TryRebase(@"C:\Program Files\UEModManager\Backups\剑星_备份");

        Assert.Equal(@"C:\Users\u\AppData\Local\UEModManager\Backups\Mods\剑星_备份", result);
    }

    [Fact]
    public void DeeplyNestedPath_KeepsWholeRelativeTail()
    {
        var result = Rule().TryRebase(@"C:\Program Files\UEModManager\Backups\剑星_备份\a\b.pak");

        Assert.Equal(@"C:\Users\u\AppData\Local\UEModManager\Backups\Mods\剑星_备份\a\b.pak", result);
    }

    [Fact]
    public void LegacyRootItself_IsRebasedToTargetRoot()
    {
        // 用户在对话框里直接选了备份根本身，没有再套一层游戏名
        Assert.Equal(TargetRoot, Rule().TryRebase(LegacyRoot));
    }

    // ─── 不该动的 ───

    [Fact]
    public void PathOutsideLegacyRoot_IsLeftAlone()
    {
        // 用户把备份放在别的盘：这次搬迁根本没碰它
        Assert.Null(Rule().TryRebase(@"D:\MyBackups\剑星_备份"));
    }

    [Fact]
    public void GameInstallPath_IsLeftAlone()
    {
        Assert.Null(Rule().TryRebase(@"D:\Steam\steamapps\common\StellarBlade"));
    }

    [Theory]
    [InlineData(@"C:\Program Files\UEModManager\Backups2\剑星_备份")]
    [InlineData(@"C:\Program Files\UEModManager\BackupsOld")]
    [InlineData(@"C:\Program Files\UEModManagerX\Backups\剑星_备份")]
    public void SiblingWithSharedPrefix_IsNotRebased(string path)
    {
        // 纯前缀匹配的经典陷阱：Backups2 不是 Backups 的子目录
        Assert.Null(Rule().TryRebase(path));
    }

    [Fact]
    public void PathAlreadyUnderTargetRoot_IsLeftAlone()
    {
        // 幂等：重复启动时旧值已经是新值，不能再动
        Assert.Null(Rule().TryRebase(@"C:\Users\u\AppData\Local\UEModManager\Backups\Mods\剑星_备份"));
    }

    [Fact]
    public void RebaseTwice_IsStable()
    {
        var rule = Rule();
        var once = rule.TryRebase(@"C:\Program Files\UEModManager\Backups\剑星_备份");

        Assert.NotNull(once);
        Assert.Null(rule.TryRebase(once));
    }

    [Fact]
    public void TargetNestedUnderLegacy_DoesNotStackAnotherLevel()
    {
        // 新根就在旧根之下（{根}\Backups → {根}\Backups\Mods）时，
        // 先判"是否已在新根之下"是唯一能防住 Mods\Mods\… 的办法
        var rule = new PathRebaseRule(@"C:\App\Backups", @"C:\App\Backups\Mods");

        Assert.Equal(@"C:\App\Backups\Mods\剑星_备份", rule.TryRebase(@"C:\App\Backups\剑星_备份"));
        Assert.Null(rule.TryRebase(@"C:\App\Backups\Mods\剑星_备份"));
    }

    [Fact]
    public void LegacyEqualsTarget_ReturnsNull()
    {
        // 源与目标同一位置：搬迁没真正发生，改写只是平白重写一遍配置
        var rule = new PathRebaseRule(LegacyRoot, LegacyRoot + @"\");

        Assert.Null(rule.TryRebase(@"C:\Program Files\UEModManager\Backups\剑星_备份"));
    }

    // ─── 写法差异：大小写、尾分隔符、斜杠方向、相对段 ───

    [Theory]
    [InlineData(@"c:\program files\uemodmanager\backups\剑星_备份")]
    [InlineData(@"C:\PROGRAM FILES\UEMODMANAGER\BACKUPS\剑星_备份")]
    [InlineData(@"C:\Program Files\UEModManager\Backups\剑星_备份\")]
    [InlineData(@"C:/Program Files/UEModManager/Backups/剑星_备份")]
    [InlineData(@"C:\Program Files\UEModManager\Data\..\Backups\剑星_备份")]
    [InlineData("  C:\\Program Files\\UEModManager\\Backups\\剑星_备份  ")]
    public void WritingVariants_StillRebase(string path)
    {
        Assert.Equal(@"C:\Users\u\AppData\Local\UEModManager\Backups\Mods\剑星_备份",
            Rule().TryRebase(path));
    }

    [Fact]
    public void RootWithTrailingSeparator_StillRebases()
    {
        var rule = new PathRebaseRule(LegacyRoot + @"\", TargetRoot + @"\");

        Assert.Equal(@"C:\Users\u\AppData\Local\UEModManager\Backups\Mods\剑星_备份",
            rule.TryRebase(@"C:\Program Files\UEModManager\Backups\剑星_备份"));
    }

    [Fact]
    public void DriveRootAsLegacyRoot_RebasesWholeTail()
    {
        var rule = new PathRebaseRule(@"C:\", @"D:\Moved");

        Assert.Equal(@"D:\Moved\a\b.txt", rule.TryRebase(@"C:\a\b.txt"));
    }

    // ─── 空值与坏值 ───

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrBlankPath_ReturnsNull(string? path)
    {
        Assert.Null(Rule().TryRebase(path));
    }

    [Fact]
    public void UnparsablePath_ReturnsNull()
    {
        // 宁可留着一个读不懂的值，也不要拿它拼出一个更离谱的新值
        Assert.Null(Rule().TryRebase("C:\\Program Files\\UEModManager\\Backups\\a\0b"));
    }

    [Fact]
    public void RelativePath_IsNotRebased()
    {
        // 相对路径按当前工作目录展开，落不进这两个假想根之下
        Assert.Null(Rule().TryRebase(@"Backups\剑星_备份"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankRoot_Throws(string? root)
    {
        Assert.Throws<ArgumentException>(() => new PathRebaseRule(root!, TargetRoot));
        Assert.Throws<ArgumentException>(() => new PathRebaseRule(LegacyRoot, root!));
    }
}

/// <summary>
/// config.json 定点改写的单测。核心关切有三：只动该动的字段、不认识的字段一个不能丢、
/// 规则为 null（对应"那一项没搬成功"）时半个字都不许改。
/// </summary>
public class AppConfigPathRewriterTests
{
    private const string LegacyBackups = @"C:\Program Files\UEModManager\Backups";
    private const string TargetBackups = @"C:\Users\u\AppData\Local\UEModManager\Backups\Mods";
    private const string LegacyData = @"C:\Program Files\UEModManager\Data";
    private const string TargetData = @"C:\Users\u\AppData\Local\UEModManager\Data";

    private static PathRebaseRule BackupsRule => new(LegacyBackups, TargetBackups);
    private static PathRebaseRule DataRule => new(LegacyData, TargetData);

    private const string SampleJson = """
        {
          "GameName": "剑星",
          "GamePath": "D:\\Steam\\StellarBlade",
          "ModPath": "D:\\Steam\\StellarBlade\\SB\\Content\\Paks\\~mods",
          "BackupPath": "C:\\Program Files\\UEModManager\\Backups\\剑星_备份",
          "ExecutableName": "SB-Win64-Shipping.exe",
          "CustomGames": [ "我的游戏" ],
          "GameIcons": {
            "剑星": "C:\\Program Files\\UEModManager\\Data\\GameIcons\\剑星.png",
            "我的游戏": "C:\\Program Files\\UEModManager\\Data\\GameIcons\\我的游戏.jpg"
          },
          "GameEngines": { "我的游戏": "Unity" },
          "PluginPaths": { "剑星": "D:\\Steam\\StellarBlade\\SB\\Binaries\\Win64" }
        }
        """;

    private static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static string? Value(string json, string property)
        => Parse(json)[property]?.GetValue<string>();

    private static string? IconOf(string json, string game)
        => Parse(json)["GameIcons"]?[game]?.GetValue<string>();

    // ─── 该改的 ───

    [Fact]
    public void BackupPath_IsRebased()
    {
        var result = AppConfigPathRewriter.Rewrite(SampleJson, BackupsRule, DataRule);

        Assert.True(result.Changed);
        Assert.Equal(TargetBackups + @"\剑星_备份", Value(result.Json, "BackupPath"));
    }

    [Fact]
    public void EveryGameIcon_IsRebased()
    {
        var result = AppConfigPathRewriter.Rewrite(SampleJson, BackupsRule, DataRule);

        Assert.Equal(TargetData + @"\GameIcons\剑星.png", IconOf(result.Json, "剑星"));
        Assert.Equal(TargetData + @"\GameIcons\我的游戏.jpg", IconOf(result.Json, "我的游戏"));
    }

    [Fact]
    public void ChangedEntries_RecordEveryRewrite()
    {
        var result = AppConfigPathRewriter.Rewrite(SampleJson, BackupsRule, DataRule);

        // 1 个 BackupPath + 2 个图标，日志里要能逐条对账
        Assert.Equal(3, result.ChangedEntries.Count);
        Assert.Contains(result.ChangedEntries, e => e.StartsWith("BackupPath:"));
        Assert.Contains(result.ChangedEntries, e => e.StartsWith("GameIcons[剑星]:"));
    }

    [Fact]
    public void PropertyNames_AreMatchedCaseInsensitively()
    {
        const string json = """
            { "backuppath": "C:\\Program Files\\UEModManager\\Backups\\剑星_备份" }
            """;

        var result = AppConfigPathRewriter.Rewrite(json, BackupsRule, DataRule);

        Assert.True(result.Changed);
        // 原有的键名要保持原样，不能顺手改成 PascalCase
        Assert.Equal(TargetBackups + @"\剑星_备份", Value(result.Json, "backuppath"));
    }

    // ─── 不该改的 ───

    [Fact]
    public void GameAndPluginPaths_AreNeverTouched()
    {
        var result = AppConfigPathRewriter.Rewrite(SampleJson, BackupsRule, DataRule);
        var root = Parse(result.Json);

        Assert.Equal(@"D:\Steam\StellarBlade", root["GamePath"]!.GetValue<string>());
        Assert.Equal(@"D:\Steam\StellarBlade\SB\Content\Paks\~mods", root["ModPath"]!.GetValue<string>());
        Assert.Equal(@"D:\Steam\StellarBlade\SB\Binaries\Win64",
            root["PluginPaths"]!["剑星"]!.GetValue<string>());
    }

    [Fact]
    public void UnknownFields_Survive()
    {
        // 定点改写而非"反序列化再序列化"的全部意义所在：
        // 旧版本残留与将来新增的字段都不能在这一步丢掉
        const string json = """
            {
              "BackupPath": "C:\\Program Files\\UEModManager\\Backups\\剑星_备份",
              "FutureFlag": true,
              "LegacyLeftover": { "nested": [ 1, 2, 3 ] }
            }
            """;

        var result = AppConfigPathRewriter.Rewrite(json, BackupsRule, DataRule);
        var root = Parse(result.Json);

        Assert.True(root["FutureFlag"]!.GetValue<bool>());
        Assert.Equal(3, root["LegacyLeftover"]!["nested"]!.AsArray().Count);
    }

    [Fact]
    public void IconOutsideLegacyDataDirectory_IsLeftAlone()
    {
        const string json = """
            { "GameIcons": { "剑星": "D:\\Pictures\\my-icon.png" } }
            """;

        var result = AppConfigPathRewriter.Rewrite(json, BackupsRule, DataRule);

        Assert.False(result.Changed);
        Assert.Equal(json, result.Json);
    }

    // ─── 搬迁没成功 → 一个字都不许改 ───

    [Fact]
    public void NoRules_LeavesJsonByteForByte()
    {
        var result = AppConfigPathRewriter.Rewrite(SampleJson, null, null);

        Assert.False(result.Changed);
        Assert.Equal(SampleJson, result.Json);
        Assert.Empty(result.ChangedEntries);
    }

    [Fact]
    public void OnlyBackupsRelocated_LeavesGameIconsAlone()
    {
        // 数据索引那一项搬失败：图标还在旧位置，改了配置就指向一个空目录
        var result = AppConfigPathRewriter.Rewrite(SampleJson, BackupsRule, null);

        Assert.Equal(TargetBackups + @"\剑星_备份", Value(result.Json, "BackupPath"));
        Assert.Equal(LegacyData + @"\GameIcons\剑星.png", IconOf(result.Json, "剑星"));
    }

    [Fact]
    public void OnlyDataRelocated_LeavesBackupPathAlone()
    {
        var result = AppConfigPathRewriter.Rewrite(SampleJson, null, DataRule);

        Assert.Equal(LegacyBackups + @"\剑星_备份", Value(result.Json, "BackupPath"));
        Assert.Equal(TargetData + @"\GameIcons\剑星.png", IconOf(result.Json, "剑星"));
    }

    // ─── 幂等 ───

    [Fact]
    public void SecondRun_ChangesNothing()
    {
        var first = AppConfigPathRewriter.Rewrite(SampleJson, BackupsRule, DataRule);
        var second = AppConfigPathRewriter.Rewrite(first.Json, BackupsRule, DataRule);

        Assert.True(first.Changed);
        Assert.False(second.Changed);
        Assert.Equal(first.Json, second.Json);
    }

    // ─── 缺字段 / 空值 / 坏结构 ───

    [Fact]
    public void EmptyObject_ChangesNothing()
    {
        var result = AppConfigPathRewriter.Rewrite("{}", BackupsRule, DataRule);

        Assert.False(result.Changed);
        Assert.Equal("{}", result.Json);
    }

    [Fact]
    public void NullValues_ChangeNothing()
    {
        const string json = """
            { "BackupPath": null, "GameIcons": null }
            """;

        var result = AppConfigPathRewriter.Rewrite(json, BackupsRule, DataRule);

        Assert.False(result.Changed);
        Assert.Equal(json, result.Json);
    }

    [Fact]
    public void NonStringValues_AreSkipped()
    {
        const string json = """
            { "BackupPath": 42, "GameIcons": { "剑星": 7 } }
            """;

        var result = AppConfigPathRewriter.Rewrite(json, BackupsRule, DataRule);

        Assert.False(result.Changed);
    }

    [Fact]
    public void NonObjectRoot_ChangesNothing()
    {
        var result = AppConfigPathRewriter.Rewrite("[1,2,3]", BackupsRule, DataRule);

        Assert.False(result.Changed);
        Assert.Equal("[1,2,3]", result.Json);
    }

    [Fact]
    public void InvalidJson_Throws()
    {
        // 解析不了必须让调用方知道，静默返回"无改动"会把损坏的配置伪装成正常
        Assert.ThrowsAny<JsonException>(
            () => AppConfigPathRewriter.Rewrite("{ 这不是 json", BackupsRule, DataRule));
    }
}
