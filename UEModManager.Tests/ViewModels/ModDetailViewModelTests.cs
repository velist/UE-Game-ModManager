using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Models;
using UEModManager.ViewModels;

namespace UEModManager.Tests.ViewModels;

/// <summary>
/// 详情面板命令的回归。
///
/// <para>
/// 事故原型：<c>ModDetailViewModel</c> 整套命令写好了却没有任何 View 绑定，
/// 也没人调 <c>ConfigureActions</c>——"接线完成"只存在于想象里。这里钉住
/// 命令到回调的转交、空选中的安全，以及"没接线的命令必须抛带说明的异常"。
/// </para>
///
/// <para>
/// 回调全部用假的：面板命令是纯视图状态，不碰仓库也不部署。
/// </para>
/// </summary>
public sealed class ModDetailViewModelTests
{
    private static (ModDetailViewModel Vm, List<string> Calls) NewVm()
    {
        var calls = new List<string>();
        var vm = new ModDetailViewModel(NullLogger.Instance);
        vm.ConfigureActions(
            toggleModAsync: (_, enable) => { calls.Add($"toggle:{enable}"); return Task.FromResult(true); },
            changePreviewAsync: _ => { calls.Add("preview"); return Task.FromResult(true); },
            deleteModAsync: _ => { calls.Add("delete"); return Task.FromResult(true); },
            renameAsync: _ => { calls.Add("rename"); return Task.FromResult(true); },
            moveToCategoryAsync: (_, name) => { calls.Add($"move:{name}"); return Task.FromResult(true); },
            editNoteAsync: _ => { calls.Add("note"); return Task.FromResult(true); });
        return (vm, calls);
    }

    private static ModInfo NewMod() => new() { Name = "武器包", RealName = "weapons.pak" };

    [Fact]
    public async Task 命令把操作转交给注入的回调()
    {
        var (vm, calls) = NewVm();
        vm.CurrentMod = NewMod();

        await vm.ToggleEnabledAsync();
        await vm.ChangePreviewAsync();
        await vm.RenameAsync();
        await vm.EditNoteAsync();
        await vm.MoveToCategoryAsync("武器");

        Assert.Equal(new[] { "toggle:True", "preview", "rename", "note", "move:武器" }, calls);
    }

    [Fact]
    public async Task 没有当前MOD时命令什么都不做也不抛()
    {
        var (vm, calls) = NewVm();
        vm.CurrentMod = null;

        await vm.ToggleEnabledAsync();
        await vm.ChangePreviewAsync();
        await vm.RenameAsync();
        await vm.EditNoteAsync();
        await vm.DeleteAsync();
        await vm.MoveToCategoryAsync("武器");

        Assert.Empty(calls);
        Assert.False(vm.HasMod);
    }

    [Fact]
    public async Task 移动到分类缺分类名时不动()
    {
        var (vm, calls) = NewVm();
        vm.CurrentMod = NewMod();

        await vm.MoveToCategoryAsync(null);
        await vm.MoveToCategoryAsync("   ");

        Assert.Empty(calls);
    }

    [Fact]
    public async Task 删除成功后收起面板并通知刷新()
    {
        var (vm, calls) = NewVm();
        vm.CurrentMod = NewMod();
        var closed = false;
        var stateChanged = false;
        vm.CloseRequested += () => closed = true;
        vm.ModStateChanged += () => stateChanged = true;

        await vm.DeleteAsync();

        Assert.Contains("delete", calls);
        Assert.Null(vm.CurrentMod);
        Assert.False(vm.HasMod);
        Assert.True(closed);
        Assert.True(stateChanged);
    }

    [Fact]
    public async Task 回调未接线时抛出可读异常()
    {
        var vm = new ModDetailViewModel(NullLogger.Instance) { CurrentMod = NewMod() };

        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.ToggleEnabledAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.RenameAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.EditNoteAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.DeleteAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.MoveToCategoryAsync("武器"));
    }

    [Fact]
    public void 文本属性返回未翻译的源串()
    {
        // 翻译交给 XAML 的 loc:Value（带语言订阅），VM 里翻好再绑的话
        // 切换语言文案不会跟着变。这里钉住"源串"这个契约。
        var (vm, _) = NewVm();
        var mod = NewMod();
        vm.CurrentMod = mod;

        Assert.Equal("已禁用", vm.StatusText);
        Assert.Equal("启用MOD", vm.ToggleText);

        mod.IsEnabled = true;
        Assert.Equal("已启用", vm.StatusText);
        Assert.Equal("禁用MOD", vm.ToggleText);

        Assert.Equal("未分类", vm.Category);
        mod.Categories = new List<string> { "武器" };
        Assert.Equal("武器", vm.Category);
    }

    [Fact]
    public void 描述为空时给出占位状态()
    {
        var (vm, _) = NewVm();
        var mod = NewMod();
        vm.CurrentMod = mod;

        Assert.False(vm.HasDescription);
        Assert.True(vm.HasNoDescription);

        mod.Description = "这是说明";
        Assert.True(vm.HasDescription);
        Assert.False(vm.HasNoDescription);
        Assert.Equal("这是说明", vm.Description);
    }

    [Fact]
    public void 关闭命令只发事件不改选中()
    {
        var (vm, _) = NewVm();
        vm.CurrentMod = NewMod();
        var closed = false;
        vm.CloseRequested += () => closed = true;

        vm.Close();

        Assert.True(closed);
        Assert.NotNull(vm.CurrentMod);
    }
}
