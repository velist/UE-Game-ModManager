using UEModManager.Services.Import;

namespace UEModManager.Core.Tests.Services.Import;

/// <summary>
/// 导入临时目录的选盘策略。
///
/// 关键性质只有一条：无论调用方传进来什么，回落链的最后一档都是 %TEMP%，
/// 绝不再出现"安装目录"——安装目录默认在系统盘上，几十 GB 的整合包解压过去
/// 就是把 C 盘写满。
/// </summary>
public class ImportTempRootSelectorTests
{
    private const string SystemTemp = @"C:\Users\dev\AppData\Local\Temp";

    private static string Expected(string parent) =>
        Path.Combine(parent, ImportTempRootSelector.FolderName);

    [Fact]
    public void 备份目录优先()
    {
        // 备份目录与解压产物同盘，且必定是本程序已经在写的位置
        var candidates = ImportTempRootSelector.GetCandidates(
            @"D:\Backups\悟空", @"E:\Game\Content\Paks\~mods", SystemTemp);

        Assert.Equal(Expected(@"D:\Backups\悟空"), candidates[0]);
    }

    [Fact]
    public void 备份目录缺失时退到游戏所在盘的MOD目录()
    {
        var candidates = ImportTempRootSelector.GetCandidates(
            null, @"E:\Game\Content\Paks\~mods", SystemTemp);

        Assert.Equal(Expected(@"E:\Game\Content\Paks\~mods"), candidates[0]);
    }

    [Fact]
    public void 两个目录都缺失时回落系统临时目录()
    {
        var candidates = ImportTempRootSelector.GetCandidates(null, null, SystemTemp);

        // 回落到 %TEMP%\UEModManager\.import-tmp，而不是安装目录
        Assert.Equal(Expected(Path.Combine(SystemTemp, "UEModManager")), Assert.Single(candidates));
    }

    [Fact]
    public void 系统临时目录永远是最后一档()
    {
        var candidates = ImportTempRootSelector.GetCandidates(
            @"D:\Backups\悟空", @"E:\Game\~mods", SystemTemp);

        Assert.Equal(3, candidates.Count);
        Assert.Equal(Expected(Path.Combine(SystemTemp, "UEModManager")), candidates[^1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 空白路径不产生候选(string blank)
    {
        // 配置里留下的空字符串不该变成"当前工作目录下的 .import-tmp"
        var candidates = ImportTempRootSelector.GetCandidates(blank, blank, SystemTemp);

        Assert.Single(candidates);
    }

    [Fact]
    public void 备份目录与MOD目录相同时不重复尝试()
    {
        var candidates = ImportTempRootSelector.GetCandidates(
            @"D:\Game\~mods", @"d:\game\~MODS", SystemTemp);

        // 大小写不同但指向同一个目录，重试它一次毫无意义
        Assert.Equal(2, candidates.Count);
    }

    [Fact]
    public void 系统临时目录为空时直接抛出()
    {
        // 连 %TEMP% 都拿不到就没有任何安全回落，静默用相对路径写几十 GB 更危险
        Assert.Throws<ArgumentException>(
            () => ImportTempRootSelector.GetCandidates(@"D:\Backups", null, ""));
    }
}
