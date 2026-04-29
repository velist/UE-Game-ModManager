using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using UEModManager.Agents;

namespace UEModManager.Services
{
    /// <summary>
    /// 代理管理服务 - 负责所有SubAgent的生命周期管理
    /// </summary>
    public class AgentManagerService
    {
        private readonly ILogger<AgentManagerService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private ControlAgent? _controlAgent;
        private bool _isInitialized = false;

        public AgentManagerService(ILogger<AgentManagerService> logger, IServiceProvider serviceProvider)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        }

        /// <summary>
        /// 获取主控代理
        /// </summary>
        public ControlAgent? ControlAgent => _controlAgent;

        /// <summary>
        /// 是否已初始化
        /// </summary>
        public bool IsInitialized => _isInitialized;

        /// <summary>
        /// 初始化代理管理服务
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_isInitialized)
            {
                _logger.LogWarning("代理管理服务已经初始化，跳过");
                return;
            }

            try
            {
                _logger.LogInformation("开始初始化代理管理服务");

                // 创建主控代理
                _controlAgent = new ControlAgent(_serviceProvider.GetRequiredService<ILogger<ControlAgent>>());

                // 注册所有专业代理
                await RegisterSpecializedAgentsAsync();

                // 初始化主控代理（会自动初始化所有注册的代理）
                await _controlAgent.InitializeAsync();

                _isInitialized = true;
                _logger.LogInformation("代理管理服务初始化完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "代理管理服务初始化失败");
                throw;
            }
        }

        /// <summary>
        /// 执行代理任务
        /// </summary>
        /// <param name="agentType">代理类型</param>
        /// <param name="taskType">任务类型</param>
        /// <param name="parameters">任务参数</param>
        /// <returns>任务结果</returns>
        public async Task<AgentResponse> ExecuteTaskAsync(AgentType agentType, string taskType, Dictionary<string, object>? parameters = null)
        {
            if (!_isInitialized || _controlAgent == null)
                return AgentResponse.Failed("代理管理服务未初始化");

            var request = new AgentRequest
            {
                Id = Guid.NewGuid().ToString(),
                TaskType = AgentTasks.COORDINATE_AGENTS,
                Parameters = new Dictionary<string, object>
                {
                    { "agentType", agentType },
                    { "taskType", taskType },
                    { "parameters", parameters ?? new Dictionary<string, object>() }
                },
                CreatedAt = DateTime.UtcNow
            };

            return await _controlAgent.ExecuteAsync(request);
        }

        /// <summary>
        /// 执行工作流
        /// </summary>
        /// <param name="workflow">工作流定义</param>
        /// <param name="continueOnError">出错时是否继续</param>
        /// <returns>工作流执行结果</returns>
        public async Task<AgentResponse> ExecuteWorkflowAsync(List<Dictionary<string, object>> workflow, bool continueOnError = false)
        {
            if (!_isInitialized || _controlAgent == null)
                return AgentResponse.Failed("代理管理服务未初始化");

            var request = new AgentRequest
            {
                Id = Guid.NewGuid().ToString(),
                TaskType = AgentTasks.EXECUTE_WORKFLOW,
                Parameters = new Dictionary<string, object>
                {
                    { "workflow", workflow },
                    { "continueOnError", continueOnError }
                },
                CreatedAt = DateTime.UtcNow
            };

            return await _controlAgent.ExecuteAsync(request);
        }

        /// <summary>
        /// 获取系统健康状态
        /// </summary>
        /// <returns>健康状态报告</returns>
        public async Task<AgentResponse> GetSystemHealthAsync()
        {
            if (!_isInitialized || _controlAgent == null)
                return AgentResponse.Failed("代理管理服务未初始化");

            var request = new AgentRequest
            {
                Id = Guid.NewGuid().ToString(),
                TaskType = AgentTasks.SYSTEM_HEALTH_CHECK,
                CreatedAt = DateTime.UtcNow
            };

            return await _controlAgent.ExecuteAsync(request);
        }

        /// <summary>
        /// 获取系统状态
        /// </summary>
        /// <returns>系统状态信息</returns>
        public async Task<AgentResponse> GetSystemStatusAsync(bool includeDetails = true)
        {
            if (!_isInitialized || _controlAgent == null)
                return AgentResponse.Failed("代理管理服务未初始化");

            var request = new AgentRequest
            {
                Id = Guid.NewGuid().ToString(),
                TaskType = "get_system_status",
                Parameters = new Dictionary<string, object>
                {
                    { "includeDetails", includeDetails }
                },
                CreatedAt = DateTime.UtcNow
            };

            return await _controlAgent.ExecuteAsync(request);
        }

        /// <summary>
        /// 批量执行任务
        /// </summary>
        /// <param name="tasks">任务列表</param>
        /// <param name="parallel">是否并行执行</param>
        /// <returns>批量任务执行结果</returns>
        public async Task<AgentResponse> ExecuteBatchTasksAsync(List<Dictionary<string, object>> tasks, bool parallel = false)
        {
            if (!_isInitialized || _controlAgent == null)
                return AgentResponse.Failed("代理管理服务未初始化");

            var request = new AgentRequest
            {
                Id = Guid.NewGuid().ToString(),
                TaskType = "execute_batch_tasks",
                Parameters = new Dictionary<string, object>
                {
                    { "tasks", tasks },
                    { "parallel", parallel }
                },
                CreatedAt = DateTime.UtcNow
            };

            return await _controlAgent.ExecuteAsync(request);
        }

        /// <summary>
        /// 获取特定代理
        /// </summary>
        /// <param name="agentType">代理类型</param>
        /// <returns>代理实例</returns>
        public ISubAgent? GetAgent(AgentType agentType)
        {
            return _controlAgent?.GetAgent(agentType);
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public async Task DisposeAsync()
        {
            if (_controlAgent != null)
            {
                try
                {
                    await _controlAgent.DisposeAsync();
                    _logger.LogInformation("代理管理服务资源释放完成");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "代理管理服务资源释放失败");
                }
            }

            _isInitialized = false;
        }

        /// <summary>
        /// 注册专业代理
        /// </summary>
        private async Task RegisterSpecializedAgentsAsync()
        {
            if (_controlAgent == null) return;

            try
            {
                // 注册项目优化代理
                var projectOptimizerAgent = new ProjectOptimizerAgent(
                    _serviceProvider.GetRequiredService<ILogger<ProjectOptimizerAgent>>(),
                    Environment.CurrentDirectory);
                _controlAgent.RegisterAgent(projectOptimizerAgent);

                // 注册认证代理 (PostgreSQLAuthService 可选)
                var authenticationAgent = new AuthenticationAgent(
                    _serviceProvider.GetRequiredService<ILogger<AuthenticationAgent>>(),
                    _serviceProvider.GetRequiredService<LocalAuthService>(),
                    _serviceProvider.GetRequiredService<UnifiedAuthService>(),
                    _serviceProvider,
                    _serviceProvider.GetService<PostgreSQLAuthService>());
                _controlAgent.RegisterAgent(authenticationAgent);

                // 注册测试代理
                var testingAgent = new TestingAgent(
                    _serviceProvider.GetRequiredService<ILogger<TestingAgent>>(),
                    Environment.CurrentDirectory);
                _controlAgent.RegisterAgent(testingAgent);

                // 注册输出代理
                var outputAgent = new OutputAgent(
                    _serviceProvider.GetRequiredService<ILogger<OutputAgent>>());
                _controlAgent.RegisterAgent(outputAgent);

                _logger.LogInformation("所有专业代理注册完成");
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "注册专业代理失败");
                throw;
            }
        }

        #region 便捷方法

        /// <summary>
        /// 用户认证
        /// </summary>
        public async Task<AgentResponse> AuthenticateUserAsync(string email, string password, string type = "login")
        {
            var parameters = new Dictionary<string, object>
            {
                { "email", email },
                { "password", password },
                { "type", type }
            };

            return await ExecuteTaskAsync(AgentType.Authentication, AgentTasks.VALIDATE_USER, parameters);
        }

        /// <summary>
        /// 设置认证系统
        /// </summary>
        public async Task<AgentResponse> SetupAuthenticationAsync(string setupType = "full")
        {
            var parameters = new Dictionary<string, object>
            {
                { "setupType", setupType }
            };

            return await ExecuteTaskAsync(AgentType.Authentication, AgentTasks.SETUP_AUTHENTICATION, parameters);
        }

        /// <summary>
        /// 权限管理
        /// </summary>
        public async Task<AgentResponse> ManagePermissionAsync(string action, string userId, string permission = "")
        {
            var parameters = new Dictionary<string, object>
            {
                { "action", action },
                { "userId", userId },
                { "permission", permission }
            };

            return await ExecuteTaskAsync(AgentType.Authentication, AgentTasks.MANAGE_PERMISSIONS, parameters);
        }

        /// <summary>
        /// 代码分析
        /// </summary>
        public async Task<AgentResponse> AnalyzeCodeAsync(string path = "", string[] options = null)
        {
            var parameters = new Dictionary<string, object>
            {
                { "path", string.IsNullOrEmpty(path) ? Environment.CurrentDirectory : path },
                { "options", options ?? new[] { "complexity", "duplication", "security" } }
            };

            return await ExecuteTaskAsync(AgentType.ProjectOptimizer, AgentTasks.ANALYZE_CODE, parameters);
        }

        /// <summary>
        /// 性能优化
        /// </summary>
        public async Task<AgentResponse> OptimizePerformanceAsync(string path = "", string[] targets = null)
        {
            var parameters = new Dictionary<string, object>
            {
                { "path", string.IsNullOrEmpty(path) ? Environment.CurrentDirectory : path },
                { "targets", targets ?? new[] { "memory", "cpu", "io" } }
            };

            return await ExecuteTaskAsync(AgentType.ProjectOptimizer, AgentTasks.OPTIMIZE_PERFORMANCE, parameters);
        }

        /// <summary>
        /// 运行单元测试
        /// </summary>
        public async Task<AgentResponse> RunUnitTestsAsync(string testProject = "", string filter = "")
        {
            var parameters = new Dictionary<string, object>
            {
                { "testProject", testProject },
                { "filter", filter }
            };

            return await ExecuteTaskAsync(AgentType.Testing, AgentTasks.RUN_UNIT_TESTS, parameters);
        }

        /// <summary>
        /// 生成报告
        /// </summary>
        public async Task<AgentResponse> GenerateReportAsync(string reportType = "general", string format = "json", Dictionary<string, object>? data = null)
        {
            var parameters = new Dictionary<string, object>
            {
                { "reportType", reportType },
                { "format", format },
                { "data", data ?? new Dictionary<string, object>() }
            };

            return await ExecuteTaskAsync(AgentType.Output, AgentTasks.GENERATE_REPORT, parameters);
        }

        #endregion
    }
}