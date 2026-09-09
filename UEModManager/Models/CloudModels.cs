using System;
using System.Text.Json.Serialization;

namespace UEModManager.Models
{
    /// <summary>
    /// 云端用户信息
    /// </summary>
    public class CloudUser
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        [JsonPropertyName("username")]
        public string? Username { get; set; }

        [JsonPropertyName("display_name")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("avatar")]
        public string? Avatar { get; set; }

        [JsonPropertyName("is_active")]
        public bool IsActive { get; set; } = true;

        [JsonPropertyName("is_verified")]
        public bool IsVerified { get; set; } = false;

        [JsonPropertyName("created_at")]
        public DateTime CreatedAt { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTime UpdatedAt { get; set; }

        [JsonPropertyName("last_login_at")]
        public DateTime LastLoginAt { get; set; }

        [JsonPropertyName("subscription_type")]
        public string SubscriptionType { get; set; } = "free";

        [JsonPropertyName("subscription_expires_at")]
        public DateTime? SubscriptionExpiresAt { get; set; }
    }

    #region API响应模型

    /// <summary>
    /// 云端登录响应
    /// </summary>
    public class CloudLoginResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("token_type")]
        public string TokenType { get; set; } = "Bearer";

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; } = 3600;

        [JsonPropertyName("user")]
        public CloudUser? User { get; set; }
    }

    /// <summary>
    /// 云端注册响应
    /// </summary>
    public class CloudRegisterResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("user_id")]
        public int UserId { get; set; }

        [JsonPropertyName("verification_required")]
        public bool VerificationRequired { get; set; }

        [JsonPropertyName("verification_email_sent")]
        public bool VerificationEmailSent { get; set; }
    }

    /// <summary>
    /// 云端令牌验证响应
    /// </summary>
    public class CloudValidateResponse
    {
        [JsonPropertyName("valid")]
        public bool Valid { get; set; }

        [JsonPropertyName("user")]
        public CloudUser? User { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }

    /// <summary>
    /// 云端错误响应
    /// </summary>
    public class CloudErrorResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; } = false;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("error_code")]
        public string? ErrorCode { get; set; }

        [JsonPropertyName("details")]
        public string? Details { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    #endregion
}
