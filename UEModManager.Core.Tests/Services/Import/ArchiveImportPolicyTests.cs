using UEModManager.Services.Import;

namespace UEModManager.Core.Tests.Services.Import;

/// <summary>
/// 解压产物筛选策略。旧实现用 EngineProfile 扩展名白名单，把插件载荷（.lua / .dll / .txt）
/// 整包丢掉且不留日志，实测 1194 个文件丢掉 1174 个。这里锁定"只排除开发副产品"的边界。
/// </summary>
public class ArchiveImportPolicyTests
{
    [Theory]
    [InlineData("mod.pak")]
    [InlineData("mod.ucas")]
    [InlineData("mod.utoc")]
    [InlineData("ue4ss/Mods/BPML_GenericFunctions.lua")]
    [InlineData("CSharpLoader/Mods/PerfectDodgeV2.4/PerfectDodgeV2.4.dll")]
    [InlineData("version.dll")]
    [InlineData("ue4ss/Mods/mods.json")]
    [InlineData("readme.txt")]
    [InlineData("说明.txt")]
    public void ShouldImport_PluginAndModPayload_True(string path)
        => Assert.True(ArchiveImportPolicy.ShouldImport(path));

    [Theory]
    [InlineData("mod.pdb")]
    [InlineData("mod.log")]
    [InlineData("crash.dmp")]
    [InlineData("trainer.exe")]
    [InlineData("install.msi")]
    [InlineData("run.bat")]
    [InlineData("mod.sig")]
    [InlineData("Thumbs.db")]
    [InlineData("._resource")]
    [InlineData("__MACOSX/._resource")]
    public void ShouldImport_DevelopmentResidue_False(string path)
        => Assert.False(ArchiveImportPolicy.ShouldImport(path));

    [Fact]
    public void ExcludedExtensions_AreLowercaseWithDot()
    {
        foreach (var ext in ArchiveImportPolicy.ExcludedExtensions)
        {
            Assert.StartsWith(".", ext);
            Assert.Equal(ext.ToLowerInvariant(), ext);
        }
    }

    [Fact]
    public void IsJunkName_NestedMacOsFolder_False()
        => Assert.True(ArchiveImportPolicy.IsJunkName("a/b/__MACOSX/c/d.png"));

    [Fact]
    public void DropReport_Describe_MentionsBothCounts()
    {
        var report = new ArchiveDropReport(KeptCount: 20, KeptBytes: 100, DroppedCount: 1174, DroppedBytes: 244);

        Assert.True(report.HasDrops);
        Assert.Contains("20", report.Describe());
        Assert.Contains("1174", report.Describe());
    }

    [Fact]
    public void DropReport_Empty_HasNoDrops()
    {
        Assert.False(ArchiveDropReport.Empty.HasDrops);
        Assert.Equal(0, ArchiveDropReport.Empty.DroppedRatio);
    }
}
