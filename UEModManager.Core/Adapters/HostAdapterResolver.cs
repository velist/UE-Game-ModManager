using System.Collections.Generic;
using System.Linq;
using UEModManager.Models;

namespace UEModManager.Adapters
{
    /// <summary>
    /// 宿主适配器解析（纯函数）。
    ///
    /// 给定适配器集合 + fallback + 查询条件（游戏名 / 引擎类型），返回最佳匹配的 adapter。
    /// 主项目 <c>HostAdapterRegistry</c> 持有运行时状态（DI 注入的 adapter 实例字典 + 日志），
    /// 把"该挑哪个 adapter"的决策抽离到此处后，主项目仅负责"持有 + 日志"。
    ///
    /// 解析顺序：
    /// 1. 排除 fallback，遍历适配器调用 <see cref="IHostAdapter.CanHandle"/>。
    /// 2. 没人接 → 按引擎类型匹配（exclude fallback，要求 EngineType 完全相等且非 Unknown）。
    /// 3. 都没匹配 → 返回 fallback。
    /// </summary>
    public static class HostAdapterResolver
    {
        /// <summary>解析适配器。</summary>
        /// <param name="adapters">所有候选适配器（含 fallback 也无妨，会自动跳过）。</param>
        /// <param name="fallback">最终兜底适配器（如 GenericFileOverlayAdapter）。</param>
        /// <param name="gameName">查询的游戏名。</param>
        /// <param name="engineType">可选引擎类型；Unknown 视为无指定。</param>
        /// <returns>匹配到的 adapter，或 fallback。</returns>
        public static IHostAdapter Resolve(
            IReadOnlyCollection<IHostAdapter> adapters,
            IHostAdapter fallback,
            string gameName,
            Models.EngineType? engineType = null)
        {
            if (adapters == null) throw new System.ArgumentNullException(nameof(adapters));
            if (fallback == null) throw new System.ArgumentNullException(nameof(fallback));

            // 1. 精确匹配：按游戏名 + 引擎类型（CanHandle 自由判断）
            foreach (var adapter in adapters)
            {
                if (adapter.AdapterKey == fallback.AdapterKey) continue;
                if (adapter.CanHandle(gameName, engineType))
                    return adapter;
            }

            // 2. 按引擎类型匹配
            if (engineType.HasValue && engineType.Value != Models.EngineType.Unknown)
            {
                var byEngine = adapters.FirstOrDefault(a =>
                    a.EngineType == engineType.Value
                    && a.AdapterKey != fallback.AdapterKey);
                if (byEngine != null) return byEngine;
            }

            // 3. Fallback
            return fallback;
        }
    }
}
