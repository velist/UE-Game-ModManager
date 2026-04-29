using System.Collections.Generic;
using UEModManager.Models;

namespace UEModManager.Services.Config
{
    /// <summary>
    /// 配置文件解析器接口。
    /// 每种配置格式（INI/JSON/CFG）实现此接口。
    /// </summary>
    public interface IConfigParser
    {
        /// <summary>支持的格式。</summary>
        ConfigFormat Format { get; }

        /// <summary>
        /// 解析配置文件内容为键值对列表。
        /// </summary>
        List<ConfigEntry> Parse(string content);

        /// <summary>
        /// 将键值对列表序列化回配置文件内容。
        /// </summary>
        string Serialize(List<ConfigEntry> entries);

        /// <summary>
        /// 检测文件内容是否为此格式。
        /// </summary>
        bool CanParse(string content);
    }
}
