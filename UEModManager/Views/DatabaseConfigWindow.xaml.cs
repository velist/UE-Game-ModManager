using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UEModManager.Services;

namespace UEModManager.Views
{
    public partial class DatabaseConfigWindow : Window
    {
        private readonly PostgreSQLAuthService _postgreSqlService;
        private readonly ILogger<DatabaseConfigWindow> _logger;
        
        public PostgreSQLConfig DatabaseConfig { get; private set; }
        
        public DatabaseConfigWindow()
        {
            InitializeComponent();
            ApplyLocalization();
            UEModManager.Services.LanguageManager.LanguageChanged += _ => { Dispatcher.Invoke(ApplyLocalization); };

            
            // 获取依赖注入的服务
            var serviceProvider = ((App)Application.Current).ServiceProvider;
            _postgreSqlService = serviceProvider.GetRequiredService<PostgreSQLAuthService>();
            _logger = serviceProvider.GetRequiredService<ILogger<DatabaseConfigWindow>>();
            
            LoadFreeProviders();
            LoadCurrentConfig();
        }

        /// <summary>
        /// 加载免费服务商信息
        /// </summary>
        private void LoadFreeProviders()
        {
            var providers = PostgreSQLAuthService.GetFreeProviders();
            var providerInfos = new List<object>();
            
            foreach (var (provider, name, description, url) in providers)
            {
                providerInfos.Add(new
                {
                    Provider = provider,
                    Name = name,
                    Description = description,
                    Url = url
                });
            }
            
            FreeProvidersItemsControl.ItemsSource = providerInfos;
        }

        /// <summary>
        /// 加载当前配置
        /// </summary>
        private void LoadCurrentConfig()
        {
            try
            {
                // 设置默认值
                ProviderComboBox.SelectedIndex = 0; // 默认选择本地
                HostTextBox.Text = "localhost";
                DatabaseTextBox.Text = "uemodmanager";
                PortTextBox.Text = "5432";
                UsernameTextBox.Text = "postgres";
                EnableSslCheckBox.IsChecked = false;
                ConnectionTimeoutTextBox.Text = "30";
                CommandTimeoutTextBox.Text = "60";
                
                UpdateProviderInfo(DatabaseProvider.Local);
                
                _logger.LogInformation("数据库配置界面初始化完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载数据库配置失败");
            }
        }

