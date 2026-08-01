using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Windows.Controls;

namespace UEModManager.Tests.Views;

/// <summary>
/// 验证"列表刷新不该拔插 ItemsSource"这个修复的前提。
///
/// 背景：MainWindow.RefreshAfterModChange 曾经写成
///   ItemsSource = null; ItemsSource = mods;
/// 而 mods 是实例从不替换的 ObservableCollection，本就会经 INotifyCollectionChanged
/// 自动更新。拔插不增加任何正确性，却强制重建全部容器——滚动位置归零、选中项丢失。
///
/// 这两条测试用真实的 WPF ListBox 把两种写法的差别钉死：
/// 拔插会销毁选中项，集合增量变更不会。
/// </summary>
public class ItemsSourceRefreshBehaviorTests
{
    private sealed record Item(string Name);

    /// <summary>WPF 控件需要 STA，单开线程跑并把结果带回。</summary>
    private static T OnSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure != null) throw new InvalidOperationException("STA 执行失败: " + failure.Message, failure);
        return result;
    }

    [Fact]
    public void 拔插ItemsSource会丢失选中项_这正是修复前的写法()
    {
        var lost = OnSta(() =>
        {
            var items = new ObservableCollection<Item> { new("a"), new("b"), new("c") };
            var list = new ListBox { ItemsSource = items };
            list.SelectedIndex = 2;

            // 修复前 RefreshAfterModChange 的写法
            list.ItemsSource = null;
            list.ItemsSource = items;

            return list.SelectedItem == null;
        });

        Assert.True(lost, "拔插后选中项本应丢失；若此断言失败说明 WPF 行为变了，修复的前提需要重新评估");
    }

    [Fact]
    public void 集合增量变更保留选中项_这是修复后依赖的行为()
    {
        var (selectedName, sameSource) = OnSta(() =>
        {
            var items = new ObservableCollection<Item> { new("a"), new("b"), new("c") };
            var list = new ListBox { ItemsSource = items };
            list.SelectedIndex = 2;
            var before = list.ItemsSource;

            // 修复后：只动集合内容，不碰 ItemsSource
            items.Add(new("d"));
            items.RemoveAt(0);

            return (((Item?)list.SelectedItem)?.Name, ReferenceEquals(before, list.ItemsSource));
        });

        Assert.Equal("c", selectedName);
        Assert.True(sameSource, "ItemsSource 引用不应被替换");
    }

    [Fact]
    public void 集合Clear会丢失选中项_说明为什么本次修复不足以消除症状()
    {
        // 这条不是在测我的改动，是把"为什么切开关后列表仍会跳回顶部"钉成可执行的说明：
        // MainViewModel.RefreshFromRepositoryAsync 全量重建 ModInfo 后
        // ModListViewModel.ApplyFilter 会 Mods.Clear() + Add()，触发 CollectionChanged.Reset。
        // 只要这条链路还在，换成绑定也保不住选中项和滚动位置。
        var lost = OnSta(() =>
        {
            var items = new ObservableCollection<Item> { new("a"), new("b"), new("c") };
            var list = new ListBox { ItemsSource = items };
            list.SelectedIndex = 2;

            items.Clear();
            items.Add(new("a"));
            items.Add(new("b"));
            items.Add(new("c"));

            return list.SelectedItem == null;
        });

        Assert.True(lost,
            "Clear+Add 后选中项仍应丢失；若这里变绿了，说明上游已改为增量更新，"
            + "届时可以复核'切开关跳回顶部'是否已消失");
    }
}
