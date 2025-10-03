using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace UEModManager.Agents
{
    /// <summary>
    /// 输出代理 - 负责报告生成、文档输出、数据导出
    /// </summary>
    public class OutputAgent : BaseSubAgent
    {
        private readonly string _outputPath;

        public OutputAgent(ILogger<OutputAgent> logger, string outputPath = "") 
            : base(logger)
        {
            _outputPath = string.IsNullOrEmpty(outputPath) 
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Reports")
                : outputPath;

            // 确保输出目录存在
            Directory.CreateDirectory(_outputPath);
        }

        public override string Name => "OutputAgent";
        public override AgentType Type => AgentType.Output;
        public override int Priority => 5;

        protected override async Task<AgentResponse> ExecuteInternalAsync(AgentRequest request)
        {
            return request.TaskType switch
            {
                AgentTasks.GENERATE_REPORT => await GenerateReportAsync(request),
                AgentTasks.CREATE_DOCUMENTATION => await CreateDocumentationAsync(request),
                AgentTasks.EXPORT_DATA => await ExportDataAsync(request),
                "generate_dashboard_report" => await GenerateDashboardReportAsync(request),
                "create_test_report" => await CreateTestReportAsync(request),
                "export_user_data" => await ExportUserDataAsync(request),
                _ => AgentResponse.Failed($"不支持的任务类型: {request.TaskType}")
            };
        }

        /// <summary>
        /// 生成报告任务
        /// </summary>
        private async Task<AgentResponse> GenerateReportAsync(AgentRequest request)
        {
            var reportType = GetParameter<string>(request, "reportType", "general");
            var format = GetParameter<string>(request, "format", "json");
            var data = GetParameter<Dictionary<string, object>>(request, "data", new Dictionary<string, object>());

            try
            {
                var report = await CreateReportAsync(reportType, data);
                var fileName = await SaveReportAsync(report, reportType, format);

                return AgentResponse.Success($"报告生成完成: {fileName}", new Dictionary<string, object>
                {
                    { "fileName", fileName },
                    { "filePath", Path.Combine(_outputPath, fileName) },
                    { "reportType", reportType },
                    { "format", format }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"生成报告失败: {reportType}");
                return AgentResponse.Failed($"报告生成失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 创建文档任务
        /// </summary>
        private async Task<AgentResponse> CreateDocumentationAsync(AgentRequest request)
        {
            var docType = GetParameter<string>(request, "docType", "api");
            var title = GetParameter<string>(request, "title", "系统文档");
            var content = GetParameter<Dictionary<string, object>>(request, "content", new Dictionary<string, object>());

            try
            {
                var documentation = await GenerateDocumentationAsync(docType, title, content);
                var fileName = await SaveDocumentationAsync(documentation, docType);

                return AgentResponse.Success($"文档创建完成: {fileName}", new Dictionary<string, object>
                {
                    { "fileName", fileName },
                    { "filePath", Path.Combine(_outputPath, fileName) },
                    { "docType", docType },
                    { "title", title }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"创建文档失败: {docType}");
                return AgentResponse.Failed($"文档创建失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 导出数据任务
        /// </summary>
        private async Task<AgentResponse> ExportDataAsync(AgentRequest request)
        {
            var dataType = GetParameter<string>(request, "dataType", "users");
            var format = GetParameter<string>(request, "format", "csv");
            var filters = GetParameter<Dictionary<string, object>>(request, "filters", new Dictionary<string, object>());

            try
            {
                var exportData = await CollectExportDataAsync(dataType, filters);
                var fileName = await SaveExportDataAsync(exportData, dataType, format);

                return AgentResponse.Success($"数据导出完成: {fileName}", new Dictionary<string, object>
                {
                    { "fileName", fileName },
                    { "filePath", Path.Combine(_outputPath, fileName) },
                    { "dataType", dataType },
                    { "format", format },
                    { "recordCount", exportData.Count }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"导出数据失败: {dataType}");
                return AgentResponse.Failed($"数据导出失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 生成仪表盘报告
        /// </summary>
        private async Task<AgentResponse> GenerateDashboardReportAsync(AgentRequest request)
        {
            var dashboardData = GetParameter<Dictionary<string, object>>(request, "dashboardData", new Dictionary<string, object>());
            var includeCharts = GetParameter<bool>(request, "includeCharts", true);

            var report = new
            {
                Title = "系统仪表盘报告",
                GeneratedAt = DateTime.UtcNow,
                Summary = new
                {
                    TotalUsers = dashboardData.GetValueOrDefault("totalUsers", 0),
                    ActiveUsers = dashboardData.GetValueOrDefault("activeUsers", 0),
                    SystemHealth = dashboardData.GetValueOrDefault("systemHealth", "良好"),
                    DatabaseStatus = dashboardData.GetValueOrDefault("databaseStatus", "正常")
                },
                Details = dashboardData,
                IncludesCharts = includeCharts,
                Recommendations = await GenerateRecommendationsAsync(dashboardData)
            };

            var fileName = await SaveReportAsync(report, "dashboard", "json");

            return AgentResponse.Success($"仪表盘报告生成完成: {fileName}", new Dictionary<string, object>
            {
                { "fileName", fileName },
                { "filePath", Path.Combine(_outputPath, fileName) }
            });
        }

        /// <summary>
        /// 创建测试报告
        /// </summary>
        private async Task<AgentResponse> CreateTestReportAsync(AgentRequest request)
        {
            var testResults = GetParameter<Dictionary<string, object>>(request, "testResults", new Dictionary<string, object>());
            var testType = GetParameter<string>(request, "testType", "综合测试");

            var report = new
            {
                Title = $"{testType}报告",
                GeneratedAt = DateTime.UtcNow,
                TestSummary = new
                {
                    TotalTests = testResults.GetValueOrDefault("totalTests", 0),
                    PassedTests = testResults.GetValueOrDefault("passedTests", 0),
                    FailedTests = testResults.GetValueOrDefault("failedTests", 0),
                    SuccessRate = testResults.GetValueOrDefault("successRate", 0.0)
                },
                DetailedResults = testResults,
                Analysis = await GenerateTestAnalysisAsync(testResults),
                Recommendations = await GenerateTestRecommendationsAsync(testResults)
            };

            var fileName = await SaveReportAsync(report, "test", "json");

            return AgentResponse.Success($"测试报告生成完成: {fileName}", new Dictionary<string, object>
            {
                { "fileName", fileName },
                { "filePath", Path.Combine(_outputPath, fileName) }
            });
        }

        /// <summary>
        /// 导出用户数据
        /// </summary>
        private async Task<AgentResponse> ExportUserDataAsync(AgentRequest request)
        {
            var includePersonalInfo = GetParameter<bool>(request, "includePersonalInfo", false);
            var format = GetParameter<string>(request, "format", "csv");

            // 模拟用户数据
            var userData = await GenerateUserDataAsync(includePersonalInfo);
            var fileName = await SaveExportDataAsync(userData, "users", format);

            return AgentResponse.Success($"用户数据导出完成: {fileName}", new Dictionary<string, object>
            {
                { "fileName", fileName },
                { "filePath", Path.Combine(_outputPath, fileName) },
                { "recordCount", userData.Count },
                { "includesPersonalInfo", includePersonalInfo }
            });
        }

        #region 辅助方法

        private async Task<object> CreateReportAsync(string reportType, Dictionary<string, object> data)
        {
            await Task.Delay(100); // 模拟报告生成时间

            return reportType switch
            {
                "general" => new
                {
                    Type = "综合报告",
                    GeneratedAt = DateTime.UtcNow,
                    Summary = "系统运行正常，所有模块功能稳定",
                    Data = data,
                    Statistics = new
                    {
                        TotalItems = data.Count,
                        ProcessedAt = DateTime.UtcNow,
                        Status = "完成"
                    }
                },
                "performance" => new
                {
                    Type = "性能报告",
                    GeneratedAt = DateTime.UtcNow,
                    PerformanceMetrics = new
                    {
                        AverageResponseTime = "150ms",
                        ThroughputPerSecond = 1250,
                        ErrorRate = "0.02%",
                        SystemLoad = "65%"
                    },
                    Data = data
                },
                "security" => new
                {
                    Type = "安全报告",
                    GeneratedAt = DateTime.UtcNow,
                    SecurityStatus = "良好",
                    VulnerabilitiesFound = 2,
                    RiskLevel = "低",
                    Data = data
                },
                _ => new
                {
                    Type = reportType,
                    GeneratedAt = DateTime.UtcNow,
                    Data = data
                }
            };
        }

        private async Task<string> SaveReportAsync(object report, string reportType, string format)
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var fileName = $"{reportType}_report_{timestamp}.{format}";
            var filePath = Path.Combine(_outputPath, fileName);

            var content = format.ToLower() switch
            {
                "json" => JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
                "xml" => ConvertToXml(report),
                "txt" => ConvertToText(report),
                _ => JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })
            };

            await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);
            return fileName;
        }

        private async Task<string> GenerateDocumentationAsync(string docType, string title, Dictionary<string, object> content)
        {
            await Task.Delay(100);

            var sb = new StringBuilder();
            sb.AppendLine($"# {title}");
            sb.AppendLine();
            sb.AppendLine($"文档类型: {docType}");
            sb.AppendLine($"生成时间: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            switch (docType)
            {
                case "api":
                    sb.AppendLine("## API文档");
                    sb.AppendLine("### 认证接口");
                    sb.AppendLine("- POST /api/auth/login - 用户登录");
                    sb.AppendLine("- POST /api/auth/register - 用户注册");
                    sb.AppendLine("- POST /api/auth/logout - 用户退出");
                    break;

                case "user":
                    sb.AppendLine("## 用户手册");
                    sb.AppendLine("### 快速开始");
                    sb.AppendLine("1. 安装应用程序");
                    sb.AppendLine("2. 创建账户");
                    sb.AppendLine("3. 开始使用");
                    break;

                case "technical":
                    sb.AppendLine("## 技术文档");
                    sb.AppendLine("### 系统架构");
                    sb.AppendLine("### 数据库设计");
                    sb.AppendLine("### 部署指南");
                    break;
            }

            if (content.Any())
            {
                sb.AppendLine();
                sb.AppendLine("## 详细内容");
                foreach (var item in content)
                {
                    sb.AppendLine($"### {item.Key}");
                    sb.AppendLine(item.Value?.ToString() ?? "");
                }
            }

            return sb.ToString();
        }

        private async Task<string> SaveDocumentationAsync(string documentation, string docType)
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var fileName = $"{docType}_documentation_{timestamp}.md";
            var filePath = Path.Combine(_outputPath, fileName);

            await File.WriteAllTextAsync(filePath, documentation, Encoding.UTF8);
            return fileName;
        }

        private async Task<List<Dictionary<string, object>>> CollectExportDataAsync(string dataType, Dictionary<string, object> filters)
        {
            await Task.Delay(100);

            return dataType switch
            {
                "users" => await GenerateUserDataAsync(false),
                "logs" => await GenerateLogDataAsync(filters),
                "settings" => await GenerateSettingsDataAsync(),
                _ => new List<Dictionary<string, object>>()
            };
        }

        private async Task<string> SaveExportDataAsync(List<Dictionary<string, object>> data, string dataType, string format)
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var fileName = $"{dataType}_export_{timestamp}.{format}";
            var filePath = Path.Combine(_outputPath, fileName);

            var content = format.ToLower() switch
            {
                "csv" => ConvertToCsv(data),
                "json" => JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }),
                "xml" => ConvertToXml(data),
                _ => JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true })
            };

            await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);
            return fileName;
        }

        private async Task<List<Dictionary<string, object>>> GenerateUserDataAsync(bool includePersonalInfo)
        {
            await Task.Delay(50);

            var users = new List<Dictionary<string, object>>();
            
            for (int i = 1; i <= 100; i++)
            {
                var user = new Dictionary<string, object>
                {
                    { "Id", i },
                    { "Username", $"user{i}" },
                    { "IsActive", i % 10 != 0 },
                    { "CreatedAt", DateTime.UtcNow.AddDays(-i) },
                    { "LastLoginAt", DateTime.UtcNow.AddHours(-i) }
                };

                if (includePersonalInfo)
                {
                    user["Email"] = $"user{i}@example.com";
                    user["DisplayName"] = $"用户 {i}";
                }

                users.Add(user);
            }

            return users;
        }

        private async Task<List<Dictionary<string, object>>> GenerateLogDataAsync(Dictionary<string, object> filters)
        {
            await Task.Delay(50);

            var logs = new List<Dictionary<string, object>>();
            
            for (int i = 1; i <= 50; i++)
            {
                logs.Add(new Dictionary<string, object>
                {
                    { "Id", i },
                    { "Timestamp", DateTime.UtcNow.AddMinutes(-i * 5) },
                    { "Level", i % 5 == 0 ? "Error" : "Info" },
                    { "Message", $"日志消息 {i}" },
                    { "Source", "System" }
                });
            }

            return logs;
        }

        private async Task<List<Dictionary<string, object>>> GenerateSettingsDataAsync()
        {
            await Task.Delay(50);

            return new List<Dictionary<string, object>>
            {
                new() { { "Key", "SystemName" }, { "Value", "UEModManager" } },
                new() { { "Key", "Version" }, { "Value", "1.7.37" } },
                new() { { "Key", "DatabaseProvider" }, { "Value", "Supabase" } }
            };
        }

        private async Task<List<string>> GenerateRecommendationsAsync(Dictionary<string, object> data)
        {
            await Task.Delay(50);

            var recommendations = new List<string>();

            if (data.TryGetValue("activeUsers", out var activeUsersObj) && activeUsersObj is int activeUsers)
            {
                if (activeUsers < 10)
                    recommendations.Add("考虑增加用户推广活动");
                else if (activeUsers > 1000)
                    recommendations.Add("考虑优化服务器性能以支持更多用户");
            }

            recommendations.Add("定期备份系统数据");
            recommendations.Add("监控系统性能指标");

            return recommendations;
        }

        private async Task<string> GenerateTestAnalysisAsync(Dictionary<string, object> testResults)
        {
            await Task.Delay(50);

            if (testResults.TryGetValue("successRate", out var successRateObj) && successRateObj is double successRate)
            {
                return successRate switch
                {
                    >= 95 => "测试结果优秀，系统质量很高",
                    >= 80 => "测试结果良好，少数问题需要关注",
                    >= 60 => "测试结果一般，需要改进多个问题",
                    _ => "测试结果较差，需要重点关注和修复"
                };
            }

            return "测试结果分析完成";
        }

        private async Task<List<string>> GenerateTestRecommendationsAsync(Dictionary<string, object> testResults)
        {
            await Task.Delay(50);

            var recommendations = new List<string>
            {
                "增加单元测试覆盖率",
                "定期运行集成测试",
                "建立自动化测试流程"
            };

            if (testResults.TryGetValue("failedTests", out var failedTestsObj) && failedTestsObj is int failedTests)
            {
                if (failedTests > 0)
                    recommendations.Add("优先修复失败的测试用例");
            }

            return recommendations;
        }

        private string ConvertToCsv(List<Dictionary<string, object>> data)
        {
            if (!data.Any()) return "";

            var sb = new StringBuilder();
            var headers = data.First().Keys;

            // 写入标题行
            sb.AppendLine(string.Join(",", headers.Select(h => $"\"{h}\"")));

            // 写入数据行
            foreach (var item in data)
            {
                var values = headers.Select(h => $"\"{item.GetValueOrDefault(h, "")?.ToString() ?? ""}\"");
                sb.AppendLine(string.Join(",", values));
            }

            return sb.ToString();
        }

        private string ConvertToXml(object data)
        {
            // 简化的XML转换
            return $"<root><data>{JsonSerializer.Serialize(data)}</data></root>";
        }

        private string ConvertToText(object data)
        {
            return data.ToString() ?? "";
        }

        #endregion
    }
}

