using System.Collections.Generic;
using System.Linq;
using UEModManager.Models;
using UEModManager.Services.Conflict;
using UEModManager.Services.Conflict.Model;

namespace UEModManager.Core.Tests.Services.Conflict;

/// <summary>
/// <see cref="FilePathAssetIndexBuilder"/> 的退化路径回归。
///
/// 非 UE 引擎和所有非 MOD 包都走这里：没有容器可解析，直接拿
/// <c>RelativeTargetPath</c> 当资产键，<c>FileHash</c> 当指纹。
/// </summary>
public class FilePathAssetIndexBuilderTests
{
    private static Package PackageWith(params (string Target, string Hash, ArtifactType Type)[] artifacts)
    {
        var package = new Package
        {
            PackageKey = "p",
            DisplayName = "P",
            Kind = PackageKind.Plugin,
            HostGameName = "G",
            ContentHash = "CONTENT"
        };

        foreach (var (target, hash, type) in artifacts)
        {
            package.Artifacts.Add(new PackageArtifact
            {
                PackageId = package.Id,
                RelativeSourcePath = "files/" + target,
                RelativeTargetPath = target,
                FileName = target.Split('/').Last(),
                FileSize = 42,
                FileHash = hash,
                ArtifactType = type
            });
        }

        return package;
    }

    [Fact]
    public void 两条产物生成两个条目()
    {
        var index = FilePathAssetIndexBuilder.Build(
            PackageWith(("ue4ss/Mods/a.lua", "h1", ArtifactType.PluginFile),
                        ("CSharpLoader/b1cs.ini", "h2", ArtifactType.ConfigFile)));

        Assert.Equal(2, index.Entries.Count);
        Assert.Equal(IndexStatus.Complete, index.Status);
        Assert.Equal(PackageKind.Plugin, index.Kind);
        Assert.Equal("CONTENT", index.ContentHash);
    }

    [Fact]
    public void AssetKey是归一化的小写正斜杠()
    {
        var index = FilePathAssetIndexBuilder.Build(
            PackageWith((@"UE4SS\Mods\A.Lua", "h1", ArtifactType.PluginFile)));

        var entry = Assert.Single(index.Entries);
        Assert.Equal("ue4ss/mods/a.lua", entry.AssetKey);
        Assert.Equal("h1", entry.Fingerprint);
        Assert.Equal(FingerprintKind.ContentHash, entry.FingerprintKind);
    }

    [Fact]
    public void 预览图不进索引()
    {
        var index = FilePathAssetIndexBuilder.Build(
            PackageWith(("ue4ss/Mods/a.lua", "h1", ArtifactType.PluginFile),
                        ("preview.png", "h2", ArtifactType.PreviewImage)));

        var entry = Assert.Single(index.Entries);
        Assert.Equal("ue4ss/mods/a.lua", entry.AssetKey);
    }

    [Fact]
    public void 没有哈希时指纹类型为None()
    {
        var index = FilePathAssetIndexBuilder.Build(
            PackageWith(("ue4ss/Mods/a.lua", null!, ArtifactType.PluginFile)));

        var entry = Assert.Single(index.Entries);
        Assert.Null(entry.Fingerprint);
        Assert.Equal(FingerprintKind.None, entry.FingerprintKind);
    }

    [Fact]
    public void 容器相关字段填哨兵值()
    {
        var index = FilePathAssetIndexBuilder.Build(
            PackageWith(("a.pak", "h1", ArtifactType.ModFile)));

        var entry = Assert.Single(index.Entries);
        Assert.Equal(-1, entry.DataOffset);
        Assert.Equal(-1, entry.StoredSize);
        Assert.Equal(-1, entry.CompressionMethod);
    }
}