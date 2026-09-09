using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Services;
using UEModManager.Services.Paths;
using UEModManager.Tests.Themes;
using UEModManager.Views;

namespace UEModManager.Tests.Views;

[Collection(ThemeResourceCollection.Name)]
public sealed class RepositorySetupWindowTests : IDisposable
{
    private const long GiB = 1024L * 1024 * 1024;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uemm_setup_window_" + Guid.NewGuid().ToString("N"));
    private readonly MemoryPreferences _preferences = new();

    public RepositorySetupWindowTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void SelectingAcrossGroupsAndThenChoosingFolderKeepsOneAccurateDestination()
        => OnWindow(new[] { Drive("main", 100 * GiB, 500 * GiB), Drive("small", 40 * 1024L * 1024, 96 * 1024L * 1024) }, window =>
        {
            Control<ToggleButton>(window, "SmallVolumesToggle").IsChecked = true;
            ViewResourceLoader.Layout(window);
            var radios = Descendants<RadioButton>((FrameworkElement)window.Content).ToArray();
            Assert.Equal(2, radios.Length);
            Assert.True(radios[0].IsChecked);

            radios[1].IsChecked = true;
            Assert.False(radios[0].IsChecked);
            Assert.Equal(Path.Combine(_root, "small"), DisplayedPath(window));

            var custom = Path.Combine(_root, "chosen folder");
            window.SelectFolder(custom);
            Assert.All(radios, radio => Assert.False(radio.IsChecked));
            Assert.Equal(custom, DisplayedPath(window));
            Assert.True(Control<Button>(window, "ConfirmButton").IsEnabled);
            Assert.Null(_preferences.RepositoryRoot);

            radios[0].IsChecked = true;
            Assert.Equal(Path.Combine(_root, "main"), DisplayedPath(window));
            Assert.Null(_preferences.RepositoryRoot);
        });

    [Fact]
    public void SmallVolumeGroupingUsesTotalCapacityAndKeepsUnknownCapacityVisible()
        => OnWindow(new[]
        {
            Drive("nearly-full-large-drive", 1024, 200 * GiB),
            Drive("unknown", null, null),
            Drive("small", 20 * 1024L * 1024, 96 * 1024L * 1024),
            Drive("full", 0, 4 * 1024L * 1024)
        }, window =>
        {
            var main = Rows(window, "DriveList");
            var small = Rows(window, "SmallDriveList");
            Assert.Equal(2, main.Length);
            Assert.Equal(2, small.Length);
            Assert.True(main[0].IsWarning);
            Assert.Equal("未知", main[1].FreeText);
            Assert.True(main[1].IsSelectable);
            Assert.False(Control<ToggleButton>(window, "SmallVolumesToggle").IsChecked);
            Assert.False(small[1].IsSelectable);
            small[1].IsSelected = true;
            Assert.False(small[1].IsSelected);
        });

    [Fact]
    public void WhenOnlySmallVolumesExistTheyAreExpandedAndNothingIsRecommended()
        => OnWindow(new[] { Drive("small", 40 * 1024L * 1024, 96 * 1024L * 1024) }, window =>
        {
            Assert.Empty(Rows(window, "DriveList"));
            Assert.True(Control<ToggleButton>(window, "SmallVolumesToggle").IsChecked);
            Assert.False(Control<Button>(window, "ConfirmButton").IsEnabled);
        });

    [Fact]
    public void InvalidCustomFolderDoesNotKeepThePreviouslyValidSelection()
        => OnWindow(new[] { Drive("main", 100 * GiB, 500 * GiB) }, window =>
        {
            window.SelectFolder("relative-folder");
            Assert.False(Rows(window, "DriveList")[0].IsSelected);
            Assert.False(Control<Button>(window, "ConfirmButton").IsEnabled);
            Assert.False(Control<Button>(window, "CopyPathButton").IsEnabled);
            Assert.NotEmpty(Control<ItemsControl>(window, "IssueList").Items);
            Assert.Null(_preferences.RepositoryRoot);
        });

    [Fact]
    public void ConfirmRechecksTheDestinationAndDoesNotSilentlySaveAChangedPath()
        => OnWindow(new[] { Drive("main", 100 * GiB, 500 * GiB) }, window =>
        {
            var custom = Path.Combine(_root, "chosen");
            Directory.CreateDirectory(custom);
            window.SelectFolder(custom);
            File.WriteAllText(Path.Combine(custom, "existing-file.txt"), "keep");

            Control<Button>(window, "ConfirmButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Null(_preferences.RepositoryRoot);
            Assert.Equal(0, _preferences.PromptedWrites);
            Assert.Equal(Path.Combine(custom, "UEModManager", "Repository"), DisplayedPath(window));
            Assert.Contains("再次确认", Control<TextBlock>(window, "FeedbackText").Text);
            Assert.Equal("keep", File.ReadAllText(Path.Combine(custom, "existing-file.txt")));
        });

    private RepositoryDriveOption Drive(string name, long? available, long? total)
        => new(Path.Combine(_root, name), name, RepositoryVolumeKind.Fixed, available, total, false);

    private void OnWindow(IReadOnlyList<RepositoryDriveOption> drives, Action<RepositorySetupWindow> check)
    {
        ThemeResourceLoader.RunOnSta(() =>
        {
            var paths = new RepositorySetupPaths(
                Path.Combine(_root, "default"), Path.Combine(_root, "legacy"),
                Path.Combine(_root, "config.json"), Path.Combine(_root, "install", "config.json"),
                Path.Combine(_root, "install", "Data"), Path.Combine(_root, "install", "Backups"),
                Path.Combine(_root, "install"));
            var service = new RepositorySetupService(NullLogger<RepositorySetupService>.Instance,
                new RepositorySetupEnvironment(paths, _preferences));
            var window = new RepositorySetupWindow(service, logger: null, drives);
            // 实际程序从 App 取得主题；测试不创建全局 Application，在窗口挂同一字典。
            window.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/UEModManager;component/Themes/CyberDarkTheme.xaml")
            });
            try { check(window); }
            finally { window.Close(); }
            Assert.Equal(1, _preferences.PromptedWrites);
            Assert.Null(_preferences.RepositoryRoot);
            return true;
        });
    }

    private static T Control<T>(Window window, string name) where T : FrameworkElement
        => Assert.IsType<T>(window.FindName(name));

    private static string DisplayedPath(Window window)
    {
        var text = Control<TextBlock>(window, "ResolvedPathText");
        return new System.Windows.Documents.TextRange(text.ContentStart, text.ContentEnd).Text;
    }

    private static RepositoryDriveRow[] Rows(Window window, string name)
        => Control<ItemsControl>(window, name).Items.Cast<RepositoryDriveRow>().ToArray();

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private sealed class MemoryPreferences : IRepositorySetupPreferences
    {
        public string? RepositoryRoot { get; private set; }
        public int PromptedWrites { get; private set; }
        public string? LoadRepositoryRoot() => RepositoryRoot;
        public void SaveRepositoryRoot(string? path) => RepositoryRoot = path;
        public bool LoadPrompted() => PromptedWrites > 0;
        public void SavePrompted() => PromptedWrites++;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
