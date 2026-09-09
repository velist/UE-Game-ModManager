using System.Globalization;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Data;
using UEModManager.Localization;
using UEModManager.Models;
using UEModManager.Services;
using UEModManager.Tests.Themes;
using UEModManager.Views;

namespace UEModManager.Tests.Views;

// Language, WPF bindings and UiPreferences are process-wide; never change them
// concurrently with the existing theme or preference fixtures.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LanguageIntegrationCollection
{
    public const string Name = "Language integration";
}

[Collection(LanguageIntegrationCollection.Name)]
public sealed class LanguageIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uemm_language_" + Guid.NewGuid().ToString("N"));
    private readonly bool _previous = LanguageManager.IsEnglish;

    public LanguageIntegrationTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("en-US", true)]
    [InlineData("en-GB", true)]
    [InlineData("de-DE", true)]
    [InlineData("zh-CN", false)]
    [InlineData("zh-TW", false)]
    public void FreshInstallUsesSystemLanguage(string culture, bool english)
    {
        using var scope = UiPreferences.OverrideConfigPathForTests(Path.Combine(_root, "ui.json"));
        LanguageManager.Initialize(CultureInfo.GetCultureInfo(culture));
        Assert.Equal(english, LanguageManager.IsEnglish);
    }

    [Theory]
    [InlineData("en-US", "zh-CN", true)]
    [InlineData("zh-CN", "en-US", false)]
    public void InstallerLanguageAppliesOnlyBeforeThePlayerSavesAPreference(string installed, string system, bool english)
    {
        using var scope = UiPreferences.OverrideConfigPathForTests(Path.Combine(_root, "ui.json"));
        LanguageManager.Initialize(CultureInfo.GetCultureInfo(system), installed);
        Assert.Equal(english, LanguageManager.IsEnglish);
        LanguageManager.SaveAndSetEnglish(!english);
        LanguageManager.Initialize(CultureInfo.GetCultureInfo(system), installed);
        Assert.Equal(!english, LanguageManager.IsEnglish);
    }

    [Fact]
    public void ProfileTemplateCountsSwitchBothWaysWithoutChangingTheCounts()
    {
        ThemeResourceLoader.RunOnSta(() =>
        {
            var window = ViewResourceLoader.Load(AppSource, "Views/ProfileManagerWindow.xaml");
            try
            {
                var template = Assert.IsType<DataTemplate>(window.Resources["ProfileCardTemplate"]);
                var card = (FrameworkElement)template.LoadContent();
                card.Resources = window.Resources;
                card.DataContext = new { Name = "Player profile", IsActive = true, Description = "Example", ModCount = 3, PluginCount = 2, ConfigCount = 1 };
                foreach (var english in new[] { true, false, true })
                {
                    LanguageManager.SetEnglish(english);
                    card.Measure(new Size(330, 200));
                    card.Arrange(new Rect(0, 0, 330, 200));
                    card.UpdateLayout();
                    var labels = Descendants(card).OfType<TextBlock>().Select(t => t.Text).ToArray();
                    Assert.Contains(english ? "Plugins: 2" : "2 插件", labels);
                    Assert.Contains(english ? "Configs: 1" : "1 配置", labels);
                    Assert.Contains("Player profile", labels);
                }
            }
            finally { window.Close(); }
            return true;
        });
    }

    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("Views/AccountSettingsWindow.xaml")]
    [InlineData("Views/AddCustomGameDialog.xaml")]
    [InlineData("Views/AdminDashboardWindow.xaml")]
    [InlineData("Views/ConflictResultWindow.xaml")]
    [InlineData("Views/CyberInputDialog.xaml")]
    [InlineData("Views/CyberMessageBox.xaml")]
    [InlineData("Views/DonateWindow.xaml")]
    [InlineData("Views/GamePathDialog.xaml")]
    [InlineData("Views/ImportConfirmDialog.xaml")]
    [InlineData("Views/ImportDialog.xaml")]
    [InlineData("Views/LaunchCenterWindow.xaml")]
    [InlineData("Views/LoginWindow.xaml")]
    [InlineData("Views/ManagementCenterWindow.xaml")]
    [InlineData("Views/ModDetailWindow.xaml")]
    [InlineData("Views/ProfileManagerWindow.xaml")]
    [InlineData("Views/RepositoryRelocationWindow.xaml")]
    [InlineData("Views/RepositorySetupWindow.xaml")]
    [InlineData("Views/SettingsWindow.xaml")]
    public void EveryWindowLoadsInEnglishAndSwitchesItsTitleBack(string path)
    {
        ThemeResourceLoader.RunOnSta(() =>
        {
            LanguageManager.SetEnglish(false);
            var window = ViewResourceLoader.Load(AppSource, path);
            try
            {
                var chinese = window.Title;
                LanguageManager.SetEnglish(true);
                ViewResourceLoader.Layout(window);
                AssertEnglish(window.Title);
                LanguageManager.SetEnglish(false);
                Assert.Equal(chinese, window.Title);
            }
            finally { window.Close(); }
            return true;
        });
    }

    [Theory]
    [InlineData("ModsCardView", "服装", "Outfits")]
    [InlineData("ModsListView", "未分类", "Uncategorized")]
    [InlineData("ModsCardView", "取消", "取消")]
    [InlineData("ModsListView", "玩家的分类", "玩家的分类")]
    public void ModTemplatesSwitchBuiltInLabelsButPreservePlayerData(string listName, string category, string englishCategory)
    {
        ThemeResourceLoader.RunOnSta(() =>
        {
            var window = ViewResourceLoader.Load(AppSource, "MainWindow.xaml");
            try
            {
                var list = (ItemsControl)window.FindName(listName);
                var item = (FrameworkElement)list.ItemTemplate.LoadContent();
                item.Resources = window.Resources;
                var mod = new ModInfo { Name = "玩家的 MOD", RealName = "config.ini", IsEnabled = true, Categories = new() { category } };
                item.DataContext = mod;
                foreach (var english in new[] { true, false, true })
                {
                    LanguageManager.SetEnglish(english);
                    item.Measure(new Size(700, 300));
                    item.Arrange(new Rect(0, 0, 700, 300));
                    item.UpdateLayout();
                    var labels = Descendants(item).OfType<TextBlock>().Select(t => t.Text).ToArray();
                    Assert.Contains(english ? englishCategory : category, labels);
                    Assert.Contains("玩家的 MOD", labels);
                    Assert.Contains(listName == "ModsCardView" ? (english ? "Config" : "配置") : (english ? "Enabled" : "已启用"), labels);
                    Assert.Equal(category, mod.PrimaryCategory);
                }
                // Data changes still propagate after multiple language changes.
                mod.Categories = new() { "武器" };
                mod.IsEnabled = false;
                item.UpdateLayout();
                var updated = Descendants(item).OfType<TextBlock>().Select(t => t.Text).ToArray();
                Assert.Contains("Weapons", updated);
                if (listName == "ModsListView") Assert.Contains("Disabled", updated);
            }
            finally { window.Close(); }
            return true;
        });
    }

    [Theory]
    [InlineData(1024, true)]
    [InlineData(1280, true)]
    [InlineData(1440, true)]
    [InlineData(1024, false)]
    public void MainToolbarFitsTheSupportedWindowWidths(int width, bool english)
    {
        ThemeResourceLoader.RunOnSta(() =>
        {
            LanguageManager.SetEnglish(english);
            var window = ViewResourceLoader.Load(AppSource, "MainWindow.xaml");
            try
            {
                window.Width = width;
                var root = ViewResourceLoader.Layout(window);
                foreach (var name in new[] { "ToolbarActions", "SidebarLogoText", "SearchBox", "LaunchGameText" })
                {
                    var element = (FrameworkElement)window.FindName(name);
                    var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
                    Assert.True(bounds.Left >= 0 && bounds.Right <= root.ActualWidth + 1,
                        $"{name} extends beyond {width}px: {bounds}");
                }
                var logo = (FrameworkElement)window.FindName("SidebarLogoText");
                var parent = (FrameworkElement)logo.Parent;
                var logoBounds = logo.TransformToAncestor(parent).TransformBounds(new Rect(logo.RenderSize));
                Assert.True(logoBounds.Right <= parent.ActualWidth + 1);
            }
            finally { window.Close(); }
            return true;
        });
    }

    [Theory]
    [InlineData(true, "zh-CN")]
    [InlineData(false, "en-US")]
    public void SavedPreferenceWinsAfterReload(bool english, string systemCulture)
    {
        var config = Path.Combine(_root, "ui.json");
        using (UiPreferences.OverrideConfigPathForTests(config))
        {
            UiPreferences.SaveRepositoryRoot(Path.Combine(_root, "玩家的 MOD"));
            LanguageManager.SaveAndSetEnglish(english);
        }
        LanguageManager.SetEnglish(!english);
        using (UiPreferences.OverrideConfigPathForTests(config))
        {
            LanguageManager.Initialize(CultureInfo.GetCultureInfo(systemCulture));
            Assert.Equal(english, LanguageManager.IsEnglish);
            Assert.Equal(Path.Combine(_root, "玩家的 MOD"), UiPreferences.LoadRepositoryRoot());
        }
    }

    [Fact]
    public void FailedSaveDoesNotChangeLanguageOrNotifyWindows()
    {
        var blocker = Path.Combine(_root, "file");
        File.WriteAllText(blocker, "keep");
        using var scope = UiPreferences.OverrideConfigPathForTests(Path.Combine(blocker, "ui.json"));
        LanguageManager.SetEnglish(false);
        var notifications = 0;
        void Changed(bool _) => notifications++;
        LanguageManager.LanguageChanged += Changed;
        try
        {
            Assert.ThrowsAny<Exception>(() => LanguageManager.SaveAndSetEnglish(true));
            Assert.False(LanguageManager.IsEnglish);
            Assert.Equal(0, notifications);
        }
        finally { LanguageManager.LanguageChanged -= Changed; }
    }

    [Fact]
    public void LoginToggleUpdatesGlobalLanguagePreservesInputAndSurvivesReopening()
    {
        var config = Path.Combine(_root, "ui.json");
        using var scope = UiPreferences.OverrideConfigPathForTests(config);
        ThemeResourceLoader.RunOnSta(() =>
        {
            using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite("Data Source=:memory:").Options);
            using var client = new HttpClient(new NoNetworkHandler());
            using var worker = new WorkerEmailService(client, NullLogger<WorkerEmailService>.Instance);
            var auth = new LocalAuthService(db, NullLogger<LocalAuthService>.Instance);
            var otp = new CustomOtpService(NullLogger<CustomOtpService>.Instance, worker);
            LanguageManager.SetEnglish(false);
            var window = new LoginWindow(auth, otp, NullLogger<LoginWindow>.Instance, ResourcesFor("Views/LoginWindow.xaml"));
            try
            {
                Control<TextBox>(window, "EmailTextBox").Text = "player@example.test";
                Control<TextBox>(window, "OtpTextBox").Text = "123456";
                Control<StackPanel>(window, "OtpInputPanel").Visibility = Visibility.Visible;
                typeof(LoginWindow).GetMethod("StartCountdown", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, new object[] { 30 });
                for (var i = 0; i < 3; i++)
                {
                    Click(window, "LanguageToggleButton");
                    Assert.True(LanguageManager.IsEnglish);
                    Assert.Equal("Resend (30s)", Control<Button>(window, "SendOtpButton").Content);
                    AssertEnglish(Control<TextBlock>(window, "EmailLabel").Text);
                    Assert.Equal("player@example.test", Control<TextBox>(window, "EmailTextBox").Text);
                    Assert.Equal("123456", Control<TextBox>(window, "OtpTextBox").Text);
                    Click(window, "LanguageToggleButton");
                    Assert.False(LanguageManager.IsEnglish);
                    Assert.Equal("邮箱地址", Control<TextBlock>(window, "EmailLabel").Text);
                }
                Click(window, "LanguageToggleButton");
                Assert.True(UiPreferences.TryLoadEnglish(out var saved) && saved);
            }
            finally { window.Close(); }
            LanguageManager.SetEnglish(false);
            LanguageManager.Initialize(CultureInfo.GetCultureInfo("zh-CN"));
            var reopened = new LoginWindow(auth, otp, NullLogger<LoginWindow>.Instance, ResourcesFor("Views/LoginWindow.xaml"));
            try
            {
                AssertEnglish(reopened.Title);
                Assert.Equal("Send code", Control<Button>(reopened, "SendOtpButton").Content);
            }
            finally { reopened.Close(); }
            return true;
        });
    }

    [Fact]
    public void SettingsLanguageSwitchPreservesUnsavedPathsAndShowsCurrentVersion()
    {
        using var scope = UiPreferences.OverrideConfigPathForTests(Path.Combine(_root, "ui.json"));
        ThemeResourceLoader.RunOnSta(() =>
        {
            var config = new GameConfigService(NullLogger<GameConfigService>.Instance, Path.Combine(_root, "game.json"));
            config.Config.GameName = "黑神话·悟空";
            config.Config.GamePath = Path.Combine(_root, "原游戏");
            LanguageManager.SetEnglish(false);
            var window = new SettingsWindow(config, resources: ResourcesFor("Views/SettingsWindow.xaml"));
            try
            {
                var editedPath = Path.Combine(_root, "未保存的游戏路径");
                Control<TextBox>(window, "GamePathTextBox").Text = editedPath;
                for (var i = 0; i < 3; i++)
                {
                    Control<ComboBox>(window, "LanguageComboBox").SelectedIndex = 1;
                    Assert.True(LanguageManager.IsEnglish);
                    AssertEnglish(window.Title);
                    Assert.Contains("Black Myth: Wukong", Control<TextBlock>(window, "CurrentGameHint").Text);
                    Assert.Equal(editedPath, Control<TextBox>(window, "GamePathTextBox").Text);
                    Control<ComboBox>(window, "LanguageComboBox").SelectedIndex = 0;
                    Assert.Equal("系统设置", window.Title);
                    Assert.Equal(editedPath, Control<TextBox>(window, "GamePathTextBox").Text);
                }
                Assert.Contains(typeof(App).Assembly.GetName().Version!.ToString(3), Control<TextBlock>(window, "VersionText").Text);
                Assert.Equal("黑神话·悟空", config.CurrentGameName);
                Assert.Equal(Path.Combine(_root, "原游戏"), config.CurrentGamePath);
            }
            finally { window.Close(); }
            return true;
        });
    }

    [Fact]
    public void TranslatingLabelsDoesNotTranslatePlayerData()
    {
        LanguageManager.SetEnglish(true);
        Assert.Equal("Stellar Blade", GameDisplayNames.For("剑星"));
        Assert.Equal("玩家自定义游戏", GameDisplayNames.For("玩家自定义游戏"));
        Assert.Equal("取消", new CategoryItem { Name = "取消", IsCustom = true }.LocalizedDisplayText);
        Assert.Equal("我的武器", new CategoryItem { Name = "武器", DisplayName = "我的武器" }.LocalizedDisplayText);
        Assert.Equal("Weapons", new CategoryItem { Name = "武器" }.LocalizedDisplayText);
        Assert.Equal("取消", CategoryDisplayNames.For("取消"));
        var playerText = "取消";
        Assert.Equal("Could not save: 取消", UiText.Interpolate($"保存失败：{playerText}"));
    }

    [Fact]
    public void AllEnglishTemplatesHaveMatchingFormatArguments()
    {
        using var stream = typeof(UiText).Assembly.GetManifestResourceStream("UEModManager.Localization.en-US.json")!;
        var catalog = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        foreach (var (source, translation) in catalog)
        {
            Assert.False(string.IsNullOrWhiteSpace(translation), source);
            static int[] Arguments(string value) => Regex.Matches(value, @"(?<!\{)\{(\d+)(?:[,}:])")
                .Select(m => int.Parse(m.Groups[1].Value)).Distinct().Order().ToArray();
            Assert.Equal(Arguments(source), Arguments(translation));
        }
    }

    [Fact]
    public void ExplicitXamlTranslationsHaveCatalogEntries()
    {
        var files = Directory.GetFiles(Path.Combine(AppSource, "Views"), "*.xaml")
            .Prepend(Path.Combine(AppSource, "MainWindow.xaml"));
        foreach (var path in files)
        {
            var document = XDocument.Load(path);
            foreach (var attribute in document.Descendants().Attributes())
            {
                var match = Regex.Match(attribute.Value, @"^\{loc:Text '(.+)'\}$", RegexOptions.Singleline);
                if (!match.Success) continue;
                var source = match.Groups[1].Value;
                Assert.True(UiText.HasEnglish(source), $"Missing English translation in {Path.GetFileName(path)}: {source}");
            }
        }
    }

    internal static string AppSource
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "UEModManager.sln"))) dir = dir.Parent;
            return Path.Combine(dir!.FullName, "UEModManager");
        }
    }

    private static ResourceDictionary ResourcesFor(string path)
    {
        var view = ViewResourceLoader.Load(AppSource, path);
        var resources = view.Resources;
        view.Close();
        return resources;
    }

    private static T Control<T>(Window window, string name) where T : FrameworkElement
        => Assert.IsType<T>(window.FindName(name));
    private static void Click(Window window, string name)
        => Control<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void AssertEnglish(string text) => Assert.DoesNotMatch("[\\u4e00-\\u9fff]", text);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Language tests must not send email or access the network.");
    }

    public void Dispose()
    {
        LanguageManager.SetEnglish(_previous);
        Directory.Delete(_root, true);
    }
}
