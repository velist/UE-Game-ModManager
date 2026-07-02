using System;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Logging;
using UEModManager.Views;

namespace UEModManager.Infrastructure
{
    public static class SafeEvent
    {
        public static async void Run(Window? owner, Func<Task> action, ILogger? logger, string operationName)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "[UI] {OperationName} failed", operationName);
                CyberMessageBox.Show(owner, $"\u64cd\u4f5c\u5931\u8d25\uff1a{ex.Message}", "\u9519\u8bef", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}