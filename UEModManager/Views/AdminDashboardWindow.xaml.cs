using UEModManager.Localization;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UEModManager.Infrastructure;
using UEModManager.Services;

namespace UEModManager.Views
{
    /// <summary>只读查看本机账户；统计与列表来自同一次真实数据库查询。</summary>
    public partial class AdminDashboardWindow : Window
    {
        private readonly ILogger<AdminDashboardWindow> _logger;
        private readonly LocalAuthService _localAuthService;
        private readonly ObservableCollection<UserInfo> _users = new();
        private bool _isLoading;
        private bool _isClosed;

        public AdminDashboardWindow()
        {
            InitializeComponent();
            var services = ((App)Application.Current).ServiceProvider;
            _logger = services.GetRequiredService<ILogger<AdminDashboardWindow>>();
            _localAuthService = services.GetRequiredService<LocalAuthService>();
            UsersDataGrid.ItemsSource = _users;

            ApplyLocalization();
            LanguageManager.LanguageChanged += OnLanguageChanged;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
            => SafeEvent.Run(this, LoadDashboardDataAsync, _logger, UiText.Get("加载本机账户"));

        private async Task LoadDashboardDataAsync()
        {
            if (_isLoading || _isClosed) return;
            _isLoading = true;
            RefreshButton.IsEnabled = false;

            try
            {
                // 查询失败必须进入 catch，不能把失败当作零用户并显示“数据库正常”。
                var users = (await _localAuthService.GetAllUsersAsync()).ToList();
                if (_isClosed) return;

                TotalUsersText.Text = users.Count.ToString();
                var thirtyDaysAgo = DateTime.Now.AddDays(-30);
                ActiveUsersText.Text = users.Count(user => user.LastLoginAt >= thirtyDaysAgo).ToString();
                DatabaseStatusText.Text = LanguageManager.IsEnglish ? "Connected" : UiText.Get("正常");
                DatabaseStatusText.Foreground = (System.Windows.Media.Brush)FindResource("StatusGreenBrush");
                DatabaseStatusText.ToolTip = null;

                var search = UserSearchBox.Text?.Trim() ?? string.Empty;
                _users.Clear();
                foreach (var user in users.Where(user =>
                    search.Length == 0 || user.Email.Contains(search, StringComparison.OrdinalIgnoreCase)))
                {
                    _users.Add(new UserInfo
                    {
                        Id = user.Id,
                        Email = user.Email,
                        CreatedAt = user.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
                        LastLoginAt = user.LastLoginAt == default
                            ? (LanguageManager.IsEnglish ? "Never" : UiText.Get("从未"))
                            : user.LastLoginAt.ToString("yyyy-MM-dd HH:mm"),
                        Status = user.IsLocked
                            ? (LanguageManager.IsEnglish ? "Locked" : UiText.Get("已锁定"))
                            : (LanguageManager.IsEnglish ? "Normal" : UiText.Get("正常"))
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "读取本机账户失败");
                if (_isClosed) return;

                _users.Clear();
                TotalUsersText.Text = "—";
                ActiveUsersText.Text = "—";
                DatabaseStatusText.Text = LanguageManager.IsEnglish ? "Read failed" : UiText.Get("读取失败");
                DatabaseStatusText.Foreground = (System.Windows.Media.Brush)FindResource("StatusRedBrush");
                DatabaseStatusText.ToolTip = ex.Message;
            }
            finally
            {
                _isLoading = false;
                if (!_isClosed) RefreshButton.IsEnabled = true;
            }
        }

        private void RefreshDataButton_Click(object sender, RoutedEventArgs e)
            => SafeEvent.Run(this, LoadDashboardDataAsync, _logger, UiText.Get("刷新本机账户"));

        private void SearchUsersButton_Click(object sender, RoutedEventArgs e)
            => SafeEvent.Run(this, LoadDashboardDataAsync, _logger, UiText.Get("搜索本机账户"));

        private void ApplyLocalization()
        {
            // Static labels are language bindings in XAML; data refresh happens in OnLanguageChanged.
        }

        private void OnLanguageChanged(bool english)
        {
            Dispatcher.Invoke(() =>
            {
                ApplyLocalization();
                SafeEvent.Run(this, LoadDashboardDataAsync, _logger, UiText.Get("更新账户显示语言"));
            });
        }

        private void OnMinimizeWindow(object sender, ExecutedRoutedEventArgs e)
            => SystemCommands.MinimizeWindow(this);

        private void OnMaximizeWindow(object sender, ExecutedRoutedEventArgs e)
            => SystemCommands.MaximizeWindow(this);

        private void OnRestoreWindow(object sender, ExecutedRoutedEventArgs e)
            => SystemCommands.RestoreWindow(this);

        private void OnCloseWindow(object sender, ExecutedRoutedEventArgs e) => Close();

        protected override void OnClosed(EventArgs e)
        {
            _isClosed = true;
            LanguageManager.LanguageChanged -= OnLanguageChanged;
            base.OnClosed(e);
        }
    }

    public sealed class UserInfo
    {
        public int Id { get; init; }
        public string Email { get; init; } = string.Empty;
        public string CreatedAt { get; init; } = string.Empty;
        public string LastLoginAt { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
    }
}
