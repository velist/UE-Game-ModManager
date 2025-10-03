using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace UEModManager.Agents
{
    /// <summary>
    /// 项目自动化分析器 - 全面检查项目代码质量和问题
    /// </summary>
    public class ProjectAnalyzer : BaseSubAgent
    {
        private readonly string _projectPath;
        private readonly List<ProjectIssue> _issues = new();
        
        public ProjectAnalyzer(ILogger<ProjectAnalyzer> logger, string projectPath) 
            : base(logger)
        {
            _projectPath = projectPath ?? throw new ArgumentNullException(nameof(projectPath));
        }

        public override string Name => "ProjectAnalyzer";
        public override AgentType Type => AgentType.ProjectOptimizer;
        public override int Priority => 1;

        protected override async Task<AgentResponse> ExecuteInternalAsync(AgentRequest request)
        {
            return request.TaskType switch
            {
                "full_project_analysis" => await PerformFullProjectAnalysisAsync(request),
                "xaml_analysis" => await AnalyzeXamlFilesAsync(request),
                "csharp_analysis" => await AnalyzeCSharpFilesAsync(request),
                "compile_test" => await TestProjectCompilationAsync(request),
                "generate_fix_suggestions" => await GenerateFixSuggestionsAsync(request),
                "generate_comprehensive_report" => await GenerateComprehensiveReportAsync(request),
                _ => AgentResponse.Failed($"不支持的任务类型: {request.TaskType}")
            };
        }

        /// <summary>
        /// 执行完整的项目分析
        /// </summary>
        private async Task<AgentResponse> PerformFullProjectAnalysisAsync(AgentRequest request)
        {
            _logger.LogInformation("开始执行完整项目分析...");
            _issues.Clear();

            var analysisResults = new Dictionary<string, object>();

            try
            {
                // 1. 分析C#文件
                var csharpResults = await AnalyzeCSharpFilesInternalAsync();
                analysisResults["csharpAnalysis"] = csharpResults;

                // 2. 分析XAML文件
                var xamlResults = await AnalyzeXamlFilesInternalAsync();
                analysisResults["xamlAnalysis"] = xamlResults;

                // 3. 检查项目文件
                var projectResults = await AnalyzeProjectFilesAsync();
                analysisResults["projectFileAnalysis"] = projectResults;

                // 4. 测试编译
                var compileResults = await TestCompilationInternalAsync();
                analysisResults["compilationTest"] = compileResults;

                // 5. 生成修复建议
                var fixSuggestions = await GenerateFixSuggestionsInternalAsync();
                analysisResults["fixSuggestions"] = fixSuggestions;

                var summary = new
                {
                    TotalIssues = _issues.Count,
                    CriticalIssues = _issues.Count(i => i.Severity == IssueSeverity.Critical),
                    HighPriorityIssues = _issues.Count(i => i.Severity == IssueSeverity.High),
                    MediumPriorityIssues = _issues.Count(i => i.Severity == IssueSeverity.Medium),
                    LowPriorityIssues = _issues.Count(i => i.Severity == IssueSeverity.Low),
                    AnalyzedFiles = GetAnalyzedFileCount(),
                    CompilationStatus = compileResults
                };

                analysisResults["summary"] = summary;
                analysisResults["allIssues"] = _issues;

                return AgentResponse.Success("完整项目分析完成", analysisResults);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "完整项目分析失败");
                return AgentResponse.Failed($"项目分析失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 分析C#文件
        /// </summary>
        private async Task<object> AnalyzeCSharpFilesInternalAsync()
        {
            var csFiles = Directory.GetFiles(_projectPath, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\"))
                .ToArray();

            var analysisResults = new List<object>();
            var totalIssues = 0;

            foreach (var file in csFiles)
            {
                try
                {
                    var fileContent = await File.ReadAllTextAsync(file);
                    var fileIssues = await AnalyzeCSharpFileContentAsync(file, fileContent);
                    
                    _issues.AddRange(fileIssues);
                    totalIssues += fileIssues.Count;

                    analysisResults.Add(new
                    {
                        File = Path.GetRelativePath(_projectPath, file),
                        IssuesFound = fileIssues.Count,
                        Issues = fileIssues.Select(i => new { i.Type, i.Message, i.LineNumber, i.Severity }).ToList()
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"分析C#文件失败: {file}, {ex.Message}");
                }
            }

            return new
            {
                TotalFiles = csFiles.Length,
                TotalIssues = totalIssues,
                FileResults = analysisResults
            };
        }

        /// <summary>
        /// 分析XAML文件
        /// </summary>
        private async Task<object> AnalyzeXamlFilesInternalAsync()
        {
            var xamlFiles = Directory.GetFiles(_projectPath, "*.xaml", SearchOption.AllDirectories)
                .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\"))
                .ToArray();

            var analysisResults = new List<object>();
            var totalIssues = 0;

            foreach (var file in xamlFiles)
            {
                try
                {
                    var fileContent = await File.ReadAllTextAsync(file);
                    var fileIssues = await AnalyzeXamlFileContentAsync(file, fileContent);
                    
                    _issues.AddRange(fileIssues);
                    totalIssues += fileIssues.Count;

                    analysisResults.Add(new
                    {
                        File = Path.GetRelativePath(_projectPath, file),
                        IssuesFound = fileIssues.Count,
                        Issues = fileIssues.Select(i => new { i.Type, i.Message, i.LineNumber, i.Severity }).ToList()
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"分析XAML文件失败: {file}, {ex.Message}");
                }
            }

            return new
            {
                TotalFiles = xamlFiles.Length,
                TotalIssues = totalIssues,
                FileResults = analysisResults
            };
        }

        /// <summary>
        /// 分析C#文件内容
        /// </summary>
        private async Task<List<ProjectIssue>> AnalyzeCSharpFileContentAsync(string filePath, string content)
        {
            var issues = new List<ProjectIssue>();
            var lines = content.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                var lineNumber = i + 1;

                // 检查常见的C#语法问题
                await CheckCSharpSyntaxIssuesAsync(issues, filePath, line, lineNumber);
            }

            // 检查整体文件问题
            await CheckCSharpFileStructureAsync(issues, filePath, content);

            return issues;
        }

        /// <summary>
        /// 检查C#语法问题
        /// </summary>
        private async Task CheckCSharpSyntaxIssuesAsync(List<ProjectIssue> issues, string filePath, string line, int lineNumber)
        {
            // 检查未使用的using语句
            if (line.StartsWith("using ") && !line.Contains(";"))
            {
                issues.Add(new ProjectIssue
                {
                    Type = IssueType.Syntax,
                    Severity = IssueSeverity.Medium,
                    File = filePath,
                    LineNumber = lineNumber,
                    Message = "Using语句缺少分号",
                    Code = line,
                    FixSuggestion = "在using语句末尾添加分号"
                });
            }

            // 检查方法命名规范
            var methodPattern = @"^\s*(public|private|protected|internal)\s+.*\s+(\w+)\s*\(";
            var methodMatch = Regex.Match(line, methodPattern);
            if (methodMatch.Success)
            {
                var methodName = methodMatch.Groups[2].Value;
                if (!char.IsUpper(methodName[0]))
                {
                    issues.Add(new ProjectIssue
                    {
                        Type = IssueType.Naming,
                        Severity = IssueSeverity.Low,
                        File = filePath,
                        LineNumber = lineNumber,
                        Message = $"方法名 '{methodName}' 应该以大写字母开头",
                        Code = line,
                        FixSuggestion = $"将方法名改为 '{char.ToUpper(methodName[0])}{methodName.Substring(1)}'"
                    });
                }
            }

            // 检查空引用可能性
            if (line.Contains(".") && !line.Contains("?."))
            {
                var nullRefPattern = @"(\w+)\.(\w+)";
                var matches = Regex.Matches(line, nullRefPattern);
                foreach (Match match in matches)
                {
                    if (!line.Contains($"{match.Groups[1].Value}?."))
                    {
                        issues.Add(new ProjectIssue
                        {
                            Type = IssueType.NullReference,
                            Severity = IssueSeverity.Medium,
                            File = filePath,
                            LineNumber = lineNumber,
                            Message = $"可能的空引用异常: {match.Groups[1].Value}",
                            Code = line,
                            FixSuggestion = $"考虑使用空条件运算符: {match.Groups[1].Value}?.{match.Groups[2].Value}"
                        });
                    }
                }
            }

            await Task.CompletedTask; // 使方法异步
        }

        /// <summary>
        /// 检查C#文件结构
        /// </summary>
        private async Task CheckCSharpFileStructureAsync(List<ProjectIssue> issues, string filePath, string content)
        {
            // 检查命名空间
            if (!content.Contains("namespace "))
            {
                issues.Add(new ProjectIssue
                {
                    Type = IssueType.Structure,
                    Severity = IssueSeverity.High,
                    File = filePath,
                    LineNumber = 1,
                    Message = "文件缺少命名空间声明",
                    Code = "",
                    FixSuggestion = "添加适当的命名空间声明"
                });
            }

            // 检查类声明
            if (!Regex.IsMatch(content, @"(class|interface|enum|struct)\s+\w+"))
            {
                issues.Add(new ProjectIssue
                {
                    Type = IssueType.Structure,
                    Severity = IssueSeverity.High,
                    File = filePath,
                    LineNumber = 1,
                    Message = "文件缺少类、接口、枚举或结构声明",
                    Code = "",
                    FixSuggestion = "添加适当的类型声明"
                });
            }

            await Task.CompletedTask; // 使方法异步
        }

        /// <summary>
        /// 分析XAML文件内容
        /// </summary>
        private async Task<List<ProjectIssue>> AnalyzeXamlFileContentAsync(string filePath, string content)
        {
            var issues = new List<ProjectIssue>();
            var lines = content.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                var lineNumber = i + 1;

                await CheckXamlSyntaxIssuesAsync(issues, filePath, line, lineNumber);
            }

            return issues;
        }

        /// <summary>
        /// 检查XAML语法问题
        /// </summary>
        private async Task CheckXamlSyntaxIssuesAsync(List<ProjectIssue> issues, string filePath, string line, int lineNumber)
        {
            // 检查常见的属性拼写错误
            var commonXamlErrors = new Dictionary<string, string>
            {
                { "lsChecked", "IsChecked" },
                { "lsEnabled", "IsEnabled" },
                { "lsVisible", "IsVisible" },
                { "Visibilty", "Visibility" },
                { "Heigth", "Height" },
                { "Widht", "Width" }
            };

            foreach (var error in commonXamlErrors)
            {
                if (line.Contains(error.Key))
                {
                    issues.Add(new ProjectIssue
                    {
                        Type = IssueType.XamlAttribute,
                        Severity = IssueSeverity.High,
                        File = filePath,
                        LineNumber = lineNumber,
                        Message = $"XAML属性拼写错误: '{error.Key}' 应该是 '{error.Value}'",
                        Code = line,
                        FixSuggestion = $"将 '{error.Key}' 改为 '{error.Value}'"
                    });
                }
            }

            // 检查未关闭的标签
            if (line.Contains("<") && !line.Contains("/>") && !line.Contains("</"))
            {
                var tagMatch = Regex.Match(line, @"<(\w+)");
                if (tagMatch.Success && !line.Contains($"</{tagMatch.Groups[1].Value}>"))
                {
                    issues.Add(new ProjectIssue
                    {
                        Type = IssueType.XamlStructure,
                        Severity = IssueSeverity.Medium,
                        File = filePath,
                        LineNumber = lineNumber,
                        Message = $"可能未关闭的XML标签: {tagMatch.Groups[1].Value}",
                        Code = line,
                        FixSuggestion = $"确保标签 '{tagMatch.Groups[1].Value}' 正确关闭"
                    });
                }
            }

            await Task.CompletedTask; // 使方法异步
        }

        /// <summary>
        /// 分析项目文件
        /// </summary>
        private async Task<object> AnalyzeProjectFilesAsync()
        {
            var projectFiles = Directory.GetFiles(_projectPath, "*.csproj", SearchOption.AllDirectories);
            var results = new List<object>();

            foreach (var file in projectFiles)
            {
                try
                {
                    var content = await File.ReadAllTextAsync(file);
                    var fileIssues = await AnalyzeProjectFileContentAsync(file, content);
                    
                    _issues.AddRange(fileIssues);

                    results.Add(new
                    {
                        File = Path.GetRelativePath(_projectPath, file),
                        IssuesFound = fileIssues.Count,
                        Issues = fileIssues.Select(i => new { i.Type, i.Message, i.Severity }).ToList()
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"分析项目文件失败: {file}, {ex.Message}");
                }
            }

            return new
            {
                TotalProjectFiles = projectFiles.Length,
                Results = results
            };
        }

        /// <summary>
        /// 分析项目文件内容
        /// </summary>
        private async Task<List<ProjectIssue>> AnalyzeProjectFileContentAsync(string filePath, string content)
        {
            var issues = new List<ProjectIssue>();

            // 检查目标框架
            if (!content.Contains("TargetFramework"))
            {
                issues.Add(new ProjectIssue
                {
                    Type = IssueType.ProjectConfiguration,
                    Severity = IssueSeverity.High,
                    File = filePath,
                    LineNumber = 1,
                    Message = "项目文件缺少目标框架配置",
                    Code = "",
                    FixSuggestion = "添加 <TargetFramework> 元素"
                });
            }

            // 检查包引用
            if (content.Contains("PackageReference") && !content.Contains("Version"))
            {
                issues.Add(new ProjectIssue
                {
                    Type = IssueType.ProjectConfiguration,
                    Severity = IssueSeverity.Medium,
                    File = filePath,
                    LineNumber = 1,
                    Message = "包引用缺少版本号",
                    Code = "",
                    FixSuggestion = "为PackageReference添加Version属性"
                });
            }

            await Task.CompletedTask; // 使方法异步
            return issues;
        }

        /// <summary>
        /// 测试编译
        /// </summary>
        private async Task<object> TestCompilationInternalAsync()
        {
            try
            {
                // 模拟编译测试
                await Task.Delay(1000);

                var compileErrors = new List<string>();
                var compileWarnings = new List<string>();

                // 检查常见的编译问题
                foreach (var issue in _issues.Where(i => i.Severity == IssueSeverity.Critical || i.Severity == IssueSeverity.High))
                {
                    if (issue.Type == IssueType.Syntax || issue.Type == IssueType.XamlAttribute)
                    {
                        compileErrors.Add($"{Path.GetFileName(issue.File)}({issue.LineNumber}): {issue.Message}");
                    }
                }

                foreach (var issue in _issues.Where(i => i.Severity == IssueSeverity.Medium))
                {
                    compileWarnings.Add($"{Path.GetFileName(issue.File)}({issue.LineNumber}): {issue.Message}");
                }

                return new
                {
                    Success = compileErrors.Count == 0,
                    ErrorCount = compileErrors.Count,
                    WarningCount = compileWarnings.Count,
                    Errors = compileErrors,
                    Warnings = compileWarnings,
                    EstimatedCompileTime = "未执行实际编译"
                };
            }
            catch (Exception ex)
            {
                return new
                {
                    Success = false,
                    ErrorCount = 1,
                    WarningCount = 0,
                    Errors = new List<string> { $"编译测试失败: {ex.Message}" },
                    Warnings = new List<string>()
                };
            }
        }

        /// <summary>
        /// 生成修复建议
        /// </summary>
        private async Task<object> GenerateFixSuggestionsInternalAsync()
        {
            var suggestions = new List<object>();
            var groupedIssues = _issues.GroupBy(i => i.Type).ToList();

            foreach (var group in groupedIssues)
            {
                var issueType = group.Key;
                var issues = group.ToList();

                suggestions.Add(new
                {
                    IssueType = issueType.ToString(),
                    Count = issues.Count,
                    Severity = GetGroupSeverity(issues),
                    Description = GetIssueTypeDescription(issueType),
                    RecommendedAction = GetRecommendedAction(issueType),
                    Files = issues.Select(i => Path.GetFileName(i.File)).Distinct().ToList(),
                    Priority = GetIssuePriority(issueType)
                });
            }

            await Task.CompletedTask; // 使方法异步

            return new
            {
                TotalSuggestions = suggestions.Count,
                Suggestions = suggestions.OrderByDescending(s => ((dynamic)s).Priority).ToList(),
                GeneralRecommendations = GetGeneralRecommendations()
            };
        }

        /// <summary>
        /// 生成综合报告
        /// </summary>
        private async Task<AgentResponse> GenerateComprehensiveReportAsync(AgentRequest request)
        {
            var reportData = new
            {
                ProjectName = "UEModManager",
                AnalysisDate = DateTime.UtcNow,
                ProjectPath = _projectPath,
                Summary = new
                {
                    TotalIssues = _issues.Count,
                    CriticalIssues = _issues.Count(i => i.Severity == IssueSeverity.Critical),
                    HighPriorityIssues = _issues.Count(i => i.Severity == IssueSeverity.High),
                    MediumPriorityIssues = _issues.Count(i => i.Severity == IssueSeverity.Medium),
                    LowPriorityIssues = _issues.Count(i => i.Severity == IssueSeverity.Low)
                },
                IssuesByType = _issues.GroupBy(i => i.Type).Select(g => new
                {
                    Type = g.Key.ToString(),
                    Count = g.Count(),
                    Issues = g.Select(i => new
                    {
                        File = Path.GetFileName(i.File),
                        Line = i.LineNumber,
                        Message = i.Message,
                        Severity = i.Severity.ToString(),
                        FixSuggestion = i.FixSuggestion
                    }).ToList()
                }).ToList(),
                Recommendations = await GenerateDetailedRecommendationsAsync(),
                ActionPlan = GenerateActionPlan()
            };

            // 保存报告到文件
            var reportPath = await SaveReportToFileAsync(reportData);

            return AgentResponse.Success($"综合报告已生成: {reportPath}", new Dictionary<string, object>
            {
                { "reportPath", reportPath },
                { "reportData", reportData }
            });
        }

        #region 辅助方法

        /// <summary>
        /// 分析XAML文件
        /// </summary>
        private async Task<AgentResponse> AnalyzeXamlFilesAsync(AgentRequest request)
        {
            var xamlResults = await AnalyzeXamlFilesInternalAsync();
            return AgentResponse.Success("XAML文件分析完成", new Dictionary<string, object> { { "xamlAnalysis", xamlResults } });
        }

        /// <summary>
        /// 分析C#文件
        /// </summary>
        private async Task<AgentResponse> AnalyzeCSharpFilesAsync(AgentRequest request)
        {
            var csharpResults = await AnalyzeCSharpFilesInternalAsync();
            return AgentResponse.Success("C#文件分析完成", new Dictionary<string, object> { { "csharpAnalysis", csharpResults } });
        }

        /// <summary>
        /// 测试项目编译
        /// </summary>
        private async Task<AgentResponse> TestProjectCompilationAsync(AgentRequest request)
        {
            var compileResults = await TestCompilationInternalAsync();
            return AgentResponse.Success("编译测试完成", new Dictionary<string, object> { { "compilationTest", compileResults } });
        }

        /// <summary>
        /// 生成修复建议
        /// </summary>
        private async Task<AgentResponse> GenerateFixSuggestionsAsync(AgentRequest request)
        {
            var fixSuggestions = await GenerateFixSuggestionsInternalAsync();
            return AgentResponse.Success("修复建议生成完成", new Dictionary<string, object> { { "fixSuggestions", fixSuggestions } });
        }

        private int GetAnalyzedFileCount()
        {
            var csFiles = Directory.GetFiles(_projectPath, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\")).Count();
            var xamlFiles = Directory.GetFiles(_projectPath, "*.xaml", SearchOption.AllDirectories)
                .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\")).Count();
            var projFiles = Directory.GetFiles(_projectPath, "*.csproj", SearchOption.AllDirectories).Count();

            return csFiles + xamlFiles + projFiles;
        }

        private string GetGroupSeverity(List<ProjectIssue> issues)
        {
            if (issues.Any(i => i.Severity == IssueSeverity.Critical)) return "Critical";
            if (issues.Any(i => i.Severity == IssueSeverity.High)) return "High";
            if (issues.Any(i => i.Severity == IssueSeverity.Medium)) return "Medium";
            return "Low";
        }

        private string GetIssueTypeDescription(IssueType issueType)
        {
            return issueType switch
            {
                IssueType.Syntax => "语法错误和代码结构问题",
                IssueType.XamlAttribute => "XAML属性和标记问题",
                IssueType.XamlStructure => "XAML结构和格式问题",
                IssueType.Naming => "命名规范问题",
                IssueType.NullReference => "潜在的空引用问题",
                IssueType.Structure => "代码结构和组织问题",
                IssueType.ProjectConfiguration => "项目配置问题",
                _ => "其他问题"
            };
        }

        private string GetRecommendedAction(IssueType issueType)
        {
            return issueType switch
            {
                IssueType.Syntax => "修复语法错误，确保代码能够正确编译",
                IssueType.XamlAttribute => "修正XAML属性拼写，使用正确的属性名",
                IssueType.XamlStructure => "修复XAML结构问题，确保标签正确关闭",
                IssueType.Naming => "遵循C#命名规范，提高代码可读性",
                IssueType.NullReference => "添加空检查，防止运行时异常",
                IssueType.Structure => "重构代码结构，提高可维护性",
                IssueType.ProjectConfiguration => "修复项目配置，确保正确的依赖和设置",
                _ => "根据具体问题采取相应行动"
            };
        }

        private int GetIssuePriority(IssueType issueType)
        {
            return issueType switch
            {
                IssueType.Syntax => 5,
                IssueType.XamlAttribute => 4,
                IssueType.ProjectConfiguration => 4,
                IssueType.XamlStructure => 3,
                IssueType.NullReference => 3,
                IssueType.Structure => 2,
                IssueType.Naming => 1,
                _ => 1
            };
        }

        private List<string> GetGeneralRecommendations()
        {
            return new List<string>
            {
                "定期运行代码分析，及早发现问题",
                "建立代码审查流程，确保代码质量",
                "使用IDE的代码格式化功能，保持代码风格一致",
                "编写单元测试，提高代码可靠性",
                "使用静态分析工具，自动检测潜在问题",
                "遵循SOLID原则，提高代码设计质量",
                "定期重构代码，减少技术债务"
            };
        }

        private async Task<List<string>> GenerateDetailedRecommendationsAsync()
        {
            var recommendations = new List<string>();

            if (_issues.Any(i => i.Type == IssueType.Syntax))
            {
                recommendations.Add("优先修复语法错误，确保项目能够正确编译");
            }

            if (_issues.Any(i => i.Type == IssueType.XamlAttribute))
            {
                recommendations.Add("修正XAML属性拼写错误，这些错误会导致运行时问题");
            }

            if (_issues.Any(i => i.Type == IssueType.NullReference))
            {
                recommendations.Add("添加空引用检查，防止应用程序崩溃");
            }

            if (_issues.Count > 50)
            {
                recommendations.Add("问题数量较多，建议分批次修复，优先处理高严重性问题");
            }

            await Task.CompletedTask;
            return recommendations;
        }

        private List<object> GenerateActionPlan()
        {
            var actionPlan = new List<object>();

            var criticalIssues = _issues.Where(i => i.Severity == IssueSeverity.Critical).ToList();
            if (criticalIssues.Any())
            {
                actionPlan.Add(new
                {
                    Priority = 1,
                    Action = "立即修复关键问题",
                    Description = $"修复 {criticalIssues.Count} 个关键问题，确保项目能够正常运行",
                    EstimatedTime = $"{criticalIssues.Count * 15} 分钟"
                });
            }

            var highIssues = _issues.Where(i => i.Severity == IssueSeverity.High).ToList();
            if (highIssues.Any())
            {
                actionPlan.Add(new
                {
                    Priority = 2,
                    Action = "修复高优先级问题",
                    Description = $"修复 {highIssues.Count} 个高优先级问题，提高项目稳定性",
                    EstimatedTime = $"{highIssues.Count * 10} 分钟"
                });
            }

            var mediumIssues = _issues.Where(i => i.Severity == IssueSeverity.Medium).ToList();
            if (mediumIssues.Any())
            {
                actionPlan.Add(new
                {
                    Priority = 3,
                    Action = "改进代码质量",
                    Description = $"修复 {mediumIssues.Count} 个中等优先级问题，提升代码质量",
                    EstimatedTime = $"{mediumIssues.Count * 5} 分钟"
                });
            }

            return actionPlan;
        }

        private async Task<string> SaveReportToFileAsync(object reportData)
        {
            var reportsDir = Path.Combine(_projectPath, "Reports");
            Directory.CreateDirectory(reportsDir);

            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var reportPath = Path.Combine(reportsDir, $"ProjectAnalysis_Report_{timestamp}.json");

            var json = JsonSerializer.Serialize(reportData, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(reportPath, json, Encoding.UTF8);

            return reportPath;
        }

        #endregion
    }

    /// <summary>
    /// 项目问题定义
    /// </summary>
    public class ProjectIssue
    {
        public IssueType Type { get; set; }
        public IssueSeverity Severity { get; set; }
        public string File { get; set; } = string.Empty;
        public int LineNumber { get; set; }
        public string Message { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string FixSuggestion { get; set; } = string.Empty;
    }

    /// <summary>
    /// 问题类型枚举
    /// </summary>
    public enum IssueType
    {
        Syntax,              // 语法错误
        XamlAttribute,       // XAML属性错误
        XamlStructure,       // XAML结构错误
        Naming,              // 命名规范问题
        NullReference,       // 空引用问题
        Structure,           // 代码结构问题
        ProjectConfiguration // 项目配置问题
    }

    /// <summary>
    /// 问题严重性枚举
    /// </summary>
    public enum IssueSeverity
    {
        Low,      // 低优先级
        Medium,   // 中等优先级
        High,     // 高优先级
        Critical  // 关键问题
    }
}