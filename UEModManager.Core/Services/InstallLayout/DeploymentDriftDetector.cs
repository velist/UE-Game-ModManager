using System;
using System.Collections.Generic;
using System.Linq;

namespace UEModManager.Services.InstallLayout
{
    /// <summary>一个文件的位置与它所属的包。</summary>
    public readonly record struct OwnedTarget(string TargetPath, string PackageKey);

    /// <summary>已装文件与「按当前规则应该装在哪」的差异。</summary>
    /// <param name="Misplaced">装在了游戏读不到的位置的文件：所属的包仍然启用，但它现在该装到别处。</param>
    /// <param name="Missing">这些包按当前规则该装、却还没装到位的目标。</param>
    /// <param name="PackageKeys">受影响的包。</param>
    public sealed record DeploymentDrift(
        IReadOnlyList<string> Misplaced, IReadOnlyList<string> Missing, IReadOnlyList<string> PackageKeys)
    {
        public bool HasDrift => Misplaced.Count > 0;
    }

    /// <summary>
    /// 比对托管文件清单与当前方案的目标路径，找出「装错位置」的包。纯函数。
    ///
    /// 升级到修正了落盘基准的版本后，老版本装下的文件正好表现为：同一个包「托管着一批文件，
    /// 但该装的位置一个都没装」。只认这一种形状——单纯的「托管但已不需要」或「需要但还没装」
    /// 是玩家停用了 MOD / 关着自动部署的正常状态，不能报成故障。
    /// </summary>
    public static class DeploymentDriftDetector
    {
        public static DeploymentDrift Detect(IEnumerable<OwnedTarget>? managed, IEnumerable<OwnedTarget>? expected)
        {
            var managedList = (managed ?? []).ToList();
            var expectedList = (expected ?? []).ToList();
            var managedPaths = managedList.Select(m => m.TargetPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var expectedPaths = expectedList.Select(e => e.TargetPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var stale = managedList.Where(m => !expectedPaths.Contains(m.TargetPath))
                .ToLookup(m => m.PackageKey, StringComparer.OrdinalIgnoreCase);
            var absent = expectedList.Where(e => !managedPaths.Contains(e.TargetPath))
                .ToLookup(e => e.PackageKey, StringComparer.OrdinalIgnoreCase);

            var packages = stale.Select(g => g.Key).Where(key => absent.Contains(key))
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToList();
            return new DeploymentDrift(
                packages.SelectMany(key => stale[key].Select(m => m.TargetPath))
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
                packages.SelectMany(key => absent[key].Select(e => e.TargetPath))
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
                packages);
        }
    }
}
