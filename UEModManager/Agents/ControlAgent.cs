using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace UEModManager.Agents
{
    /// <summary>
    /// 主控代理 - 负责协调和管理所有子代理
    /// </summary>
    public class ControlAgent : BaseSubAgent
    {
        private readonly Dictionary<AgentType, ISubAgent> _agents;
        private readonly object _agentsLock = new object();

        public ControlAgent(ILogger<ControlAgent> logger) 
            : base(logger)
        {
            _agents = new Dictionary<AgentType, ISubAgent>();
        }

        public override string Name => "ControlAgent";
        public override AgentType Type => AgentType.Control;
        public override int Priority => 0; // 最高优先级

        protected override async Task<AgentResponse> ExecuteInternalAsync(AgentRequest request)
        {
            return request.TaskType switch
            {
                AgentTasks.COORDINATE_AGENTS => await CoordinateAgentsAsync(request),
                AgentTasks.SYSTEM_HEALTH_CHECK => await SystemHealthCheckAsync(request),
                AgentTasks.EXECUTE_WORKFLOW => await ExecuteWorkflowAsync(request),
                "manage_agents" => await ManageAgentsAsync(request),
                "get_system_status" => await GetSystemStatusAsync(request),
                "execute_batch_tasks" => await ExecuteBatchTasksAsync(request),
                _ => AgentResponse.Failed($"不支持的任务类型: {request.TaskType}")
            };
        }

        /// <summary>
        /// 注册代理
        /// </summary>
        public void RegisterAgent(ISubAgent agent)
        {
            if (agent == null)
                throw new ArgumentNullException(nameof(agent));

            lock (_agentsLock)
            {
                _agents[agent.Type] = agent;
                _logger.LogInformation($"已注册代理: {agent.Name} ({agent.Type})");
            }
        }

        /// <summary>
        /// 注销代理
        /// </summary>
        public bool UnregisterAgent(AgentType agentType)
        {
            lock (_agentsLock)
            {
                if (_agents.Remove(agentType))
                {
                    _logger.LogInformation($"已注销代理: {agentType}");
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 获取代理
        /// </summary>
        public ISubAgent? GetAgent(AgentType agentType)
        {
            lock (_agentsLock)
            {
                return _agents.TryGetValue(agentType, out var agent) ? agent : null;
            }
        }

        /// <summary>
        /// 协调代理任务
        /// </summary>
        private async Task<AgentResponse> CoordinateAgentsAsync(AgentRequest request)
        {
            var targetAgentType = GetParameter<AgentType>(request, "agentType", AgentType.Authentication);
            var subTaskType = GetParameter<string>(request, "taskType", "");
            var parameters = GetParameter<Dictionary<string, object>>(request, "parameters", new Dictionary<string, object>());

            if (string.IsNullOrEmpty(subTaskType))
                return AgentResponse.Failed("子任务类型不能为空");

            var targetAgent = GetAgent(targetAgentType);
            if (targetAgent == null)
                return AgentResponse.Failed($"未找到代理: {targetAgentType}");

            var subRequest = new AgentRequest
            {
                Id = Guid.NewGuid().ToString(),
                TaskType = subTaskType,
                Parameters = parameters,
                CreatedAt = DateTime.UtcNow
            };

            var response = await targetAgent.ExecuteAsync(subRequest);
            
            return AgentResponse.Success($"代理协调完成: {targetAgentType}", new Dictionary<string, object>
            {
                { "targetAgent", targetAgentType.ToString() },
                { "subTask", subTaskType },
                { "result", response }
            });
        }

        /// <summary>
        /// 系统健康检查
        /// </summary>
        private async Task<AgentResponse> SystemHealthCheckAsync(AgentRequest request)
        {
            var results = new Dictionary<string, object>();
            var healthyAgents = 0;
            var totalAgents = 0;

            lock (_agentsLock)
            {
                totalAgents = _agents.Count;
            }

            var agentHealthResults = new List<object>();

            foreach (var agentKvp in _agents.ToList())
            {
                try
                {
                    var isHealthy = await agentKvp.Value.HealthCheckAsync();
                    if (isHealthy) healthyAgents++;

                    agentHealthResults.Add(new
                    {
                        AgentType = agentKvp.Key.ToString(),
                        AgentName = agentKvp.Value.Name,
                        Status = agentKvp.Value.Status.ToString(),
                        IsHealthy = isHealthy,
                        Priority = agentKvp.Value.Priority
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"健康检查失败: {agentKvp.Value.Name}");
                    agentHealthResults.Add(new
                    {
                        AgentType = agentKvp.Key.ToString(),
                        AgentName = agentKvp.Value.Name,
                        Status = "Error",
                        IsHealthy = false,
                        Error = ex.Message
                    });
                }
            }

            results["totalAgents"] = totalAgents;
            results["healthyAgents"] = healthyAgents;
            results["systemHealth"] = totalAgents > 0 ? (double)healthyAgents / totalAgents : 0.0;
            results["agentDetails"] = agentHealthResults;

            var systemHealthPercent = (double)healthyAgents / Math.Max(totalAgents, 1) * 100;
            var healthStatus = systemHealthPercent switch
            {
                100 => "优秀",
                >= 80 => "良好", 
                >= 60 => "一般",
                _ => "需要关注"
            };

            return AgentResponse.Success($"系统健康检查完成: {healthStatus} ({healthyAgents}/{totalAgents})", results);
        }

        /// <summary>
        /// 执行工作流
        /// </summary>
        private async Task<AgentResponse> ExecuteWorkflowAsync(AgentRequest request)
        {
            var workflow = GetParameter<List<Dictionary<string, object>>>(request, "workflow", new List<Dictionary<string, object>>());
            var continueOnError = GetParameter<bool>(request, "continueOnError", false);

            if (!workflow.Any())
                return AgentResponse.Failed("工作流不能为空");

            var workflowResults = new List<object>();
            var successCount = 0;
            var failedCount = 0;

            foreach (var step in workflow)
            {
                try
                {
                    var stepAgentType = (AgentType)Enum.Parse(typeof(AgentType), step["agentType"].ToString());
                    var stepTaskType = step["taskType"].ToString();
                    var stepParameters = step.ContainsKey("parameters") 
                        ? (Dictionary<string, object>)step["parameters"] 
                        : new Dictionary<string, object>();

                    var stepRequest = new AgentRequest
                    {
                        Id = Guid.NewGuid().ToString(),
                        TaskType = stepTaskType,
                        Parameters = stepParameters,
                        CreatedAt = DateTime.UtcNow
                    };

                    var targetAgent = GetAgent(stepAgentType);
                    if (targetAgent == null)
                    {
                        var errorResult = new
                        {
                            Step = workflow.IndexOf(step) + 1,
                            AgentType = stepAgentType.ToString(),
                            TaskType = stepTaskType,
                            Success = false,
                            Error = $"未找到代理: {stepAgentType}"
                        };

                        workflowResults.Add(errorResult);
                        failedCount++;

                        if (!continueOnError)
                            break;
                        continue;
                    }

                    var stepResponse = await targetAgent.ExecuteAsync(stepRequest);

                    var stepResult = new
                    {
                        Step = workflow.IndexOf(step) + 1,
                        AgentType = stepAgentType.ToString(),
                        TaskType = stepTaskType,
                        Success = stepResponse.IsSuccess,
                        Message = stepResponse.Message,
                        ExecutionTime = stepResponse.ExecutionTime,
                        Data = stepResponse.Data
                    };

                    workflowResults.Add(stepResult);

                    if (stepResponse.IsSuccess)
                        successCount++;
                    else
                    {
                        failedCount++;
                        if (!continueOnError)
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"工作流步骤执行失败: {step}");
                    
                    var errorResult = new
                    {
                        Step = workflow.IndexOf(step) + 1,
                        Success = false,
                        Error = ex.Message
                    };

                    workflowResults.Add(errorResult);
                    failedCount++;

                    if (!continueOnError)
                        break;
                }
            }

            return AgentResponse.Success($"工作流执行完成: {successCount} 成功, {failedCount} 失败", 
                new Dictionary<string, object>
                {
                    { "totalSteps", workflow.Count },
                    { "successCount", successCount },
                    { "failedCount", failedCount },
                    { "results", workflowResults }
                });
        }

        /// <summary>
        /// 代理管理任务
        /// </summary>
        private async Task<AgentResponse> ManageAgentsAsync(AgentRequest request)
        {
            var action = GetParameter<string>(request, "action", "list");
            var results = new Dictionary<string, object>();

            switch (action)
            {
                case "list":
                    var agentList = new List<object>();
                    foreach (var agent in _agents.Values)
                    {
                        agentList.Add(new
                        {
                            Name = agent.Name,
                            Type = agent.Type.ToString(),
                            Status = agent.Status.ToString(),
                            Priority = agent.Priority
                        });
                    }
                    results["agents"] = agentList;
                    break;

                case "initialize_all":
                    var initResults = new List<object>();
                    foreach (var agent in _agents.Values.OrderBy(a => a.Priority))
                    {
                        try
                        {
                            await agent.InitializeAsync();
                            initResults.Add(new { Agent = agent.Name, Success = true });
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"初始化代理失败: {agent.Name}");
                            initResults.Add(new { Agent = agent.Name, Success = false, Error = ex.Message });
                        }
                    }
                    results["initializationResults"] = initResults;
                    break;

                case "status":
                    results["agentCount"] = _agents.Count;
                    results["runningAgents"] = _agents.Values.Count(a => a.Status == AgentStatus.Running);
                    results["idleAgents"] = _agents.Values.Count(a => a.Status == AgentStatus.Idle);
                    results["errorAgents"] = _agents.Values.Count(a => a.Status == AgentStatus.Error);
                    break;

                default:
                    return AgentResponse.Failed($"不支持的管理操作: {action}");
            }

            return AgentResponse.Success($"代理管理操作完成: {action}", results);
        }

        /// <summary>
        /// 获取系统状态
        /// </summary>
        private async Task<AgentResponse> GetSystemStatusAsync(AgentRequest request)
        {
            var includeDetails = GetParameter<bool>(request, "includeDetails", true);
            
            var status = new Dictionary<string, object>
            {
                { "controllerStatus", Status.ToString() },
                { "totalAgents", _agents.Count },
                { "timestamp", DateTime.UtcNow }
            };

            if (includeDetails)
            {
                var agentStatuses = new List<object>();
                foreach (var agent in _agents.Values)
                {
                    agentStatuses.Add(new
                    {
                        Name = agent.Name,
                        Type = agent.Type.ToString(),
                        Status = agent.Status.ToString(),
                        Priority = agent.Priority
                    });
                }
                status["agents"] = agentStatuses;
            }

            // 获取系统指标
            var systemMetrics = await GetSystemMetricsAsync();
            status["metrics"] = systemMetrics;

            return AgentResponse.Success("系统状态获取完成", status);
        }

        /// <summary>
        /// 批量执行任务
        /// </summary>
        private async Task<AgentResponse> ExecuteBatchTasksAsync(AgentRequest request)
        {
            var tasks = GetParameter<List<Dictionary<string, object>>>(request, "tasks", new List<Dictionary<string, object>>());
            var parallelExecution = GetParameter<bool>(request, "parallel", false);

            if (!tasks.Any())
                return AgentResponse.Failed("批量任务列表不能为空");

            var results = new List<object>();

            if (parallelExecution)
            {
                // 并行执行
                var parallelTasks = tasks.Select<Dictionary<string, object>, Task<object>>(async task =>
                {
                    try
                    {
                        var agentType = (AgentType)Enum.Parse(typeof(AgentType), task["agentType"].ToString());
                        var taskType = task["taskType"].ToString();
                        var parameters = task.ContainsKey("parameters") 
                            ? (Dictionary<string, object>)task["parameters"] 
                            : new Dictionary<string, object>();

                        var taskRequest = new AgentRequest
                        {
                            Id = Guid.NewGuid().ToString(),
                            TaskType = taskType,
                            Parameters = parameters,
                            CreatedAt = DateTime.UtcNow
                        };

                        var targetAgent = GetAgent(agentType);
                        if (targetAgent == null)
                        {
                            return new { Success = false, Error = $"未找到代理: {agentType}" };
                        }

                        var response = await targetAgent.ExecuteAsync(taskRequest);
                        return new { Success = response.IsSuccess, Response = response };
                    }
                    catch (Exception ex)
                    {
                        return new { Success = false, Error = ex.Message };
                    }
                });

                var parallelResults = await Task.WhenAll(parallelTasks);
                results.AddRange(parallelResults);
            }
            else
            {
                // 串行执行
                foreach (var task in tasks)
                {
                    try
                    {
                        var agentType = (AgentType)Enum.Parse(typeof(AgentType), task["agentType"].ToString());
                        var taskType = task["taskType"].ToString();
                        var parameters = task.ContainsKey("parameters") 
                            ? (Dictionary<string, object>)task["parameters"] 
                            : new Dictionary<string, object>();

                        var taskRequest = new AgentRequest
                        {
                            Id = Guid.NewGuid().ToString(),
                            TaskType = taskType,
                            Parameters = parameters,
                            CreatedAt = DateTime.UtcNow
                        };

                        var targetAgent = GetAgent(agentType);
                        if (targetAgent == null)
                        {
                            results.Add(new { Success = false, Error = $"未找到代理: {agentType}" });
                            continue;
                        }

                        var response = await targetAgent.ExecuteAsync(taskRequest);
                        results.Add(new { Success = response.IsSuccess, Response = response });
                    }
                    catch (Exception ex)
                    {
                        results.Add(new { Success = false, Error = ex.Message });
                    }
                }
            }

            var successCount = results.Count(r => ((dynamic)r).Success);
            var failedCount = results.Count - successCount;

            return AgentResponse.Success($"批量任务执行完成: {successCount} 成功, {failedCount} 失败", 
                new Dictionary<string, object>
                {
                    { "totalTasks", tasks.Count },
                    { "successCount", successCount },
                    { "failedCount", failedCount },
                    { "results", results },
                    { "executionMode", parallelExecution ? "parallel" : "sequential" }
                });
        }

        /// <summary>
        /// 获取系统指标
        /// </summary>
        private async Task<object> GetSystemMetricsAsync()
        {
            await Task.Delay(10); // 模拟获取系统指标

            return new
            {
                MemoryUsage = GC.GetTotalMemory(false),
                UpTime = DateTime.UtcNow.Subtract(Process.GetCurrentProcess().StartTime),
                ThreadCount = System.Threading.ThreadPool.ThreadCount,
                WorkingSet = Environment.WorkingSet
            };
        }

        protected override async Task<bool> PerformHealthCheckAsync()
        {
            try
            {
                var healthyAgents = 0;
                var totalAgents = _agents.Count;

                foreach (var agent in _agents.Values)
                {
                    if (await agent.HealthCheckAsync())
                        healthyAgents++;
                }

                // 系统健康要求至少80%的代理正常
                return totalAgents == 0 || (double)healthyAgents / totalAgents >= 0.8;
            }
            catch
            {
                return false;
            }
        }

        protected override async Task InitializeInternalAsync()
        {
            _logger.LogInformation("ControlAgent 开始初始化所有注册的代理");

            var initTasks = _agents.Values
                .OrderBy(a => a.Priority)
                .Select(async agent =>
                {
                    try
                    {
                        await agent.InitializeAsync();
                        _logger.LogInformation($"代理初始化成功: {agent.Name}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"代理初始化失败: {agent.Name}");
                    }
                });

            await Task.WhenAll(initTasks);
            _logger.LogInformation("ControlAgent 代理初始化完成");
        }

        protected override async Task DisposeInternalAsync()
        {
            _logger.LogInformation("ControlAgent 开始释放所有代理资源");

            var disposeTasks = _agents.Values
                .OrderByDescending(a => a.Priority)
                .Select(async agent =>
                {
                    try
                    {
                        await agent.DisposeAsync();
                        _logger.LogInformation($"代理资源释放成功: {agent.Name}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"代理资源释放失败: {agent.Name}");
                    }
                });

            await Task.WhenAll(disposeTasks);

            lock (_agentsLock)
            {
                _agents.Clear();
            }

            _logger.LogInformation("ControlAgent 资源释放完成");
        }
    }
}