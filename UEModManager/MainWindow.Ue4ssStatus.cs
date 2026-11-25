using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UEModManager.Infrastructure;
using UEModManager.Localization;
using UEModManager.Services;
using UEModManager.Services.Loaders;
using UEModManager.Views;

namespace UEModManager
{
    /// <summary>
    /// UE 游戏的 UE4SS 状态：检测 → 提示。
    ///
    /// UE4SS 只有脚本 MOD（Lua / C++）才用得上，大多数玩家只装 pak，所以
    /// <b>方案里启用了脚本 MOD、而 UE4SS 没装或装残了</b>才出现提示条。
    /// 不同游戏对 UE4SS 的版本和配置要求不一样，提示里明说「以 MOD 说明为准」，并且可以按游戏关掉。
    ///
    /// 正式版线（v2.1.x）没有一键安装：主按钮打开官方发布页，玩家下载后把压缩包拖进来，
    /// 走普通的导入流水线就能装对位置。2.2.0 线的同名文件在这之上多了查新、下载与一键安装，
    /// 控件名（LoaderStatusBar 等）与设置字段两边一致。
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>玩家点了「稍后」的游戏：本次运行内不再弹状态条。</summary>
        private string? _loaderDismissedForGame;

        private Ue4ssService? Ue4ssLoaderService
            => ((App)Application.Current).ServiceProvider?.GetService<Ue4ssService>();

        /// <summary>刷新 UE4SS 的状态条。当前游戏不是 UE 游戏（或游戏路径没配好）时返回 false。</summary>
        private bool RefreshUe4ssStatus()
        {
            try
            {
                var service = Ue4ssLoaderService;
                var detection = service?.Detect();
                if (service == null || detection == null)
                {
                    HideLoaderBar();
                    return false;
                }

                // 已安装（现行或老式结构都算）就没什么可提示的
                if (detection.IsInstalled)
                {
                    HideLoaderBar();
                    return true;
                }

                var gameName = _gameConfig.CurrentGameName;
                // 点过「稍后」的游戏本次运行不再出现，也就不必再去翻方案里的包
                if (string.Equals(_loaderDismissedForGame, gameName, StringComparison.Ordinal))
                {
                    HideLoaderBar();
                    return true;
                }

                var scriptMods = service.EnabledScriptModNames();
                // 没有脚本 MOD 的玩家用不上 UE4SS，不打扰；选过「不再提示」的游戏也不再出现
                if (scriptMods.Count == 0 || UiPreferences.LoadUe4ssPromptSuppressed(gameName))
                {
                    HideLoaderBar();
                    return true;
                }

                var sample = string.Join(UiText.Get("、"), scriptMods.Take(3));
                // 兼容性的话每种情形都带上：官方通用版不是对每款游戏都合适，不能替玩家打包票
                var compatibility = UiText.Get("部分游戏需要 MOD 作者指定的 UE4SS 版本，以 MOD 说明为准。");
                switch (detection.State, detection.Problem)
                {
                    case (Ue4ssInstallState.Incomplete, Ue4ssProblem.MissingCore):
                        ShowLoaderBar(
                            UiText.Get("UE4SS 安装不完整：找不到 UE4SS.dll"),
                            UiText.Get("脚本 MOD 不会生效，游戏启动时还可能弹出报错。常见原因是 UE4SS.dll 被杀毒软件删除。从 UE4SS 官方发布页重新下载，下载后把压缩包拖进来即可安装。") + compatibility);
                        break;

                    case (Ue4ssInstallState.Incomplete, _):
                        ShowLoaderBar(
                            UiText.Get("UE4SS 安装不完整：缺少代理 DLL"),
                            UiText.Get("游戏目录里有 UE4SS，但没有把它带起来的代理 DLL（如 dwmapi.dll），游戏启动时不会加载它。从 UE4SS 官方发布页重新下载，下载后把压缩包拖进来即可安装。") + compatibility);
                        break;

                    default:
                        ShowLoaderBar(
                            UiText.Get("已启用的脚本 MOD 需要 UE4SS 才能生效"),
                            UiText.Interpolate($"没有检测到 UE4SS，{scriptMods.Count} 个脚本 MOD（{sample}）装好了也不会被游戏加载。从 UE4SS 官方发布页下载，下载后把压缩包拖进来即可安装。") + compatibility);
                        break;
                }
                return true;
            }
            catch (Exception ex)
            {
                // 提示条只是锦上添花：探测出错不能连累列表刷新
                _logger?.LogWarning(ex, "刷新 UE4SS 状态失败");
                HideLoaderBar();
                return false;
            }
        }

        private void ShowLoaderBar(string title, string notes)
        {
            LoaderStatusTitle.Text = title;
            LoaderStatusNotes.Text = notes;
            LoaderStatusBar.Visibility = Visibility.Visible;
        }

        private void HideLoaderBar() => LoaderStatusBar.Visibility = Visibility.Collapsed;

        private void LoaderLater_Click(object sender, RoutedEventArgs e)
            => SafeEvent.Run(this, () =>
            {
                _loaderDismissedForGame = _gameConfig.CurrentGameName;
                HideLoaderBar();
                return Task.CompletedTask;
            }, _logger, "暂时关闭 UE4SS 安装提示");

        /// <summary>「我已自行安装，不再提示」：按游戏记住，之后这款游戏不再出现安装 UE4SS 的提示条。</summary>
        private void LoaderNever_Click(object sender, RoutedEventArgs e)
            => SafeEvent.Run(this, () =>
            {
                UiPreferences.SaveUe4ssPromptSuppressed(_gameConfig.CurrentGameName, true);
                HideLoaderBar();
                ShowToast(UiText.Get("已记住：这款游戏不再提示安装 UE4SS"));
                return Task.CompletedTask;
            }, _logger, "关闭 UE4SS 安装提示");

        /// <summary>
        /// 打开 UE4SS 官方发布页。提示条留着不关：玩家下载完把压缩包拖进来、装好之后它自己消失。
        /// </summary>
        private void LoaderInstall_Click(object sender, RoutedEventArgs e)
            => SafeEvent.Run(this, () =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = Ue4ssLoader.ReleasesPageUrl, UseShellExecute = true,
                    });
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "打开 UE4SS 发布页失败");
                    CyberMessageBox.Show(this,
                        UiText.Interpolate($"无法打开浏览器，请手动复制此链接访问：\n\n{Ue4ssLoader.ReleasesPageUrl}"),
                        UiText.Get("安装 UE4SS"), MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return Task.CompletedTask;
            }, _logger, "打开 UE4SS 发布页");
    }
}
