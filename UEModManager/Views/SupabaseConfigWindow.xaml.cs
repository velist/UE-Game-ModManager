using System;
using System.Windows;
using System.Windows.Controls;
using System.Net.Http;
using System.Threading.Tasks;

namespace UEModManager.Views
{
    public partial class SupabaseConfigWindow : Window
    {
        private readonly HttpClient _httpClient = new HttpClient();
        
        public string SupabaseUrl { get; set; }
        public string SupabaseAnonKey { get; set; }
        public bool UseDemoMode { get; set; }
        public bool ConfigurationSaved { get; private set; }

        public SupabaseConfigWindow()
        {
            InitializeComponent();
            ApplyLocalization();
            UEModManager.Services.LanguageManager.LanguageChanged += _ => { Dispatcher.Invoke(ApplyLocalization); };

            DataContext = this;
            LoadCurrentConfig();
        }

        private void LoadCurrentConfig()
        {
            // 尝试加载现有配置
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var configFile = System.IO.Path.Combine(appDataPath, "UEModManager", "supabase_config.json");
            
            if (System.IO.File.Exists(configFile))
            {
                try
                {
                    var json = System.IO.File.ReadAllText(configFile);
                    dynamic config = Newtonsoft.Json.JsonConvert.DeserializeObject(json);
                    
                    SupabaseUrl = config?.url ?? "";
                    SupabaseAnonKey = config?.anonKey ?? "";
                    UseDemoMode = config?.demoMode ?? true;
                    
                    UrlTextBox.Text = SupabaseUrl;
                    AnonKeyTextBox.Text = SupabaseAnonKey;
                    DemoModeCheckBox.IsChecked = UseDemoMode;
                }
                catch
                {
                    // 使用默认值
                    UseDemoMode = true;
                    DemoModeCheckBox.IsChecked = true;
                }
            }
            else
            {
                // 默认使用演示模式
                UseDemoMode = true;
                DemoModeCheckBox.IsChecked = true;
            }
            
            UpdateUIState();
        }

        private void DemoModeCheckBox_Click(object sender, RoutedEventArgs e)
        {
            UseDemoMode = DemoModeCheckBox.IsChecked == true;
            UpdateUIState();
        }

        private void UpdateUIState()
        {
            var isRealMode = !UseDemoMode;
            UrlTextBox.IsEnabled = isRealMode;
            AnonKeyTextBox.IsEnabled = isRealMode;
            TestButton.IsEnabled = isRealMode && !string.IsNullOrWhiteSpace(UrlTextBox.Text) && !string.IsNullOrWhiteSpace(AnonKeyTextBox.Text);
            
            if (UseDemoMode)
            {
                StatusTextBlock.Text = "演示模式：将使用本地存储，无需网络连接";
                StatusTextBlock.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green);
            }
            else
            {
                StatusTextBlock.Text = "真实模式：需要有效的Supabase配置";
                StatusTextBlock.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Blue);
            }
        }

        private async void TestButton_Click(object sender, RoutedEventArgs e)
        {
            TestButton.IsEnabled = false;
            StatusTextBlock.Text = "正在测试连接...";
            StatusTextBlock.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Orange);

            try
            {
                // 测试Supabase连接
                var testUrl = UrlTextBox.Text.TrimEnd('/') + "/rest/v1/";
                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.Add("apikey", AnonKeyTextBox.Text);
                _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {AnonKeyTextBox.Text}");

                var response = await _httpClient.GetAsync(testUrl);
                
                if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    StatusTextBlock.Text = "✓ 连接成功！配置有效。";
                    StatusTextBlock.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green);
                }
                else
                {
                    StatusTextBlock.Text = $"✗ 连接失败：HTTP {response.StatusCode}";
                    StatusTextBlock.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red);
                }
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"✗ 连接失败：{ex.Message}";
                StatusTextBlock.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red);
            }
            finally
            {
                TestButton.IsEnabled = true;
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 保存配置
                var config = new
                {
                    url = UseDemoMode ? "" : UrlTextBox.Text,
                    anonKey = UseDemoMode ? "" : AnonKeyTextBox.Text,
                    demoMode = UseDemoMode,
                    savedAt = DateTime.Now
                };

                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var configDir = System.IO.Path.Combine(appDataPath, "UEModManager");
                
                if (!System.IO.Directory.Exists(configDir))
                    System.IO.Directory.CreateDirectory(configDir);
                
                var configFile = System.IO.Path.Combine(configDir, "supabase_config.json");
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(config, Newtonsoft.Json.Formatting.Indented);
                System.IO.File.WriteAllText(configFile, json);

                ConfigurationSaved = true;
                MessageBox.Show(UseDemoMode ? 
                    "已保存为演示模式，将使用本地存储。" : 
                    "Supabase配置已保存！", 
                    "保存成功", 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Information);
                    
                DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存配置失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void TextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateUIState();
        }

        // 标题栏按钮命令处理（Qt6风格自绘）
        private void OnMinimizeWindow(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
        {
            try { SystemCommands.MinimizeWindow(this); } catch { }
        }
        private void OnMaximizeWindow(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
        {
            try { SystemCommands.MaximizeWindow(this); } catch { }
        }
        private void OnRestoreWindow(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
        {
            try { SystemCommands.RestoreWindow(this); } catch { }
        }
        private void OnCloseWindow(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
        {
            try { SystemCommands.CloseWindow(this); } catch { }
        }
        private void ApplyLocalization()
        {
            var toEnglish = UEModManager.Services.LanguageManager.IsEnglish;
            var map = new System.Collections.Generic.Dictionary<string,string>
            {
                {"Supabase配置向导","Supabase Configuration Wizard"},
                {"配置云端认证服务","Configure Cloud Authentication"},
                {"使用演示模式（无需配置，仅本地存储）","Use demo mode (no config, local only)"},
                {"演示模式将使用本地数据库存储用户信息，不需要网络连接。","Demo mode stores users locally. No network required."},
                {"Supabase配置（真实云端服务）","Supabase Configuration (Cloud)"},
                {"项目URL：","Project URL:"},
                {"Anon Key：","Anon Key:"},
                {"测试连接","Test Connection"},
                {"保存","Save"},
                {"取消","Cancel"},
                {"演示模式：将使用本地存储，无需网络连接","Demo mode: Local storage, no network"},
                {"真实模式：需要有效的Supabase配置","Cloud mode: Valid Supabase config required"},
                {"正在测试连接...","Testing..."},
                {"✓ 连接成功！配置有效。","✓ Connected! Configuration is valid."},
                {"✗ 连接失败：","✗ Failed: "}
            };
            UEModManager.Services.LocalizationHelper.Apply(this, toEnglish, map);
            this.Title = toEnglish ? "Supabase Configuration Wizard" : "Supabase配置向导";
        }

    }
}

