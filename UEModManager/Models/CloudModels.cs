using System;
using System.ComponentModel.DataAnnotations;
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

    /// <summary>
    /// 云端用户偏好设置
    /// </summary>
    public class CloudUserPreferences
    {
        [JsonPropertyName("default_game_path")]
        public string? DefaultGamePath { get; set; }

        [JsonPropertyName("language")]
        public string Language { get; set; } = "zh-CN";

        [JsonPropertyName("theme")]
        public string Theme { get; set; } = "Dark";

        [JsonPropertyName("auto_check_updates")]
        public bool AutoCheckUpdates { get; set; } = true;

        [JsonPropertyName("auto_backup")]
        public bool AutoBackup { get; set; } = true;

        [JsonPropertyName("show_notifications")]
        public bool ShowNotifications { get; set; } = true;

        [JsonPropertyName("minimize_to_tray")]
        public bool MinimizeToTray { get; set; } = false;

        [JsonPropertyName("enable_cloud_sync")]
        public bool EnableCloudSync { get; set; } = true;

        [JsonPropertyName("updated_at")]
        public DateTime UpdatedAt { get; set; } = DateTime.Now;

        [JsonPropertyName("sync_frequency")]
        public int SyncFrequencyMinutes { get; set; } = 30;

        [JsonPropertyName("auto_sync_enabled")]
        public bool AutoSyncEnabled { get; set; } = true;
    }

    /// <summary>
    /// 云端MOD信息
    /// </summary>
    public class CloudMod
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("mod_id")]
        public string ModId { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("author")]
        public string? Author { get; set; }

        [JsonPropertyName("game_name")]
        public string GameName { get; set; } = string.Empty;

        [JsonPropertyName("category")]
        public string? Category { get; set; }

        [JsonPropertyName("tags")]
        public string[]? Tags { get; set; }

        [JsonPropertyName("download_url")]
        public string? DownloadUrl { get; set; }

        [JsonPropertyName("file_size")]
        public long FileSize { get; set; }

        [JsonPropertyName("download_count")]
        public int DownloadCount { get; set; }

        [JsonPropertyName("rating")]
        public decimal Rating { get; set; }

        [JsonPropertyName("rating_count")]
        public int RatingCount { get; set; }

        [JsonPropertyName("is_featured")]
        public bool IsFeatured { get; set; }

        [JsonPropertyName("is_verified")]
        public bool IsVerified { get; set; }

        [JsonPropertyName("created_at")]
        public DateTime CreatedAt { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTime UpdatedAt { get; set; }

        [JsonPropertyName("screenshots")]
        public string[]? Screenshots { get; set; }

        [JsonPropertyName("compatibility")]
        public string? Compatibility { get; set; }
    }

    /// <summary>
    /// 用户云端MOD收藏
    /// </summary>
    public class CloudUserModFavorite
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("user_id")]
        public int UserId { get; set; }

        [JsonPropertyName("mod_id")]
        public string ModId { get; set; } = string.Empty;

        [JsonPropertyName("game_name")]
        public string GameName { get; set; } = string.Empty;

        [JsonPropertyName("is_installed")]
        public bool IsInstalled { get; set; }

        [JsonPropertyName("is_enabled")]
        public bool IsEnabled { get; set; }

        [JsonPropertyName("install_date")]
        public DateTime? InstallDate { get; set; }

        [JsonPropertyName("created_at")]
        public DateTime CreatedAt { get; set; }

        [JsonPropertyName("notes")]
        public string? Notes { get; set; }
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
    /// 云端令牌刷新响应
    /// </summary>
    public class CloudRefreshResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; } = 3600;
    }

    /// <summary>
    /// 云端偏好设置响应
    /// </summary>
    public class CloudPreferencesResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("preferences")]
        public CloudUserPreferences? Preferences { get; set; }

        [JsonPropertyName("last_sync_at")]
        public DateTime LastSyncAt { get; set; }
    }

    /// <summary>
    /// 云端MOD列表响应
    /// </summary>
    public class CloudModListResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("mods")]
        public CloudMod[]? Mods { get; set; }

        [JsonPropertyName("total_count")]
        public int TotalCount { get; set; }

        [JsonPropertyName("page")]
        public int Page { get; set; }

        [JsonPropertyName("page_size")]
        public int PageSize { get; set; }

        [JsonPropertyName("has_next_page")]
        public bool HasNextPage { get; set; }
    }

    /// <summary>
    /// 云端MOD详情响应
    /// </summary>
    public class CloudModDetailResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("mod")]
        public CloudMod? Mod { get; set; }

        [JsonPropertyName("related_mods")]
        public CloudMod[]? RelatedMods { get; set; }

        [JsonPropertyName("user_favorite")]
        public CloudUserModFavorite? UserFavorite { get; set; }
    }

    /// <summary>
    /// 云端用户收藏响应
    /// </summary>
    public class CloudUserFavoritesResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("favorites")]
        public CloudUserModFavorite[]? Favorites { get; set; }

        [JsonPropertyName("total_count")]
        public int TotalCount { get; set; }
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

    /// <summary>
    /// 通用云端响应
    /// </summary>
    public class CloudResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("data")]
        public object? Data { get; set; }
    }

    #endregion
}