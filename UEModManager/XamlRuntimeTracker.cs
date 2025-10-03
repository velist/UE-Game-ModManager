using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Markup;
using System.Xml;

namespace UEModManager
{
    /// <summary>
    /// 实时XAML错误追踪器 - 用于捕获和分析XAML加载错误
    /// </summary>
    public static class XamlRuntimeTracker
    {
        private static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "XamlErrorTracking.log");
        private static readonly object LogLock = new object();
        
        static XamlRuntimeTracker()
        {
            // 订阅全局异常处理
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            Application.Current.DispatcherUnhandledException += OnDispatcherUnhandledException;
            
            // 清空日志文件
            File.WriteAllText(LogPath, $"=== XAML Runtime Tracker Started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n\n");
        }

        /// <summary>
        /// 追踪XAML加载
        /// </summary>
        public static T TrackXamlLoad<T>(string xamlFile, Func<T> loadFunc, [CallerMemberName] string callerMethod = "")
        {
            var startTime = DateTime.Now;
            LogMessage($"[XAML_LOAD_START] Loading {xamlFile} from {callerMethod}");
            
            try
            {
                // 检查XAML文件内容
                if (File.Exists(xamlFile))
                {
                    var content = File.ReadAllText(xamlFile);
                    
                    // 检查可疑的属性名
                    if (content.Contains("lsChecked"))
                    {
                        LogMessage($"[ERROR] Found 'lsChecked' in {xamlFile}!");
                        LogMessage($"[CONTENT] File content around error:\n{GetContentAroundPattern(content, "lsChecked", 200)}");
                    }
                    
                    // 检查第110行
                    var lines = content.Split('\n');
                    if (lines.Length >= 110)
                    {
                        LogMessage($"[LINE_110] Line 110 content: {lines[109].Trim()}");
                    }
                }
                
                // 执行加载
                var result = loadFunc();
                
                var duration = (DateTime.Now - startTime).TotalMilliseconds;
                LogMessage($"[XAML_LOAD_SUCCESS] {xamlFile} loaded in {duration:F2}ms");
                
                return result;
            }
            catch (XamlParseException xpe)
            {
                LogXamlParseException(xpe, xamlFile);
                throw;
            }
            catch (Exception ex)
            {
                LogException(ex, $"Loading {xamlFile}");
                throw;
            }
        }

