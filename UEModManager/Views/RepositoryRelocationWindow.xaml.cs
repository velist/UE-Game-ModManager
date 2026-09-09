using UEModManager.Localization;
using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using UEModManager.Infrastructure;
using UEModManager.Services;
using UEModManager.Services.Paths;

namespace UEModManager.Views
{
    /// <summary>
    /// "换个地方存 MOD"的一键流程。
    ///
    /// <para><b>产品要求：傻瓜式，一键</b></para>
    /// 发现问题时不能只告知。此前部署降级只弹一句"跨盘用不了硬链接，你可以去设置里改路径"
    /// ——而相当一部分玩家看到"你自己去别处解决"就等于没看到，关掉弹窗然后放弃；
    /// 更糟的是照做之后会撞上"改位置只改指针不搬数据"，MOD 当场从界面上消失。
    /// 所以从告知的那个位置直接给动作：点一下，程序自己把几十 GB 搬过去。
    ///
    /// <para><b>一个窗口三个状态，而不是三个窗口</b></para>
    /// 确认 → 进行中 → 结果。普通玩家每多关掉一个框就多一次"这是要干嘛"的犹豫，
    /// 而这三步说的是同一件事。整条流程只需要点三下，中间不换窗口、位置不跳。
    ///
    /// <para><b>模态是并发保护的第一道防线</b></para>
    /// 项目里所有会写仓库的动作（导入、部署、删包、回收、整合包导入）都由主窗口上的
    /// 用户操作发起，Owner 是主窗口的模态框把它们全挡住了。第二道防线在
    /// <see cref="ObjectStore.BeginRelocation"/>，撞上时用户看到的是一句
    /// "正在搬移，请稍等"，而不是一个包被静默删掉。
    ///
    /// <para><b>视觉</b></para>
    /// 新增的独立对话框，不在 1:1 UI 原型范围内，主界面布局一点没动。
    /// 令牌全部取自 <c>CyberStyles.xaml</c>，与 <see cref="CyberMessageBox"/> 同一套。
    /// 一律不用原生 <c>MessageBox</c>。
    /// </summary>
    public partial class RepositoryRelocationWindow : Window
    {
        /// <summary>窗口此刻处在哪个状态。</summary>
        private enum Phase
        {
            /// <summary>请用户确认。</summary>
            Confirm,

            /// <summary>正在搬。</summary>
            Running,

            /// <summary>搬完了/停下了/失败了。</summary>
            Result,
        }

        private readonly RepositoryRelocationService _service;
        private readonly RepositoryRelocationPlan _plan;
        private readonly bool _anyDeployed;
        private readonly ILogger? _logger;

        private CancellationTokenSource? _cts;
        private Phase _phase = Phase.Confirm;

        /// <summary>搬移的最终结果；从没跑起来（用户直接关掉）时为 <c>null</c>。</summary>
        public RepositoryRelocationOutcome? Outcome { get; private set; }

        /// <summary>存放位置是否真的换成新的了。调用方据此决定要不要刷新列表。</summary>
        public bool Switched => Outcome?.Switched == true;

        /// <param name="anyDeployed">
        /// 当前有没有已经部署到游戏目录的 MOD。只影响结果文案里要不要提"重新装一次"
        /// ——已装进游戏目录的文件是指向<b>旧仓库</b>的硬链接或副本，搬完之后游戏照常能玩，
        /// 但那份空间要重新部署一次才腾得出来。没部署过的人看到这句话只会困惑。
        /// </param>
        public RepositoryRelocationWindow(
            RepositoryRelocationService service,
            RepositoryRelocationPlan plan,
            bool anyDeployed,
            ILogger? logger = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _plan = plan ?? throw new ArgumentNullException(nameof(plan));
            _anyDeployed = anyDeployed;
            _logger = logger;

            InitializeComponent();
            ShowConfirm();
        }

        // ─── 确认 ───

