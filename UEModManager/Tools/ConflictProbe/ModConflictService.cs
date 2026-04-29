using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Versions;

namespace ConflictProbe.Services
{
    public class ConflictEntry
    {
        public string AssetPath { get; set; } = string.Empty;
        public List<string> Mods { get; set; } = new List<string>();
    }

    public class ModConflictSummary
    {
        public string ModName { get; set; } = string.Empty;
        public int ConflictCount { get; set; }
        public List<string> ConflictAssetsTop5 { get; set; } = new List<string>();
    }

    public class ModConflictResult
    {
        public int ScannedMods { get; set; }
        public int TotalAssets { get; set; }
        public int ConflictAssets { get; set; }
        public List<ConflictEntry> Conflicts { get; set; } = new List<ConflictEntry>();
        public List<ModConflictSummary> Summaries { get; set; } = new List<ModConflictSummary>();
        public string ModeDescription { get; set; } = string.Empty;
        public TimeSpan Elapsed { get; set; } = TimeSpan.Zero;
    }

    public class ModConflictService
    {
        private readonly VersionContainer _version = new VersionContainer(EGame.GAME_UE5_3);

        private void RegisterVfsReaders(object provider, IEnumerable<string> vfsPaths)
        {
            try
            {
                var tProv = provider.GetType();
                var mRegister = tProv
                    .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .FirstOrDefault(mi => mi.Name == "RegisterVfs" && mi.GetParameters().Length == 1 && mi.GetParameters()[0].ParameterType == typeof(FileInfo));
                if (mRegister == null) { Console.WriteLine("[ConflictService] 未找到 RegisterVfs(FileInfo) 方法"); return; }
                foreach (var path in vfsPaths)
                {
                    try
                    {
                        var ret = mRegister.Invoke(provider, new object[] { new FileInfo(path) });
                        (ret as System.Threading.Tasks.Task)?.Wait(500);
                        Console.WriteLine($"[ConflictService] RegisterVfs 成功: {path}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ConflictService] RegisterVfs 失败: {path} -> {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ConflictService] RegisterVfsReaders 异常: {ex.Message}");
            }
        }

        public async Task<ModConflictResult> DetectConflictsAsync(
            string enabledModsRoot,
            string backupModsRoot,
            IEnumerable<(string DisplayName, string RealName, string Status)> mods,
            bool enabledOnly,
            CancellationToken cancellationToken = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var pathToMods = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            int totalAssets = 0;
            var modsList = mods.Where(m => !enabledOnly || m.Status == "已启用").Distinct().ToList();

            foreach (var mod in modsList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var modDir = mod.Status == "已启用" ? Path.Combine(enabledModsRoot, mod.RealName) : Path.Combine(backupModsRoot, mod.RealName);
                if (!Directory.Exists(modDir)) continue;

                try
                {
                    var assetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    using (var provider = new DefaultFileProvider(modDir, SearchOption.AllDirectories, _version, StringComparer.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"[ConflictService] ProviderType={provider.GetType().FullName}");
                        var paks = Directory.GetFiles(modDir, "*.pak", SearchOption.AllDirectories);
                        var utocs = Directory.GetFiles(modDir, "*.utoc", SearchOption.AllDirectories);
                        RegisterVfsReaders(provider, paks.Concat(utocs));
                        try { provider.Mount(); } catch { }
                        try { provider.Mount(); } catch { }
                        try { provider.Initialize(); } catch { }
                        foreach (var kv in provider.Files)
                        {
                            var gf = kv.Value;
                            if (gf.IsUePackage)
                                assetPaths.Add(gf.PathWithoutExtension);
                        }
                    }
                    if (assetPaths.Count == 0)
                    {
                        foreach (var f in Directory.EnumerateFiles(modDir, "*.*", SearchOption.AllDirectories).Where(f => f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)))
                        {
                            var rel = Path.GetRelativePath(modDir, f).Replace('\\','/');
                            var noext = rel.Contains('.') ? rel.Substring(0, rel.LastIndexOf('.')) : rel;
                            assetPaths.Add(noext);
                        }
                    }
                    Console.WriteLine($"[ConflictService] {mod.RealName} 采集到资源条目: {assetPaths.Count}");
                    totalAssets += assetPaths.Count;
                    foreach (var a in assetPaths)
                    {
                        if (!pathToMods.TryGetValue(a, out var set)) { set = new HashSet<string>(StringComparer.OrdinalIgnoreCase); pathToMods[a] = set; }
                        set.Add(mod.RealName);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ConflictService] 解析失败: {mod.RealName} - {ex.Message}");
                }
            }

            var conflicts = new List<ConflictEntry>();
            foreach (var kv in pathToMods)
            {
                if (kv.Value.Count > 1)
                    conflicts.Add(new ConflictEntry { AssetPath = kv.Key, Mods = kv.Value.OrderBy(n=>n, StringComparer.OrdinalIgnoreCase).ToList() });
            }
            var summaries = modsList.Select(m => m.RealName).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => {
                    var related = conflicts.Where(c => c.Mods.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
                    return new ModConflictSummary{ ModName = name, ConflictCount = related.Count, ConflictAssetsTop5 = related.Select(r=>r.AssetPath).Take(5).ToList() };
                })
                .OrderByDescending(s => s.ConflictCount)
                .ToList();

            sw.Stop();
            return new ModConflictResult
            {
                ScannedMods = modsList.Count,
                TotalAssets = totalAssets,
                ConflictAssets = conflicts.Count,
                Conflicts = conflicts.OrderBy(c => c.AssetPath, StringComparer.OrdinalIgnoreCase).ToList(),
                Summaries = summaries,
                ModeDescription = enabledOnly ? "仅扫描已启用MOD" : "扫描全部MOD (已启用+备份库)",
                Elapsed = sw.Elapsed
            };
        }
    }
}