        /// <summary>
        /// 记录XAML解析异常
        /// </summary>
        private static void LogXamlParseException(XamlParseException xpe, string xamlFile)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"\n[XAML_PARSE_ERROR] at {DateTime.Now:HH:mm:ss.fff}");
            sb.AppendLine($"File: {xamlFile}");
            sb.AppendLine($"Line: {xpe.LineNumber}, Position: {xpe.LinePosition}");
            sb.AppendLine($"Message: {xpe.Message}");
            
            // 检查内部异常
            var inner = xpe.InnerException;
            int level = 1;
            while (inner != null)
            {
                sb.AppendLine($"[INNER_{level}] {inner.GetType().Name}: {inner.Message}");
                
                // 检查异常消息中是否包含 lsChecked
                if (inner.Message.Contains("lsChecked"))
                {
                    sb.AppendLine($"[CRITICAL] Found 'lsChecked' in exception message!");
                    
                    // 尝试获取堆栈跟踪
                    if (!string.IsNullOrEmpty(inner.StackTrace))
                    {
                        sb.AppendLine($"[STACK] {inner.StackTrace}");
                    }
                }
                
                inner = inner.InnerException;
                level++;
            }
            
            LogMessage(sb.ToString());
            
            // 尝试读取实际文件内容
            InspectXamlFile(xamlFile, xpe.LineNumber, xpe.LinePosition);
        }

        /// <summary>
        /// 检查XAML文件内容
        /// </summary>
        private static void InspectXamlFile(string xamlFile, int errorLine, int errorPosition)
        {
            try
            {
                if (!File.Exists(xamlFile))
                {
                    LogMessage($"[FILE_NOT_FOUND] {xamlFile}");
                    return;
                }
                
                var lines = File.ReadAllLines(xamlFile);
                var sb = new StringBuilder();
                sb.AppendLine($"\n[FILE_INSPECTION] {xamlFile}");
                
                // 显示错误行及其上下文
                int startLine = Math.Max(0, errorLine - 6);
                int endLine = Math.Min(lines.Length, errorLine + 5);
                
                for (int i = startLine; i < endLine; i++)
                {
                    var prefix = (i + 1 == errorLine) ? ">>> " : "    ";
                    sb.AppendLine($"{prefix}Line {i + 1}: {lines[i]}");
                    
                    // 在错误行标记错误位置
                    if (i + 1 == errorLine && errorPosition > 0)
                    {
                        sb.AppendLine($"    {new string(' ', errorPosition + 8)}^ Error here");
                    }
                }
                
                LogMessage(sb.ToString());
            }
            catch (Exception ex)
            {
                LogMessage($"[INSPECTION_ERROR] {ex.Message}");
            }
        }

        /// <summary>
        /// 检查已加载的程序集
        /// </summary>
        public static void InspectLoadedAssemblies()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"\n[ASSEMBLY_INSPECTION] at {DateTime.Now:HH:mm:ss}");
            
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in assemblies)
            {
                try
                {
                    if (assembly.FullName.Contains("UEModManager"))
                    {
                        sb.AppendLine($"Assembly: {assembly.FullName}");
                        sb.AppendLine($"  Location: {assembly.Location}");
                        sb.AppendLine($"  CodeBase: {assembly.CodeBase}");
                        
                        // 检查嵌入的资源
                        var resources = assembly.GetManifestResourceNames();
                        foreach (var resource in resources)
                        {
                            if (resource.Contains("loginwindow.baml") || resource.Contains("loginwindow.g"))
                            {
                                sb.AppendLine($"  Resource: {resource}");
                                
                                // 尝试读取资源内容
                                using (var stream = assembly.GetManifestResourceStream(resource))
                                {
                                    if (stream != null)
                                    {
                                        var buffer = new byte[Math.Min(stream.Length, 1000)];
                                        stream.Read(buffer, 0, buffer.Length);
                                        var content = Encoding.UTF8.GetString(buffer);
                                        
                                        if (content.Contains("lsChecked"))
                                        {
                                            sb.AppendLine($"    [FOUND] 'lsChecked' in resource!");
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"  Error inspecting assembly: {ex.Message}");
                }
            }
            
            LogMessage(sb.ToString());
        }

        /// <summary>
        /// 检查生成的文件
        /// </summary>
        public static void InspectGeneratedFiles()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"\n[GENERATED_FILES_INSPECTION] at {DateTime.Now:HH:mm:ss}");
            
            var objDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "obj");
            if (Directory.Exists(objDir))
            {
                var generatedFiles = Directory.GetFiles(objDir, "*.g.cs", SearchOption.AllDirectories)
                    .Concat(Directory.GetFiles(objDir, "*.g.i.cs", SearchOption.AllDirectories))
                    .Concat(Directory.GetFiles(objDir, "*.baml", SearchOption.AllDirectories));
                
                foreach (var file in generatedFiles)
                {
                    if (file.ToLower().Contains("login"))
                    {
                        sb.AppendLine($"Checking: {file}");
                        
                        try
                        {
                            var content = File.ReadAllText(file);
                            if (content.Contains("lsChecked"))
                            {
                                sb.AppendLine($"  [FOUND] 'lsChecked' in {Path.GetFileName(file)}!");
                                sb.AppendLine($"  Context: {GetContentAroundPattern(content, "lsChecked", 100)}");
                            }
                        }
                        catch (Exception ex)
                        {
                            sb.AppendLine($"  Error reading file: {ex.Message}");
                        }
                    }
                }
            }
            
            LogMessage(sb.ToString());
        }

        private static string GetContentAroundPattern(string content, string pattern, int contextLength)
        {
            var index = content.IndexOf(pattern);
            if (index < 0) return "Pattern not found";
            
            var start = Math.Max(0, index - contextLength);
            var length = Math.Min(content.Length - start, contextLength * 2);
            
            return content.Substring(start, length).Replace("\r", "").Replace("\n", "\\n");
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                LogException(ex, "UnhandledException");
            }
        }

        private static void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            LogException(e.Exception, "DispatcherUnhandledException");
        }

        private static void LogException(Exception ex, string context)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"\n[EXCEPTION] {context} at {DateTime.Now:HH:mm:ss.fff}");
            sb.AppendLine($"Type: {ex.GetType().FullName}");
            sb.AppendLine($"Message: {ex.Message}");
            
            if (ex.Message.Contains("lsChecked"))
            {
                sb.AppendLine("[CRITICAL] Exception message contains 'lsChecked'!");
            }
            
            if (!string.IsNullOrEmpty(ex.StackTrace))
            {
                sb.AppendLine($"Stack: {ex.StackTrace}");
            }
            
            var inner = ex.InnerException;
            int level = 1;
            while (inner != null)
            {
                sb.AppendLine($"[INNER_{level}] {inner.GetType().Name}: {inner.Message}");
                inner = inner.InnerException;
                level++;
            }
            
            LogMessage(sb.ToString());
        }

        public static void LogMessage(string message)
        {
            lock (LogLock)
            {
                try
                {
                    File.AppendAllText(LogPath, message + "\n");
                    Debug.WriteLine(message);
                    Console.WriteLine(message);
                }
                catch { }
            }
        }

        /// <summary>
        /// 获取日志内容
        /// </summary>
        public static string GetLogContent()
        {
            try
            {
                return File.ReadAllText(LogPath);
            }
            catch
            {
                return "Unable to read log file";
            }
        }
    }
}