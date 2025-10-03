using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace UEModManager.Services
{
    /// <summary>
    /// 简易本地化帮助：通过文本映射批量替换 TextBlock.Text 与 Button/ContentControl.Content。
    /// 仅用于窗口内“文案切换”，不改变业务逻辑。
    /// </summary>
    public static class LocalizationHelper
    {
        public static void Apply(DependencyObject root, bool toEnglish, IDictionary<string, string> zhToEn)
        {
            if (root == null || zhToEn == null || zhToEn.Count == 0) return;
            Traverse(root, toEnglish, zhToEn);
        }

        private static void Traverse(DependencyObject obj, bool toEnglish, IDictionary<string, string> zhToEn)
        {
            if (obj == null) return;

            // TextBlock.Text
            if (obj is TextBlock tb && !string.IsNullOrEmpty(tb.Text))
            {
                var text = tb.Text.Trim();
                if (toEnglish)
                {
                    if (zhToEn.TryGetValue(text, out var en)) tb.Text = en;
                }
                else
                {
                    // 反向替换：若当前是英文，尝试找到对应中文
                    foreach (var kv in zhToEn)
                    {
                        if (string.Equals(text, kv.Value, StringComparison.Ordinal))
                        {
                            tb.Text = kv.Key; break;
                        }
                    }
                }
            }

            // ContentControl.Content（Button、CheckBox、ComboBoxItem等）
            if (obj is ContentControl cc && cc.Content is string s && !string.IsNullOrWhiteSpace(s))
            {
                var text = s.Trim();
                if (toEnglish)
                {
                    if (zhToEn.TryGetValue(text, out var en)) cc.Content = en;
                }
                else
                {
                    foreach (var kv in zhToEn)
                    {
                        if (string.Equals(text, kv.Value, StringComparison.Ordinal))
                        { cc.Content = kv.Key; break; }
                    }
                }
            }

            // 递归遍历可视/逻辑树
            var count = VisualTreeHelper.GetChildrenCount(obj);
            if (count > 0)
            {
                for (int i = 0; i < count; i++) Traverse(VisualTreeHelper.GetChild(obj, i), toEnglish, zhToEn);
            }
            else
            {
                foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(obj))
                {
                    if (child is DependencyObject d) Traverse(d, toEnglish, zhToEn);
                }
            }
        }
    }
}
