using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace UEModManager.Agents
{
    /// <summary>
    /// SubAgent基础实现类
    /// </summary>
    public abstract class BaseSubAgent : ISubAgent
    {
        protected readonly ILogger _logger;
        private AgentStatus _status = AgentStatus.Uninitialized;
        private readonly object _statusLock = new object();

        protected BaseSubAgent(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public abstract string Name { get; }
        public abstract AgentType Type { get; }
        public abstract int Priority { get; }

        public AgentStatus Status
        {
            get
            {
                lock (_statusLock)
                {
                    return _status;
                }
            }
            protected set
            {
                lock (_statusLock)
                {
                    var oldStatus = _status;
                    _status = value;
                    OnStatusChanged(oldStatus, value);
                }
            }
        }

        public virtual async Task<AgentResponse> ExecuteAsync(AgentRequest request)
        {
            if (request == null)
                return AgentResponse.Failed("请求不能为空");

            if (Status != AgentStatus.Idle)
                return AgentResponse.Failed($"代理状态不正确: {Status}");

            Status = AgentStatus.Running;
            var stopwatch = Stopwatch.StartNew();

            try
            {
                _logger.LogInformation($"[{Name}] 开始执行任务: {request.TaskType}");

                var response = await ExecuteInternalAsync(request);
                response.RequestId = request.Id;
                response.ExecutionTime = stopwatch.Elapsed;

                _logger.LogInformation($"[{Name}] 任务完成: {request.TaskType}, 耗时: {stopwatch.ElapsedMilliseconds}ms");

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[{Name}] 执行任务失败: {request.TaskType}");
                return AgentResponse.Failed($"执行失败: {ex.Message}", new List<string> { ex.ToString() });
            }
            finally
            {
                Status = AgentStatus.Idle;
                stopwatch.Stop();
            }
        }

        /// <summary>
        /// 子类实现具体的任务执行逻辑
        /// </summary>
        protected abstract Task<AgentResponse> ExecuteInternalAsync(AgentRequest request);

        public virtual async Task<bool> HealthCheckAsync()
        {
            try
            {
                return await PerformHealthCheckAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[{Name}] 健康检查失败");
                return false;
            }
        }

        /// <summary>
        /// 子类实现具体的健康检查逻辑
        /// </summary>
        protected virtual Task<bool> PerformHealthCheckAsync()
        {
            return Task.FromResult(Status == AgentStatus.Idle || Status == AgentStatus.Running);
        }

        public virtual async Task InitializeAsync()
        {
            if (Status != AgentStatus.Uninitialized)
            {
                _logger.LogWarning($"[{Name}] 代理已经初始化，跳过");
                return;
            }

            Status = AgentStatus.Initializing;

            try
            {
                _logger.LogInformation($"[{Name}] 开始初始化");
                await InitializeInternalAsync();
                Status = AgentStatus.Idle;
                _logger.LogInformation($"[{Name}] 初始化完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[{Name}] 初始化失败");
                Status = AgentStatus.Error;
                throw;
            }
        }

        /// <summary>
        /// 子类实现具体的初始化逻辑
        /// </summary>
        protected virtual Task InitializeInternalAsync()
        {
            return Task.CompletedTask;
        }

        public virtual async Task DisposeAsync()
        {
            if (Status == AgentStatus.Stopped)
                return;

            try
            {
                _logger.LogInformation($"[{Name}] 开始释放资源");
                await DisposeInternalAsync();
                Status = AgentStatus.Stopped;
                _logger.LogInformation($"[{Name}] 资源释放完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[{Name}] 释放资源失败");
                throw;
            }
        }

        /// <summary>
        /// 子类实现具体的资源释放逻辑
        /// </summary>
        protected virtual Task DisposeInternalAsync()
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// 状态变更通知
        /// </summary>
        protected virtual void OnStatusChanged(AgentStatus oldStatus, AgentStatus newStatus)
        {
            _logger.LogDebug($"[{Name}] 状态变更: {oldStatus} -> {newStatus}");
        }

        /// <summary>
        /// 验证请求参数
        /// </summary>
        protected bool ValidateRequest(AgentRequest request, string requiredTaskType, out AgentResponse? errorResponse)
        {
            errorResponse = null;

            if (request.TaskType != requiredTaskType)
            {
                errorResponse = AgentResponse.Failed($"不支持的任务类型: {request.TaskType}");
                return false;
            }

            return true;
        }

        /// <summary>
        /// 获取请求参数
        /// </summary>
        protected T? GetParameter<T>(AgentRequest request, string key, T? defaultValue = default)
        {
            if (request.Parameters.TryGetValue(key, out var value))
            {
                try
                {
                    return (T?)Convert.ChangeType(value, typeof(T));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"[{Name}] 参数转换失败: {key} -> {typeof(T).Name}, {ex.Message}");
                }
            }

            return defaultValue;
        }
    }
}