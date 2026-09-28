using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using UEModManager.Localization;
using UEModManager.Models;
using UEModManager.Services.Conflict;
using UEModManager.Services.Conflict.Model;

namespace UEModManager.ViewModels;

/// <summary>
/// 冲突面板的视图模型。
///
/// 面板只表达一件事：**哪几个 MOD 改到了同一批资源，我该留哪个**。
/// 一个组 = 一个决策；按钮上写 MOD 名，不写"胜者/败者"，也不用先"应用"。
///
/// 每次动作之后整份报告重新分析并整屏重画 —— 面板内容完全由"当前启用集合"决定，
/// 关掉一个 MOD 会让别的组一起变，局部更新必然对不上。
/// </summary>
public sealed partial class ConflictViewModel : ObservableObject
{
    private readonly ConflictModuleService _conflictModule;
    private readonly MainViewModel _main;
    private readonly ILogger _logger;

    public ConflictViewModel(
        ConflictModuleService conflictModule,
        MainViewModel main,
        ILogger logger)
    {
        _conflictModule = conflictModule;
        _main = main;
        _logger = logger;
    }

    /// <summary>标题下方的统计行。</summary>
    [ObservableProperty]
    private string _headerText = UiText.Get("冲突检测");

    /// <summary>动作完成后的一行反馈（"已禁用 X 并重新部署"）。</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>没有任何组 / 重复 / 无法分析时显示大字空状态。</summary>
    [ObservableProperty]
    private bool _isEmpty = true;

    public IReadOnlyList<ConflictGroupItemViewModel> Groups { get; private set; } = [];
    public IReadOnlyList<DuplicateItemViewModel> Duplicates { get; private set; } = [];
    public IReadOnlyList<SharedComponentItemViewModel> SharedComponents { get; private set; } = [];
    public IReadOnlyList<UnanalyzableItemViewModel> Unanalyzable { get; private set; } = [];

    public bool HasGroups => Groups.Count > 0;
    public bool HasDuplicates => Duplicates.Count > 0;
    public bool HasSharedComponents => SharedComponents.Count > 0;
    public bool HasUnanalyzable => Unanalyzable.Count > 0;

    /// <summary>重新分析并重画整个面板。动作按钮在构造时就把委托挂好了。</summary>
    public async Task LoadAsync()
    {
        var report = await _conflictModule.AnalyzeAsync();

        Groups = report.Groups
            .Select(g => new ConflictGroupItemViewModel(g, DisableAsync, KeepOnlyAsync))
            .ToList();
        Duplicates = report.Duplicates
            .SelectMany(d => d.Copies.Select(c => new DuplicateItemViewModel(d.Primary, c, DeleteDuplicateAsync)))
            .ToList();
        SharedComponents = report.SharedComponents
            .Select(c => new SharedComponentItemViewModel(c, ChooseSharedVersionAsync))
            .ToList();
        Unanalyzable = report.Unanalyzable.Select(m => new UnanalyzableItemViewModel(m)).ToList();

        HeaderText = UiText.Format("{0} 组冲突 · {1} 处重复 · {2} 个共享组件",
            report.Groups.Count, Duplicates.Count, report.SharedComponents.Count);
        IsEmpty = Groups.Count == 0 && Duplicates.Count == 0 && Unanalyzable.Count == 0;

        OnPropertyChanged(nameof(Groups));
        OnPropertyChanged(nameof(Duplicates));
        OnPropertyChanged(nameof(SharedComponents));
        OnPropertyChanged(nameof(Unanalyzable));
        OnPropertyChanged(nameof(HasGroups));
        OnPropertyChanged(nameof(HasDuplicates));
        OnPropertyChanged(nameof(HasSharedComponents));
        OnPropertyChanged(nameof(HasUnanalyzable));
    }

