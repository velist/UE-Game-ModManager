using System;
using System.Threading.Tasks;

namespace UEModManager.Services
{
    /// <summary>
    /// 邮件发送服务接口
    /// </summary>
    public interface IEmailSender
    {
        /// <summary>
        /// 发送邮件
        /// </summary>
        /// <param name="to">收件人邮箱</param>
        /// <param name="subject">邮件主题</param>
        /// <param name="htmlContent">HTML内容</param>
        /// <param name="textContent">纯文本内容（可选）</param>
        /// <returns>发送结果</returns>
        Task<EmailSendResult> SendEmailAsync(string to, string subject, string htmlContent, string? textContent = null);

        /// <summary>
        /// 健康检查
        /// </summary>
        /// <returns>服务是否可用</returns>
        Task<bool> HealthCheckAsync();

        /// <summary>
        /// 服务名称
        /// </summary>
        string ServiceName { get; }
    }

    /// <summary>
    /// 邮件发送结果
    /// </summary>
    public class EmailSendResult
    {
        public bool Success { get; set; }
        public string? MessageId { get; set; }
        public string? ErrorMessage { get; set; }
        public int? RetryAfterSeconds { get; set; }
        public EmailSendErrorType ErrorType { get; set; }

        public static EmailSendResult CreateSuccess(string? messageId = null)
        {
            return new EmailSendResult
            {
                Success = true,
                MessageId = messageId,
                ErrorType = EmailSendErrorType.None
            };
        }

        public static EmailSendResult CreateFailure(string errorMessage, EmailSendErrorType errorType, int? retryAfterSeconds = null)
        {
            return new EmailSendResult
            {
                Success = false,
                ErrorMessage = errorMessage,
                ErrorType = errorType,
                RetryAfterSeconds = retryAfterSeconds
            };
        }
    }

    /// <summary>
    /// 邮件发送错误类型
    /// </summary>
    public enum EmailSendErrorType
    {
        None,
        RateLimit,          // 限流
        InvalidRecipient,   // 无效收件人
        AuthenticationFailed, // 认证失败
        NetworkError,       // 网络错误
        ServerError,        // 服务器错误
        Unknown             // 未知错误
    }
}
