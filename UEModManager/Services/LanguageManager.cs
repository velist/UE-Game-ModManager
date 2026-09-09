using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;

namespace UEModManager.Services
{
    /// <summary>
    /// 全局语言管理，仅用于UI文案切换。
    /// </summary>
    public static class LanguageManager
    {
        private static bool _isEnglish;
        public static bool IsEnglish => _isEnglish;
        public static LanguageState State { get; } = new();
        public static event Action<bool>? LanguageChanged;

        public static void Initialize(CultureInfo? systemCulture = null, string? initialLanguage = null)
        {
            if (initialLanguage == null)
            {
                try
                {
                    var marker = Path.Combine(AppContext.BaseDirectory, "initial-language.txt");
                    if (File.Exists(marker)) initialLanguage = File.ReadAllText(marker).Trim();
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            var defaultEnglish = initialLanguage switch
            {
                "en-US" => true,
                "zh-CN" => false,
                _ => (systemCulture ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName != "zh"
            };
            SetEnglish(UiPreferences.TryLoadEnglish(out var saved)
                ? saved
                : defaultEnglish);
        }

        /// <summary>Persist before notifying windows, so a failed save cannot appear successful.</summary>
        public static void SaveAndSetEnglish(bool english)
        {
            UiPreferences.SaveEnglish(english);
            SetEnglish(english);
        }

        public static void SetEnglish(bool english)
        {
            if (_isEnglish == english) return;
            _isEnglish = english;
            State.NotifyLanguageChanged();
            RaiseLanguageChanged();
        }

        /// <summary>
        /// 逐个订阅者派发，每个订阅者单独捕获异常。
        /// 不能直接 <c>LanguageChanged?.Invoke(...)</c>：多播委托是串行调用，
        /// 只要某个 handler 抛异常，调用列表中排在它后面的 handler 全部不会被执行。
        /// 之前外层的 try/catch 还会把异常整个吞掉，表现为"切换语言只有部分界面生效"且毫无日志。
        /// </summary>
        private static void RaiseLanguageChanged()
        {
            var handlers = LanguageChanged;
            if (handlers == null) return;

            foreach (var handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<bool>)handler)(_isEnglish);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[LanguageManager] 订阅者 " +
                        $"{handler.Method.DeclaringType?.Name}.{handler.Method.Name} 处理语言切换失败: {ex}");
                }
            }
        }
    }

    public sealed class LanguageState : INotifyPropertyChanged
    {
        public bool IsEnglish => LanguageManager.IsEnglish;
        public event PropertyChangedEventHandler? PropertyChanged;
        internal void NotifyLanguageChanged()
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnglish)));
    }
}
