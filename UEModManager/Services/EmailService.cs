using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Text.Json;

namespace UEModManager.Services
{
    /// <summary>
    /// 邮件服务配置
    /// </summary>
    public class EmailConfig
    {
        public string SmtpServer { get; set; } = string.Empty;
        public int SmtpPort { get; set; } = 587;
        public string SenderEmail { get; set; } = string.Empty;
        public string SenderPassword { get; set; } = string.Empty;
        public string SenderName { get; set; } = "UEModManager";
        public bool EnableSsl { get; set; } = true;
        public EmailProvider Provider { get; set; } = EmailProvider.Custom;
    }

    /// <summary>
    /// 邮件提供商枚举
    /// </summary>
    public enum EmailProvider
    {
        Custom,
        Gmail,
        Outlook,
        QQ,
        NetEase163,
        Sina
    }

    /// <summary>
    /// 邮件发送结果
    /// </summary>
    public class EmailResult
    {
        public bool IsSuccess { get; private set; }
        public string Message { get; private set; } = string.Empty;
        public Exception? Exception { get; private set; }

        public EmailResult(bool isSuccess, string message, Exception? exception = null)
        {
            IsSuccess = isSuccess;
            Message = message;
            Exception = exception;
        }

        public static EmailResult Success(string message = "邮件发送成功")
        {
            return new EmailResult(true, message);
        }

        public static EmailResult Failed(string message, Exception? exception = null)
        {
            return new EmailResult(false, message, exception);
        }
    }

    /// <summary>
    /// 邮件服务 - 支持多种邮件提供商
    /// </summary>
    public class EmailService
    {
        private async Task<EmailResult> SendEmailAsync(string recipientEmail, string subject, string body, bool isHtml = true)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(recipientEmail))
                    return EmailResult.Failed("收件人邮箱为空");

                if (string.IsNullOrEmpty(_config.SenderEmail) || string.IsNullOrEmpty(_config.SenderPassword) || string.IsNullOrEmpty(_config.SmtpServer))
                {
                    return EmailResult.Failed("邮件服务未配置，请先在邮件设置中填写发件人邮箱/授权码与SMTP服务器");
                }

                using var client = new SmtpClient(_config.SmtpServer, _config.SmtpPort)
                {
                    EnableSsl = _config.EnableSsl,
                    Credentials = new NetworkCredential(_config.SenderEmail, _config.SenderPassword),
                    Timeout = 15000
                };

                using var message = new MailMessage();
                message.From = new MailAddress(_config.SenderEmail, string.IsNullOrWhiteSpace(_config.SenderName) ? "爱酱工作室" : _config.SenderName, Encoding.UTF8);
                message.To.Add(new MailAddress(recipientEmail));
                message.Subject = subject;
                message.SubjectEncoding = Encoding.UTF8;
                message.Body = body;
                message.BodyEncoding = Encoding.UTF8;
                message.IsBodyHtml = isHtml;

