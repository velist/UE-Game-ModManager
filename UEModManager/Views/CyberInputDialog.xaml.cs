using UEModManager.Localization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace UEModManager.Views
{
    public partial class CyberInputDialog : Window
    {
        public string InputResult { get; private set; } = "";

        public CyberInputDialog(string title, string prompt, string defaultValue = "", bool multiline = false)
        {
            InitializeComponent();
            Title = UiText.Get(title);
            PromptText.Text = UiText.Get(prompt);
            InputBox.Text = defaultValue;

            if (multiline)
            {
                // 多行模式（编辑描述）：Enter 归输入框自己（换行），提交只能点"确定"。
                // WPF 里 AcceptsReturn=True 的 TextBox 会吃掉 Enter，IsDefault 按钮不会被误触。
                InputBox.AcceptsReturn = true;
                InputBox.TextWrapping = TextWrapping.Wrap;
                InputBox.MinHeight = 96;
                InputBox.MaxHeight = 240;
                InputBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                InputBox.VerticalAlignment = VerticalAlignment.Top;
            }

            Loaded += (_, _) =>
            {
                InputBox.Focus();
                InputBox.SelectAll();
            };
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            InputResult = InputBox.Text;
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            // 多行模式下 Enter 是换行，不是提交
            if (e.Key == Key.Enter && InputBox.AcceptsReturn) return;

            if (e.Key == Key.Enter)
            {
                InputResult = InputBox.Text;
                DialogResult = true;
            }
            else if (e.Key == Key.Escape)
            {
                DialogResult = false;
            }
        }

        /// <summary>
        /// 静态方法：弹出输入对话框并返回结果
        /// </summary>
        public static string? Show(Window owner, string title, string prompt, string defaultValue = "", bool multiline = false)
        {
            var dlg = new CyberInputDialog(title, prompt, defaultValue, multiline) { Owner = owner };
            return dlg.ShowDialog() == true ? dlg.InputResult : null;
        }
    }
}
