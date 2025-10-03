using System;

namespace UEModManager.Services
{
    /// <summary>
    /// 全局语言管理，仅用于UI文案切换。
    /// </summary>
    public static class LanguageManager
    {
        private static bool _isEnglish;
        public static bool IsEnglish => _isEnglish;
        public static event Action<bool>? LanguageChanged;

        public static void SetEnglish(bool english)
        {
            if (_isEnglish == english) return;
            _isEnglish = english;
            try { LanguageChanged?.Invoke(_isEnglish); } catch { }
        }
    }
}
