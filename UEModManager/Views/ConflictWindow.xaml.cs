using System.Windows;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;

using UEModManager.Infrastructure;
using UEModManager.Localization;
using UEModManager.Services.Conflict;
using UEModManager.ViewModels;

namespace UEModManager.Views;

/// <summary>
/// 冲突面板。
///
/// 面板里的每个动作点击即生效并重新部署，没有"应用"按钮 ——
/// 玩家做的是"这几个 MOD 我留下哪个"的取舍，不是编辑一份待提交的规则表。
/// 因此这里也不向调用方回传"有没有改过"：开关与重新部署都在
/// <see cref="MainViewModel"/> 的动作里完成，调用方无需再补一次。
/// </summary>
public partial class ConflictWindow : Window
{
    private readonly ConflictViewModel _vm;

    /// <summary>
    /// 面板的 ViewModel。
    ///
    /// 只为测试而存在的读数口：本窗口接线错一次就会让"禁用"打在另一个 MainViewModel 上
    /// （面板显示已禁用、主列表不动）。接线是构造期的事，没有别的运行期信号可见，
    /// 所以留这一个 internal 访问器给测试钉住它持有的就是调用方传进来的那个实例。
    /// </summary>
    internal ConflictViewModel ViewModelForTests => _vm;

    public ConflictWindow(
        ConflictModuleService conflictModule,
        MainViewModel main,
        ILogger<ConflictWindow> logger)
    {
        InitializeComponent();
        _vm = new ConflictViewModel(conflictModule, main, logger);
        DataContext = _vm;
        Loaded += OnLoaded;
    }

    /// <summary>
    /// 打开时定位到该 MOD 所在的冲突组（主列表"查看冲突"用）。
    /// 由调用方在 <c>ShowDialog</c> 之前设置 —— 构造走 DI，多的参数进不来。
    /// </summary>
    public string? FocusPackageKey { get; set; }

    private void OnLoaded(object sender, RoutedEventArgs e)
        => SafeEvent.Run(this, LoadAndFocusAsync, null, "冲突检测");

    /// <summary>
    /// 分析一次并整屏重画；带定位目标时把该 MOD 所在的组滚到可视区。
    ///
    /// 定位不做成"重新分析一遍再滚"：面板内容由启用集合决定，翻开两次报告之间
    /// 集合没变，多算一遍纯属浪费（真实仓库一次分析要读十几个 pak 的索引）。
    /// </summary>
    private async Task LoadAndFocusAsync()
    {
        await _vm.LoadAsync();
        if (FocusPackageKey is not { Length: > 0 } focusKey) return;

        var index = _vm.Groups
            .Select((group, i) => (group, i))
            .Where(item => item.group.Group.Members.Any(m =>
                string.Equals(m.PackageKey, focusKey, StringComparison.OrdinalIgnoreCase)))
            .Select(item => item.i)
            .DefaultIfEmpty(-1)
            .First();
        if (index < 0) return;

        // 模板要先生成容器才有可滚动的行；布局跑完再找。
        UpdateLayout();
        if (GroupList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement row)
            row.BringIntoView();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 一键处理：先确认，再让面板按默认规则批量禁用。
    ///
    /// 确认信息里列出前 5 个会被禁用的 MOD —— 这是唯一一处会一次动到多个 MOD 的按钮，
    /// 用户点下去之前应该知道具体关掉哪些，而不是只看到一个数字。
    /// </summary>
    private void ResolveAll_Click(object sender, RoutedEventArgs e)
        => SafeEvent.Run(this, async () =>
        {
            if (_vm.PlanDefaultResolution().Count == 0) return;

            if (CyberMessageBox.Show(this, _vm.BuildResolutionConfirmation(), UiText.Get("一键处理"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            await _vm.ResolveAllAsync();
        }, null, "一键处理冲突");
}