    /// <summary>
    /// 所有动作的共同尾巴：执行 → 重新分析 → 留一行可见反馈。
    /// 失败时也要写 StatusText：面板动作没有对话框，静默失败会被当成"点了没反应"。
    /// </summary>
    private async Task<OperationResult> RunActionAsync(string successText, Func<Task<OperationResult>> action)
    {
        OperationResult result;
        try
        {
            result = await action();
        }
        catch (Exception ex)
        {
            // 按钮走的是 AsyncRelayCommand：这里不接住，异常就成了"未观察的 Task 异常"——
            // 没有弹窗、没有日志、面板一动不动，用户看到的就是"点了没反应"。
            _logger.LogError(ex, "[Conflict] 面板动作失败");
            StatusText = UiText.Format("操作失败：{0}", ex.Message);
            return OperationResult.Fail(ex.Message);
        }

        if (!result.Success)
        {
            _logger.LogWarning("[Conflict] 面板动作未成功: {Error}", result.Error);
            StatusText = result.Error ?? OperationResult.DefaultError;
            return result;
        }

        await LoadAsync();
        StatusText = successText;
        return result;
    }

    /// <summary>带包名的成功反馈：文案里的 {0} 用 MOD 显示名填。</summary>
    private Task<OperationResult> RunActionAsync(
        string packageKey, string successTemplate, Func<string, Task<OperationResult>> action)
        => RunActionAsync(UiText.Format(successTemplate, DisplayNameFor(packageKey)), () => action(packageKey));

    // 这几个包装都把 RunActionAsync 的真实结果往上抛，不再无条件 Ok()：
    // 动作失败时面板已经写了错误进 StatusText，但调用方（动作按钮）拿到的若仍是"成功"，
    // "面板显示已禁用、实际什么都没变"就有第二条路可走。

    private Task<OperationResult> DisableAsync(string packageKey)
        => DisableManyAsync([packageKey], UiText.Format("已禁用 {0} 并重新部署", DisplayNameFor(packageKey)));

    private Task<OperationResult> KeepOnlyAsync(IReadOnlyList<string> others)
        => DisableManyAsync(others, UiText.Get("已按选择重新部署"));

    /// <summary>
    /// 禁用一组包的共同入口。
    ///
    /// 先自己看一眼"这批包里还有没有启用中的"：一个都没有时什么都不做、也不显示"已禁用"，
    /// 只留一行说明。面板上的"已禁用 X"必须对应一次真实变更 —— 否则用户看到成功、
    /// 主列表却纹丝不动，正是这次要修的病症。
    /// </summary>
    private async Task<OperationResult> DisableManyAsync(IReadOnlyList<string> packageKeys, string successText)
    {
        // "找不到"和"本来就是禁用"是两件事：前者是接线错误，必须报错并留日志；
        // 后者才是真的无事可做。合并成一句"没有需要禁用的 MOD"会把 bug 说成正常。
        var missing = packageKeys.Where(k => FindMod(k) is null).ToList();
        if (missing.Count > 0)
        {
            _logger.LogError("[Conflict] 主列表里找不到 {Keys}（AllMods 共 {Count} 项）",
                string.Join(", ", missing), _main.AllMods.Count);
            StatusText = UiText.Format("找不到 MOD：{0}", string.Join("、", missing));
            return OperationResult.Fail(StatusText);
        }

        var enabled = packageKeys.Where(k => FindMod(k) is { IsEnabled: true }).ToList();
        if (enabled.Count == 0)
        {
            StatusText = UiText.Get("没有需要禁用的 MOD");
            return OperationResult.Ok();
        }

        return await RunActionAsync(successText, () => _main.DisableManyAsync(enabled));
    }

    /// <summary>
    /// "一键处理"会禁用的包：**每个冲突组保留显示顺序里的第一个成员，禁用其余**。
    ///
    /// 一个包可能同时出现在多个组里；前面的组把它判给"保留"时，后面的组就不能再禁用它，
    /// 否则同一张卡片会在一次操作里被两个组各下一道判决。只处理冲突组 ——
    /// "重复"和"共享组件"是另一类取舍，不在这里替用户做主。
    /// </summary>
    public IReadOnlyList<string> PlanDefaultResolution()
    {
        var toDisable = new List<string>();
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in Groups)
        {
            var members = item.Group.Members;
            if (members.Count == 0) continue;

            var keeper = members.FirstOrDefault(m => !toDisable.Contains(m.PackageKey, StringComparer.OrdinalIgnoreCase))
                         ?? members[0];
            kept.Add(keeper.PackageKey);

            foreach (var member in members)
                if (!string.Equals(member.PackageKey, keeper.PackageKey, StringComparison.OrdinalIgnoreCase)
                    && !kept.Contains(member.PackageKey))
                    toDisable.Add(member.PackageKey);
        }

