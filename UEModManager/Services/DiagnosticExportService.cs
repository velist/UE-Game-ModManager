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
using UEModManager.Logging;

namespace UEModManager.Services
{
    /// <summary>
    /// 诊断包导出服务（IO 层）。
    ///
    /// 职责：
    /// - 收集程序目录下的 console.log + 轮转日志
    /// - 收集 Data/ 目录下当前游戏的 JSON 索引
    /// - 收集 Data/Backups/{id}/transaction.json 最近若干个
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

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var dataDir = Path.Combine(baseDir, "Data");
            var backupsDir = Path.Combine(dataDir, "Backups");

            var logFiles = CollectLogFiles(baseDir);
            var dataFiles = CollectDataFiles(dataDir);
            var txFiles = CollectRecentTransactions(backupsDir);

            var currentGame = _gameConfigService.CurrentGameName;
            var manifest = DiagnosticManifestBuilder.Build(
                logFiles: logFiles,
                dataFiles: dataFiles,
                recentTransactionFiles: txFiles,
                appVersion: GetAppVersion(),
                osVersion: Environment.OSVersion.VersionString,
                dotNetVersion: Environment.Version.ToString(),
                currentGame: currentGame);

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

        private static List<string> CollectLogFiles(string baseDir)
        {
            var result = new List<string>();
            var current = Path.Combine(baseDir, "console.log");
            if (File.Exists(current)) result.Add(current);

            var rotated = Directory.Exists(baseDir)
                ? Directory.GetFiles(baseDir, "console_*.log")
                    .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                    .Take(MaxRotatedLogs)
                : [];

            result.AddRange(rotated);
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
