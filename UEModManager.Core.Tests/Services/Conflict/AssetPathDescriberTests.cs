using System.Linq;
using UEModManager.Services.Conflict;
using UEModManager.Services.Conflict.Model;

namespace UEModManager.Core.Tests.Services.Conflict;

/// <summary>
/// <see cref="AssetPathDescriber"/> 的归类与摘要回归。
///
/// 类别判定只看文件名前缀，量词决定了"1 张贴图"这类文案读起来对不对。
/// </summary>
public class AssetPathDescriberTests
{
    [Theory]
    [InlineData("b1/content/x/textures/t_wukong_born_cloth_ring_d", AssetCategory.Texture)]
    [InlineData("a/b/sk_wukong_body_dashengtao", AssetCategory.Mesh)]
    [InlineData("a/b/sm_rock", AssetCategory.Mesh)]
    [InlineData("a/b/skel_human", AssetCategory.Mesh)]
    [InlineData("a/b/pa_sword", AssetCategory.Mesh)]
    [InlineData("a/b/m_steel", AssetCategory.Material)]
    [InlineData("a/b/mi_steel_red", AssetCategory.Material)]
    [InlineData("a/b/bp_modloader", AssetCategory.Blueprint)]
    [InlineData("a/b/a_idle", AssetCategory.Animation)]
    [InlineData("a/b/am_attack", AssetCategory.Animation)]
    [InlineData("a/b/abp_mod_in", AssetCategory.Animation)]
    [InlineData("a/b/sw_hit", AssetCategory.Audio)]
    [InlineData("a/b/sc_ambient", AssetCategory.Audio)]
    [InlineData("a/b/dt_mod_equip", AssetCategory.Data)]
    [InlineData("a/b/ct_something", AssetCategory.Data)]
    [InlineData("a/b/random_thing", AssetCategory.Other)]
    public void 按前缀归类(string assetKey, AssetCategory expected)
    {
        Assert.Equal(expected, AssetPathDescriber.Categorize(assetKey));
    }

    [Fact]
    public void 归类大小写不敏感()
    {
        Assert.Equal(AssetCategory.Texture, AssetPathDescriber.Categorize("a/b/T_WuKong_Born"));
    }

    [Theory]
    [InlineData("b1/content/x/t_wukong_born_cloth_ring_d", "wukong_born_cloth_ring")]
    [InlineData("b1/content/x/sk_wukong_body_dashengtao", "wukong_body_dashengtao")]
    [InlineData("b1/content/x/t_armor_n", "armor")]
    [InlineData("b1/content/x/t_armor_p", "armor")]
    [InlineData("b1/content/x/t_armor_orm", "armor")]
    [InlineData("b1/content/x/t_armor_m", "armor")]
    [InlineData("b1/content/x/t_armor_r", "armor")]
    public void 短名去前缀与变体后缀(string assetKey, string expected)
    {
        Assert.Equal(expected, AssetPathDescriber.ShortName(assetKey));
    }

    [Fact]
    public void 摘要按固定顺序且用量词()
    {
        var summary = AssetPathDescriber.Summarize(
            [AssetCategory.Texture, AssetCategory.Mesh, AssetCategory.Texture]);

        Assert.Equal("2 张贴图、1 个模型", summary);
    }

    [Fact]
    public void 摘要只出现存在的类别()
    {
        Assert.Equal("1 段动画", AssetPathDescriber.Summarize([AssetCategory.Animation]));
    }

    [Fact]
    public void 空集合摘要为空串()
    {
        Assert.Equal(string.Empty, AssetPathDescriber.Summarize([]));
    }

    [Theory]
    [InlineData(AssetCategory.Texture, "张贴图")]
    [InlineData(AssetCategory.Animation, "段动画")]
    [InlineData(AssetCategory.Data, "张数据表")]
    [InlineData(AssetCategory.Mesh, "个模型")]
    public void 量词正确(AssetCategory category, string expected)
    {
        Assert.Equal(expected, AssetPathDescriber.Measure(category));
    }
}