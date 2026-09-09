using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using UEModManager.Models;
using UEModManager.Services;
using UEModManager.ViewModels;

namespace UEModManager.Tests.ViewModels;

/// <summary>
/// 右键"移动到分类"子菜单的数据源。
///
/// 背景：这个菜单从 v2.0 基线起就是死的——XAML 里挂着 <c>MoveToCategoryMenuItem</c>，
/// C# 里零引用，样式里也没有能显示子项的模板。重新接线后子项由
/// <see cref="CategoryViewModel.AssignableCategories"/> 驱动，所以这个集合
/// 必须（a）永远不含三个系统筛选视图、（b）跟着分类增删实时变。
/// 差一条的表现分别是"移进去等于什么都没做"和"菜单里躺着一个已删掉的分类"。
/// </summary>
public sealed class CategoryViewModelAssignableTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "uemm_assign_" + Guid.NewGuid().ToString("N")[..8]);

    public CategoryViewModelAssignableTests() => Directory.CreateDirectory(_root);

    private async Task<(NewCategoryService Service, CategoryViewModel Vm)> NewVmAsync()
    {
        var service = new NewCategoryService(NullLogger<NewCategoryService>.Instance, _root);
        var vm = new CategoryViewModel(service);
        await service.SetCurrentGameAsync("悟空");
        return (service, vm);
    }

    [Fact]
    public async Task 可选分类里没有三个系统筛选视图()
    {
        var (_, vm) = await NewVmAsync();

        // 三个系统分类一定在 Categories 里（侧边栏导航要按名字查它们）
        Assert.All(CategoryItem.SystemNames,
            n => Assert.Contains(vm.Categories, c => c.Name == n));

        // 但一个都不能出现在可选目标里
        Assert.All(CategoryItem.SystemNames,
            n => Assert.DoesNotContain(vm.AssignableCategories, c => c.Name == n));
    }

    [Fact]
    public async Task 新建分类后立刻出现在可选项里()
    {
        var (service, vm) = await NewVmAsync();

        await service.AddCategoryAsync("武器");

        Assert.Contains(vm.AssignableCategories, c => c.Name == "武器");
    }

    [Fact]
    public async Task 删除分类后立刻从可选项消失()
    {
        var (service, vm) = await NewVmAsync();
        var cat = await service.AddCategoryAsync("武器");

        await service.RemoveCategoryAsync(cat);

        Assert.DoesNotContain(vm.AssignableCategories, c => c.Name == "武器");
    }

    [Fact]
    public async Task 切换游戏后可选项跟着换成新游戏的分类()
    {
        // Categories 在加载时是 Clear + 逐条 Add，可选项必须跟着整体重建，
        // 否则菜单里会留着上一个游戏的分类。
        // （剑星先备好自己的分类文件——否则服务会有意从悟空那份继承过去，
        //   那是"换游戏不用重建分类"的既定行为，不是这条用例要验的东西。）
        var items = new[] { "剑星专用" }
            .Select((n, i) => new CategoryItem { Name = n, FullPath = n, IsCustom = true, SortOrder = i })
            .ToList();
        File.WriteAllText(Path.Combine(_root, "剑星_categories.json"), JsonSerializer.Serialize(items));

        var (service, vm) = await NewVmAsync();
        await service.AddCategoryAsync("悟空专用");

        await service.SetCurrentGameAsync("剑星");

        Assert.Contains(vm.AssignableCategories, c => c.Name == "剑星专用");
        Assert.DoesNotContain(vm.AssignableCategories, c => c.Name == "悟空专用");
    }

    [Fact]
    public async Task 可选项保持侧边栏的先后顺序()
    {
        // 用户自己拖出来的顺序；菜单里换个顺序会让人以为点错了地方
        var (service, vm) = await NewVmAsync();
        await service.AddCategoryAsync("甲");
        await service.AddCategoryAsync("乙");
        await service.AddCategoryAsync("丙");

        var expected = vm.Categories
            .Where(c => !CategoryItem.SystemNames.Contains(c.Name))
            .Select(c => c.Name)
            .ToArray();

        Assert.Equal(expected, vm.AssignableCategories.Select(c => c.Name).ToArray());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* 临时目录清理失败不影响断言 */ }
    }
}
