using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using UEModManager.Models;

namespace UEModManager.Adapters
{
    /// <summary>
    /// 宿主适配器注册表。
    /// 管理所有已注册的适配器，根据游戏名称或引擎类型查找最佳匹配。
    /// </summary>
    public class HostAdapterRegistry
    {
        private readonly ILogger<HostAdapterRegistry> _logger;
        private readonly Dictionary<string, IHostAdapter> _adapters = new(StringComparer.OrdinalIgnoreCase);
        private readonly IHostAdapter _fallback;

        public HostAdapterRegistry(
            ILogger<HostAdapterRegistry> logger,
            IEnumerable<IHostAdapter> adapters)
        {
            _logger = logger;
            _fallback = new GenericFileOverlayAdapter();

            foreach (var adapter in adapters)
            {
                _adapters[adapter.AdapterKey] = adapter;
                _logger.LogInformation("Registered host adapter: {Key} ({Name})", adapter.AdapterKey, adapter.DisplayName);
            }

            // 确保 fallback 存在
            if (!_adapters.ContainsKey(_fallback.AdapterKey))
                _adapters[_fallback.AdapterKey] = _fallback;
        }

        /// <summary>获取所有已注册适配器。</summary>
        public IReadOnlyList<IHostAdapter> GetAll() => _adapters.Values.ToList();

        /// <summary>按 AdapterKey 获取适配器。</summary>
        public IHostAdapter? GetByKey(string adapterKey)
        {
            return _adapters.TryGetValue(adapterKey, out var adapter) ? adapter : null;
        }

        /// <summary>
        /// 为指定游戏查找最佳匹配适配器。
        /// 优先匹配特定适配器，找不到则返回 fallback。
        /// </summary>
        public IHostAdapter Resolve(string gameName, EngineType? engineType = null)
        {
            var resolved = HostAdapterResolver.Resolve(_adapters.Values.ToList(), _fallback, gameName, engineType);
            if (ReferenceEquals(resolved, _fallback))
                _logger.LogDebug("Using fallback adapter for game {Game}", gameName);
            else
                _logger.LogDebug("Resolved adapter {Key} for game {Game}", resolved.AdapterKey, gameName);
            return resolved;
        }

        /// <summary>
        /// 为指定游戏查找最佳匹配适配器（使用旧版 GameType 枚举）。
        /// 兼容 v1.x 代码路径。
        /// </summary>
        public IHostAdapter ResolveByGameType(GameType gameType)
        {
            var engineType = gameType switch
            {
                GameType.StellarBlade or GameType.BlackMythWukong or GameType.WuchangFallenFeathers
                    or GameType.Expedition33 or GameType.Borderlands4 => EngineType.UnrealEngine,
                GameType.StellarBladeCNS => EngineType.UnrealEngine,
                _ => EngineType.Unknown
            };

            var gameName = gameType switch
            {
                GameType.StellarBlade => "剑星",
                GameType.StellarBladeCNS => "剑星（CNS模式）",
                GameType.BlackMythWukong => "黑神话·悟空",
                GameType.WuchangFallenFeathers => "明末·渊虚之羽",
                GameType.Expedition33 => "光与影：33号远征队",
                GameType.Borderlands4 => "无主之地4",
                _ => gameType.ToString()
            };

            // CNS 模式优先用 CNS 适配器
            if (gameType == GameType.StellarBladeCNS)
            {
                var cns = GetByKey("unreal-engine-cns");
                if (cns != null) return cns;
            }

            return Resolve(gameName, engineType);
        }
    }
}
