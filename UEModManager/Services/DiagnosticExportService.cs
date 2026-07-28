using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Diagnostics;
using UEModManager.Infrastructure;
using UEModManager.Logging;

namespace UEModManager.Services
{
    /// <summary>
    /// 诊断包导出服务（IO 层）。
    ///
    /// 职责：
    /// - 收集 <see cref="AppPaths.LogsDirectory"/> 下的 console.log + 轮转日志 + XAML 错误日志
    /// - 收集 <see cref="AppPaths.DataDirectory"/> 下的 JSON 索引
    /// - 收集 <see cref="AppPaths.DeploymentBackupsDirectory"/> 下最近若干个 transaction.json
    /// - 同时把旧位置（exe 旁）的同类文件按 <c>legacy/</c> 前缀一并收进来，理由见
    ///   <see cref="AppendLegacyEntries"/>
    /// - 委托 <see cref="DiagnosticManifestBuilder"/> 决定如何打包
    /// - 实际读文件 + 调 <see cref="LogRedactor"/> 脱敏 + 写 zip
    /// </summary>
    public class DiagnosticExportService
    {
        private const int MaxRotatedLogs = 5;
        private const int MaxRecentTransactions = 10;

        private readonly ILogger<DiagnosticExportService> _logger;
        private readonly GameConfigService _gameConfigService;
        private readonly HealthCheckService? _healthCheck;

        public DiagnosticExportService(
            ILogger<DiagnosticExportService> logger,
            GameConfigService gameConfigService,
            HealthCheckService? healthCheck = null)
        {
            _logger = logger;
            _gameConfigService = gameConfigService;
            _healthCheck = healthCheck;
        }

        /// <summary>
        /// 导出诊断包到指定路径。返回包含的条目数量（含 metadata.txt）。
        /// </summary>
        public async Task<int> ExportToZipAsync(string outputZipPath)
        {
            if (string.IsNullOrWhiteSpace(outputZipPath))
                throw new ArgumentException("Output path required", nameof(outputZipPath));

            // 采集根一律走 AppPaths：日志与数据已迁到 %LOCALAPPDATA%\UEModManager，
            // 继续按 AppDomain.BaseDirectory 拼路径只会捞到一个空目录，
            // 而"诊断包是空的"这件事本身没人会去核对，最后是拿着一份没有证据的包排障。
            var logFiles = CollectLogFiles(AppPaths.LogsDirectory);
            var dataFiles = CollectDataFiles(AppPaths.DataDirectory);
            var txFiles = CollectRecentTransactions(AppPaths.DeploymentBackupsDirectory);

            var currentGame = _gameConfigService.CurrentGameName;
            var manifest = DiagnosticManifestBuilder.Build(
                logFiles: logFiles,
                dataFiles: dataFiles,
                recentTransactionFiles: txFiles,
                appVersion: GetAppVersion(),
                osVersion: Environment.OSVersion.VersionString,
                dotNetVersion: Environment.Version.ToString(),
                currentGame: currentGame);

            AppendLegacyEntries(manifest);

            // 确保输出目录存在
            var outDir = Path.GetDirectoryName(outputZipPath);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);

            // 旧文件先删
            if (File.Exists(outputZipPath)) File.Delete(outputZipPath);

            int writtenCount = 0;
            using (var zipStream = File.Create(outputZipPath))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                // metadata.txt 先写
                await WriteTextEntryAsync(archive, "metadata.txt", manifest.ToMetadataText());
                writtenCount++;

                // 健康报告：实时跑一次，作为 health-report.txt 加入诊断包
                if (_healthCheck != null)
                {
                    try
                    {
                        var report = await _healthCheck.CheckAsync();
                        await WriteTextEntryAsync(archive, "health-report.txt", report.ToText());
                        writtenCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Diagnostic export: health report failed, skipped");
                    }
                }

                foreach (var entry in manifest.Entries)
                {
                    try
                    {
                        if (!File.Exists(entry.SourcePath))
                        {
                            _logger.LogDebug("Diagnostic export: missing source skipped {Path}", entry.SourcePath);
                            continue;
                        }

                        if (entry.RequiresRedaction)
                        {
                            var raw = await File.ReadAllTextAsync(entry.SourcePath);
                            await WriteTextEntryAsync(archive, entry.ZipEntryName, LogRedactor.Redact(raw));
                        }
                        else
                        {
                            await CopyFileEntryAsync(archive, entry.SourcePath, entry.ZipEntryName);
                        }
                        writtenCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Diagnostic export: failed to add {Entry}", entry.ZipEntryName);
                    }
                }
            }

