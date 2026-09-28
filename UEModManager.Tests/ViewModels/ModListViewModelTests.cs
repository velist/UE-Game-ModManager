using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Models;
using UEModManager.ViewModels;

namespace UEModManager.Tests.ViewModels;

/// <summary>
/// 列表多选的回归。
///
/// <para>
/// 事故原型：<c>SelectAll</c> 每循环一个 MOD 就把 <c>IsSelectAll</c> 读一遍，而这个字段会被
/// <c>UpdateSelectionState</c> 重算成"选中的确实全选了"。第一个 MOD 被选中后它就被改回
/// <c>false</c>，循环里剩下的卡片全被设成 <c>false</c> —— 表现是"每点一次只多选一个"。
/// </para>
///
/// <para>
/// 这里只放 MOD 不注册任何服务：多选是纯视图状态，不碰仓库也不部署。
/// </para>
/// </summary>
public sealed class ModListViewModelTests
{
    [Fact]
    public void 全选一次就选中全部可见MOD()
    {
        var list = NewListWith(5);

        list.SelectAll();

        Assert.Equal(5, list.Mods.Count(m => m.IsSelected));
        Assert.Equal(5, list.SelectedCount);
        Assert.True(list.IsSelectAll);
        Assert.Equal("取消全选", list.SelectAllText);
    }

    [Fact]
    public void 再点一次全部取消()
    {
        var list = NewListWith(5);
        list.SelectAll();

        list.SelectAll();

        Assert.DoesNotContain(list.Mods, m => m.IsSelected);
        Assert.Equal(0, list.SelectedCount);
        Assert.False(list.IsSelectAll);
        Assert.Equal("全选", list.SelectAllText);
    }

    [Fact]
    public void 部分选中时点全选是补齐而不是取消()
    {
        var list = NewListWith(5);
        // 手动选两个：此时 IsSelectAll 是 false，但用户点"全选"的意图显然是补齐。
        list.Mods[0].IsSelected = true;
        list.Mods[1].IsSelected = true;
        Assert.False(list.IsSelectAll);

        list.SelectAll();

        Assert.All(list.Mods, m => Assert.True(m.IsSelected));
        Assert.True(list.IsSelectAll);
    }

    [Fact]
    public void 全选后取消单个MOD会把全选状态退回()
    {
        var list = NewListWith(5);
        list.SelectAll();

        // 批量期间被挡住的重算，必须在循环结束后补上：之后单独改一张卡片仍要能反应过来。
        list.Mods[2].IsSelected = false;

        Assert.False(list.IsSelectAll);
        Assert.Equal(4, list.SelectedCount);
        Assert.Equal("全选", list.SelectAllText);
    }

    [Fact]
    public void 空列表时全选不改变状态()
    {
        var list = new ModListViewModel(NullLogger.Instance);
        list.SetSource([]);

        list.SelectAll();

        Assert.False(list.IsSelectAll);
        Assert.Equal(0, list.SelectedCount);
    }

    private static ModListViewModel NewListWith(int count)
    {
        var mods = Enumerable.Range(1, count)
            .Select(i => new ModInfo { Name = $"MOD {i}", RealName = $"mod-{i}" })
            .ToList();
        var list = new ModListViewModel(NullLogger.Instance);
        list.SetSource(new System.Collections.ObjectModel.ObservableCollection<ModInfo>(mods));
        return list;
    }
}
