using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ConflictProbe.Services;
using CUE4Parse.FileProvider;

class ProbeConfig { public string? ModPath { get; set; } public string? BackupPath { get; set; } }

class Program
{
    static void DumpCue4Parse()
    {
        try
        {
            var asm = typeof(DefaultFileProvider).Assembly;
            Console.WriteLine($"[Probe] CUE4Parse Assembly: {asm.FullName}");
            var types = asm.GetTypes();
            var hits = types.Where(t => (t.FullName??"").IndexOf("Pak", StringComparison.OrdinalIgnoreCase)>=0 || (t.FullName??"").IndexOf("IoStore", StringComparison.OrdinalIgnoreCase)>=0 || (t.FullName??"").IndexOf("Reader", StringComparison.OrdinalIgnoreCase)>=0 || (t.FullName??"").IndexOf("FileProvider", StringComparison.OrdinalIgnoreCase)>=0 ).Select(t=>t.FullName).OrderBy(n=>n).ToArray();
            var baseDir2 = AppDomain.CurrentDomain.BaseDirectory;
            var ummRoot = Path.GetFullPath(Path.Combine(baseDir2, "..", "..", "..", "..", ".."));
            var outFile = Path.Combine(ummRoot, "bin", "Debug", "net8.0-windows", "CUE4Parse.dump.txt");
            File.WriteAllLines(outFile, hits);
            Console.WriteLine($"[Probe] Dump写入: {outFile}");
            var tProv = typeof(DefaultFileProvider);
            foreach (var m in tProv.GetMethods(System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Static))
            {
                if (m.Name.IndexOf("Mount", StringComparison.OrdinalIgnoreCase)>=0)
                    Console.WriteLine("[Probe][Method] DefaultFileProvider." + m.ToString());
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Probe] DumpCue4Parse ERROR: " + ex.Message);
        }
    }

    static async Task<int> Main(string[] args)
    {
        DumpCue4Parse();
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var ummRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
            var cfgPath = Path.Combine(ummRoot, "bin", "Debug", "net8.0-windows", "config.json");
            Console.WriteLine($"[Probe] 使用配置: {cfgPath}");
            if (!File.Exists(cfgPath)) { Console.WriteLine("[Probe] 未找到 config.json"); return 2; }
            var cfg = JsonSerializer.Deserialize<ProbeConfig>(File.ReadAllText(cfgPath));
            var modRoot = cfg?.ModPath ?? string.Empty;
            var backupRoot = cfg?.BackupPath ?? string.Empty;
            Console.WriteLine($"[Probe] ModPath={modRoot}");
            Console.WriteLine($"[Probe] BackupPath={backupRoot}");
            if (string.IsNullOrEmpty(modRoot) || !Directory.Exists(modRoot)) { Console.WriteLine("[Probe] ModPath 无效"); return 3; }
            var mods = Directory.GetDirectories(modRoot).Select(d => (DisplayName: Path.GetFileName(d)!, RealName: Path.GetFileName(d)!, Status: "已启用"));
            var svc = new ModConflictService();
            var result = await svc.DetectConflictsAsync(modRoot, backupRoot, mods, enabledOnly: true);
            Console.WriteLine($"[Probe] 扫描完成: Mods={result.ScannedMods}, Assets={result.TotalAssets}, Conflicts={result.ConflictAssets}, 耗时={result.Elapsed.TotalSeconds:F1}s");
            var report = Path.Combine(modRoot, $"ConflictReport_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            File.WriteAllText(report, JsonSerializer.Serialize(result, new JsonSerializerOptions{ WriteIndented = true }));
            Console.WriteLine($"[Probe] 报告已写入: {report}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Probe][ERROR] " + ex);
            return 1;
        }
    }
}