        private void ShowConfirm()
        {
            _phase = Phase.Confirm;

            SourceText.Text = _plan.SourceRoot;
            TargetText.Text = _plan.TargetRoot;
            RoutePanel.Visibility = Visibility.Visible;
            ProgressPanel.Visibility = Visibility.Collapsed;

            if (_plan.CanProceed)
            {
                TitleGlyph.Text = "";
                TitleGlyph.SetResourceReference(ForegroundProperty, "PrimaryBrush");
                TitleText.Text = RepositoryRelocationMessages.ConfirmTitle(_plan, LanguageManager.IsEnglish);
                BodyText.Text = RepositoryRelocationMessages.ConfirmBody(_plan, LanguageManager.IsEnglish);

                PrimaryButton.Content = UiText.Get("开始搬");
                PrimaryButton.Visibility = Visibility.Visible;
                SecondaryButton.Content = UiText.Get("先不用");
                SecondaryButton.Visibility = Visibility.Visible;
                return;
            }

            // 拦下的情形（空间不够、目标非空、目录嵌套）：主按钮直接不出现。
            // 给一个点了没反应的灰按钮，用户会反复点它然后以为程序坏了。
            TitleGlyph.Text = "";
            TitleGlyph.SetResourceReference(ForegroundProperty, "StatusOrangeBrush");
            TitleText.Text = UiText.Get("这次搬不了");
            BodyText.Text = RepositoryRelocationMessages.BlockedBody(_plan, LanguageManager.IsEnglish);

            PrimaryButton.Visibility = Visibility.Collapsed;
            SecondaryButton.Content = UiText.Get("知道了");
            SecondaryButton.Visibility = Visibility.Visible;
        }

        // ─── 进行中 ───

        private void ShowRunning()
        {
            _phase = Phase.Running;

            TitleGlyph.Text = "";
            TitleGlyph.SetResourceReference(ForegroundProperty, "PrimaryBrush");
            TitleText.Text = UiText.Get("正在搬…");
            BodyText.Text = UiText.Get("搬好之前你的 MOD 一直都在原来的位置，随时可以停下来。");

            // 路线那一块收起来：进行中界面上只该有一件事在动
            RoutePanel.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Visible;
            MoveProgressBar.Value = 0;
            ProgressText.Text = UiText.Get("正在看看有多少要搬…");

            PrimaryButton.Visibility = Visibility.Collapsed;
            SecondaryButton.Content = UiText.Get("停下来");
            SecondaryButton.Visibility = Visibility.Visible;
            SecondaryButton.IsEnabled = true;
        }

        private void OnProgress(RepositoryRelocationProgress progress)
        {
            MoveProgressBar.IsIndeterminate = progress.IsIndeterminate;
            if (!progress.IsIndeterminate) MoveProgressBar.Value = progress.Percent;

            ProgressText.Text = progress.GetStatusText(LanguageManager.IsEnglish);

            // 过了落定点就不能取消了：在写墓碑和删源之间停下来，留下的正是
            // "一半在这边一半在那边"的状态，而那恰恰是整套四步搬移要防的事。
            // 按钮此时禁用而不是消失——消失会让界面在最后几秒突然跳一下。
            if (progress.Stage is RepositoryRelocationStage.Committing
                or RepositoryRelocationStage.CleaningUp
                or RepositoryRelocationStage.Done)
            {
                SecondaryButton.IsEnabled = false;
            }
        }

        // ─── 结果 ───

