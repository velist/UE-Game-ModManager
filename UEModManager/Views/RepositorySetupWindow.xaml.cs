using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using UEModManager.Services;
using UEModManager.Services.Paths;
using UEModManager.Localization;

namespace UEModManager.Views
{
    /// <summary>
    /// 首次运行时问一句"MOD 存哪个盘"。
    ///
    /// <para>
    /// 容量、选中状态与最终保存路径优先展示；小容量卷按真实总容量归组。
    /// 仅负责交互编排，推荐、目录校验与偏好保存仍由现有服务处理。
    /// </para>
    ///
    /// <para>
    /// <b>无论从哪条路径离开，都恰好记一次"已问过"。</b>确认走
    /// <see cref="RepositorySetupService.Apply"/>（它自己会记），其余一切路径
    /// ——"以后再说"、标题栏的关闭按钮、Alt+F4、被 Owner 关掉——统一由
    /// <see cref="OnClosed"/> 兜底调 <see cref="RepositorySetupService.Skip"/>。
    /// 不这么做的话，直接关窗口的用户每次启动都会被再问一次，
    /// 而"跳过的人不该被问第二次"是这套引导的硬要求。
    /// </para>
    /// </summary>
    public partial class RepositorySetupWindow : Window
    {
        private readonly RepositorySetupService _service;
        private readonly ILogger? _logger;
        private readonly DispatcherTimer _feedbackTimer;
        private IReadOnlyList<RepositoryDriveRow> _drives = Array.Empty<RepositoryDriveRow>();
        private RepositoryDriveRow? _selectedDrive;
        private bool _syncingSelection;
        private string _pathHintSource = string.Empty;
        private string _feedbackSource = string.Empty;

        /// <summary>用户手动挑的文件夹；为 null 时以列表里选中的盘为准。</summary>
        private string? _manualPath;

        /// <summary>当前候选位置的判定结果；没有任何候选时为 null。</summary>
        private RepositoryLocationVerdict? _verdict;

        /// <summary>已经落定（成功设置或已跳过），<see cref="OnClosed"/> 不再补记。</summary>
        private bool _settled;

        public RepositorySetupWindow(RepositorySetupService service, ILogger? logger = null)
            : this(service, logger, drives: null)
        {
        }

        internal RepositorySetupWindow(RepositorySetupService service, ILogger? logger,
            IReadOnlyList<RepositoryDriveOption>? drives)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _logger = logger;