        return toDisable.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>按默认规则处理全部冲突组：一次禁用、一次重新部署。</summary>
    public Task<OperationResult> ResolveAllAsync()
        => DisableManyAsync(PlanDefaultResolution(), UiText.Get("已按默认规则处理全部冲突并重新部署"));

    /// <summary>
    /// "一键处理"的确认文案。
    ///
    /// 拼装在 VM 里而不是窗口里：这是用户点下去之前唯一能看到"到底关掉哪些"的地方，
    /// 放这里就能被单测钉住（前 5 个名字逐个列出、超出部分折算成总数）。
    /// </summary>
    public string BuildResolutionConfirmation()
    {
        var toDisable = PlanDefaultResolution();
        var message = UiText.Format("将禁用 {0} 个 MOD（每组冲突只保留第一个），继续？", toDisable.Count);
        if (toDisable.Count == 0) return message;

        var listed = toDisable.Take(ConfirmationNamesShown).Select(DisplayNameFor).ToList();
        if (toDisable.Count > listed.Count)
            listed.Add(UiText.Format("等 {0} 个", toDisable.Count - listed.Count));

        return message + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, listed);
    }

    /// <summary>确认文案里逐个列出的名字上限；再多就折算成"等 N 个"。</summary>
    internal const int ConfirmationNamesShown = 5;

    private async Task<OperationResult> DeleteDuplicateAsync(string packageKey)
    {
        var mod = FindMod(packageKey);
        if (mod is null)
        {
            var missing = UiText.Get("仓库中找不到这个 MOD");
            return await RunActionAsync(missing, () => Task.FromResult(OperationResult.Fail(missing)));
        }

        return await RunActionAsync(UiText.Format("已删除 {0}", mod.Name), () => _main.DeletePackageModAsync(mod));
    }

    private Task<OperationResult> ChooseSharedVersionAsync(string componentName, string packageKey)
        => RunActionAsync(packageKey, "已改用 {0} 的版本并重新部署", async key =>
        {
            await _conflictModule.ApplySharedComponentChoiceAsync(componentName, key);
            return await _main.RedeployCurrentProfileAsync();
        });

    private ModInfo? FindMod(string packageKey)
        => _main.AllMods.FirstOrDefault(m =>
            string.Equals(m.RealName, packageKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 面板实际持有的主视图模型。
    ///
    /// 只为测试而存在的读数口：面板接错 MainViewModel 时"面板显示已禁用、主列表纹丝不动"
    /// 会静默复现，而这在运行期没有任何别的信号。测试断言它与调用方传入的是同一个实例。
    /// </summary>
    internal MainViewModel MainForTests => _main;

    private string DisplayNameFor(string packageKey)
        => FindMod(packageKey)?.Name ?? packageKey;

    // ─── 子项视图模型 ───

    /// <summary>一个冲突组：一个标题、一句说明、一组按钮、一个可展开的资源清单。</summary>
    public sealed class ConflictGroupItemViewModel
    {
        private readonly Func<string, Task<OperationResult>> _disable;
        private readonly Func<IReadOnlyList<string>, Task<OperationResult>> _keepOnly;

        public ConflictGroupItemViewModel(
            ConflictGroup group,
            Func<string, Task<OperationResult>> disable,
            Func<IReadOnlyList<string>, Task<OperationResult>> keepOnly)
        {
            Group = group;
            _disable = disable;
            _keepOnly = keepOnly;

            var members = group.Members;
            Actions = members.Select(member => new ActionButtonViewModel(
                members.Count <= 2
                    ? UiText.Format("禁用 {0}", member.DisplayName)
                    : UiText.Format("只保留 {0}", member.DisplayName),
                members.Count <= 2
                    ? () => _disable(member.PackageKey)
                    : () => _keepOnly(members
                        .Where(m => !string.Equals(
                            m.PackageKey, member.PackageKey, StringComparison.OrdinalIgnoreCase))
                        .Select(m => m.PackageKey)
                        .ToList()))).ToList();
        }

        public ConflictGroup Group { get; }

        /// <summary>成员名用 " ⇄ " 连接。</summary>
        public string Title => string.Join("  ⇄  ", Group.Members.Select(m => m.DisplayName));

        public string Description => UiText.Format(
            "都修改了 {0} 个相同资源：{1}。同时开启时只有一个会生效。",
            Group.SharedAssets.Count, Group.Summary.Summary);

        /// <summary>可展开的资源清单：短名（类别）。</summary>
        public IReadOnlyList<string> Assets => Group.SharedAssets
            .Select(a => UiText.Format("{0}（{1}）", a.ShortName, AssetPathDescriber.DisplayName(a.Category)))
            .ToList();

        /// <summary>两成员给"禁用 X"，三成员及以上给"只保留 X"。</summary>
        public IReadOnlyList<ActionButtonViewModel> Actions { get; }
    }

    /// <summary>一个按钮：文案 + 点击动作。</summary>
    public sealed partial class ActionButtonViewModel : ObservableObject
    {
        private readonly Func<Task<OperationResult>> _invoke;

        public ActionButtonViewModel(string label, Func<Task<OperationResult>> invoke)
        {
            Label = label;
            _invoke = invoke;
        }

        public string Label { get; }

        [RelayCommand]
        private Task RunAsync() => _invoke();
    }

    /// <summary>一处重复：副本可以删掉，保留 Primary。</summary>
    public sealed class DuplicateItemViewModel
    {
        private readonly Func<string, Task<OperationResult>> _delete;

        public DuplicateItemViewModel(
            ConflictMember primary, ConflictMember copy,
            Func<string, Task<OperationResult>> delete)
        {
            Primary = primary;
            Copy = copy;
            _delete = delete;

            Actions = [new ActionButtonViewModel(
                UiText.Format("删除 {0}", copy.DisplayName),
                () => _delete(copy.PackageKey))];
        }

        public ConflictMember Primary { get; }
        public ConflictMember Copy { get; }

        public string Text => UiText.Format("{0} 与 {1} 内容完全相同", Copy.DisplayName, Primary.DisplayName);

        public IReadOnlyList<ActionButtonViewModel> Actions { get; }
    }

    /// <summary>一个共享组件：已自动处理，仍允许改用另一份。</summary>
    public sealed class SharedComponentItemViewModel
    {
        private readonly Func<string, string, Task<OperationResult>> _choose;

        public SharedComponentItemViewModel(
            SharedComponent component,
            Func<string, string, Task<OperationResult>> choose)
        {
            Component = component;
            _choose = choose;

            // 每个"非当前保留"的来源各给一个"改用另一份"。
            Actions = component.Providers
                .Where(p => !string.Equals(
                    p.PackageKey, component.ChosenPackageKey, StringComparison.OrdinalIgnoreCase))
                .Select(p => new ActionButtonViewModel(
                    UiText.Format("改用另一份（{0}）", p.DisplayName),
                    () => _choose(component.Name, p.PackageKey)))
                .ToList();
        }

        public SharedComponent Component { get; }

        public string Name => Component.Name;

        public string Detail
        {
            get
            {
                var sources = string.Join("、", Component.Providers.Select(p => p.DisplayName));
                var chosen = Component.Providers.FirstOrDefault(p =>
                    string.Equals(p.PackageKey, Component.ChosenPackageKey, StringComparison.OrdinalIgnoreCase));
                return UiText.Format("来源：{0}（{1}，已保留 {2}）",
                    sources, Component.Reason, chosen?.DisplayName ?? Component.ChosenPackageKey);
            }
        }

        public IReadOnlyList<ActionButtonViewModel> Actions { get; }
    }

    /// <summary>读不出索引的包。</summary>
    public sealed class UnanalyzableItemViewModel
    {
        public UnanalyzableItemViewModel(ConflictMember member) => Member = member;

        public ConflictMember Member { get; }

        public string Detail => UiText.Format("{0}：容器索引已加密或格式不支持", Member.DisplayName);
    }
}
