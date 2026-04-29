using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace UEModManager.Models
{
    /// <summary>
    /// 本地用户信息
    /// </summary>
    public class LocalUser
    {
        public int Id { get; set; }
        
        [Required]
        [MaxLength(255)]
        public string Email { get; set; } = string.Empty;
        
        [MaxLength(100)]
        public string? Username { get; set; }
        
        [Required]
        public string PasswordHash { get; set; } = string.Empty;
        
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime LastLoginAt { get; set; } = DateTime.Now;
        public bool IsActive { get; set; } = true;
        public bool IsLocked { get; set; } = false;
        public bool IsAdmin { get; set; } = false;
        
        // 扩展属性
        [MaxLength(500)]
        public string? Avatar { get; set; }
        
        [MaxLength(200)]
        public string? DisplayName { get; set; }
    }

    /// <summary>
    /// 用户偏好设置
    /// </summary>
    public class UserPreferences
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public LocalUser User { get; set; } = null!;
        
        // 游戏设置
        [MaxLength(500)]
        public string? DefaultGamePath { get; set; }
        
        [MaxLength(50)]
        public string Language { get; set; } = "zh-CN";
        
        [MaxLength(50)]
        public string Theme { get; set; } = "Dark";
        
        // 应用行为设置
        public bool AutoCheckUpdates { get; set; } = true;
        public bool AutoBackup { get; set; } = true;
        public bool ShowNotifications { get; set; } = true;
        public bool MinimizeToTray { get; set; } = false;
        
        // 同步设置
        public bool EnableCloudSync { get; set; } = false;
        public DateTime LastSyncAt { get; set; } = DateTime.MinValue;
        
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// MOD本地缓存信息
    /// </summary>
    public class LocalModCache
    {
        public int Id { get; set; }
        
        [Required]
        [MaxLength(100)]
        public string ModId { get; set; } = string.Empty;
        
        [Required]
        [MaxLength(200)]
        public string ModName { get; set; } = string.Empty;
        
        [MaxLength(1000)]
        public string? Description { get; set; }
        
        [MaxLength(50)]
        public string? Version { get; set; }
        
        [MaxLength(100)]
        public string? Author { get; set; }
        
        [MaxLength(100)]
        public string GameName { get; set; } = string.Empty;
        
        // 本地文件路径
        [MaxLength(500)]
        public string? LocalPath { get; set; }
        
        // 下载地址
        [MaxLength(1000)]
        public string? DownloadUrl { get; set; }
        
        // 缓存文件路径
        [MaxLength(500)]
        public string? FilePath { get; set; }
        
        // 状态信息
        public bool IsInstalled { get; set; } = false;
        public bool IsEnabled { get; set; } = false;
        public bool IsFavorite { get; set; } = false;
        
        // 元数据
        public long FileSize { get; set; } = 0;
        public DateTime InstallDate { get; set; } = DateTime.MinValue;
        public DateTime CacheTime { get; set; } = DateTime.Now;
        public DateTime CachedAt { get; set; } = DateTime.Now;
        public DateTime LastUpdated { get; set; } = DateTime.Now;
        
        // 统计信息
        public int DownloadCount { get; set; } = 0;
        public decimal Rating { get; set; } = 0;
    }

    /// <summary>
    /// 应用程序配置
    /// </summary>
    public class AppConfiguration
    {
        public int Id { get; set; }
        
        [Required]
        [MaxLength(100)]
        public string Key { get; set; } = string.Empty;
        
        public string Value { get; set; } = string.Empty;
        
        [MaxLength(200)]
        public string? Description { get; set; }
        
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// 用户会话信息
    /// </summary>
    public class UserSession
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public LocalUser User { get; set; } = null!;
        
        [Required]
        public string SessionToken { get; set; } = string.Empty;
        
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime ExpiresAt { get; set; }
        public DateTime? LastAccessAt { get; set; }
        
        public bool IsActive { get; set; } = true;
        
        [MaxLength(200)]
        public string? DeviceInfo { get; set; }
    }

    /// <summary>
    /// 失败登录尝试记录
    /// </summary>
    public class FailedLoginAttempt
    {
        public int Id { get; set; }
        
        [Required]
        [MaxLength(255)]
        public string Email { get; set; } = string.Empty;
        
        public int? UserId { get; set; }
        public LocalUser? User { get; set; }
        
        public DateTime AttemptTime { get; set; } = DateTime.Now;
        
        [MaxLength(45)]
        public string? IpAddress { get; set; }
        
        [MaxLength(500)]
        public string? UserAgent { get; set; }
        
        [MaxLength(200)]
        public string? FailureReason { get; set; }
    }
}