            InitializeComponent();
            MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 32);
            Loaded += (_, _) => KeepWithinWorkArea();
            SizeChanged += (_, _) => KeepWithinWorkArea();
            _feedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
            _feedbackTimer.Tick += (_, _) => ClearFeedback();
            LoadDrives(drives ?? _service.ListDrives());
            LanguageManager.LanguageChanged += OnLanguageChanged;
        }

        private void OnLanguageChanged(bool english) => Dispatcher.Invoke(() =>
        {
            foreach (var drive in _drives) drive.RefreshLanguage();
            UpdateVolumeCount();
            PathHintText.Text = UiText.Get(_pathHintSource);
            FeedbackText.Text = UiText.Get(_feedbackSource);
            if (_verdict != null)
                IssueList.ItemsSource = _verdict.Issues
                    .Select(i => RepositoryIssueRow.Create(i, _verdict.Severity, FindBrush)).ToList();
        });

        private void LanguageToggle_Click(object sender, RoutedEventArgs e)
        {
            try { LanguageManager.SaveAndSetEnglish(!LanguageManager.IsEnglish); }
            catch (Exception ex)
            {
                CyberMessageBox.Show(this, UiText.Format("保存语言设置失败：{0}", ex.Message), UiText.Get("错误"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateVolumeCount()
        {
            var count = _drives.Count(d => d.IsSmallVolume);
            SmallVolumesToggle.Tag = UiText.Format("{0} 个", count);
            AutomationProperties.SetName(SmallVolumesToggle, UiText.Format("显示或收起 {0} 个小容量卷", count));
        }

        // ─── 初始化 ───

        private void KeepWithinWorkArea()
        {
            if (!IsLoaded) return;
            var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
            var area = screen.WorkingArea;
            var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var bounds = Rect.Transform(new Rect(area.X, area.Y, area.Width, area.Height), transform);
            MaxHeight = Math.Max(320, bounds.Height - 32);

            // SizeToContent 展开后保留原来的 Top；靠近屏幕底部时需上移，避免按钮落到屏外。
            if (!double.IsNaN(Top))
                Top = Math.Clamp(Top, bounds.Top + 16,
                    Math.Max(bounds.Top + 16, bounds.Bottom - Math.Min(ActualHeight, MaxHeight) - 16));
        }

        private void LoadDrives(IReadOnlyList<RepositoryDriveOption> drives)
        {
            var recommended = RepositoryDriveAdvisor.Recommend(drives);

            _drives = drives
                .Select(d => RepositoryDriveRow.Create(
                    d, isRecommended: recommended != null && ReferenceEquals(d, recommended), FindBrush))
                .ToArray();
            foreach (var drive in _drives) drive.PropertyChanged += Drive_PropertyChanged;

            var mainDrives = _drives.Where(d => !d.IsSmallVolume).ToArray();
            var smallDrives = _drives.Where(d => d.IsSmallVolume).ToArray();
            DriveList.ItemsSource = mainDrives;
            SmallDriveList.ItemsSource = smallDrives;
            SmallVolumesToggle.Visibility = smallDrives.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            UpdateVolumeCount();
            SmallVolumesToggle.IsChecked = mainDrives.Length == 0 && smallDrives.Length > 0;
            EmptyDrivesText.Visibility = _drives.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // 没有足够空间的固定盘时，保持未选择，交给用户决定。
            SetSelectedDrive(_drives.FirstOrDefault(r => r.IsRecommended));
            RefreshVerdict();
        }

        private Brush? FindBrush(string key)
        {
            try
            {
                // 使用引导专用的主题令牌，不覆盖主程序其他窗口的配色。
                return TryFindResource(key switch
                {
                    "StatusRedBrush" => "SetupErrorBrush",
                    "StatusOrangeBrush" => "SetupWarningBrush",
                    "StatusGreenBrush" or "PrimaryBrush" => "SetupAccentBrush",
                    _ => "SetupSecondaryBrush"
                }) as Brush ?? TryFindResource(key) as Brush;
            }
            catch { return null; }
        }

        // ─── 候选位置 ───

        private void Drive_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_syncingSelection || e.PropertyName != nameof(RepositoryDriveRow.IsSelected)
                || sender is not RepositoryDriveRow { IsSelected: true } drive) return;

            SetSelectedDrive(drive);
            _manualPath = null;
            RefreshVerdict();
        }

        private void SetSelectedDrive(RepositoryDriveRow? selected)
        {
            _syncingSelection = true;
            try
            {
                _selectedDrive = selected;
                foreach (var drive in _drives) drive.IsSelected = ReferenceEquals(drive, selected);
            }
            finally { _syncingSelection = false; }
        }

        private void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = UiText.Get("选择一个文件夹存放 MOD"),
                Multiselect = false,
            };
            var initialPath = CurrentSelectionRoot();
            if (Directory.Exists(initialPath)) dialog.InitialDirectory = initialPath;

            if (dialog.ShowDialog(this) == true) SelectFolder(dialog.FolderName);
        }

        internal void SelectFolder(string path)
        {
            // 清除两组单选项时不能让取消选择事件覆盖刚选好的自定义路径。
            SetSelectedDrive(null);
            _manualPath = path;
            RefreshVerdict();
        }

        private string? CurrentSelectionRoot()
            => _manualPath ?? _selectedDrive?.RootPath;

        /// <summary>
        /// 重新检查当前候选位置并刷新界面。
        /// 检查里包含一次真实的试写，失败不该把窗口带崩——落到 catch 就当作"没有候选"，
        /// 用户可以换一个再试。
        /// </summary>
        private void RefreshVerdict()
        {
            ClearFeedback();
            var candidate = CurrentSelectionRoot();
            if (string.IsNullOrWhiteSpace(candidate))
            {
                _verdict = null;
                ShowPath(_service.DefaultRepositoryRoot);
                ShowIssues(Array.Empty<RepositoryIssueRow>(), "选择一个磁盘或文件夹，确认后使用上方位置。");
                ConfirmButton.IsEnabled = false;
                CopyPathButton.IsEnabled = false;
                return;
            }

            try
            {
                _verdict = _service.Inspect(candidate);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[UI] 检查仓库候选位置失败");
                _verdict = null;
            }

            if (_verdict == null)
            {
                ShowPath(candidate);
                ShowIssues(Array.Empty<RepositoryIssueRow>(), "这个位置暂时无法检查，请换一个文件夹。");
                ConfirmButton.IsEnabled = false;
                CopyPathButton.IsEnabled = false;
                return;
            }

            var verdict = _verdict;
            ShowPath(verdict.CanUse ? verdict.ResolvedPath : candidate);
            var issues = verdict.Issues
                .Select(i => RepositoryIssueRow.Create(i, verdict.Severity, FindBrush))
                .ToList();
            ShowIssues(issues, "MOD 将独立存放，不会与已有文件混放。");
            ConfirmButton.IsEnabled = verdict.CanUse;
            CopyPathButton.IsEnabled = verdict.CanUse;
        }

        private void ShowPath(string path)
        {
            string root;
            try { root = Path.GetPathRoot(path) ?? string.Empty; }
            catch (ArgumentException) { root = string.Empty; }
            PathRootRun.Text = root;
            PathRemainderRun.Text = path[root.Length..];
            ResolvedPathText.ToolTip = path;
        }

        private void ShowIssues(IReadOnlyList<RepositoryIssueRow> issues, string hint)
        {
            IssueList.ItemsSource = issues;
            IssueList.Visibility = issues.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            DefaultPathHint.Visibility = issues.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _pathHintSource = hint;
            PathHintText.Text = UiText.Get(hint);
        }

        private void CopyPath_OnClick(object sender, RoutedEventArgs e)
        {
            if (_verdict is not { CanUse: true }) return;
            try
            {
                System.Windows.Clipboard.SetText(_verdict.ResolvedPath);
                ShowFeedback("路径已复制。", warning: false);
            }
            catch (System.Runtime.InteropServices.ExternalException ex)
            {
                _logger?.LogWarning(ex, "[UI] 复制仓库位置失败");
                ShowFeedback("暂时无法复制路径，请稍后重试。", warning: true);
            }
            _feedbackTimer.Start();
        }

        private void ShowFeedback(string message, bool warning)
        {
            _feedbackSource = message;
            FeedbackText.Text = UiText.Get(message);
            FeedbackText.Foreground = (Brush)FindResource(warning ? "SetupWarningBrush" : "SetupSecondaryBrush");
            FeedbackText.Visibility = Visibility.Visible;
        }

        private void ClearFeedback()
        {
            _feedbackTimer.Stop();
            FeedbackText.Visibility = Visibility.Collapsed;
        }

        // ─── 三个出口 ───

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            if (_verdict == null || !_verdict.CanUse) return;
            var displayedPath = _verdict.ResolvedPath;
            RefreshVerdict();
            if (_verdict == null || !_verdict.CanUse) return;
            if (!string.Equals(displayedPath, _verdict.ResolvedPath, StringComparison.OrdinalIgnoreCase))
            {
                ShowFeedback("保存位置发生了变化，请核对上方路径后再次确认。", warning: true);
                return;
            }

            // 有代价的位置（可移动盘 / 网络位置 / 安装目录内 / 空间偏少）再确认一次。
            // 不做成硬禁止：这些都是用户可能确实想要的选择，我们只负责让他知道代价。
            if (_verdict.NeedsConfirmation)
            {
                var warnings = string.Join(Environment.NewLine + Environment.NewLine,
                    _verdict.Issues.Select(RepositoryIssueRow.MessageFor));
                var choice = CyberMessageBox.Show(this,
                    warnings + Environment.NewLine + Environment.NewLine + UiText.Get("确定要用这个位置吗？"),
                    UiText.Get("请确认"), MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    yesText: UiText.Get("确定使用"), noText: UiText.Get("换一个"));
                if (choice != MessageBoxResult.Yes) return;
            }

            try
            {
                _service.Apply(_verdict.ResolvedPath);
                _settled = true;
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                // 与 SettingsWindow.Save_Click 同一模式：写失败时窗口保持打开，
                // 用户看到原因后可以换个位置重试。这里绝不能吞——设置没生效却关掉窗口，
                // 用户会以为自己已经把 MOD 换到别的盘了。
                _logger?.LogError(ex, "[UI] 保存仓库位置失败");
                CyberMessageBox.Show(this,
                    UiText.Interpolate($"没能把位置保存下来：{ex.Message}\n\n请换一个文件夹再试，或者先点「以后再说」。"),
                    UiText.Get("保存失败"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Skip_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        }

        private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        }

        /// <summary>
        /// 兜底记账。放在 <see cref="OnClosed"/> 而不是各个按钮里，是因为"关闭窗口"的路径
        /// 数不清（标题栏按钮、Alt+F4、Owner 关闭、系统菜单），漏掉任何一条的表现都是
        /// "这个框每次启动都弹"。
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            _feedbackTimer.Stop();
            LanguageManager.LanguageChanged -= OnLanguageChanged;
            foreach (var drive in _drives) drive.PropertyChanged -= Drive_PropertyChanged;
            if (!_settled)
            {
                _settled = true;
                try { _service.Skip(); }
                catch (Exception ex)
                {
                    // Skip 自己不抛，这层只防将来有人改坏它。记不上标记的唯一后果是下次再问一次。
                    _logger?.LogWarning(ex, "[UI] 记录仓库位置引导标记失败");
                }
            }

            base.OnClosed(e);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  行展示模型
    //
    //  与 ManagementCenterWindow 里的三个 Row 同一形态：结构交给 XAML 的
    //  DataTemplate，这里只把模型翻译成"显示什么文字、什么颜色"，
    //  于是这段映射可以直接单测——引导界面在无头环境里点不了，
    //  但"多大空间显示成什么颜色""可移动盘有没有打标"是能钉死的。
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>磁盘列表的一行。</summary>
    public sealed class RepositoryDriveRow : INotifyPropertyChanged
    {
        private bool _isSelected;
        private long? _availableBytes;
        private long? _totalBytes;
        private RepositoryDriveOption _option = null!;

        public event PropertyChangedEventHandler? PropertyChanged;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (value && !IsSelectable || _isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        // 按总容量整理小卷，而不是把快满的大磁盘折叠掉；未知容量仍留在主列表。
        public bool IsSmallVolume => _totalBytes is > 0 and < 1024L * 1024 * 1024;
        public bool IsSelectable => _availableBytes is null or > 0;
        public bool IsWarning => _availableBytes is >= 0 and < RepositoryLocationValidator.RecommendedFreeBytes;
        public string FreeText => FormatCapacity(_availableBytes).Value;
        public string FreeUnitLabel => UiText.Interpolate($"{FormatCapacity(_availableBytes).Unit} 可用").Trim();
        public string TotalLabel => _totalBytes is > 0 ? UiText.Interpolate($"共 {DiskSpacePrecheck.Humanize(_totalBytes.Value)}") : UiText.Get("总容量未知");
        public string UsedDescription => _availableBytes is null || _totalBytes is null or <= 0
            ? UiText.Get("已用空间未知") : UiText.Interpolate($"已用 {UsedPercent:0.#}%");
        public string AccessibleName => $"{DisplayName}，{FreeText} {FreeUnitLabel}，{TotalLabel}，{BadgeText}";

        private static (string Value, string Unit) FormatCapacity(long? bytes)
        {
            if (bytes is null) return (UiText.Get("未知"), string.Empty);
            var parts = DiskSpacePrecheck.Humanize(Math.Max(0, bytes.Value)).Split(' ', 2);
            return (parts[0], parts[1]);
        }

        private static string DisplayNameFor(RepositoryDriveOption option)
        {
            var volume = option.RootPath.TrimEnd('\\', '/');
            if (volume.Length != 2 || volume[1] != ':') return option.DisplayName;
            var label = option.DisplayName.StartsWith(volume, StringComparison.OrdinalIgnoreCase)
                ? option.DisplayName[volume.Length..].Trim() : option.DisplayName;
            return $"{(string.IsNullOrEmpty(label) ? UiText.Get("本地磁盘") : label)} ({volume})";
        }

        private RepositoryDriveRow(
            string rootPath, string displayName, string capacityText,
            string badgeText, string badgeBrushKey, Brush? badgeBrush, Visibility badgeVisibility,
            double usedPercent, string usageBrushKey, Brush? usageBrush, bool isRecommended,
            string gameVolumeText, string gameVolumeBrushKey, Brush? gameVolumeBrush,
            Visibility gameVolumeVisibility)
        {
            RootPath = rootPath;
            BadgeBrushKey = badgeBrushKey;
            BadgeBrush = badgeBrush;
            BadgeVisibility = badgeVisibility;
            UsedPercent = usedPercent;
            UsageBrushKey = usageBrushKey;
            UsageBrush = usageBrush;
            IsRecommended = isRecommended;
            GameVolumeBrushKey = gameVolumeBrushKey;
            GameVolumeBrush = gameVolumeBrush;
            GameVolumeVisibility = gameVolumeVisibility;
        }

        public string RootPath { get; }
        public string DisplayName => DisplayNameFor(_option);
        public string CapacityText => CapacityTextFor(_availableBytes, _totalBytes);
        public string BadgeText => _availableBytes is <= 0 ? UiText.Get("已满")
            : BadgeTextFor(_option.Kind, IsRecommended, _option.IsCurrentDefault);
        public string BadgeBrushKey { get; }
        public Brush? BadgeBrush { get; }
        public Visibility BadgeVisibility { get; }
        public double UsedPercent { get; }
        public string UsageBrushKey { get; }
        public Brush? UsageBrush { get; }
        public bool IsRecommended { get; }
        public string GameVolumeText => GameVolumeTextFor(_option.HostsCurrentGame);
        public string GameVolumeBrushKey { get; }
        public Brush? GameVolumeBrush { get; }
        public Visibility GameVolumeVisibility { get; }

        internal void RefreshLanguage() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

        /// <summary>
        /// 角标文字。优先级是"风险 &gt; 推荐 &gt; 现状"：
        /// 一个可移动的 U 盘哪怕空间再大也不该顶着"推荐"两个字。
        /// </summary>
        public static string BadgeTextFor(RepositoryVolumeKind kind, bool isRecommended, bool isCurrentDefault)
            => kind switch
            {
                RepositoryVolumeKind.Removable => UiText.Get("可移动"),
                RepositoryVolumeKind.Network => UiText.Get("网络位置"),
                _ when isRecommended => UiText.Get("推荐"),
                _ when isCurrentDefault => UiText.Get("当前位置"),
                _ => string.Empty,
            };

        public static string BadgeBrushKeyFor(RepositoryVolumeKind kind, bool isRecommended)
            => kind is RepositoryVolumeKind.Removable or RepositoryVolumeKind.Network
                ? "StatusOrangeBrush"
                : isRecommended ? "StatusGreenBrush" : "Text500Brush";

        /// <summary>
        /// 已用比例条的颜色。低于 <see cref="RepositoryLocationValidator.RecommendedFreeBytes"/>
        /// 转红：这条比例条本身只说明"盘满不满"，而用户真正要判断的是"还装不装得下 MOD"，
        /// 一个 90% 已用的 4 TB 盘仍然绰绰有余。所以判据取剩余绝对值，不取百分比。
        /// </summary>
        public static string UsageBrushKeyFor(long? availableBytes)
            => availableBytes is { } available && available < RepositoryLocationValidator.RecommendedFreeBytes
                ? "StatusRedBrush"
                : "PrimaryBrush";

        public static string CapacityTextFor(long? availableBytes, long? totalBytes)
        {
            if (availableBytes is null || totalBytes is null || totalBytes <= 0) return UiText.Get("容量未知");
            return UiText.Interpolate($"可用 {DiskSpacePrecheck.Humanize(availableBytes.Value)}")
                + UiText.Interpolate($" / 共 {DiskSpacePrecheck.Humanize(totalBytes.Value)}");
        }

        public static double UsedPercentFor(long? availableBytes, long? totalBytes)
        {
            if (availableBytes is null || totalBytes is null || totalBytes <= 0) return 0;
            var used = Math.Max(0, totalBytes.Value - availableBytes.Value);
            return Math.Clamp(used * 100d / totalBytes.Value, 0, 100);
        }

        /// <summary>
        /// "与游戏同一个盘"这一行的文字。不占用角标，<b>单独一行</b>。
        ///
        /// <para>
        /// 不做成角标是因为角标那一格已经按"风险 &gt; 推荐 &gt; 现状"排好了优先级，
        /// 挤进去必然要和"可移动"抢位置——而一块装着游戏的移动硬盘，
        /// "拔掉就全没了"和"放这里能省空间"两件事都成立，都得让用户看见，
        /// 不该由我们替他二选一。
        /// </para>
        ///
        /// <para>
        /// <b>拿不到游戏路径时返回空串</b>，整行随之隐藏。首次运行引导跑在启动早期、
        /// config.json 还不存在（那正是引导会弹的前提之一），所以这是常态而非异常，
        /// 界面上绝不能因此显示"未知"或任何错误。
        /// </para>
        /// </summary>
        public static string GameVolumeTextFor(bool hostsCurrentGame)
            => hostsCurrentGame ? UiText.Get("游戏就装在这个盘 · 存这里的话，MOD 不会再多占一份空间") : string.Empty;

        /// <summary>这是一条好消息，用与"推荐"同一档的绿色；没有这行时颜色无意义。</summary>
        public static string GameVolumeBrushKeyFor(bool hostsCurrentGame)
            => hostsCurrentGame ? "StatusGreenBrush" : "Text500Brush";

        public static RepositoryDriveRow Create(
            RepositoryDriveOption option, bool isRecommended, Func<string, Brush?> resolveBrush)
        {
            if (option is null) throw new ArgumentNullException(nameof(option));
            if (resolveBrush is null) throw new ArgumentNullException(nameof(resolveBrush));

            var badgeText = option.AvailableBytes is <= 0 ? UiText.Get("已满")
                : BadgeTextFor(option.Kind, isRecommended, option.IsCurrentDefault);
            var badgeKey = BadgeBrushKeyFor(option.Kind, isRecommended);
            var usageKey = UsageBrushKeyFor(option.AvailableBytes);
            var gameText = GameVolumeTextFor(option.HostsCurrentGame);
            var gameKey = GameVolumeBrushKeyFor(option.HostsCurrentGame);

            return new RepositoryDriveRow(
                option.RootPath,
                DisplayNameFor(option),
                CapacityTextFor(option.AvailableBytes, option.TotalBytes),
                badgeText,
                badgeKey,
                resolveBrush(badgeKey),
                badgeText.Length == 0 ? Visibility.Collapsed : Visibility.Visible,
                UsedPercentFor(option.AvailableBytes, option.TotalBytes),
                usageKey,
                resolveBrush(usageKey),
                isRecommended,
                gameText,
                gameKey,
                resolveBrush(gameKey),
                gameText.Length == 0 ? Visibility.Collapsed : Visibility.Visible)
            {
                _option = option,
                _availableBytes = option.AvailableBytes,
                _totalBytes = option.TotalBytes
            };
        }
    }

    /// <summary>候选位置提示列表的一行。</summary>
    public sealed class RepositoryIssueRow
    {
        private RepositoryIssueRow(string glyph, string message, string brushKey, Brush? brush)
        {
            Glyph = glyph;
            Message = message;
            BrushKey = brushKey;
            Brush = brush;
        }

        public string Glyph { get; }
        public string Message { get; }
        public string BrushKey { get; }
        public Brush? Brush { get; }

        /// <summary>
        /// "落点变了"是提示不是警告，用普通的信息图标与灰字；
        /// 其余每一条都对应一种真实的数据风险，一律走警示色。
        /// </summary>
        public static bool IsAdvisory(RepositoryLocationIssueCode code)
            => code == RepositoryLocationIssueCode.DirectoryNotEmpty;

        /// <summary>Segoe MDL2 图标：提示用 Info，风险用 Warning。</summary>
        public static string GlyphFor(RepositoryLocationIssueCode code)
            => IsAdvisory(code) ? "" : "";

        public static string BrushKeyFor(RepositoryLocationIssueCode code, RepositoryLocationSeverity severity)
        {
            if (IsAdvisory(code)) return "Text500Brush";
            return severity == RepositoryLocationSeverity.Blocked ? "StatusRedBrush" : "StatusOrangeBrush";
        }

        public static RepositoryIssueRow Create(
            RepositoryLocationIssue issue, RepositoryLocationSeverity severity,
            Func<string, Brush?> resolveBrush)
        {
            if (issue is null) throw new ArgumentNullException(nameof(issue));
            if (resolveBrush is null) throw new ArgumentNullException(nameof(resolveBrush));

            var key = BrushKeyFor(issue.Code, severity);
            return new RepositoryIssueRow(GlyphFor(issue.Code), MessageFor(issue), key, resolveBrush(key));
        }

        public static string MessageFor(RepositoryLocationIssue issue)
        {
            if (!LanguageManager.IsEnglish) return issue.Message;
            return issue.Code switch
            {
                RepositoryLocationIssueCode.Empty => "Choose a drive or folder.",
                RepositoryLocationIssueCode.NotAbsolute => "Enter a full path, such as D:\\Mods.",
                RepositoryLocationIssueCode.Malformed => "This path is not valid. Choose another folder.",
                RepositoryLocationIssueCode.NotWritable => "This location is not writable. Choose another folder or check its permissions.",
                RepositoryLocationIssueCode.RemovableVolume => "Mods will be unavailable when this removable drive is disconnected.",
                RepositoryLocationIssueCode.NetworkVolume => "Mods will be unavailable if the network location cannot be reached.",
                RepositoryLocationIssueCode.InsideInstallDirectory => "This folder is inside the app installation. Updates or uninstallation could remove its files.",
                RepositoryLocationIssueCode.LowFreeSpace => "Free space is low. A drive with at least 10 GB available is recommended.",
                RepositoryLocationIssueCode.DirectoryNotEmpty => "This folder already contains files. Mods will use the dedicated subfolder shown above.",
                _ => issue.Message
            };
        }
    }
}
