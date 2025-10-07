using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace UEModManager.Services
{
    /// <summary>
    /// 智能故障切换邮件服务
    /// 主通道：Brevo API（MailerSend已移除）
    /// 备用通道：Brevo (300封/天)
    /// </summary>
    public class FallbackEmailService : IEmailSender
    {
        private readonly ILogger<FallbackEmailService> _logger;
        private readonly List<IEmailSender> _senders;
        private readonly Dictionary<string, ServiceHealthStatus> _healthStatus;

        private const int MaxRetryAttempts = 2;
        private const int HealthCheckCacheSeconds = 60;

        public string ServiceName => "FallbackEmailService";

        public FallbackEmailService(
            ILogger<FallbackEmailService> logger,
            IEnumerable<IEmailSender> senders)
        {
            _logger = logger;
            _senders = senders.ToList();
            _healthStatus = new Dictionary<string, ServiceHealthStatus>();

            if (_senders.Count == 0)
            {
                throw new ArgumentException("至少需要一个邮件发送服务", nameof(senders));
            }

            _logger.LogInformation($"[FallbackEmail] 已注册 {_senders.Count} 个邮件发送服务: {string.Join(", ", _senders.Select(s => s.ServiceName))}");
        }

        public async Task<EmailSendResult> SendEmailAsync(string to, string subject, string htmlContent, string? textContent = null)
        {
            EmailSendResult? lastResult = null;

            foreach (var sender in _senders)
            {
                // 检查服务健康状态
                var health = await GetServiceHealthAsync(sender);
                                if (health.Status == HealthStatusType.Unhealthy)
                {
                    // 健康检查仅作参考：若处于冷却期内则跳过，否则仍尝试一次
                    if (health.UnhealthyUntil.HasValue && DateTime.UtcNow < health.UnhealthyUntil.Value)
                    {
                        _logger.LogWarning($"[FallbackEmail] 跳过不健康的服务(冷却中): {sender.ServiceName}");
                        continue;
                    }
                    else
                    {
                        _logger.LogWarning($"[FallbackEmail] 健康检查未通过，但仍尝试一次: {sender.ServiceName}");
                    }
                }

                // 尝试发送
                try
                {
                    _logger.LogInformation($"[FallbackEmail] 使用 {sender.ServiceName} 发送邮件");
                    var result = await sender.SendEmailAsync(to, subject, htmlContent, textContent);

                    if (result.Success)
                    {
                        _logger.LogInformation($"[FallbackEmail] ✓ {sender.ServiceName} 发送成功");
                        UpdateHealthStatus(sender.ServiceName, true);
                        return result;
                    }

                    lastResult = result;
                    _logger.LogWarning($"[FallbackEmail] ✗ {sender.ServiceName} 发送失败: {result.ErrorMessage}");

                    // 如果是限流错误，标记为不健康并尝试下一个服务
                    if (result.ErrorType == EmailSendErrorType.RateLimit)
                    {
                        _logger.LogWarning($"[FallbackEmail] {sender.ServiceName} 触发限流，切换到备用服务");
                        UpdateHealthStatus(sender.ServiceName, false, result.RetryAfterSeconds ?? 300);
                        continue; // 立即尝试下一个服务
                    }

                    // 如果是认证失败或无效收件人，不需要重试其他服务
                    if (result.ErrorType == EmailSendErrorType.AuthenticationFailed ||
                        result.ErrorType == EmailSendErrorType.InvalidRecipient)
                    {
                        _logger.LogError($"[FallbackEmail] {sender.ServiceName} 认证失败或无效收件人，停止重试");
                        return result;
                    }

                    // 其他错误继续尝试下一个服务
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"[FallbackEmail] {sender.ServiceName} 发送异常");
                    UpdateHealthStatus(sender.ServiceName, false);
                }
            }

            // 所有服务都失败
            _logger.LogError("[FallbackEmail] 所有邮件发送服务均失败");
            return lastResult ?? EmailSendResult.CreateFailure(
                "所有邮件发送服务均不可用",
                EmailSendErrorType.ServerError
            );
        }

        public async Task<bool> HealthCheckAsync()
        {
            var tasks = _senders.Select(s => s.HealthCheckAsync());
            var results = await Task.WhenAll(tasks);
            return results.Any(r => r);
        }

        /// <summary>
        /// 获取服务健康状态（带缓存）
        /// </summary>
        private async Task<ServiceHealthStatus> GetServiceHealthAsync(IEmailSender sender)
        {
            var serviceName = sender.ServiceName;

            // 检查缓存
            if (_healthStatus.TryGetValue(serviceName, out var cached))
            {
                var age = DateTime.UtcNow - cached.LastChecked;
                if (age.TotalSeconds < HealthCheckCacheSeconds)
                {
                    return cached;
                }
            }

            // 执行健康检查
            try
            {
                var isHealthy = await sender.HealthCheckAsync();
                var status = new ServiceHealthStatus
                {
                    Status = isHealthy ? HealthStatusType.Healthy : HealthStatusType.Unhealthy,
                    LastChecked = DateTime.UtcNow,
                    ConsecutiveFailures = isHealthy ? 0 : (cached?.ConsecutiveFailures ?? 0) + 1
                };

                _healthStatus[serviceName] = status;
                return status;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"[FallbackEmail] {serviceName} 健康检查异常");
                var status = new ServiceHealthStatus
                {
                    Status = HealthStatusType.Unhealthy,
                    LastChecked = DateTime.UtcNow,
                    ConsecutiveFailures = (cached?.ConsecutiveFailures ?? 0) + 1
                };

                _healthStatus[serviceName] = status;
                return status;
            }
        }

        /// <summary>
        /// 更新健康状态
        /// </summary>
        private void UpdateHealthStatus(string serviceName, bool success, int unhealthyDurationSeconds = 300)
        {
            if (!_healthStatus.TryGetValue(serviceName, out var status))
            {
                status = new ServiceHealthStatus();
            }

            if (success)
            {
                status.Status = HealthStatusType.Healthy;
                status.ConsecutiveFailures = 0;
            }
            else
            {
                status.Status = HealthStatusType.Unhealthy;
                status.ConsecutiveFailures++;
                status.UnhealthyUntil = DateTime.UtcNow.AddSeconds(unhealthyDurationSeconds);
            }

            status.LastChecked = DateTime.UtcNow;
            _healthStatus[serviceName] = status;

            _logger.LogInformation($"[FallbackEmail] {serviceName} 状态更新: {status.Status}, 连续失败: {status.ConsecutiveFailures}");
        }

        /// <summary>
        /// 获取当前活跃的服务名称
        /// </summary>
        public string GetActiveServiceName()
        {
            foreach (var sender in _senders)
            {
                if (_healthStatus.TryGetValue(sender.ServiceName, out var status))
                {
                    if (status.Status == HealthStatusType.Healthy)
                    {
                        return sender.ServiceName;
                    }
                }
                else
                {
                    return sender.ServiceName; // 未检查过，假定健康
                }
            }

            return _senders.FirstOrDefault()?.ServiceName ?? "None";
        }
    }

    /// <summary>
    /// 服务健康状态
    /// </summary>
    internal class ServiceHealthStatus
    {
        public HealthStatusType Status { get; set; } = HealthStatusType.Unknown;
        public DateTime LastChecked { get; set; } = DateTime.MinValue;
        public int ConsecutiveFailures { get; set; } = 0;
        public DateTime? UnhealthyUntil { get; set; }
    }

    /// <summary>
    /// 健康状态类型
    /// </summary>
    internal enum HealthStatusType
    {
        Unknown,
        Healthy,
        Unhealthy
    }
}

