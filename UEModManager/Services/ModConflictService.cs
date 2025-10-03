using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Versions;

namespace UEModManager.Services
{
    // 冲突条目：某个资源路径被多个MOD提供
    public class ConflictEntry
    {
        public string AssetPath { get; set; } = string.Empty; // 如 /Game/Characters/Hero/HeroBody
        public List<string> Mods { get; set; } = new List<string>();
    }

    // 每个MOD的冲突摘要
    public class ModConflictSummary
    {
        // 展示给用户的名称（优先取实际容器文件名的基名，如 pak/utoc/ucas 主文件名），用于UI显示
        public string ModName { get; set; } = string.Empty;
        // 原始真实名称（RealName/目录名），用于徽章映射与内部匹配
        public string OriginalName { get; set; } = string.Empty;
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
        // 默认按 UE4.27 处理（剑星为UE4系）。可通过 config.json/环境变量覆盖。
        private readonly VersionContainer _defaultVersion = new VersionContainer(EGame.GAME_UE4_27);

        private VersionContainer ResolveVersion()
        {
            try
            {
                // 环境覆盖优先
                var env = Environment.GetEnvironmentVariable("UEMM_UE_VERSION");
                if (!string.IsNullOrWhiteSpace(env))
                {
                    var v = MapVersion(env.Trim());
                    Console.WriteLine($"[ConflictService] 使用环境指定UE版本: {env} -> {v}");
                    return new VersionContainer(v);
                }

                var cfgPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
                if (File.Exists(cfgPath))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cfgPath));
                    if (doc.RootElement.TryGetProperty("UEVersion", out var verProp))
                    {
                        var s = verProp.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                        {
                            var v = MapVersion(s.Trim());
                            Console.WriteLine($"[ConflictService] 使用配置指定UE版本: {s} -> {v}");
                            return new VersionContainer(v);
                        }
                    }
                }
            }
            catch { }
            Console.WriteLine("[ConflictService] 使用默认UE版本: UE4_27");
            return _defaultVersion;
        }

        private EGame MapVersion(string s)
        {
            return s.ToUpperInvariant() switch
            {
                "UE4_25" => EGame.GAME_UE4_25,
                "UE4_26" => EGame.GAME_UE4_26,
                "UE4_27" => EGame.GAME_UE4_27,
                "UE5_0" or "UE5" => EGame.GAME_UE5_0,
                "UE5_1" => EGame.GAME_UE5_1,
                "UE5_2" => EGame.GAME_UE5_2,
                "UE5_3" => EGame.GAME_UE5_3,
                _ => EGame.GAME_UE4_27
            };
        }

        // 利用 CUE4Parse 的 RegisterVfs/Mount 注册 pak/utoc（兼容不同版本签名）
        private void RegisterVfsReaders(object provider, IEnumerable<string> vfsPaths)
        {
            try
            {
                var tProv = provider.GetType();
                var methods = tProv.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var mRegisterFileInfo = methods.FirstOrDefault(mi => mi.Name == "RegisterVfs" && mi.GetParameters().Length >= 1 && mi.GetParameters()[0].ParameterType == typeof(FileInfo));
                var mMountFileInfo = methods.FirstOrDefault(mi => mi.Name == "Mount" && mi.GetParameters().Length >= 1 && mi.GetParameters()[0].ParameterType == typeof(FileInfo));
                var mMountString = methods.FirstOrDefault(mi => mi.Name == "Mount" && mi.GetParameters().Length >= 1 && mi.GetParameters()[0].ParameterType == typeof(string));
                var mMountAsyncFileInfo = methods.FirstOrDefault(mi => mi.Name == "MountAsync" && mi.GetParameters().Length >= 1 && mi.GetParameters()[0].ParameterType == typeof(FileInfo));
                var mMountAsyncString = methods.FirstOrDefault(mi => mi.Name == "MountAsync" && mi.GetParameters().Length >= 1 && mi.GetParameters()[0].ParameterType == typeof(string));

                if (mRegisterFileInfo == null && mMountFileInfo == null && mMountString == null && mMountAsyncFileInfo == null && mMountAsyncString == null)
                {
                    Console.WriteLine("[ConflictService] 未找到 RegisterVfs/Mount 相关方法，可能CUE4Parse版本API变更");
                }

                foreach (var path in vfsPaths)
                {
                    try
                    {
                        bool mounted = false;
                        // 1) RegisterVfs(FileInfo)
                        if (mRegisterFileInfo != null)
                        {
                            try
                            {
                                var ret = mRegisterFileInfo.Invoke(provider, new object[] { new FileInfo(path) });
                                (ret as System.Threading.Tasks.Task)?.Wait(500);
                                mounted = true;
                                Console.WriteLine($"[ConflictService] RegisterVfs 成功: {path}");
                            }
                            catch (Exception rex)
                            {
                                Console.WriteLine($"[ConflictService] RegisterVfs 失败: {path} -> {rex.Message}");
                            }
                        }

                        // 2) Mount(FileInfo)
                        if (!mounted && mMountFileInfo != null)
                        {
                            try
                            {
                                var ret = mMountFileInfo.Invoke(provider, new object[] { new FileInfo(path) });
                                (ret as System.Threading.Tasks.Task)?.Wait(500);
                                mounted = true;
                                Console.WriteLine($"[ConflictService] Mount(FileInfo) 成功: {path}");
                            }
                            catch (Exception mex)
                            {
                                Console.WriteLine($"[ConflictService] Mount(FileInfo) 失败: {path} -> {mex.Message}");
                            }
                        }

                        // 3) Mount(string)
                        if (!mounted && mMountString != null)
                        {
                            try
                            {
                                var ret = mMountString.Invoke(provider, new object[] { path });
                                (ret as System.Threading.Tasks.Task)?.Wait(500);
                                mounted = true;
                                Console.WriteLine($"[ConflictService] Mount(string) 成功: {path}");
                            }
                            catch (Exception mex2)
                            {
                                Console.WriteLine($"[ConflictService] Mount(string) 失败: {path} -> {mex2.Message}");
                            }
                        }

                        // 4) MountAsync(FileInfo)
                        if (!mounted && mMountAsyncFileInfo != null)
                        {
                            try
                            {
                                var ret = mMountAsyncFileInfo.Invoke(provider, new object[] { new FileInfo(path) });
                                (ret as System.Threading.Tasks.Task)?.Wait(800);
                                mounted = true;
                                Console.WriteLine($"[ConflictService] MountAsync(FileInfo) 成功: {path}");
                            }
                            catch (Exception amex)
                            {
                                Console.WriteLine($"[ConflictService] MountAsync(FileInfo) 失败: {path} -> {amex.Message}");
                            }
                        }

                        // 5) MountAsync(string)
                        if (!mounted && mMountAsyncString != null)
                        {
                            try
                            {
                                var ret = mMountAsyncString.Invoke(provider, new object[] { path });
                                (ret as System.Threading.Tasks.Task)?.Wait(800);
                                mounted = true;
                                Console.WriteLine($"[ConflictService] MountAsync(string) 成功: {path}");
                            }
                            catch (Exception amex2)
                            {
                                Console.WriteLine($"[ConflictService] MountAsync(string) 失败: {path} -> {amex2.Message}");
                            }
                        }

                        if (!mounted)
                        {
                            Console.WriteLine($"[ConflictService] 未能挂载: {path}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ConflictService] 尝试挂载异常: {path} -> {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ConflictService] RegisterVfsReaders 异常: {ex.Message}");
            }
        }

        // 强类型挂载：直接构造 Pak/IoStore Reader 并 Mount(reader)
        private void StrongTypeMount(object provider, IEnumerable<string> pakPaths, IEnumerable<string> utocPaths, VersionContainer version)
        {
            try
            {
                var tProv = provider.GetType();
                var asm = tProv.Assembly;

                // 查找潜在的 Reader 与接口
                var tPakReader = asm.GetTypes().FirstOrDefault(t => t.Name.IndexOf("Pak", StringComparison.OrdinalIgnoreCase) >= 0 && t.Name.IndexOf("Reader", StringComparison.OrdinalIgnoreCase) >= 0);
                var tIoReader = asm.GetTypes().FirstOrDefault(t => t.Name.IndexOf("IoStore", StringComparison.OrdinalIgnoreCase) >= 0 && t.Name.IndexOf("Reader", StringComparison.OrdinalIgnoreCase) >= 0);
                var tVfsInterface = asm.GetTypes().FirstOrDefault(t => t.Name.IndexOf("IVfs", StringComparison.OrdinalIgnoreCase) >= 0 && t.IsInterface);

                var mMountReader = tProv
                    .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .FirstOrDefault(mi => mi.Name == "Mount" && mi.GetParameters().Length == 1 &&
                                          (tVfsInterface == null || mi.GetParameters()[0].ParameterType == tVfsInterface || mi.GetParameters()[0].ParameterType.IsInterface));

                object? TryCreateReader(Type? tReader, string path, string? pairPath = null)
                {
                    if (tReader == null) return null;
                    foreach (var ctor in tReader.GetConstructors())
                    {
                        var ps = ctor.GetParameters();
                        try
                        {
                            // (string path, VersionContainer)
                            if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(VersionContainer))
                                return ctor.Invoke(new object[] { path, version });
                            // (FileInfo file, VersionContainer)
                            if (ps.Length == 2 && ps[0].ParameterType == typeof(FileInfo) && ps[1].ParameterType == typeof(VersionContainer))
                                return ctor.Invoke(new object[] { new FileInfo(path), version });
                            // (string utoc, string ucas, VersionContainer)
                            if (pairPath != null && ps.Length == 3 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(string) && ps[2].ParameterType == typeof(VersionContainer))
                                return ctor.Invoke(new object[] { path, pairPath, version });
                            // (FileInfo utoc, FileInfo ucas, VersionContainer)
                            if (pairPath != null && ps.Length == 3 && ps[0].ParameterType == typeof(FileInfo) && ps[1].ParameterType == typeof(FileInfo) && ps[2].ParameterType == typeof(VersionContainer))
                                return ctor.Invoke(new object[] { new FileInfo(path), new FileInfo(pairPath), version });
                            // (string)
                            if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                                return ctor.Invoke(new object[] { path });
                            // (FileInfo)
                            if (ps.Length == 1 && ps[0].ParameterType == typeof(FileInfo))
                                return ctor.Invoke(new object[] { new FileInfo(path) });
                        }
                        catch { }
                    }
                    return null;
                }

                bool TryMountReader(object reader)
                {
                    try
                    {
                        // 优先 Mount(IVfs) 这类签名
                        foreach (var mi in tProv.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                        {
                            if (mi.Name != "Mount" || mi.GetParameters().Length != 1) continue;
                            var pt = mi.GetParameters()[0].ParameterType;
                            if (pt.IsInstanceOfType(reader) || pt.IsAssignableFrom(reader.GetType()) || (tVfsInterface != null && pt == tVfsInterface))
                            {
                                var ret = mi.Invoke(provider, new object[] { reader });
                                (ret as System.Threading.Tasks.Task)?.Wait(800);
                                return true;
                            }
                        }
                        // 兜底失败
                        return false;
                    }
                    catch (Exception mex)
                    {
                        Console.WriteLine($"[ConflictService] Mount(reader) 异常: {mex.Message}");
                        return false;
                    }
                }

                // 处理 PAK
                foreach (var pak in pakPaths)
                {
                    try
                    {
                        var reader = TryCreateReader(tPakReader, pak);
                        if (reader != null && TryMountReader(reader))
                            Console.WriteLine($"[ConflictService] 强类型挂载 PAK 成功: {pak}");
                        else
                            Console.WriteLine($"[ConflictService] 强类型挂载 PAK 失败: {pak}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ConflictService] 强类型PAK异常: {pak} -> {ex.Message}");
                    }
                }

                // 处理 IoStore (.utoc + .ucas)
                foreach (var utoc in utocPaths)
                {
                    try
                    {
                        string? ucas = null;
                        try
                        {
                            var dir = Path.GetDirectoryName(utoc)!;
                            var baseName = Path.GetFileNameWithoutExtension(utoc);
                            // 常见命名：xxx.utoc 对应 xxx.ucas
                            var candidate = Path.Combine(dir, baseName + ".ucas");
                            if (File.Exists(candidate)) ucas = candidate;
                            else
                            {
                                // 备选：同目录任意 .ucas 作为兜底
                                ucas = Directory.EnumerateFiles(dir, "*.ucas", SearchOption.TopDirectoryOnly).FirstOrDefault();
                            }
                        }
                        catch { }

                        var reader = TryCreateReader(tIoReader, utoc, ucas);
                        if (reader != null && TryMountReader(reader))
                            Console.WriteLine($"[ConflictService] 强类型挂载 IoStore 成功: {utoc} (ucas={ucas})");
                        else
                            Console.WriteLine($"[ConflictService] 强类型挂载 IoStore 失败: {utoc} (ucas={ucas})");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ConflictService] 强类型IoStore异常: {utoc} -> {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ConflictService] StrongTypeMount 异常: {ex.Message}");
            }
        }

        // 从 config.json 读取 AES Key（可选）
        private string? LoadAesKeyFromConfig()
        {
            try
            {
                var cfg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
                if (!File.Exists(cfg)) return null;
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cfg));
                if (doc.RootElement.TryGetProperty("AesKey", out var keyProp))
                {
                    var v = keyProp.GetString();
                    if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
                }
                if (doc.RootElement.TryGetProperty("AESKey", out keyProp))
                {
                    var v = keyProp.GetString();
                    if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
                }
            }
            catch { }
            return null;
        }

        // 通过反射提交 AES Key（IoStore 需要）
        private void TrySubmitAesKey(object provider)
        {
            try
            {
                var hex = LoadAesKeyFromConfig();
                if (string.IsNullOrWhiteSpace(hex)) { Console.WriteLine("[ConflictService] 未配置AES Key，跳过提交"); return; }
                var tProv = provider.GetType();
                var asm = tProv.Assembly;
                var tAes = asm.GetType("CUE4Parse.UE4.Objects.Core.Misc.FAesKey", throwOnError: false) ?? asm.GetType("CUE4Parse.UE4.AES.FAesKey", throwOnError: false);
                var tGuid = asm.GetType("CUE4Parse.UE4.Objects.Core.Misc.FGuid", throwOnError: false);
                if (tAes == null || tGuid == null) { Console.WriteLine("[ConflictService] 找不到 FAesKey/FGuid 类型"); return; }
                object? aesObj = null;
                foreach (var ctor in tAes.GetConstructors())
                {
                    var ps = ctor.GetParameters();
                    try
                    {
                        if (ps.Length == 1 && ps[0].ParameterType == typeof(string)) { aesObj = ctor.Invoke(new object[] { hex }); break; }
                        if (ps.Length == 1 && ps[0].ParameterType == typeof(byte[])) { aesObj = ctor.Invoke(new object[] { Convert.FromHexString(hex) }); break; }
                    }
                    catch { }
                }
                if (aesObj == null) { Console.WriteLine("[ConflictService] 无法构造 FAesKey"); return; }
                var guidObj = Activator.CreateInstance(tGuid);
                var mSubmitKey = tProv.GetMethods(System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)
                    .FirstOrDefault(mi => mi.Name.Contains("SubmitKey") && mi.GetParameters().Length == 2);
                if (mSubmitKey != null)
                {
                    try { var ret = mSubmitKey.Invoke(provider, new object[] { guidObj!, aesObj! }); (ret as System.Threading.Tasks.Task)?.Wait(300); Console.WriteLine("[ConflictService] SubmitKey 成功"); return; } catch (Exception ex) { Console.WriteLine("[ConflictService] SubmitKey 失败: " + ex.Message); }
                }
                var mSubmitKeys = tProv.GetMethods(System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)
                    .FirstOrDefault(mi => mi.Name.Contains("SubmitKeys") && mi.GetParameters().Length == 1);
                if (mSubmitKeys != null)
                {
                    try
                    {
                        var dictType = typeof(Dictionary<,>).MakeGenericType(new Type[]{ tGuid, tAes });
                        var dict = Activator.CreateInstance(dictType);
                        dictType.GetMethod("Add")!.Invoke(dict, new object[]{ guidObj!, aesObj! });
                        var ret = mSubmitKeys.Invoke(provider, new object[]{ dict! });
                        (ret as System.Threading.Tasks.Task)?.Wait(300);
                        Console.WriteLine("[ConflictService] SubmitKeys 成功");
                        return;
                    }
                    catch (Exception ex) { Console.WriteLine("[ConflictService] SubmitKeys 失败: " + ex.Message); }
                }
                Console.WriteLine("[ConflictService] 未找到可用的 SubmitKey(s) 方法");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[ConflictService] TrySubmitAesKey 异常: " + ex.Message);
            }
        }

        // 主检测逻辑
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
            // 去重：按 RealName 去重，避免同一MOD重复扫描（例如同名在不同来源）
            var modsList = mods.Where(m => !enabledOnly || m.Status == "已启用")
                               .GroupBy(m => m.RealName, StringComparer.OrdinalIgnoreCase)
                               .Select(g => g.First())
                               .ToList();

            // 为每个MOD预先计算“显示名”映射（RealName -> DisplayName）
            var displayNameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var mod in modsList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var modDir = mod.Status == "已启用" ? Path.Combine(enabledModsRoot, mod.RealName) : Path.Combine(backupModsRoot, mod.RealName);
                if (!Directory.Exists(modDir)) continue;

                try
                {
                    var assetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var version = ResolveVersion();
                    using (var provider = new DefaultFileProvider(modDir, SearchOption.AllDirectories, version, StringComparer.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"[ConflictService] ProviderType={provider.GetType().FullName}");
                        var paks = Directory.GetFiles(modDir, "*.pak", SearchOption.AllDirectories);
                        var utocs = Directory.GetFiles(modDir, "*.utoc", SearchOption.AllDirectories);
                        Console.WriteLine($"[ConflictService] {mod.RealName} pak数量={paks.Length} utoc数量={utocs.Length}");
                        // 收集允许的容器文件名（不含路径），用于从 provider.Files 过滤掉基础游戏条目
                        var allowedContainers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var p in paks) { try { allowedContainers.Add(Path.GetFileName(p)); } catch { } }
                        foreach (var u in utocs)
                        {
                            try { allowedContainers.Add(Path.GetFileName(u)); } catch { }
                            // 可能枚举到 ucas，提前加入同名 .ucas 作为容器匹配
                            try
                            {
                                var dir = Path.GetDirectoryName(u)!;
                                var baseName = Path.GetFileNameWithoutExtension(u);
                                var ucas = Path.Combine(dir, baseName + ".ucas");
                                if (File.Exists(ucas)) allowedContainers.Add(Path.GetFileName(ucas));
                            }
                            catch { }
                        }
                        // 先挂载基础游戏容器（如可用），再挂载模组容器，最后 Mount+Initialize
                        try
                        {
                            // 尝试从环境变量或 config.json 读取基础游戏路径
                            string? gameBasePath = Environment.GetEnvironmentVariable("UEMM_GAME_BASE");
                            if (string.IsNullOrWhiteSpace(gameBasePath))
                            {
                                var cfgPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
                                if (File.Exists(cfgPath))
                                {
                                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cfgPath));
                                    if (doc.RootElement.TryGetProperty("GameBasePath", out var gbp))
                                    {
                                        gameBasePath = gbp.GetString();
                                    }
                                }
                            }
                            if (!string.IsNullOrWhiteSpace(gameBasePath) && Directory.Exists(gameBasePath))
                            {
                                var basePakRoot = Path.Combine(gameBasePath, "Content", "Paks");
                                if (Directory.Exists(basePakRoot))
                                {
                                    var basePaks = Directory.GetFiles(basePakRoot, "*.pak", SearchOption.AllDirectories);
                                    var baseUtocs = Directory.GetFiles(basePakRoot, "*.utoc", SearchOption.AllDirectories);
                                    Console.WriteLine($"[ConflictService] 基础挂载: pak={basePaks.Length} utoc={baseUtocs.Length} (gameBasePath={gameBasePath})");
                                    StrongTypeMount(provider, basePaks, baseUtocs, version);
                                    RegisterVfsReaders(provider, basePaks.Concat(baseUtocs));
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[ConflictService] 基础容器处理异常: {ex.Message}");
                        }

                        // 模组容器
                        StrongTypeMount(provider, paks, utocs, version);
                        RegisterVfsReaders(provider, paks.Concat(utocs));
                        TrySubmitAesKey(provider);
                        try { provider.Mount(); Console.WriteLine("[ConflictService] provider.Mount() 完成"); } catch (Exception mex) { Console.WriteLine("[ConflictService] provider.Mount 异常: " + mex.Message); }
                        try { provider.Initialize(); Console.WriteLine("[ConflictService] provider.Initialize() 完成"); } catch (Exception iex) { Console.WriteLine("[ConflictService] Initialize 异常: " + iex.Message); }
                        Console.WriteLine($"[ConflictService] provider.Files 计数={provider.Files.Count}");
                        int addedFromContainers = 0;
                        foreach (var kv in provider.Files)
                        {
                            var gf = kv.Value;
                            if (!gf.IsUePackage) continue;
                            // 通过反射获取 gf.Vfs 对应的底层容器文件名（pak/utoc/ucas），仅统计来自当前模组容器的条目
                            try
                            {
                                string? containerName = null;
                                var tGf = gf.GetType();
                                var pVfs = tGf.GetProperty("Vfs", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                                var vfsObj = pVfs?.GetValue(gf);
                                if (vfsObj != null)
                                {
                                    var tV = vfsObj.GetType();
                                    // 常见：FilePath (string)
                                    var pFilePath = tV.GetProperty("FilePath")?.GetValue(vfsObj) as string;
                                    if (!string.IsNullOrEmpty(pFilePath)) containerName = Path.GetFileName(pFilePath);
                                    if (string.IsNullOrEmpty(containerName))
                                    {
                                        // 有的实现暴露 File (FileInfo)
                                        var pFile = tV.GetProperty("File")?.GetValue(vfsObj);
                                        var fileInfoPath = (pFile as System.IO.FileInfo)?.FullName;
                                        if (!string.IsNullOrEmpty(fileInfoPath)) containerName = Path.GetFileName(fileInfoPath);
                                    }
                                    if (string.IsNullOrEmpty(containerName))
                                    {
                                        // 兜底：Name/Path
                                        var pName = tV.GetProperty("Name")?.GetValue(vfsObj) as string;
                                        if (!string.IsNullOrEmpty(pName)) containerName = Path.GetFileName(pName);
                                        var pPath = tV.GetProperty("Path")?.GetValue(vfsObj) as string;
                                        if (string.IsNullOrEmpty(containerName) && !string.IsNullOrEmpty(pPath)) containerName = Path.GetFileName(pPath);
                                    }
                                }

                                if (string.IsNullOrEmpty(containerName))
                                {
                                    // 如果无法识别容器且当前存在允许容器列表，则跳过，避免把基础游戏条目计入
                                    if (allowedContainers.Count > 0) continue;
                                }
                                else
                                {
                                    if (allowedContainers.Count > 0 && !allowedContainers.Contains(containerName))
                                    {
                                        // 非当前模组容器的条目，跳过
                                        continue;
                                    }
                                }

                                assetPaths.Add(gf.PathWithoutExtension);
                                addedFromContainers++;
                            }
                            catch { }
                        }
                        Console.WriteLine($"[ConflictService] 过滤后加入条目: {addedFromContainers}");
                    }

                    // 回退：扫描解包后的 uasset/umap
                    if (assetPaths.Count == 0)
                    {
                        foreach (var f in Directory.EnumerateFiles(modDir, "*.*", SearchOption.AllDirectories)
                                  .Where(f => f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)))
                        {
                            var rel = Path.GetRelativePath(modDir, f).Replace('\\','/');
                            var noext = rel.Contains('.') ? rel.Substring(0, rel.LastIndexOf('.')) : rel;
                            assetPaths.Add(noext);
                        }
                    }

                    Console.WriteLine($"[ConflictService] {mod.RealName} 采集到资源条目: {assetPaths.Count}");
                    totalAssets += assetPaths.Count;

                    // 计算显示名：优先使用容器文件名（pak/utoc/ucas），否则回退为 RealName
                    try
                    {
                        var containerBase = InferPrimaryContainerBaseName(modDir);
                        var disp = !string.IsNullOrWhiteSpace(containerBase) ? containerBase : mod.RealName;
                        displayNameMap[mod.RealName] = disp;
                        Console.WriteLine($"[ConflictService] 显示名映射: {mod.RealName} -> {disp}");
                    }
                    catch { displayNameMap[mod.RealName] = mod.RealName; }

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

            // 生成冲突列表
            var conflicts = new List<ConflictEntry>();
            foreach (var kv in pathToMods)
            {
                if (kv.Value.Count > 1)
                {
                    // 使用显示名输出到明细
                    var dispMods = kv.Value
                        .Select(n => displayNameMap.TryGetValue(n, out var d) ? d : n)
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    conflicts.Add(new ConflictEntry { AssetPath = kv.Key, Mods = dispMods });
                }
            }

            // 每个MOD的冲突计数
            var summaries = modsList.Select(m => m.RealName).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => {
                    var related = conflicts.Where(c => c.Mods.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
                    // 此处 related 是按显示名匹配，需将统计改为以 RealName 统计
                    var count = pathToMods.Where(kv => kv.Value.Contains(name, StringComparer.OrdinalIgnoreCase)).Count(kv => kv.Value.Count > 1);
                    var disp = displayNameMap.TryGetValue(name, out var dname) ? dname : name;
                    return new ModConflictSummary{ ModName = disp, OriginalName = name, ConflictCount = count, ConflictAssetsTop5 = related.Select(r=>r.AssetPath).Take(5).ToList() };
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

        // 推断一个目录中的“主容器”文件名（不带扩展名）。优先 pak，其次 utoc/ucas，同名对按优先顺序。
        private static string? InferPrimaryContainerBaseName(string modDir)
        {
            try
            {
                // 优先 pak
                var paks = Directory.GetFiles(modDir, "*.pak", SearchOption.AllDirectories)
                                    .OrderByDescending(f => Path.GetFileNameWithoutExtension(f).Length)
                                    .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                                    .ToList();
                if (paks.Count > 0) return Path.GetFileNameWithoutExtension(paks.First());

                // 再看 utoc（若存在，通常配套 ucas）
                var utocs = Directory.GetFiles(modDir, "*.utoc", SearchOption.AllDirectories)
                                     .OrderByDescending(f => Path.GetFileNameWithoutExtension(f).Length)
                                     .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                                     .ToList();
                if (utocs.Count > 0) return Path.GetFileNameWithoutExtension(utocs.First());

                // 最后看 ucas（极少单独使用）
                var ucas = Directory.GetFiles(modDir, "*.ucas", SearchOption.AllDirectories)
                                    .OrderByDescending(f => Path.GetFileNameWithoutExtension(f).Length)
                                    .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                                    .ToList();
                if (ucas.Count > 0) return Path.GetFileNameWithoutExtension(ucas.First());
            }
            catch { }
            return null;
        }
    }
}

