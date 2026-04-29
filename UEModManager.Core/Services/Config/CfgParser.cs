using System.Collections.Generic;
using System.Text;
using UEModManager.Models;

namespace UEModManager.Services.Config
{
    /// <summary>
    /// CFG 文件解析器。
    /// 支持简单 Key=Value 格式（无 Section），常用于通用游戏配置。
    /// </summary>
    public class CfgParser : IConfigParser
    {
        public ConfigFormat Format => ConfigFormat.Cfg;

        public List<ConfigEntry> Parse(string content)
        {
            var entries = new List<ConfigEntry>();

            foreach (var rawLine in content.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r').Trim();
                if (string.IsNullOrEmpty(line)) continue;

                if (line.StartsWith(';') || line.StartsWith('#') || line.StartsWith("//"))
                {
                    entries.Add(new ConfigEntry { Key = "", Value = "", Comment = line });
                    continue;
                }

                var eqIdx = line.IndexOf('=');
                if (eqIdx > 0)
                {
                    entries.Add(new ConfigEntry
                    {
                        Section = "",
                        Key = line[..eqIdx].Trim(),
                        Value = line[(eqIdx + 1)..].Trim()
                    });
                }
            }

            return entries;
        }

        public string Serialize(List<ConfigEntry> entries)
        {
            var sb = new StringBuilder();
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.Key) && entry.Comment != null)
                    sb.AppendLine(entry.Comment);
                else if (!string.IsNullOrEmpty(entry.Key))
                    sb.AppendLine($"{entry.Key}={entry.Value}");
            }
            return sb.ToString();
        }

        public bool CanParse(string content)
        {
            var lines = content.Split('\n', 5);
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.Contains('=') && !line.StartsWith('[') && !line.StartsWith('{'))
                    return true;
            }
            return false;
        }
    }
}
