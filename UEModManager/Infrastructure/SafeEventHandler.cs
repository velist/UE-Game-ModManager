using UEModManager.Localization;
using System;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Logging;
using UEModManager.Views;

namespace UEModManager.Infrastructure
{
    public static class SafeEvent
    {
        /// <summary>
        /// \u5305\u88f9 async void \u4e8b\u4ef6\u5904\u7406\u5668\u7684\u5904\u7406\u4f53\uff1a\u5931\u8d25\u5fc5\u5b9a\u8bb0\u65e5\u5fd7 + \u5f39\u7a97\u3002
        /// \u672a\u5305\u88f9\u7684 async void \u5f02\u5e38\u4f1a\u843d\u5230 App \u7684\u5168\u5c40 DispatcherUnhandledException\uff0c
        /// \u5bf9\u7528\u6237\u8868\u73b0\u4e3a"\u70b9\u4e86\u6ca1\u53cd\u5e94"\u3002
        /// </summary>
        public static async void Run(Window? owner, Func<Task> action, ILogger? logger, string operationName)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                if (logger != null)
                {
                    logger.LogError(ex, "[UI] {OperationName} failed", operationName);
                }
                else
                {
                    // \u591a\u6570\u5bf9\u8bdd\u6846\u7a97\u53e3\u6ca1\u6709\u6ce8\u5165 ILogger\uff1bConsole \u5df2\u88ab App \u91cd\u5b9a\u5411\u5230\u7ed3\u6784\u5316\u65e5\u5fd7\u6587\u4ef6\uff0c
                    // \u56de\u843d\u5230\u5b83\u53ef\u4ee5\u4fdd\u8bc1\u4efb\u4f55\u4e00\u6b21\u5931\u8d25\u90fd\u6709\u53d6\u8bc1\u8bb0\u5f55\u3002
                    try { Console.WriteLine($"[UI] {operationName} failed: {ex}"); } catch { }
                }

                CyberMessageBox.Show(owner, UiText.Interpolate($"\u64cd\u4f5c\u5931\u8d25\uff1a{ex.Message}"), UiText.Get("\u9519\u8bef"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}