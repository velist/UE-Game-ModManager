using System;
using System.Collections.Generic;
using System.Linq;
using UEModManager.Models;

namespace UEModManager.Services.Backends
{
    /// <summary>
    /// 部署后端注册表构建。纯函数：把 DI 汇集来的一组后端实例整理成按类型索引的字典。
    ///
    /// <para>
    /// 拆出来是为了能测：<c>DeploymentService</c> 的构造需要 <c>OverwriteStore</c>
    /// 等一长串真实依赖，而"重复类型怎么办""缺 Copy 怎么办"这些规则本身
    /// 只跟后端集合有关，不该被那串依赖绑住。
    /// </para>
    /// </summary>
    public static class DeploymentBackendRegistry
    {
        /// <summary>
        /// 构建注册表。
        /// </summary>
        /// <param name="backends">DI 汇集的后端实例，顺序即注册顺序。</param>
        /// <param name="onDuplicate">
        /// 发现同一 <see cref="DeploymentBackendType"/> 被注册多次时的回调（用于记日志）。
        /// </param>
        /// <exception cref="ArgumentNullException">backends 为 null。</exception>
        /// <exception cref="InvalidOperationException">
        /// 没有注册 <see cref="DeploymentBackendType.Copy"/> 后端。
        /// Copy 是 <c>GetBackend</c> 的兜底目标，缺了它任何一次降级都会变成
        /// 难以定位的 KeyNotFoundException，故在装配阶段就直接失败。
        /// </exception>
        public static IReadOnlyDictionary<DeploymentBackendType, IDeploymentBackend> Build(
            IEnumerable<IDeploymentBackend> backends,
            Action<string>? onDuplicate = null)
        {
            if (backends is null) throw new ArgumentNullException(nameof(backends));

            var registry = new Dictionary<DeploymentBackendType, IDeploymentBackend>();

            foreach (var backend in backends.Where(b => b is not null))
            {
                if (registry.TryGetValue(backend.Type, out var existing))
                {
                    // 后注册的覆盖先注册的：内置后端在 App.xaml.cs 里先注册，
                    // 这样自定义实现只要注册在后面就能替换掉同类型的内置实现，
                    // 无需改动本类或 DeploymentService。
                    //
                    // 消息里带上 DisplayName 而不只是类名：两个实现完全可能同名
                    // （不同程序集里的 MirrorBackend），此时只有 DisplayName 能区分谁是谁。
                    onDuplicate?.Invoke(
                        $"部署后端类型 {backend.Type} 被重复注册：" +
                        $"「{existing.DisplayName}」({existing.GetType().Name}) 将被 " +
                        $"「{backend.DisplayName}」({backend.GetType().Name}) 覆盖");
                }

                registry[backend.Type] = backend;
            }

            if (!registry.ContainsKey(DeploymentBackendType.Copy))
            {
                throw new InvalidOperationException(
                    "未注册 Copy 部署后端。Copy 是后端降级的兜底目标，必须存在——" +
                    "请检查 App.xaml.cs 中 AddSingleton<IDeploymentBackend, CopyBackend>() 的注册。");
            }

            return registry;
        }
    }
}
