using System.Windows;
using System.Windows.Controls;
using UEModManager.Services;

namespace UEModManager.Views
{
    public partial class CyberMessageBox : Window
    {
        public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

        private CyberMessageBox()
        {
            InitializeComponent();
            BackgroundManager.ApplyToDialog(DialogBgImage, DialogBgOverlay);
        }

        /// <summary>
        /// 显示主题化消息框
        /// </summary>
        public static MessageBoxResult Show(Window? owner, string message, string title = "提示",
            MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.Information)
        {
            var dlg = new CyberMessageBox();
            dlg.Title = title;
            dlg.MessageText.Text = message;

            if (owner != null && owner.IsVisible)
                dlg.Owner = owner;
            else
                dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            // 图标
            dlg.IconText.Text = icon switch
            {
                MessageBoxImage.Warning => "⚠",
                MessageBoxImage.Error => "✕",
                MessageBoxImage.Question => "?",
                _ => "ℹ"
            };
            dlg.IconText.Foreground = icon switch
            {
                MessageBoxImage.Warning => dlg.FindResource("StatusRedBrush") as System.Windows.Media.Brush,
                MessageBoxImage.Error => dlg.FindResource("StatusRedBrush") as System.Windows.Media.Brush,
                MessageBoxImage.Question => dlg.FindResource("PrimaryBrush") as System.Windows.Media.Brush,
                _ => dlg.FindResource("PrimaryBrush") as System.Windows.Media.Brush
            } ?? System.Windows.Media.Brushes.White;

            // 按钮
            switch (buttons)
            {
                case MessageBoxButton.YesNo:
                    var noBtn = CreateButton(dlg, "否", MessageBoxResult.No, false);
                    var yesBtn = CreateButton(dlg, "是", MessageBoxResult.Yes, true);
                    dlg.ButtonPanel.Children.Add(noBtn);
                    dlg.ButtonPanel.Children.Add(yesBtn);
                    break;
                case MessageBoxButton.OKCancel:
                    var cancelBtn = CreateButton(dlg, "取消", MessageBoxResult.Cancel, false);
                    var okBtn = CreateButton(dlg, "确定", MessageBoxResult.OK, true);
                    dlg.ButtonPanel.Children.Add(cancelBtn);
                    dlg.ButtonPanel.Children.Add(okBtn);
                    break;
                case MessageBoxButton.YesNoCancel:
                    var cancelBtn2 = CreateButton(dlg, "取消", MessageBoxResult.Cancel, false);
                    var noBtn2 = CreateButton(dlg, "否", MessageBoxResult.No, false);
                    var yesBtn2 = CreateButton(dlg, "是", MessageBoxResult.Yes, true);
                    dlg.ButtonPanel.Children.Add(cancelBtn2);
                    dlg.ButtonPanel.Children.Add(noBtn2);
                    dlg.ButtonPanel.Children.Add(yesBtn2);
                    break;
                default: // OK
                    var okOnlyBtn = CreateButton(dlg, "确定", MessageBoxResult.OK, true);
                    dlg.ButtonPanel.Children.Add(okOnlyBtn);
                    break;
            }

            dlg.ShowDialog();
            return dlg.Result;
        }

        private static Button CreateButton(CyberMessageBox dlg, string text, MessageBoxResult result, bool isPrimary)
        {
            var btn = new Button
            {
                Content = text,
                Width = 80,
                Height = 36,
                Margin = new Thickness(4, 0, 4, 0),
                Style = dlg.FindResource(isPrimary ? "CyberPrimaryButton" : "CyberSecondaryButton") as Style
            };
            btn.Click += (_, _) =>
            {
                dlg.Result = result;
                dlg.DialogResult = true;
            };
            return btn;
        }
    }
}
