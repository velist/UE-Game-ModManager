using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using UEModManager.Infrastructure;
using UEModManager.Localization;
using UEModManager.ViewModels;
using UEModManager.Views;

namespace UEModManager
{
    /// <summary>
    /// 「刷新」按钮与 F5：重新核对磁盘上的 MOD 文件。
    ///
    /// <para>
    /// 流程、判断和每一句文案都在 <see cref="MainViewModel.RefreshFromDiskAsync"/> 与
    /// <see cref="DiskRefreshText"/> 里；这里只做界面这一层能做的事——把问题弹出来、把回答带回去、
    /// 把轻提示显示出来。对话框统一用 <see cref="CyberMessageBox"/>，不为这个功能另造窗口。
    /// </para>
    ///
    /// <para>
    /// 另管一件相关的小事：启动游戏时管理器把丢失的 MOD 文件装了回去，
    /// 等玩家回到主窗口时用轻提示说一声（<see cref="ShowLaunchRestoreNotice"/>）。
    /// </para>
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>已经提示过"启动前装回了文件"的那一次启动，避免每次回到窗口都再说一遍。</summary>
        private Guid _launchRestoreNoticeShownFor;

        /// <summary>构造函数里调用一次。</summary>
        private void InitializeDiskRefresh()
        {
            // 启动中心是模态窗口：它关掉、或玩家从游戏切回来时主窗口重新激活，这时再提示才看得见
            Activated += (_, _) => ShowLaunchRestoreNotice();
        }

        private void RefreshMods_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            StartDiskRefresh();
        }

        /// <summary>F5 = 点「刷新」。返回这次按键是不是已经处理掉了。</summary>
        private bool HandleRefreshShortcut(KeyEventArgs e)
        {
            if (e.Key != Key.F5) return false;

            e.Handled = true;
            StartDiskRefresh();
            return true;
        }

        private void StartDiskRefresh()
            => SafeEvent.Run(this, async () =>
            {
                // 上一次还没结束时这里直接返回（VM 里有闸），连点和按住 F5 都不会重入
                var result = await _vm.RefreshFromDiskAsync(new DiskRefreshDialogs(this));
                if (result.Skipped) return;

                // 列表、冲突角标、分类计数由 VM 重建；左栏计数、标题、空状态、加载器提示条是手工算的
                UpdateNavCounts();
                UpdateModCountText();
                UpdateEmptyState();
            }, _logger, "刷新 MOD 列表");

        /// <summary>
        /// 最近一次启动游戏时有 MOD 的文件在游戏目录里丢了、已被重新装好：说一声。
        /// 每次启动只说一次。
        /// </summary>
        private void ShowLaunchRestoreNotice()
        {
            try
            {
                var session = _vm.Launcher.LastSession;
                if (session == null || session.Id == _launchRestoreNoticeShownFor) return;
                if (session.RestoredPackages.Count == 0) return;

                _launchRestoreNoticeShownFor = session.Id;
                ShowToast(DiskRefreshText.RestoredBeforeLaunch(session.RestoredPackages.Count));
            }
            catch (Exception ex)
            {
                // 只是一条提示：出错不能影响窗口激活
                _logger?.LogWarning(ex, "显示启动前修复提示失败");
            }
        }

        /// <summary>把「刷新」的提问与告知接到对话框和轻提示上。</summary>
        private sealed class DiskRefreshDialogs(MainWindow owner) : IDiskRefreshPrompts
        {
            public DiskRefreshAnswer Ask(DiskRefreshQuestion question)
            {
                // 能找回是好消息，用提问图标；其余都是"有东西不见了"，用警告图标
                var icon = question.Kind == DiskRefreshQuestionKind.RecoverFromGame
                    ? MessageBoxImage.Question
                    : MessageBoxImage.Warning;

                if (question.SecondaryText == null)
                {
                    var answer = CyberMessageBox.Show(owner, question.Message, question.Title,
                        MessageBoxButton.YesNo, icon,
                        yesText: question.PrimaryText, noText: question.DismissText, danger: question.Danger);
                    return answer == MessageBoxResult.Yes ? DiskRefreshAnswer.Primary : DiskRefreshAnswer.Dismiss;
                }

                // 三个按钮：Esc 与关闭窗口都落在"什么都不改"上（No / None），不会误触发任何动作
                var choice = CyberMessageBox.Show(owner, question.Message, question.Title,
                    MessageBoxButton.YesNoCancel, icon,
                    yesText: question.PrimaryText, noText: question.DismissText, cancelText: question.SecondaryText);
                return choice switch
                {
                    MessageBoxResult.Yes => DiskRefreshAnswer.Primary,
                    MessageBoxResult.Cancel => DiskRefreshAnswer.Secondary,
                    _ => DiskRefreshAnswer.Dismiss,
                };
            }

            public void Notify(DiskRefreshNotice notice)
            {
                if (notice.IsToast)
                {
                    owner.ShowToast(notice.Message);
                    return;
                }

                CyberMessageBox.Show(owner, notice.Message, notice.Title, MessageBoxButton.OK,
                    MessageBoxImage.Warning, okText: UiText.Get("知道了"));
            }
        }
    }
}
