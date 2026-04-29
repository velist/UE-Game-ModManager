using System;
using System.Collections.Generic;
using System.Text;
using UEModManager.Models;

namespace UEModManager.Services.Config
{
    /// <summary>
    /// INI 文件解析器。
    /// 支持 [Section] / Key=Value / ; 注释 格式。
    /// UE 引擎的 DefaultEngine.ini / DefaultGame.ini 等使用此格式。
    /// </summary>
    public class IniParser : IConfigParser
    {
        public ConfigFormat Format => ConfigFormat.Ini;

        public List<ConfigEntry> Parse(string content)
        {
            var entries = new List<ConfigEntry>();
            var currentSection = "";

            foreach (var rawLine in content.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r').Trim();

                // 空行和注释跳过
                if (string.IsNullOrEmpty(line)) continue;
                if (line.StartsWith(';') || line.StartsWith('#'))
                {
                    entries.Add(new ConfigEntry
                    {
                        Section = currentSection,
                        Key = "",
                        Value = "",
                        Comment = line
                    });
                    continue;
                }

                // Section 头
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    currentSection = line[1..^1].Trim();
                    continue;
                }

                // Key=Value
                var eqIdx = line.IndexOf('=');
                if (eqIdx > 0)
                {
                    var key = line[..eqIdx].Trim();
                    var value = line[(eqIdx + 1)..].Trim();

                    // UE INI 特殊前缀处理（+, -, .）
                    string? comment = null;
                    var semicolonIdx = value.IndexOf(';');
                    if (semicolonIdx >= 0 && semicolonIdx < value.Length - 1)
                    {
                        comment = value[(semicolonIdx + 1)..].Trim();
                        value = value[..semicolonIdx].Trim();
                    }

                    entries.Add(new ConfigEntry
                    {
                        Section = currentSection,
                        Key = key,
                        Value = value,
                        Comment = comment
                    });
                }
            }

            return entries;
        }

        public string Serialize(List<ConfigEntry> entries)
        {
            var sb = new StringBuilder();
            var currentSection = (string?)null;

            foreach (var entry in entries)
            {
                // 纯注释行
                if (string.IsNullOrEmpty(entry.Key) && entry.Comment != null)
                {
                    sb.AppendLine(entry.Comment);
                    continue;
                }

                // Section 变化
                if (entry.Section != currentSection)
                {
                    if (currentSection != null) sb.AppendLine();
                    if (!string.IsNullOrEmpty(entry.Section))
                        sb.AppendLine($"[{entry.Section}]");
                    currentSection = entry.Section;
                }

                // Key=Value
                if (!string.IsNullOrEmpty(entry.Key))
                {
                    var line = $"{entry.Key}={entry.Value}";
                    if (!string.IsNullOrEmpty(entry.Comment))
                        line += $" ; {entry.Comment}";
                    sb.AppendLine(line);
                }
            }

            return sb.ToString();
        }

        public bool CanParse(string content)
        {
            // 简单检测：包含 [Section] 或 Key=Value 模式
            var lines = content.Split('\n', 10);
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.StartsWith('[') && line.Contains(']')) return true;
                if (line.Contains('=') && !line.StartsWith('{')) return true;
            }
            return false;
        }
    }
}
