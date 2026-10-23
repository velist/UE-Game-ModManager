using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UEModManager.Models;

namespace UEModManager.Services;

/// <summary>
/// 包的内容指纹：各文件哈希按 Ordinal 排序后整体哈希（纯函数）。
///
/// <para>
/// 用来代替 <see cref="Package.ContentHash"/> 做"两个包内容是否相同"的判断。
/// ContentHash 按文件路径的**默认（文化相关）排序**拼接，中文系统和英文系统给同一个 MOD
/// 算出不同的值：英文系统导出的方案拿到中文系统导入会误报"内容不一致"。
/// 指纹只看文件哈希的多重集，与路径、枚举顺序、系统语言都无关；
/// 直接由已入库的逐文件哈希算出，不需要重读文件，也不必迁移老数据。
/// </para>
/// </summary>
public static class PackageFingerprint
{
    /// <summary>
    /// 计算指纹。预览图不算内容；没有任何文件哈希时退回 <see cref="Package.ContentHash"/>
    /// （极老的数据），仍然为空则返回 null。
    /// </summary>
    public static string? Of(Package package)
    {
        if (package == null) throw new ArgumentNullException(nameof(package));

        var hashes = package.Artifacts
            .Where(a => a.ArtifactType != ArtifactType.PreviewImage && !string.IsNullOrWhiteSpace(a.FileHash))
            .Select(a => a.FileHash!.Trim().ToLowerInvariant())
            .OrderBy(h => h, StringComparer.Ordinal)
            .ToList();
        if (hashes.Count == 0)
            return string.IsNullOrWhiteSpace(package.ContentHash) ? null : package.ContentHash;

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", hashes)));
        return "fp1-" + Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