        /// <summary>
        /// 提供商选择变化
        /// </summary>
        private void ProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ProviderComboBox.SelectedItem is ComboBoxItem selectedItem)
            {
                var providerTag = selectedItem.Tag?.ToString();
                if (Enum.TryParse<DatabaseProvider>(providerTag, out var provider))
                {
                    UpdateProviderInfo(provider);
                    ApplyProviderDefaults(provider);
                }
            }
        }

        /// <summary>
        /// 更新提供商信息显示
        /// </summary>
        private void UpdateProviderInfo(DatabaseProvider provider)
        {
            var descriptions = new Dictionary<DatabaseProvider, string>
            {
                [DatabaseProvider.Local] = "本地PostgreSQL数据库，需要自己安装和维护PostgreSQL服务器。",
                [DatabaseProvider.ElephantSQL] = "ElephantSQL提供免费的PostgreSQL托管服务，免费层包含20MB存储空间，适合小项目测试。",
                [DatabaseProvider.Neon] = "Neon是现代化的PostgreSQL云服务，免费层提供3GB存储空间和优秀的性能。",
                [DatabaseProvider.Railway] = "Railway提供简单易用的PostgreSQL服务，免费层包含512MB存储空间。",
                [DatabaseProvider.Supabase] = "Supabase是开源的Firebase替代方案，免费层提供500MB PostgreSQL数据库。",
                [DatabaseProvider.Custom] = "自定义PostgreSQL配置，适合使用其他服务商或特殊需求的场景。"
            };

            if (descriptions.TryGetValue(provider, out var description))
            {
                ProviderDescriptionTextBlock.Text = description;
            }
        }

        /// <summary>
        /// 应用提供商默认配置
        /// </summary>
        private void ApplyProviderDefaults(DatabaseProvider provider)
        {
            switch (provider)
            {
                case DatabaseProvider.Local:
                    HostTextBox.Text = "localhost";
                    PortTextBox.Text = "5432";
                    UsernameTextBox.Text = "postgres";
                    EnableSslCheckBox.IsChecked = false;
                    break;
                    
                case DatabaseProvider.ElephantSQL:
                    HostTextBox.Text = ""; // 用户需要填写具体的主机地址
                    PortTextBox.Text = "5432";
                    EnableSslCheckBox.IsChecked = true;
                    break;
                    
                case DatabaseProvider.Neon:
                case DatabaseProvider.Railway:
                case DatabaseProvider.Supabase:
                    HostTextBox.Text = ""; // 用户需要填写具体的主机地址
                    PortTextBox.Text = "5432";
                    EnableSslCheckBox.IsChecked = true;
                    break;
                    
                case DatabaseProvider.Custom:
                    // 保持当前值
                    break;
            }
        }

        /// <summary>
        /// 测试数据库连接
        /// </summary>
        private async void TestConnection_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateInputs())
                return;

            try
            {
                ShowLoading(true, "正在测试数据库连接...");
                TestConnectionButton.IsEnabled = false;
                TestResultTextBlock.Text = "";

                var config = CreateDatabaseConfig();
                var serviceProvider = ((App)Application.Current).ServiceProvider;
                var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
                var postgreSqlLogger = loggerFactory.CreateLogger<PostgreSQLAuthService>();
                var testService = new PostgreSQLAuthService(config, postgreSqlLogger);
                
                var (success, message) = await testService.TestConnectionAsync();

                if (success)
                {
                    TestResultTextBlock.Text = $"✅ {message}";
                    TestResultTextBlock.Foreground = new SolidColorBrush(Colors.Green);
                    InitializeDatabaseButton.IsEnabled = true;
                }
                else
                {
                    TestResultTextBlock.Text = $"❌ 连接失败: {message}";
                    TestResultTextBlock.Foreground = new SolidColorBrush(Colors.Red);
                    InitializeDatabaseButton.IsEnabled = false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "测试数据库连接异常");
                TestResultTextBlock.Text = $"❌ 连接测试异常: {ex.Message}";
                TestResultTextBlock.Foreground = new SolidColorBrush(Colors.Red);
                InitializeDatabaseButton.IsEnabled = false;
            }
            finally
            {
                ShowLoading(false);
                TestConnectionButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// 初始化数据库
        /// </summary>
        private async void InitializeDatabase_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "确定要初始化数据库架构吗？\n这将创建所有必要的表和索引。",
                "确认初始化",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            try
            {
                ShowLoading(true, "正在初始化数据库架构...");
                InitializeDatabaseButton.IsEnabled = false;

                var config = CreateDatabaseConfig();
                var serviceProvider = ((App)Application.Current).ServiceProvider;
                var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
                var postgreSqlLogger = loggerFactory.CreateLogger<PostgreSQLAuthService>();
                var initService = new PostgreSQLAuthService(config, postgreSqlLogger);
                
                var success = await initService.InitializeDatabaseAsync();

                if (success)
                {
                    TestResultTextBlock.Text = "✅ 数据库架构初始化完成！可以开始使用PostgreSQL认证服务。";
                    TestResultTextBlock.Foreground = new SolidColorBrush(Colors.Green);
                }
                else
                {
                    TestResultTextBlock.Text = "❌ 数据库架构初始化失败，请检查日志。";
                    TestResultTextBlock.Foreground = new SolidColorBrush(Colors.Red);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "初始化数据库异常");
                TestResultTextBlock.Text = $"❌ 初始化异常: {ex.Message}";
                TestResultTextBlock.Foreground = new SolidColorBrush(Colors.Red);
            }
            finally
            {
                ShowLoading(false);
                InitializeDatabaseButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// 保存配置
        /// </summary>
        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateInputs())
                return;

            try
            {
                DatabaseConfig = CreateDatabaseConfig();
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存数据库配置异常");
                MessageBox.Show($"保存配置失败: {ex.Message}", "保存失败", 
                               MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 取消
        /// </summary>
        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        // 标题栏按钮命令处理（Qt6风格自绘）
        private void OnMinimizeWindow(object sender, ExecutedRoutedEventArgs e)
        {
            try { SystemCommands.MinimizeWindow(this); } catch { }
        }
        private void OnMaximizeWindow(object sender, ExecutedRoutedEventArgs e)
        {
            try { SystemCommands.MaximizeWindow(this); } catch { }
        }
        private void OnRestoreWindow(object sender, ExecutedRoutedEventArgs e)
        {
            try { SystemCommands.RestoreWindow(this); } catch { }
        }
        private void OnCloseWindow(object sender, ExecutedRoutedEventArgs e)
        {
            try { SystemCommands.CloseWindow(this); } catch { }
        }

        /// <summary>
        /// 处理超链接点击
        /// </summary>
        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = e.Uri.AbsoluteUri,
                    UseShellExecute = true
                });
                e.Handled = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"无法打开链接: {e.Uri}");
            }
        }

        /// <summary>
        /// 验证输入
        /// </summary>
        private bool ValidateInputs()
        {
            if (string.IsNullOrWhiteSpace(HostTextBox.Text))
            {
                MessageBox.Show("请输入主机地址。", "输入错误", 
                               MessageBoxButton.OK, MessageBoxImage.Warning);
                HostTextBox.Focus();
                return false;
            }

            if (string.IsNullOrWhiteSpace(DatabaseTextBox.Text))
            {
                MessageBox.Show("请输入数据库名。", "输入错误", 
                               MessageBoxButton.OK, MessageBoxImage.Warning);
                DatabaseTextBox.Focus();
                return false;
            }

            if (!int.TryParse(PortTextBox.Text, out var port) || port <= 0 || port > 65535)
            {
                MessageBox.Show("请输入有效的端口号 (1-65535)。", "输入错误", 
                               MessageBoxButton.OK, MessageBoxImage.Warning);
                PortTextBox.Focus();
                return false;
            }

            if (string.IsNullOrWhiteSpace(UsernameTextBox.Text))
            {
                MessageBox.Show("请输入用户名。", "输入错误", 
                               MessageBoxButton.OK, MessageBoxImage.Warning);
                UsernameTextBox.Focus();
                return false;
            }

            if (!int.TryParse(ConnectionTimeoutTextBox.Text, out var connTimeout) || connTimeout <= 0)
            {
                MessageBox.Show("请输入有效的连接超时时间。", "输入错误", 
                               MessageBoxButton.OK, MessageBoxImage.Warning);
                ConnectionTimeoutTextBox.Focus();
                return false;
            }

            if (!int.TryParse(CommandTimeoutTextBox.Text, out var cmdTimeout) || cmdTimeout <= 0)
            {
                MessageBox.Show("请输入有效的命令超时时间。", "输入错误", 
                               MessageBoxButton.OK, MessageBoxImage.Warning);
                CommandTimeoutTextBox.Focus();
                return false;
            }

            return true;
        }

        /// <summary>
        /// 创建数据库配置对象
        /// </summary>
        private PostgreSQLConfig CreateDatabaseConfig()
        {
            var selectedItem = (ComboBoxItem)ProviderComboBox.SelectedItem;
            var providerTag = selectedItem.Tag?.ToString();
            Enum.TryParse<DatabaseProvider>(providerTag, out var provider);

            return new PostgreSQLConfig
            {
                Host = HostTextBox.Text.Trim(),
                Port = int.Parse(PortTextBox.Text),
                Database = DatabaseTextBox.Text.Trim(),
                Username = UsernameTextBox.Text.Trim(),
                Password = PasswordBox.Password,
                UseSsl = EnableSslCheckBox.IsChecked == true,
                ConnectionTimeout = int.Parse(ConnectionTimeoutTextBox.Text),
                CommandTimeout = int.Parse(CommandTimeoutTextBox.Text),
                Provider = provider
            };
        }

        /// <summary>
        /// 显示加载状态
        /// </summary>
        private void ShowLoading(bool show, string message = "")
        {
            LoadingOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            LoadingText.Text = message;
        }
        private void ApplyLocalization()
        {
            var toEnglish = UEModManager.Services.LanguageManager.IsEnglish;
            var map = new System.Collections.Generic.Dictionary<string,string>
            {
                {"数据库配置","Database Configuration"},
                {"连接超时(秒):","Connection Timeout (s):"},
                {"命令超时(秒):","Command Timeout (s):"},
                {"🔗 测试连接","🔗 Test Connection"},
                {"🏗️ 初始化数据库","🏗️ Initialize Database"},
                {"免费PostgreSQL服务商推荐","Free PostgreSQL Providers"},
                {"保存配置","Save"},
                {"取消","Cancel"},
                {"正在连接...","Connecting..."}
            };
            UEModManager.Services.LocalizationHelper.Apply(this, toEnglish, map);
            this.Title = toEnglish ? "Database Configuration" : "数据库配置";
            if (LoadingText != null) LoadingText.Text = toEnglish ? "Connecting..." : "正在连接...";
        }

    }
}

