using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using UEModManager.Services;
using UEModManager.Services.Paths;

namespace UEModManager.Views
{
    /// <summary>
    /// 首次运行时问一句"MOD 存哪个盘"。
    ///
    /// <para>
    /// 这是一个<b>新增的独立对话框</b>，不在 1:1 UI 原型的范围内，主界面布局一点没动。
    /// 视觉全部取自 <c>CyberStyles.xaml</c> 的设计令牌，与 <see cref="CyberMessageBox"/>
    /// 同一套（<c>CyberModalWindow</c> 外壳 + Primary/Secondary/Ghost 三种按钮）。
    /// 一律不用原生 <c>MessageBox</c>：审计项 P1-7 正在清理它对暗色主题的破坏，
    /// 这里不能再添一处。
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

        /// <summary>用户手动挑的文件夹；为 null 时以列表里选中的盘为准。</summary>
        private string? _manualPath;

        /// <summary>当前候选位置的判定结果；没有任何候选时为 null。</summary>
        private RepositoryLocationVerdict? _verdict;

        /// <summary>已经落定（成功设置或已跳过），<see cref="OnClosed"/> 不再补记。</summary>
        private bool _settled;

        public RepositorySetupWindow(RepositorySetupService service, ILogger? logger = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _logger = logger;

            InitializeComponent();
            LoadDrives();
        }

        // ─── 初始化 ───

        private void LoadDrives()
        {
            var drives = _service.ListDrives();
            var recommended = RepositoryDriveAdvisor.Recommend(drives);

            var rows = drives
                .Select(d => RepositoryDriveRow.Create(
                    d, isRecommended: recommended != null && ReferenceEquals(d, recommended), FindBrush))
                .ToList();

            DriveList.ItemsSource = rows;

            // 默认选中推荐盘。选不出推荐盘（只有系统盘且已经很满、或全是可移动/网络盘）时
            // 一个都不预选：与其替用户挑一个装不下的位置，不如让他自己看着数字决定。
            DriveList.SelectedItem = rows.FirstOrDefault(r => r.IsRecommended);

            if (DriveList.SelectedItem == null) RefreshVerdict();
        }

        private Brush? FindBrush(string key)
        {
            try { return TryFindResource(key) as Brush; }
            catch { return null; }
        }

        // ─── 候选位置 ───