        private void ShowResult(RepositoryRelocationOutcome outcome)
        {
            _phase = Phase.Result;
            Outcome = outcome;

            RoutePanel.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Collapsed;

            switch (outcome.Status)
            {
                case RepositoryRelocationStatus.Moved:
                case RepositoryRelocationStatus.PointerOnly:
                    TitleGlyph.Text = "";
                    TitleGlyph.SetResourceReference(ForegroundProperty, "StatusGreenBrush");
                    TitleText.Text = UiText.Get("搬好了");
                    BodyText.Text = RepositoryRelocationMessages.SuccessBody(_plan, _anyDeployed, LanguageManager.IsEnglish);
                    break;

                case RepositoryRelocationStatus.Cancelled:
                    TitleGlyph.Text = "";
                    TitleGlyph.SetResourceReference(ForegroundProperty, "PrimaryBrush");
                    TitleText.Text = UiText.Get("已经停下来了");
                    BodyText.Text = RepositoryRelocationMessages.CancelledBody(_plan, LanguageManager.IsEnglish);
                    break;

                case RepositoryRelocationStatus.NothingToDo:
                    TitleGlyph.Text = "";
                    TitleGlyph.SetResourceReference(ForegroundProperty, "PrimaryBrush");
                    TitleText.Text = UiText.Get("不用搬");
                    BodyText.Text = UiText.Get("新位置就是现在这个位置，什么都没变。");
                    break;

                default:
                    TitleGlyph.Text = "";
                    TitleGlyph.SetResourceReference(ForegroundProperty, "StatusOrangeBrush");
                    TitleText.Text = UiText.Get("没搬成");
                    BodyText.Text = RepositoryRelocationMessages.FailureBody(
                        _plan, outcome.FailureDetail ?? string.Empty, LanguageManager.IsEnglish);
                    break;
            }

            PrimaryButton.Visibility = Visibility.Collapsed;
            SecondaryButton.Content = UiText.Get("知道了");
            SecondaryButton.Visibility = Visibility.Visible;
            SecondaryButton.IsEnabled = true;
        }

        // ─── 按钮 ───

        /// <summary>
        /// 主按钮只在确认阶段出现，功能只有一个：开始搬。
        /// 走 <see cref="SafeEvent.Run"/>——它是本项目 UI 事件处理器的统一包法，
        /// 未包裹的 async void 异常会落到全局兜底，对用户表现为"点了没反应"。
        /// </summary>
        private void Primary_Click(object sender, RoutedEventArgs e)
            => SafeEvent.Run(this, StartAsync, _logger, UiText.Get("搬移 MOD 存放位置"));

        private async Task StartAsync()
        {
            if (_phase != Phase.Confirm || !_plan.CanProceed) return;

            _cts = new CancellationTokenSource();
            ShowRunning();

            // Progress<T> 在这里构造，于是它捕获了 UI 线程的同步上下文，
            // 服务在后台线程上 Report 的每一条都会自动回到 UI 线程 —— 不用手写 Dispatcher。
            var progress = new Progress<RepositoryRelocationProgress>(OnProgress);

            RepositoryRelocationOutcome outcome;
            try
            {
                outcome = await _service.ExecuteAsync(_plan, progress, _cts.Token);
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
            }

            ShowResult(outcome);
        }

        /// <summary>
        /// 副按钮在三个阶段是三件事：先不用 / 停下来 / 知道了。
        /// 合并成一个按钮而不是三个各自显隐，是因为按钮位置一跳，
        /// 用户就得重新找一遍"我该点哪"。
        /// </summary>
        private void Secondary_Click(object sender, RoutedEventArgs e)
        {
            if (_phase == Phase.Running)
            {
                RequestCancel();
                return;
            }

            Close();
        }

        private void RequestCancel()
        {
            if (_cts is null || _cts.IsCancellationRequested) return;

            _logger?.LogInformation("[UI] 用户请求停止搬移");
            _cts.Cancel();

            // 立刻给反馈：取消要等当前这个文件复制完才生效，而用户点完按钮之后
            // 如果界面一秒钟没有任何变化，他会以为没点上，然后连点。
            SecondaryButton.IsEnabled = false;
            ProgressText.Text = UiText.Get("正在停下来，把已经复制过去的清理掉…");
        }

        private void OnCloseWindow(object sender, ExecutedRoutedEventArgs e) => Close();

        /// <summary>
        /// 搬移途中不允许关窗口。
        ///
        /// <para>
        /// 关掉窗口不会让后台那个复制停下来（<c>Task</c> 不认窗口），而窗口一关，
        /// 主界面就解除了模态封锁——用户可以立刻去导入 MOD，导进去的包正好会随旧位置
        /// 一起被清空。所以这里把关闭改写成"请求取消"：结果一样是停下来，
        /// 但要等回退真的做完、窗口自己切到结果页。
        /// </para>
        /// </summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            if (_phase == Phase.Running)
            {
                e.Cancel = true;
                RequestCancel();
                return;
            }

            base.OnClosing(e);
        }
    }
}
