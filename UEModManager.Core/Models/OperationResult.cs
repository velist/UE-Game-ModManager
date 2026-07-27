using System;
using System.Collections.Generic;
using System.Linq;

namespace UEModManager.Models
{
    /// <summary>
    /// 一次用户发起的操作的结果。
    ///
    /// 存在的理由：这些操作过去一律以 <c>bool</c> 表达成败，失败原因（部署事务的
    /// ErrorMessage、异常消息）只写进日志就被丢掉，View 拿到 false 直接 return，
    /// 于是"磁盘满 / 文件被游戏占用 / 权限不足"全都表现为"开关弹回原位，什么都没说"。
    /// 这个类型的唯一职责就是把失败原因带到 View 层，让它能弹给用户看。
    ///
    /// 三种状态是互斥的：
    /// - 成功：<see cref="Success"/> = true
    /// - 失败：<see cref="Success"/> = false 且 <see cref="Error"/> 非空
    /// - 用户主动取消：<see cref="IsCancelled"/> = true，View 不应弹错误框
    /// </summary>
    public sealed class OperationResult
    {
        private OperationResult(bool success, string? error, bool cancelled)
        {
            Success = success;
            Error = error;
            IsCancelled = cancelled;
        }

        /// <summary>操作是否成功完成。</summary>
        public bool Success { get; }

        /// <summary>失败原因，可直接展示给用户。成功或取消时为 null。</summary>
        public string? Error { get; }

        /// <summary>是否为用户主动取消。取消不是失败，不应弹错误提示。</summary>
        public bool IsCancelled { get; }

        /// <summary>成功。</summary>
        public static OperationResult Ok() => new(true, null, false);

        /// <summary>失败，附带可展示给用户的原因。</summary>
        public static OperationResult Fail(string? error)
            => new(false, string.IsNullOrWhiteSpace(error) ? DefaultError : error, false);

        /// <summary>用户主动取消。</summary>
        public static OperationResult Cancel() => new(false, null, true);

        /// <summary>原因缺失时的兜底文案——绝不能让用户看到空白提示框。</summary>
        public const string DefaultError = "操作失败，原因未知。详细信息请查看日志。";

        /// <summary>
        /// 合并批量操作的结果。
        /// 全部成功 → 成功；全部取消 → 取消；否则汇总所有失败原因（去重后逐行列出）。
        /// 空集合视为成功——没有要做的事就不算失败。
        /// </summary>
        public static OperationResult Aggregate(IEnumerable<OperationResult> results)
        {
            if (results == null) throw new ArgumentNullException(nameof(results));

            var list = results.ToList();
            if (list.Count == 0) return Ok();

            var failures = list.Where(r => !r.Success && !r.IsCancelled).ToList();
            if (failures.Count == 0)
                return list.All(r => r.IsCancelled) ? Cancel() : Ok();

            var reasons = failures
                .Select(f => f.Error ?? DefaultError)
                .Distinct()
                .ToList();

            return Fail(string.Join(Environment.NewLine, reasons));
        }
    }
}
