using UEModManager.Localization;
using System.Windows;
using System.Windows.Controls;
using System;
using System.Diagnostics;
using UEModManager.Diagnostics;
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
        /// 显示主题化消息框。
        /// Esc 等价于点"否/取消"（只有"确定"时即关闭；标题栏关闭仍返回 None），Enter 触发默认按钮。
        /// danger=true 用于删除/清理等不可撤销的操作：确认按钮变红，默认焦点落在安全选项上，
        /// 防止用户顺手按 Enter 或点高亮按钮造成误删。
        /// </summary>
        public static MessageBoxResult Show(Window? owner, string message, string title = "提示",
            MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.Information,
            string? yesText = null, string? noText = null, string? cancelText = null, string? okText = null,
            bool danger = false)
        {
            var dlg = new CyberMessageBox();
            dlg.Title = UiText.Get(title);
            dlg.MessageText.Text = UiText.Get(message);

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

            // 按钮：confirm 为肯定项（是/确定），safe 为 Esc 对应的安全项
            Button confirm;
            Button safe;
            switch (buttons)
            {
                case MessageBoxButton.YesNo:
                    safe = CreateButton(dlg, noText ?? "否", MessageBoxResult.No, ButtonKind.Secondary);
                    confirm = CreateButton(dlg, yesText ?? "是", MessageBoxResult.Yes, danger ? ButtonKind.Danger : ButtonKind.Primary);
                    dlg.ButtonPanel.Children.Add(safe);
                    dlg.ButtonPanel.Children.Add(confirm);
                    break;
                case MessageBoxButton.OKCancel:
                    safe = CreateButton(dlg, cancelText ?? "取消", MessageBoxResult.Cancel, ButtonKind.Secondary);
                    confirm = CreateButton(dlg, okText ?? "确定", MessageBoxResult.OK, danger ? ButtonKind.Danger : ButtonKind.Primary);
                    dlg.ButtonPanel.Children.Add(safe);
                    dlg.ButtonPanel.Children.Add(confirm);
                    break;
                case MessageBoxButton.YesNoCancel:
                    // 唯一的调用方（崩溃恢复）把 Cancel 用作"不再提醒"，不是安全项；Esc 落在 No 上
                    var cancelBtn = CreateButton(dlg, cancelText ?? "取消", MessageBoxResult.Cancel, ButtonKind.Secondary);
                    safe = CreateButton(dlg, noText ?? "否", MessageBoxResult.No, ButtonKind.Secondary);
                    confirm = CreateButton(dlg, yesText ?? "是", MessageBoxResult.Yes, danger ? ButtonKind.Danger : ButtonKind.Primary);
                    dlg.ButtonPanel.Children.Add(cancelBtn);
                    dlg.ButtonPanel.Children.Add(safe);
                    dlg.ButtonPanel.Children.Add(confirm);
                    break;
                default: // OK
                    confirm = CreateButton(dlg, okText ?? "确定", MessageBoxResult.OK, ButtonKind.Primary);
                    safe = confirm;
                    dlg.ButtonPanel.Children.Add(confirm);
                    break;
            }

            safe.IsCancel = true;
            var defaultButton = danger ? safe : confirm;
            defaultButton.IsDefault = true;
            dlg.Loaded += (_, _) => defaultButton.Focus();

            dlg.ShowDialog();
            return dlg.Result;
        }

        /// <summary>
        /// 显示操作失败的提示。异常会被翻译成白话（见 Core 的 UserFacingErrors），
        /// 原始异常不直接展示，只放进"复制诊断信息"，方便用户反馈。
        /// 权限不足时额外提供"以管理员身份重启"一键处理。调用方仍需自行记日志。
        /// </summary>
        /// <param name="operation">失败的操作名，如"导入 MOD"，会显示为"导入 MOD 失败"</param>
        public static void ShowError(Window? owner, string operation, Exception ex)
        {
            // 用户主动取消不算失败，不打扰
            if (ex is OperationCanceledException && ex.InnerException is not TimeoutException) return;

            var error = UserFacingErrors.Describe(ex);
            var (title, message) = DescribeError(operation, error);

            var dlg = new CyberMessageBox();
            dlg.Title = title;
            dlg.MessageText.Text = message;
            dlg.IconText.Text = "✕";
            dlg.IconText.Foreground = dlg.FindResource("StatusRedBrush") as System.Windows.Media.Brush;
            if (owner != null && owner.IsVisible)
                dlg.Owner = owner;
            else
                dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            // 复制后不关闭窗口，所以不走 CreateButton（它的 Click 会关窗）
            var copy = new Button
            {
                Content = UiText.Get("复制诊断信息"),
                MinWidth = 80,
                Padding = new Thickness(16, 0, 16, 0),
                Height = 36,
                Margin = new Thickness(4, 4, 4, 4),
                Style = dlg.FindResource("CyberSecondaryButton") as Style
            };
            copy.Click += (_, _) =>
            {
                try
                {
                    Clipboard.SetText($"操作: {operation}\n时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n版本: {typeof(CyberMessageBox).Assembly.GetName().Version}\n系统: {Environment.OSVersion}\n\n{ex}");
                    copy.Content = UiText.Get("已复制 ✓");
                }
                catch
                {
                    // 剪贴板被其他程序占用时会抛，提示一下即可
                    copy.Content = UiText.Get("复制失败，请重试");
                }
            };
            dlg.ButtonPanel.Children.Add(copy);

            var ok = CreateButton(dlg, "知道了", MessageBoxResult.OK,
                error.Action == UserFacingErrorAction.RestartAsAdmin ? ButtonKind.Secondary : ButtonKind.Primary);
            ok.IsCancel = true;
            dlg.ButtonPanel.Children.Add(ok);
            var defaultButton = ok;

            if (error.Action == UserFacingErrorAction.RestartAsAdmin)
            {
                var restart = CreateButton(dlg, "以管理员身份重启", MessageBoxResult.Yes, ButtonKind.Primary);
                dlg.ButtonPanel.Children.Add(restart);
                defaultButton = restart;
            }

            defaultButton.IsDefault = true;
            dlg.Loaded += (_, _) => defaultButton.Focus();
            dlg.ShowDialog();

            if (dlg.Result == MessageBoxResult.Yes)
                RestartAsAdmin();
        }

        /// <summary>
        /// 只拼出失败提示的标题和正文、不弹窗。给弹窗做成可注入委托的窗口用（如登录窗口），
        /// 保证它们在测试里走注入的委托、在生产里与 <see cref="ShowError"/> 文案一致。
        /// </summary>
        internal static (string Title, string Message) DescribeError(string operation, Exception ex)
            => DescribeError(operation, UserFacingErrors.Describe(ex));

        private static (string Title, string Message) DescribeError(string operation, UserFacingError error)
        {
            var op = UiText.Get(operation);
            // 中文里"导入 MOD失败"读着别扭：操作名以字母数字结尾时补一个空格；
            // 英文模板是 "Could not {0}"，操作名译文是首字母大写的动词短语（与按钮共用词条），
            // 句中要转小写——但 "MOD ..." 这类以缩写开头的不动
            if (!LanguageManager.IsEnglish && op.Length > 0 && char.IsAsciiLetterOrDigit(op[^1])) op += " ";
            if (LanguageManager.IsEnglish && op.Length > 1 && char.IsUpper(op[0]) && !char.IsUpper(op[1]))
                op = char.ToLowerInvariant(op[0]) + op[1..];
            var message = $"{UiText.Format("{0}失败。", op)}\n\n{UiText.Get(error.Summary)}";
            if (!string.IsNullOrEmpty(error.Suggestion))
                message += $"\n{UiText.Get(error.Suggestion)}";
            return (UiText.Format("{0}失败", op), message);
        }

        private static void RestartAsAdmin()
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            try
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // 用户在 UAC 弹窗里点了"否"：保持当前实例继续运行
                return;
            }
            Application.Current.Shutdown();
        }

        private enum ButtonKind { Primary, Secondary, Danger }

        private static Button CreateButton(CyberMessageBox dlg, string text, MessageBoxResult result, ButtonKind kind)
        {
            var btn = new Button
            {
                Content = UiText.Get(text),
                MinWidth = 80,
                Padding = new Thickness(16, 0, 16, 0),
                Height = 36,
                Margin = new Thickness(4, 4, 4, 4),
                Style = dlg.FindResource(kind switch
                {
                    ButtonKind.Primary => "CyberPrimaryButton",
                    ButtonKind.Danger => "CyberDangerButton",
                    _ => "CyberSecondaryButton"
                }) as Style
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
