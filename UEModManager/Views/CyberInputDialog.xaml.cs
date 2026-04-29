using System.Windows;
using System.Windows.Input;

namespace UEModManager.Views
{
    public partial class CyberInputDialog : Window
    {
        public string InputResult { get; private set; } = "";

        public CyberInputDialog(string title, string prompt, string defaultValue = "")
        {
            InitializeComponent();
            Title = title;
            PromptText.Text = prompt;
            InputBox.Text = defaultValue;
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
        public static string? Show(Window owner, string title, string prompt, string defaultValue = "")
        {
            var dlg = new CyberInputDialog(title, prompt, defaultValue) { Owner = owner };
            return dlg.ShowDialog() == true ? dlg.InputResult : null;
        }
    }
}
