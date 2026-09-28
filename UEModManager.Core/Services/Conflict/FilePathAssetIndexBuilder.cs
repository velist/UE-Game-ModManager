using System;
using System.Collections.Generic;
using UEModManager.Models;
using UEModManager.Services.Conflict.Indexing;
using UEModManager.Services.Conflict.Model;

namespace UEModManager.Services.Conflict;

/// <summary>
/// 非容器路径：把一个包的 Artifacts 直接当成资产条目。
///
/// 用于非 UE 引擎的游戏，以及全部 <see cref="PackageKind.Mod"/> 以外的包
/// （插件带的 <c>ue4ss/</c>、<c>CSharpLoader/</c> 本来就是相对游戏根的文件路径，
/// 没有容器索引可读）。
///
/// <c>AssetKey</c> 取归一化的 <c>RelativeTargetPath</c>，语义与
/// <c>ConflictDetector.ComputeLoadConflictKey</c> 去掉根目录后一致 ——
/// 部署层仍按这套键决定同路径文件落盘哪一份，两边必须能对上。
/// </summary>
public static class FilePathAssetIndexBuilder
{
    public static ModAssetIndex Build(Package package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var entries = new List<AssetEntry>();
        foreach (var artifact in package.Artifacts)
        {
            if (artifact.ArtifactType == ArtifactType.PreviewImage) continue;
            if (string.IsNullOrWhiteSpace(artifact.RelativeTargetPath)) continue;

            var displayPath = AssetKeyNormalizer.NormalizePath(artifact.RelativeTargetPath);
            if (displayPath.Length == 0) continue;

            entries.Add(new AssetEntry(
                AssetKey: displayPath,
                DisplayPath: displayPath,
                Fingerprint: artifact.FileHash,
                // 文件哈希是读完整文件算出来的，可以直接代表内容。
                FingerprintKind: artifact.FileHash is null ? FingerprintKind.None : FingerprintKind.ContentHash,
                Size: artifact.FileSize,
                DataOffset: -1,
                StoredSize: -1,
                CompressionMethod: -1,
                SourceFile: artifact.RelativeSourcePath ?? string.Empty));
        }

        return new ModAssetIndex(
            PackageKey: package.PackageKey,
            DisplayName: package.DisplayName,
            Kind: package.Kind,
            ContentHash: package.ContentHash,
            Entries: entries,
            Status: IndexStatus.Complete,
            StatusDetail: null);
    }
}
