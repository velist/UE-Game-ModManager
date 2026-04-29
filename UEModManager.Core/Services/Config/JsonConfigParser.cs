using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UEModManager.Models;

namespace UEModManager.Services.Config
{
    /// <summary>
    /// JSON 配置文件解析器。
    /// 将嵌套 JSON 展平为 Section.Key 路径格式的键值对。
    /// </summary>
    public class JsonConfigParser : IConfigParser
    {
        public ConfigFormat Format => ConfigFormat.Json;

        public List<ConfigEntry> Parse(string content)
        {
            var entries = new List<ConfigEntry>();

            try
            {
                var token = JToken.Parse(content);
                FlattenToken(token, "", entries);
            }
            catch
            {
                // 解析失败，返回空
            }

            return entries;
        }

        public string Serialize(List<ConfigEntry> entries)
        {
            var root = new JObject();

            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.Key)) continue;

                var fullPath = string.IsNullOrEmpty(entry.Section)
                    ? entry.Key
                    : $"{entry.Section}.{entry.Key}";

                SetNestedValue(root, fullPath, entry.Value);
            }

            return root.ToString(Formatting.Indented);
        }

        public bool CanParse(string content)
        {
            var trimmed = content.TrimStart();
            return trimmed.StartsWith('{') || trimmed.StartsWith('[');
        }

        // ─── 内部方法 ───

        private static void FlattenToken(JToken token, string prefix, List<ConfigEntry> entries)
        {
            switch (token.Type)
            {
                case JTokenType.Object:
                    foreach (var prop in ((JObject)token).Properties())
                    {
                        var childPrefix = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";
                        FlattenToken(prop.Value, childPrefix, entries);
                    }
                    break;

                case JTokenType.Array:
                    var arr = (JArray)token;
                    for (int i = 0; i < arr.Count; i++)
                    {
                        FlattenToken(arr[i], $"{prefix}[{i}]", entries);
                    }
                    break;

                default:
                    // 叶子节点：拆分为 Section + Key
                    var lastDot = prefix.LastIndexOf('.');
                    var section = lastDot > 0 ? prefix[..lastDot] : "";
                    var key = lastDot > 0 ? prefix[(lastDot + 1)..] : prefix;

                    entries.Add(new ConfigEntry
                    {
                        Section = section,
                        Key = key,
                        Value = token.ToString()
                    });
                    break;
            }
        }

        private static void SetNestedValue(JObject root, string path, string value)
        {
            var parts = path.Split('.');
            var current = root;

            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (current[parts[i]] is not JObject child)
                {
                    child = new JObject();
                    current[parts[i]] = child;
                }
                current = child;
            }

            var lastKey = parts[^1];

            // 尝试保留原始类型
            if (bool.TryParse(value, out var boolVal))
                current[lastKey] = boolVal;
            else if (long.TryParse(value, out var longVal))
                current[lastKey] = longVal;
            else if (double.TryParse(value, out var doubleVal))
                current[lastKey] = doubleVal;
            else
                current[lastKey] = value;
        }
    }
}
