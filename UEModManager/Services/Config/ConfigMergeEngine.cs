using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Models;

namespace UEModManager.Services.Config
{
    /// <summary>
    /// 配置合并引擎（IO 适配器）。
    ///
    /// 纯逻辑（解析/合并/冲突检测/来源追踪）已下沉到 UEModManager.Core 的 <see cref="ConfigMerger"/>，
    /// 此类只负责：
    /// - 注入 parser 注册表
    /// - 读取 ConfigMergeSource.SourceFilePath 文件内容
    /// - 调用 ConfigMerger.Merge 完成纯函数合并
    /// - 日志记录
    /// </summary>
    public class ConfigMergeEngine
    {
        private readonly ILogger<ConfigMergeEngine> _logger;
        private readonly Dictionary<ConfigFormat, IConfigParser> _parsers;

        public ConfigMergeEngine(ILogger<ConfigMergeEngine> logger)
        {
            _logger = logger;

            _parsers = new()
            {
                [ConfigFormat.Ini] = new IniParser(),
                [ConfigFormat.Json] = new JsonConfigParser(),
                [ConfigFormat.Cfg] = new CfgParser()
            };
        }

        /// <summary>检测配置文件格式（按扩展名）。</summary>
        public static ConfigFormat DetectFormat(string filePath)
            => ConfigMerger.DetectFormat(filePath);

        /// <summary>检测配置文件格式（按内容）。</summary>
        public ConfigFormat DetectFormatByContent(string content)
            => ConfigMerger.DetectFormatByContent(content, _parsers);

        /// <summary>
        /// 执行配置合并。先并行读取所有来源文件，再调用纯函数合并。
        /// </summary>
        public async Task<ConfigMergeResult> MergeAsync(ConfigMergePlan plan)
        {
            var sourceContents = await LoadSourceContentsAsync(plan);
            var result = ConfigMerger.Merge(plan, sourceContents, _parsers);

            if (result.Success)
            {
                _logger.LogInformation(
                    "Config merge completed: {Path}, {Entries} entries, {Conflicts} conflicts",
                    plan.TargetRelativePath, result.EntrySourceMap.Count, result.Conflicts.Count);
            }
            else
            {
                _logger.LogError(
                    "Config merge failed: {Path} — {Error}",
                    plan.TargetRelativePath, result.ErrorMessage);
            }

            return result;
        }

        /// <summary>
        /// 为配置文件生成合并预览（不实际写文件）。当前与 MergeAsync 等价。
        /// </summary>
        public Task<ConfigMergeResult> PreviewAsync(ConfigMergePlan plan) => MergeAsync(plan);

        // ─── IO 边界 ───

        private static async Task<Dictionary<string, string>> LoadSourceContentsAsync(ConfigMergePlan plan)
        {
            var contents = new Dictionary<string, string>();
            foreach (var source in plan.Sources)
            {
                if (string.IsNullOrEmpty(source.SourceFilePath)) continue;
                if (!File.Exists(source.SourceFilePath)) continue;

                contents[source.PackageKey] = await File.ReadAllTextAsync(source.SourceFilePath);
            }
            return contents;
        }
    }
}
