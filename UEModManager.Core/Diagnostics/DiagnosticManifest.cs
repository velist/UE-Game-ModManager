using System;
using System.Collections.Generic;
using System.Linq;

namespace UEModManager.Diagnostics
{
    /// <summary>
    /// 诊断包条目：描述一个待加入诊断 zip 的文件来源。
    /// </summary>
    public sealed record DiagnosticEntry(
        string SourcePath,
        string ZipEntryName,
        bool RequiresRedaction);

    /// <summary>
    /// 诊断包清单：所有要打包的文件 + 用于 zip 顶部的元信息。
    /// </summary>
    public sealed class DiagnosticManifest
    {
        public string AppVersion { get; init; } = "";
        public string OsVersion { get; init; } = "";
        public string DotNetVersion { get; init; } = "";
        public string CurrentGame { get; init; } = "";
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
        public List<DiagnosticEntry> Entries { get; init; } = [];

        /// <summary>
        /// 渲染为 metadata.txt 内容（人类可读，UTF-8）。
        /// </summary>
        public string ToMetadataText()
        {
            var lines = new List<string>
            {
                $"# UEModManager Diagnostic Bundle",
                $"Created (UTC): {CreatedAt:yyyy-MM-ddTHH:mm:ssZ}",
                $"App Version: {AppVersion}",
                $"OS: {OsVersion}",
                $".NET: {DotNetVersion}",
                $"Current Game: {CurrentGame}",
                "",
                $"# Entries ({Entries.Count})",
            };
            lines.AddRange(Entries.Select(e =>
                $"- {e.ZipEntryName}{(e.RequiresRedaction ? "  [redacted]" : "")}    ({e.SourcePath})"));
            return string.Join("\n", lines) + "\n";
        }
    }

    /// <summary>
    /// 诊断清单构建器（纯函数）。
    ///
    /// 收集者（主项目 DiagnosticExportService）调用 <see cref="Build"/>，
    /// 传入"游戏数据目录中的可见文件路径"+"程序目录下的日志路径"，
    /// 由这里决定哪些进入诊断包、用什么 zip 内路径、是否需要脱敏。
    /// </summary>
    public static class DiagnosticManifestBuilder
    {
        /// <summary>
        /// 构造 manifest。所有路径过滤/分类逻辑在此集中。
        /// </summary>
        /// <param name="logFiles">主程序目录下能找到的日志文件绝对路径列表（包含当前 + 历史轮转）。</param>
        /// <param name="dataFiles">Data/ 目录下命中的 JSON 文件路径列表（profiles/packages/categories/overrides/overwrites）。</param>
        /// <param name="recentTransactionFiles">最近事务的 transaction.json 路径列表。</param>
        /// <param name="appVersion">主程序版本号。</param>
        /// <param name="osVersion">操作系统版本（Environment.OSVersion）。</param>
        /// <param name="dotNetVersion">.NET 运行时版本。</param>
        /// <param name="currentGame">当前选中的游戏名（可空）。</param>
        public static DiagnosticManifest Build(
            IReadOnlyList<string> logFiles,
            IReadOnlyList<string> dataFiles,
            IReadOnlyList<string> recentTransactionFiles,
            string appVersion,
            string osVersion,
            string dotNetVersion,
            string? currentGame)
        {
            var entries = new List<DiagnosticEntry>();

            foreach (var path in logFiles ?? [])
            {
                var name = System.IO.Path.GetFileName(path);
                entries.Add(new DiagnosticEntry(path, $"logs/{name}", RequiresRedaction: true));
            }

            foreach (var path in dataFiles ?? [])
            {
                var name = System.IO.Path.GetFileName(path);
                // Data 文件可能含游戏路径但不应有 token/邮箱；保守起见也走脱敏
                entries.Add(new DiagnosticEntry(path, $"data/{name}", RequiresRedaction: true));
            }

            foreach (var path in recentTransactionFiles ?? [])
            {
                var name = System.IO.Path.GetFileName(path);
                var dirName = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path) ?? "tx");
                entries.Add(new DiagnosticEntry(path, $"transactions/{dirName}_{name}", RequiresRedaction: false));
            }

            return new DiagnosticManifest
            {
                AppVersion = appVersion,
                OsVersion = osVersion,
                DotNetVersion = dotNetVersion,
                CurrentGame = currentGame ?? "(none)",
                Entries = entries
            };
        }
    }
}
