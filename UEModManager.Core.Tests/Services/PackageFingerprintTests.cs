using UEModManager.Models;
using UEModManager.Services;
using UEModManager.Services.Lock;

namespace UEModManager.Core.Tests.Services;

public class PackageFingerprintTests
{
    private static Package Pkg(string key, params (string path, string hash)[] files) => new()
    {
        PackageKey = key, DisplayName = key, HostGameName = "G", ContentHash = "legacy-" + key,
        Artifacts = files.Select(f => new PackageArtifact
        {
            RelativeTargetPath = f.path, FileName = Path.GetFileName(f.path), FileHash = f.hash,
            ArtifactType = ArtifactType.ModFile
        }).ToList()
    };

    [Fact]
    public void 与文件顺序和路径无关()
    {
        var a = Pkg("a", ("Content/Paks/x-1.pak", "AA"), ("Content/Paks/x_2.pak", "BB"));
        var b = Pkg("b", ("other/x_2.pak", "bb"), ("other/x-1.pak", "aa"));

        Assert.Equal(PackageFingerprint.Of(a), PackageFingerprint.Of(b));
    }

    [Fact]
    public void 内容不同指纹不同_预览图不算内容()
    {
        var a = Pkg("a", ("x.pak", "AA"));
        var b = Pkg("b", ("x.pak", "AB"));
        var withPreview = Pkg("c", ("x.pak", "AA"));
        withPreview.Artifacts.Add(new PackageArtifact { RelativeTargetPath = "p.png", FileHash = "PP", ArtifactType = ArtifactType.PreviewImage });

        Assert.NotEqual(PackageFingerprint.Of(a), PackageFingerprint.Of(b));
        Assert.Equal(PackageFingerprint.Of(a), PackageFingerprint.Of(withPreview));
    }

    [Fact]
    public void 没有文件哈希时退回ContentHash()
        => Assert.Equal("legacy-old", PackageFingerprint.Of(Pkg("old")));

    [Fact]
    public void Lock比对优先用指纹_ContentHash不同但内容相同不算不一致()
    {
        // 英文系统导出（ContentHash 按英文排序）→ 中文系统导入（ContentHash 按中文排序）
        var exported = Pkg("m", ("a-1.pak", "AA"), ("a_2.pak", "BB"));
        var profile = new InstanceProfile { HostGameName = "G", Packages = [new ProfilePackageEntry { PackageKey = "m", IsEnabled = true }] };
        var lockFile = ProfileLockBuilder.Build(profile, new Dictionary<string, Package> { ["m"] = exported });

        var local = Pkg("m", ("a_2.pak", "BB"), ("a-1.pak", "AA"));
        local.ContentHash = "different-on-this-machine";
        var diff = ProfileLockComparator.Compare(lockFile, new Dictionary<string, Package> { ["m"] = local });

        Assert.Equal(LockPackageImportStatus.Matched, Assert.Single(diff.PackageDiffs).Status);
    }
}