                await client.SendMailAsync(message);
                _logger.LogInformation($"邮件发送成功 -> {recipientEmail}: {subject}");
                return EmailResult.Success();
            }
            catch (SmtpException ex)
            {
                _logger.LogError(ex, $"SMTP发送失败 -> {recipientEmail}");
                return EmailResult.Failed($"SMTP错误: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"邮件发送异常 -> {recipientEmail}");
                return EmailResult.Failed($"发送异常: {ex.Message}", ex);
            }
        }
        /// <summary>
        /// 发送欢迎邮件（中英双语），落款：爱酱工作室
        /// </summary>
        public async Task<EmailResult> SendWelcomeEmailAsync(string recipientEmail, string displayName = "用户")
        {
            try
            {
                var subject = "欢迎使用 UEModManager / Welcome to UEModManager";
                var body = $@"<!DOCTYPE html>
<html>
<head>
  <meta charset='UTF-8'>
  <title>Welcome</title>
</head>
<body style='font-family: Microsoft YaHei, Arial, sans-serif; margin:0; padding:24px; background:#f5f7fb;'>
  <div style='max-width:640px;margin:0 auto;background:#ffffff;border-radius:8px;box-shadow:0 6px 18px rgba(0,0,0,0.06);'>
    <div style='background:linear-gradient(135deg,#4f46e5 0%,#7c3aed 100%);color:#fff;padding:28px 32px;border-radius:8px 8px 0 0;'>
      <h1 style='margin:0;font-size:22px;'>UEModManager</h1>
      <p style='margin:8px 0 0 0;opacity:0.9;'>Welcome Email / 欢迎邮件</p>
    </div>
    <div style='padding:28px 32px;color:#1f2937;'>
      <h2 style='margin:0 0 12px 0;font-size:18px;'>亲爱的 {displayName}，</h2>
      <p style='margin:0 0 10px 0;line-height:1.7;'>欢迎加入 UEModManager！感谢你的注册。这封邮件用于确认你的账户已成功创建。</p>
      <p style='margin:0 0 10px 0;line-height:1.7;'>我们建议你：
        <br>• 在设置中配置游戏与 MOD 目录
        <br>• 使用“冲突检测”快速排查 MOD 覆盖冲突
        <br>• 开启“备份/还原”，保护原始文件
      </p>
      <hr style='border:none;border-top:1px solid #e5e7eb;margin:22px 0;'>
      <h2 style='margin:0 0 12px 0;font-size:18px;'>Dear {displayName},</h2>
      <p style='margin:0 0 10px 0;line-height:1.7;'>Welcome to UEModManager! Your account has been created successfully. This email confirms your registration.</p>
      <p style='margin:0 0 10px 0;line-height:1.7;'>Quick tips:
        <br>• Configure your game and MOD directories in Settings
        <br>• Use ""Conflict Detection"" to find resource overlaps
        <br>• Enable ""Backup/Restore"" to protect originals
      </p>
    </div>
    <div style='background:#f9fafb;padding:16px 24px;border-radius:0 0 8px 8px;color:#6b7280;text-align:center;font-size:13px;'>
      <p style='margin:0;'>此邮件由 爱酱工作室 发送 / Sent by Ai-chan Studio</p>
    </div>
  </div>
</body>
</html>";
                return await SendEmailAsync(recipientEmail, subject, body, true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"发送欢迎邮件失败: {recipientEmail}");
                return EmailResult.Failed("发送欢迎邮件失败", ex);
            }
        }

        private readonly ILogger<EmailService> _logger;
        private readonly EmailConfig _config;
        private readonly Dictionary<string, (string code, DateTime expiry)> _activationCodes;

        // 预设的邮件服务商配置
        private static readonly Dictionary<EmailProvider, EmailConfig> PresetConfigs = new()
        {
            [EmailProvider.Gmail] = new EmailConfig 
            { 
                SmtpServer = "smtp.gmail.com", 
                SmtpPort = 587, 
                EnableSsl = true 
            },
            [EmailProvider.Outlook] = new EmailConfig 
            { 
                SmtpServer = "smtp-mail.outlook.com", 
                SmtpPort = 587, 
                EnableSsl = true 
            },
            [EmailProvider.QQ] = new EmailConfig 
            { 
                SmtpServer = "smtp.qq.com", 
                SmtpPort = 587, 
                EnableSsl = true 
            },
            [EmailProvider.NetEase163] = new EmailConfig 
            { 
                SmtpServer = "smtp.163.com", 
                SmtpPort = 25, 
                EnableSsl = false 
            },
            [EmailProvider.Sina] = new EmailConfig 
            { 
                SmtpServer = "smtp.sina.com", 
                SmtpPort = 587, 
                EnableSsl = true 
            }
        };

        public EmailService(ILogger<EmailService> logger)
        {
            _logger = logger;
            _config = LoadEmailConfig();
            _activationCodes = new Dictionary<string, (string, DateTime)>();
        }

        /// <summary>
        /// 加载邮件配置
        /// </summary>
        private EmailConfig LoadEmailConfig()
        {
            try
            {
                var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "email_config.json");
                if (File.Exists(configPath))
                {
                    var json = File.ReadAllText(configPath, Encoding.UTF8);
                    var config = JsonSerializer.Deserialize<EmailConfig>(json);
                    if (config != null)
                    {
                        _logger.LogInformation("邮件配置加载成功");
                        return config;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载邮件配置失败");
            }

            _logger.LogWarning("使用默认邮件配置");
            return new EmailConfig();
        }

        /// <summary>
        /// 保存邮件配置
        /// </summary>
        public async Task<bool> SaveEmailConfigAsync(EmailConfig config)
        {
            try
            {
                var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "email_config.json");
                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions 
                { 
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
                
                await File.WriteAllTextAsync(configPath, json, Encoding.UTF8);
                _logger.LogInformation("邮件配置保存成功");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存邮件配置失败");
                return false;
            }
        }

        /// <summary>
        /// 应用预设配置
        /// </summary>
        public EmailConfig ApplyPresetConfig(EmailProvider provider, string senderEmail, string senderPassword)
        {
            if (PresetConfigs.TryGetValue(provider, out var presetConfig))
            {
                var config = new EmailConfig
                {
                    SmtpServer = presetConfig.SmtpServer,
                    SmtpPort = presetConfig.SmtpPort,
                    EnableSsl = presetConfig.EnableSsl,
                    SenderEmail = senderEmail,
                    SenderPassword = senderPassword,
                    Provider = provider
                };
                
                _logger.LogInformation($"应用预设配置: {provider}");
                return config;
            }
            
            throw new ArgumentException($"不支持的邮件提供商: {provider}");
        }

        /// <summary>
        /// 生成激活码
        /// </summary>
        public string GenerateActivationCode(string email)
        {
            var code = new Random().Next(100000, 999999).ToString();
            var expiry = DateTime.Now.AddMinutes(5); // 5分钟有效期
            
            _activationCodes[email] = (code, expiry);
            _logger.LogInformation($"为 {email} 生成激活码: {code}");
            
            return code;
        }

        /// <summary>
        /// 验证激活码
        /// </summary>
        public bool ValidateActivationCode(string email, string code)
        {
            if (_activationCodes.TryGetValue(email, out var stored))
            {
                var isValid = stored.code == code && stored.expiry > DateTime.Now;
                
                if (isValid)
                {
                    // 验证成功后删除激活码
                    _activationCodes.Remove(email);
                    _logger.LogInformation($"激活码验证成功: {email}");
                }
                else
                {
                    _logger.LogWarning($"激活码验证失败: {email} - 代码不匹配或已过期");
                }
                
                return isValid;
            }
            
            _logger.LogWarning($"激活码验证失败: {email} - 未找到激活码");
            return false;
        }

        /// <summary>
        /// 发送激活码邮件
        /// </summary>
        public async Task<EmailResult> SendActivationCodeAsync(string recipientEmail)
        {
            try
            {
                if (string.IsNullOrEmpty(_config.SenderEmail) || string.IsNullOrEmpty(_config.SenderPassword))
                {
                    return EmailResult.Failed("邮件服务未配置，请先设置邮件发送账户");
                }

                var activationCode = GenerateActivationCode(recipientEmail);
                var subject = "UEModManager - 邮箱验证码";
                var body = $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset=""utf-8"">
    <style>
        body {{ font-family: 'Microsoft YaHei', Arial, sans-serif; margin: 0; padding: 20px; background-color: #f5f5f5; }}
        .container {{ max-width: 600px; margin: 0 auto; background: white; border-radius: 8px; box-shadow: 0 2px 10px rgba(0,0,0,0.1); }}
        .header {{ background: linear-gradient(135deg, #667eea 0%, #764ba2 100%); color: white; padding: 30px; text-align: center; border-radius: 8px 8px 0 0; }}
        .content {{ padding: 40px; text-align: center; }}
        .code {{ font-size: 32px; font-weight: bold; color: #667eea; background: #f8f9ff; padding: 20px; border-radius: 8px; border: 2px dashed #667eea; margin: 20px 0; letter-spacing: 5px; }}
        .footer {{ background: #f8f9fa; padding: 20px; text-align: center; border-radius: 0 0 8px 8px; color: #6c757d; font-size: 14px; }}
        .warning {{ color: #dc3545; font-size: 14px; margin-top: 20px; }}
    </style>
</head>
<body>
    <div class=""container"">
        <div class=""header"">
            <h1>🎮 UEModManager</h1>
            <p>邮箱验证码</p>
        </div>
        <div class=""content"">
            <h2>您好！</h2>
            <p>您正在使用 UEModManager 进行邮箱认证，验证码为：</p>
            <div class=""code"">{activationCode}</div>
            <p>请在 5 分钟内输入此验证码完成认证。</p>
            <div class=""warning"">
                <p>⚠️ 如果不是您本人操作，请忽略此邮件</p>
                <p>⚠️ 验证码仅用于本次认证，请勿泄露给他人</p>
            </div>
        </div>
        <div class=""footer"">
            <p>此邮件由 UEModManager 自动发送，请勿回复</p>
            <p>如有疑问，请联系技术支持</p>
        </div>
    </div>
</body>
</html>";

                return await SendEmailAsync(recipientEmail, subject, body, true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"发送激活码邮件失败: {recipientEmail}");
                return EmailResult.Failed("发送邮件失败", ex);
            }
        }

        /// 测试邮件配置
        /// </summary>
        public async Task<EmailResult> TestEmailConfigAsync(EmailConfig config)
        {
            try
            {
                using var client = new SmtpClient(config.SmtpServer, config.SmtpPort);
                client.EnableSsl = config.EnableSsl;
                client.Credentials = new NetworkCredential(config.SenderEmail, config.SenderPassword);
                client.Timeout = 10000; // 10秒超时

                // 发送测试邮件给发送者自己
                using var message = new MailMessage();
                message.From = new MailAddress(config.SenderEmail, config.SenderName);
                message.To.Add(new MailAddress(config.SenderEmail));
                message.Subject = "UEModManager - 邮件配置测试";
                message.Body = "此邮件用于测试 UEModManager 邮件服务配置，收到此邮件说明配置正确。";
                message.BodyEncoding = Encoding.UTF8;

                await client.SendMailAsync(message);
                
                _logger.LogInformation("邮件配置测试成功");
                return EmailResult.Success("邮件配置测试成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "邮件配置测试失败");
                return EmailResult.Failed($"邮件配置测试失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 清理过期的激活码
        /// </summary>
        public void CleanupExpiredCodes()
        {
            var expiredEmails = new List<string>();
            var now = DateTime.Now;

            foreach (var kvp in _activationCodes)
            {
                if (kvp.Value.expiry < now)
                {
                    expiredEmails.Add(kvp.Key);
                }
            }

            foreach (var email in expiredEmails)
            {
                _activationCodes.Remove(email);
            }

            if (expiredEmails.Count > 0)
            {
                _logger.LogInformation($"清理了 {expiredEmails.Count} 个过期激活码");
            }
        }

        /// <summary>
        /// 发送测试邮件（用于仪表盘测试）
        /// </summary>
        public async Task<EmailResult> SendTestEmailAsync(string testEmail)
        {
            try
            {
                var subject = "UEModManager - 系统测试邮件";
                var body = $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset='UTF-8'>
    <title>系统测试邮件</title>
</head>
<body style='font-family: Microsoft YaHei, Arial, sans-serif; margin: 0; padding: 20px; background-color: #f5f5f5;'>
    <div style='max-width: 600px; margin: 0 auto; background: white; border-radius: 8px; box-shadow: 0 2px 10px rgba(0,0,0,0.1);'>
        <div style='background: linear-gradient(135deg, #667eea 0%, #764ba2 100%); color: white; padding: 30px; text-align: center; border-radius: 8px 8px 0 0;'>
            <h1 style='margin: 0; font-size: 24px;'>🎛️ UEModManager</h1>
            <p style='margin: 10px 0 0 0; opacity: 0.9;'>管理员仪表盘测试邮件</p>
        </div>
        <div style='padding: 40px; text-align: center;'>
            <h2 style='color: #333; margin-bottom: 20px;'>系统邮件服务测试</h2>
            <p style='color: #666; font-size: 16px; line-height: 1.6; margin-bottom: 30px;'>
                这是一封来自 UEModManager 管理员仪表盘的测试邮件。<br>
                如果您收到此邮件，说明系统邮件服务运行正常。
            </p>
            <div style='background: #f8f9ff; padding: 20px; border-radius: 8px; border: 2px solid #667eea; margin: 20px 0;'>
                <p style='color: #667eea; font-weight: bold; margin: 0;'>✅ 邮件服务状态：正常</p>
                <p style='color: #666; margin: 10px 0 0 0; font-size: 14px;'>测试时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>
            </div>
        </div>
        <div style='background: #f8f9fa; padding: 20px; text-align: center; border-radius: 0 0 8px 8px; color: #6c757d; font-size: 14px;'>
            <p style='margin: 0;'>此邮件由 UEModManager 系统自动发送，请勿回复。</p>
        </div>
    </div>
</body>
</html>";

                return await SendEmailAsync(testEmail, subject, body, true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发送测试邮件失败");
                return EmailResult.Failed("发送测试邮件失败", ex);
            }
        }
    }
}