            _logger.LogInformation(
                "Diagnostic bundle exported: {Path} ({Count} entries)", outputZipPath, writtenCount);
            return writtenCount;
        }

        // ─── 收集 ───

        /// <summary>
        /// 收集一个日志目录：当前 console.log、最近若干份轮转日志、以及 XAML 运行时错误日志。
        /// XamlErrorTracking.log 与 console.log 现在同处 <see cref="AppPaths.LogsDirectory"/>，
        /// 漏掉它等于在排查界面崩溃时把唯一一份 XAML 解析错误记录留在用户机器上。
        /// </summary>
        private static List<string> CollectLogFiles(string logDir)
        {
            var result = new List<string>();
            if (!Directory.Exists(logDir)) return result;

            var current = Path.Combine(logDir, "console.log");
            if (File.Exists(current)) result.Add(current);

            var xaml = Path.Combine(logDir, "XamlErrorTracking.log");
            if (File.Exists(xaml)) result.Add(xaml);

            result.AddRange(Directory.GetFiles(logDir, "console_*.log")
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .Take(MaxRotatedLogs));

            return result;
        }

        private static List<string> CollectDataFiles(string dataDir)
        {
            if (!Directory.Exists(dataDir)) return [];

            // 收集所有 JSON 索引（profiles/packages/categories/overrides/overwrites）
            return Directory.GetFiles(dataDir, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f)
                .ToList();
        }

        private static List<string> CollectRecentTransactions(string backupsDir)
        {
            if (!Directory.Exists(backupsDir)) return [];

            var transactions = Directory.GetDirectories(backupsDir)
                .Select(d => new
                {
                    Path = Path.Combine(d, "transaction.json"),
                    Time = Directory.GetLastWriteTimeUtc(d)
                })
                .Where(x => File.Exists(x.Path))
                .OrderByDescending(x => x.Time)
                .Take(MaxRecentTransactions)
                .Select(x => x.Path)
                .ToList();

            return transactions;
        }

        /// <summary>
        /// 追加旧位置（exe 旁）的同类文件，zip 内统一挂在 <c>legacy/</c> 下。
        ///
        /// 为什么新旧都采而不是只采新位置：搬迁动作至今没有真正执行
        /// （<c>DataLocationMigrator.RelocationExecutionEnabled</c> 仍为 false），
        /// 老用户机器上的数据实际还全在安装目录里；就算开关打开了，搬迁也可能中途失败、
        /// 或因空间不足降级为原地保留。诊断包的全部价值就在这些"没按预期发生"的时刻，
        /// 只采新位置恰恰会在最需要证据的场景下交出一个空包。
        /// 另外 <c>App.SetupFileLogging</c> 在新日志目录建不出来时会退回安装目录写日志，
        /// 那种情况下旧位置就是唯一有日志的地方。
        ///
        /// 前缀不能省：<see cref="DiagnosticManifestBuilder"/> 的条目名只取文件名，
        /// 新旧两处的 console.log / {game}_mods.json 完全同名，直接混在一起会让 zip 里
        /// 出现两个同名条目——解压时互相覆盖，且分不清剩下的那个来自哪边。
        /// 重写条目名放在这里而不是 Core，是因为"旧位置"是一段过渡期状态，
        /// 不值得让纯函数的命名规则长期背着它。
        /// </summary>
        private static void AppendLegacyEntries(DiagnosticManifest manifest)
        {
            var logs = CollectLegacy(AppPaths.Legacy.InstallDirectory, AppPaths.LogsDirectory, CollectLogFiles);
            var data = CollectLegacy(AppPaths.Legacy.DataDirectory, AppPaths.DataDirectory, CollectDataFiles);
            var tx = CollectLegacy(
                AppPaths.Legacy.DeploymentBackupsDirectory,
                AppPaths.DeploymentBackupsDirectory,
                CollectRecentTransactions);

            if (logs.Count == 0 && data.Count == 0 && tx.Count == 0) return;

            var legacy = DiagnosticManifestBuilder.Build(
                logFiles: logs,
                dataFiles: data,
                recentTransactionFiles: tx,
                appVersion: manifest.AppVersion,
                osVersion: manifest.OsVersion,
                dotNetVersion: manifest.DotNetVersion,
                currentGame: manifest.CurrentGame);

            manifest.Entries.AddRange(
                legacy.Entries.Select(e => e with { ZipEntryName = $"legacy/{e.ZipEntryName}" }));
        }

        /// <summary>
        /// 采集旧目录；若它与新目录其实是同一个（用户把数据放回了安装目录，
        /// 或日志因新目录不可写而退回安装目录），返回空，避免同一份文件被收两遍。
        /// </summary>
        private static List<string> CollectLegacy(
            string legacyDir, string currentDir, Func<string, List<string>> collect)
            => IsSameDirectory(legacyDir, currentDir) ? [] : collect(legacyDir);

        private static bool IsSameDirectory(string a, string b)
        {
            try
            {
                return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // 路径非法时按"不同目录"处理：多采一次远好过漏采
                return false;
            }

            static string Normalize(string p) =>
                Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        // ─── ZIP 写入 ───

        private static async Task WriteTextEntryAsync(ZipArchive archive, string entryName, string content)
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using var s = entry.Open();
            using var writer = new StreamWriter(s, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await writer.WriteAsync(content);
        }

        private static async Task CopyFileEntryAsync(ZipArchive archive, string sourcePath, string entryName)
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            await using var entryStream = entry.Open();
            await using var fileStream = File.OpenRead(sourcePath);
            await fileStream.CopyToAsync(entryStream);
        }

        private static string GetAppVersion()
        {
            try
            {
                return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
            }
            catch { return "unknown"; }
        }
    }
}