        private void DriveList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // 在列表里点一下就等于放弃之前手选的文件夹，否则界面显示的落点会和选中项对不上
            _manualPath = null;
            RefreshVerdict();
        }

        private void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择一个文件夹存放 MOD",
                UseDescriptionForTitle = true,
                SelectedPath = CurrentSelectionRoot() ?? string.Empty,
            };

            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            _manualPath = dialog.SelectedPath;
            DriveList.SelectedItem = null;
            RefreshVerdict();
        }

        private string? CurrentSelectionRoot()
            => _manualPath ?? (DriveList.SelectedItem as RepositoryDriveRow)?.RootPath;

        /// <summary>
        /// 重新检查当前候选位置并刷新界面。
        /// 检查里包含一次真实的试写，失败不该把窗口带崩——落到 catch 就当作"没有候选"，
        /// 用户可以换一个再试。
        /// </summary>
        private void RefreshVerdict()
        {
            var candidate = CurrentSelectionRoot();
            if (string.IsNullOrWhiteSpace(candidate))
            {
                _verdict = null;
                ResolvedPathText.Text = "（还没选，保持默认：" + AppPathsDefaultText() + "）";
                IssueList.ItemsSource = null;
                ConfirmButton.IsEnabled = false;
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
                ResolvedPathText.Text = "这个位置检查不了，请换一个。";
                IssueList.ItemsSource = null;
                ConfirmButton.IsEnabled = false;
                return;
            }

            var verdict = _verdict;
            ResolvedPathText.Text = verdict.CanUse ? verdict.ResolvedPath : candidate;
            IssueList.ItemsSource = verdict.Issues
                .Select(i => RepositoryIssueRow.Create(i, verdict.Severity, FindBrush))
                .ToList();
            ConfirmButton.IsEnabled = verdict.CanUse;
        }

        private static string AppPathsDefaultText() => Infrastructure.AppPaths.RepositoryRoot;

        // ─── 三个出口 ───

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            if (_verdict == null || !_verdict.CanUse) return;

            // 有代价的位置（可移动盘 / 网络位置 / 安装目录内 / 空间偏少）再确认一次。
            // 不做成硬禁止：这些都是用户可能确实想要的选择，我们只负责让他知道代价。
            if (_verdict.NeedsConfirmation)
            {
                var warnings = string.Join(Environment.NewLine + Environment.NewLine,
                    _verdict.Issues.Select(i => i.Message));
                var choice = CyberMessageBox.Show(this,
                    warnings + Environment.NewLine + Environment.NewLine + "确定要用这个位置吗？",
                    "请确认", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    yesText: "确定使用", noText: "换一个");
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
                    $"没能把位置保存下来：{ex.Message}\n\n请换一个文件夹再试，或者先点「以后再说」。",
                    "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Skip_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void OnCloseWindow(object sender, ExecutedRoutedEventArgs e) => Close();

        /// <summary>
        /// 兜底记账。放在 <see cref="OnClosed"/> 而不是各个按钮里，是因为"关闭窗口"的路径
        /// 数不清（标题栏按钮、Alt+F4、Owner 关闭、系统菜单），漏掉任何一条的表现都是
        /// "这个框每次启动都弹"。
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
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
    public sealed class RepositoryDriveRow
    {
        private RepositoryDriveRow(
            string rootPath, string displayName, string capacityText,
            string badgeText, string badgeBrushKey, Brush? badgeBrush, Visibility badgeVisibility,
            double usedPercent, string usageBrushKey, Brush? usageBrush, bool isRecommended)
        {
            RootPath = rootPath;
            DisplayName = displayName;
            CapacityText = capacityText;
            BadgeText = badgeText;
            BadgeBrushKey = badgeBrushKey;
            BadgeBrush = badgeBrush;
            BadgeVisibility = badgeVisibility;
            UsedPercent = usedPercent;
            UsageBrushKey = usageBrushKey;
            UsageBrush = usageBrush;
            IsRecommended = isRecommended;
        }

        public string RootPath { get; }
        public string DisplayName { get; }
        public string CapacityText { get; }
        public string BadgeText { get; }
        public string BadgeBrushKey { get; }
        public Brush? BadgeBrush { get; }
        public Visibility BadgeVisibility { get; }
        public double UsedPercent { get; }
        public string UsageBrushKey { get; }
        public Brush? UsageBrush { get; }
        public bool IsRecommended { get; }

        /// <summary>
        /// 角标文字。优先级是"风险 &gt; 推荐 &gt; 现状"：
        /// 一个可移动的 U 盘哪怕空间再大也不该顶着"推荐"两个字。
        /// </summary>
        public static string BadgeTextFor(RepositoryVolumeKind kind, bool isRecommended, bool isCurrentDefault)
            => kind switch
            {
                RepositoryVolumeKind.Removable => "可移动",
                RepositoryVolumeKind.Network => "网络位置",
                _ when isRecommended => "推荐",
                _ when isCurrentDefault => "当前位置",
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
            if (availableBytes is null || totalBytes is null || totalBytes <= 0) return "容量未知";
            return $"可用 {DiskSpacePrecheck.Humanize(availableBytes.Value)}"
                + $" / 共 {DiskSpacePrecheck.Humanize(totalBytes.Value)}";
        }

        public static double UsedPercentFor(long? availableBytes, long? totalBytes)
        {
            if (availableBytes is null || totalBytes is null || totalBytes <= 0) return 0;
            var used = Math.Max(0, totalBytes.Value - availableBytes.Value);
            return Math.Clamp(used * 100d / totalBytes.Value, 0, 100);
        }

        public static RepositoryDriveRow Create(
            RepositoryDriveOption option, bool isRecommended, Func<string, Brush?> resolveBrush)
        {
            if (option is null) throw new ArgumentNullException(nameof(option));
            if (resolveBrush is null) throw new ArgumentNullException(nameof(resolveBrush));

            var badgeText = BadgeTextFor(option.Kind, isRecommended, option.IsCurrentDefault);
            var badgeKey = BadgeBrushKeyFor(option.Kind, isRecommended);
            var usageKey = UsageBrushKeyFor(option.AvailableBytes);

            return new RepositoryDriveRow(
                option.RootPath,
                option.DisplayName,
                CapacityTextFor(option.AvailableBytes, option.TotalBytes),
                badgeText,
                badgeKey,
                resolveBrush(badgeKey),
                badgeText.Length == 0 ? Visibility.Collapsed : Visibility.Visible,
                UsedPercentFor(option.AvailableBytes, option.TotalBytes),
                usageKey,
                resolveBrush(usageKey),
                isRecommended);
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
            return new RepositoryIssueRow(GlyphFor(issue.Code), issue.Message, key, resolveBrush(key));
        }
    }
}
