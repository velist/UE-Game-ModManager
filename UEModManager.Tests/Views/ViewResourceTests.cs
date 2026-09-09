using System.Windows;
using System.Windows.Controls;
using UEModManager.Tests.Themes;

namespace UEModManager.Tests.Views;

[Collection(ThemeResourceCollection.Name)]
public sealed class ViewResourceTests
{
    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("Views/ManagementCenterWindow.xaml")]
    [InlineData("Views/SettingsWindow.xaml")]
    [InlineData("Views/ImportConfirmDialog.xaml")]
    [InlineData("Views/ConflictResultWindow.xaml")]
    [InlineData("Views/AdminDashboardWindow.xaml")]
    [InlineData("Views/RepositorySetupWindow.xaml")]
    public void ViewsAndModTemplates_ResolveTheirActualResources(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "UEModManager.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);

        ThemeResourceLoader.RunOnSta(() =>
        {
            var window = ViewResourceLoader.Load(Path.Combine(directory.FullName, "UEModManager"), relativePath);
            try
            {
                ViewResourceLoader.Layout(window);
                if (relativePath == "MainWindow.xaml")
                {
                    var cards = Assert.IsAssignableFrom<ItemsControl>(window.FindName("ModsCardView"));
                    // WPF 会延迟实例化模板；只载入空窗口抓不到 MOD 卡片里的缺失转换器。
                    var card = Assert.IsAssignableFrom<FrameworkElement>(cards.ItemTemplate.LoadContent());
                    card.Measure(new Size(280, 340));
                    card.Arrange(new Rect(0, 0, 280, 340));
                }
                return true;
            }
            finally { window.Close(); }
        });
    }
}
