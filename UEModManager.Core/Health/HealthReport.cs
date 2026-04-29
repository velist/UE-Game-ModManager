using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace UEModManager.Health
{
    /// <summary>健康检查项的状态等级。</summary>
    public enum HealthStatus
    {
        /// <summary>正常 — 全部预期满足。</summary>
        Ok,
        /// <summary>警告 — 功能可用，但某些条件不理想（如某可选目录不存在）。</summary>
        Warning,
        /// <summary>错误 — 功能不可用或严重异常（如必需路径缺失）。</summary>
        Error,
    }

    /// <summary>单项健康检查的结果。</summary>
    public sealed record HealthCheck(
        string Name,
        HealthStatus Status,
        string Message,
        string? Detail = null);

    /// <summary>
    /// 健康报告。汇总一次启动检查的所有 <see cref="HealthCheck"/>，
    /// 提供整体状态聚合与文本渲染。
    /// </summary>
    public sealed class HealthReport
    {
        public DateTime CheckedAt { get; init; } = DateTime.UtcNow;
        public List<HealthCheck> Checks { get; init; } = [];

        /// <summary>整体状态：任一 Error → Error；否则任一 Warning → Warning；否则 Ok。</summary>
        public HealthStatus OverallStatus
        {
            get
            {
                if (Checks.Count == 0) return HealthStatus.Ok;
                if (Checks.Any(c => c.Status == HealthStatus.Error)) return HealthStatus.Error;
                if (Checks.Any(c => c.Status == HealthStatus.Warning)) return HealthStatus.Warning;
                return HealthStatus.Ok;
            }
        }

        public int OkCount => Checks.Count(c => c.Status == HealthStatus.Ok);
        public int WarningCount => Checks.Count(c => c.Status == HealthStatus.Warning);
        public int ErrorCount => Checks.Count(c => c.Status == HealthStatus.Error);

        /// <summary>渲染为人类可读文本（用于日志/诊断包）。</summary>
        public string ToText() => HealthReportFormatter.Format(this);
    }

    /// <summary>健康报告渲染器。纯函数。</summary>
    public static class HealthReportFormatter
    {
        public static string Format(HealthReport report)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));

            var sb = new StringBuilder();
            sb.AppendLine($"# Health Report ({report.CheckedAt:yyyy-MM-ddTHH:mm:ssZ})");
            sb.AppendLine($"Overall: {report.OverallStatus}  (Ok={report.OkCount}, Warn={report.WarningCount}, Err={report.ErrorCount})");
            sb.AppendLine();

            foreach (var c in report.Checks)
            {
                var marker = c.Status switch
                {
                    HealthStatus.Ok => "[OK]",
                    HealthStatus.Warning => "[WARN]",
                    HealthStatus.Error => "[ERR]",
                    _ => "[?]"
                };
                sb.AppendLine($"{marker} {c.Name}: {c.Message}");
                if (!string.IsNullOrEmpty(c.Detail))
                    sb.AppendLine($"      └ {c.Detail}");
            }
            return sb.ToString();
        }
    }
}
