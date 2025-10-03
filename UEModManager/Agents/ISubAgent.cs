using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace UEModManager.Agents
{
    /// <summary>
    /// SubAgent接口定义 - 所有智能代理的基础接口
    /// </summary>
    public interface ISubAgent
    {
        /// <summary>
        /// 代理名称
        /// </summary>
        string Name { get; }

        /// <summary>
        /// 代理类型
        /// </summary>
        AgentType Type { get; }

        /// <summary>
        /// 代理状态
        /// </summary>
        AgentStatus Status { get; }

        /// <summary>
        /// 代理优先级 (1-10, 1最高)
        /// </summary>
        int Priority { get; }

        /// <summary>
        /// 执行代理任务
        /// </summary>
        /// <param name="request">任务请求</param>
        /// <returns>任务结果</returns>
        Task<AgentResponse> ExecuteAsync(AgentRequest request);

        /// <summary>
        /// 代理健康检查
        /// </summary>
        /// <returns>健康状态</returns>
        Task<bool> HealthCheckAsync();

        /// <summary>
        /// 初始化代理
        /// </summary>
        Task InitializeAsync();

        /// <summary>
        /// 释放代理资源
        /// </summary>
        Task DisposeAsync();
    }

    /// <summary>
    /// 代理类型枚举
    /// </summary>
    public enum AgentType
    {
        /// <summary>
        /// 主控代理 - 负责协调其他代理
        /// </summary>
        Control,

        /// <summary>
        /// 项目优化代理 - 负责代码优化、性能分析
        /// </summary>
        ProjectOptimizer,

        /// <summary>
        /// 认证代理 - 负责用户认证、权限管理
        /// </summary>
        Authentication,

        /// <summary>
        /// 测试代理 - 负责自动测试、质量保证
        /// </summary>
        Testing,

        /// <summary>
        /// 输出代理 - 负责报告生成、文档输出
        /// </summary>
        Output,

        /// <summary>
        /// 监控代理 - 负责系统监控、日志分析
        /// </summary>
        Monitoring
    }

    /// <summary>
    /// 代理状态枚举
    /// </summary>
    public enum AgentStatus
    {
        /// <summary>
        /// 未初始化
        /// </summary>
        Uninitialized,

        /// <summary>
        /// 初始化中
        /// </summary>
        Initializing,

        /// <summary>
        /// 空闲状态
        /// </summary>
        Idle,

        /// <summary>
        /// 执行中
        /// </summary>
        Running,

        /// <summary>
        /// 等待中
        /// </summary>
        Waiting,

        /// <summary>
        /// 错误状态
        /// </summary>
        Error,

        /// <summary>
        /// 已停止
        /// </summary>
        Stopped
    }

    /// <summary>
    /// 代理请求
    /// </summary>
    public class AgentRequest
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string TaskType { get; set; } = string.Empty;
        public Dictionary<string, object> Parameters { get; set; } = new();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public int Priority { get; set; } = 5;
        public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(10);
        public string RequesterId { get; set; } = string.Empty;
        public Dictionary<string, string> Context { get; set; } = new();
    }

    /// <summary>
    /// 代理响应
    /// </summary>
    public class AgentResponse
    {
        public string RequestId { get; set; } = string.Empty;
        public bool IsSuccess { get; set; }
        public string Message { get; set; } = string.Empty;
        public Dictionary<string, object> Data { get; set; } = new();
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public DateTime CompletedAt { get; set; } = DateTime.UtcNow;
        public TimeSpan ExecutionTime { get; set; }
        public Dictionary<string, object> Metrics { get; set; } = new();

        public static AgentResponse Success(string message = "操作成功", Dictionary<string, object>? data = null)
        {
            return new AgentResponse
            {
                IsSuccess = true,
                Message = message,
                Data = data ?? new Dictionary<string, object>()
            };
        }

        public static AgentResponse Failed(string message, List<string>? errors = null)
        {
            return new AgentResponse
            {
                IsSuccess = false,
                Message = message,
                Errors = errors ?? new List<string>()
            };
        }
    }

    /// <summary>
    /// 代理任务类型常量
    /// </summary>
    public static class AgentTasks
    {
        // 控制代理任务
        public const string COORDINATE_AGENTS = "coordinate_agents";
        public const string MANAGE_WORKFLOW = "manage_workflow";
        public const string EXECUTE_WORKFLOW = "execute_workflow";

        // 优化代理任务
        public const string ANALYZE_CODE = "analyze_code";
        public const string OPTIMIZE_PERFORMANCE = "optimize_performance";
        public const string REFACTOR_CODE = "refactor_code";

        // 认证代理任务
        public const string VALIDATE_USER = "validate_user";
        public const string MANAGE_PERMISSIONS = "manage_permissions";
        public const string SETUP_AUTHENTICATION = "setup_authentication";

        // 测试代理任务
        public const string RUN_UNIT_TESTS = "run_unit_tests";
        public const string INTEGRATION_TEST = "integration_test";
        public const string PERFORMANCE_TEST = "performance_test";

        // 输出代理任务
        public const string GENERATE_REPORT = "generate_report";
        public const string CREATE_DOCUMENTATION = "create_documentation";
        public const string EXPORT_DATA = "export_data";

        // 监控代理任务
        public const string SYSTEM_HEALTH_CHECK = "system_health_check";
        public const string LOG_ANALYSIS = "log_analysis";
        public const string PERFORMANCE_MONITORING = "performance_monitoring";
    }
}