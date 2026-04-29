using UEModManager.Services.Detection;

namespace UEModManager.Core.Tests.Services.Detection;

public class ModCategoryClassifierTests
{
    // ─── 单关键词命中 ───

    [Theory]
    [InlineData("FaceMod", "面部")]
    [InlineData("FacialEnhance", "面部")]
    [InlineData("脸部美化", "面部")]
    [InlineData("面部修复", "面部")]
    public void Classify_FaceKeywords(string name, string expected)
    {
        Assert.Equal(expected, ModCategoryClassifier.Classify(name));
    }

    [Theory]
    [InlineData("CharacterReplace", "人物")]
    [InlineData("BodyMod", "人物")]
    [InlineData("SkinPack", "人物")]
    [InlineData("人物替换", "人物")]
    [InlineData("角色重做", "人物")]
    [InlineData("身体调整", "人物")]
    public void Classify_CharacterKeywords(string name, string expected)
    {
        Assert.Equal(expected, ModCategoryClassifier.Classify(name));
    }

    [Theory]
    [InlineData("WeaponPack", "武器")]
    [InlineData("SwordOfTruth", "武器")]
    [InlineData("Gun-Mod", "武器")]
    [InlineData("武器包", "武器")]
    [InlineData("剑魂", "武器")]
    [InlineData("刀光剑影", "武器")]
    public void Classify_WeaponKeywords(string name, string expected)
    {
        Assert.Equal(expected, ModCategoryClassifier.Classify(name));
    }

    [Theory]
    [InlineData("OutfitMod", "服装")]
    [InlineData("ClothPack", "服装")]
    [InlineData("SuitMaster", "服装")]
    [InlineData("服装替换", "服装")]
    [InlineData("衣服扩充", "服装")]
    [InlineData("套装合集", "服装")]
    public void Classify_OutfitKeywords(string name, string expected)
    {
        Assert.Equal(expected, ModCategoryClassifier.Classify(name));
    }

    [Theory]
    [InlineData("HairStyleX", "发型")]
    [InlineData("头发优化", "发型")]
    [InlineData("发型大全", "发型")]
    public void Classify_HairKeywords(string name, string expected)
    {
        Assert.Equal(expected, ModCategoryClassifier.Classify(name));
    }

    // ─── 大小写 ───

    [Theory]
    [InlineData("FACEMOD")]
    [InlineData("facemod")]
    [InlineData("FaCeMod")]
    public void Classify_CaseInsensitive(string name)
    {
        Assert.Equal("面部", ModCategoryClassifier.Classify(name));
    }

    // ─── 默认兜底 ───

    [Theory]
    [InlineData("RandomMod")]
    [InlineData("UnknownPack")]
    [InlineData("123")]
    [InlineData("环境光照")]
    public void Classify_NoMatch_ReturnsDefault(string name)
    {
        Assert.Equal("其他", ModCategoryClassifier.Classify(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Classify_NullOrWhitespace_ReturnsDefault(string? name)
    {
        Assert.Equal("其他", ModCategoryClassifier.Classify(name));
    }

    // ─── 优先级（按声明顺序，先命中先返回） ───

    [Fact]
    public void Classify_FaceBeatsCharacter_WhenBothPresent()
    {
        // "face" 排在 "character" 之前 → 面部胜出
        Assert.Equal("面部", ModCategoryClassifier.Classify("FaceCharacter"));
    }

    [Fact]
    public void Classify_CharacterBeatsWeapon_WhenBothPresent()
    {
        Assert.Equal("人物", ModCategoryClassifier.Classify("CharacterWeapon"));
    }

    // ─── 默认分类常量 ───

    [Fact]
    public void DefaultCategory_IsOther()
    {
        Assert.Equal("其他", ModCategoryClassifier.DefaultCategory);
    }

    // ─── KnownCategories ───

    [Fact]
    public void KnownCategories_HasFiveCategoriesInPriorityOrder()
    {
        var cats = ModCategoryClassifier.KnownCategories;

        Assert.Equal(5, cats.Count);
        Assert.Equal("面部", cats[0]);
        Assert.Equal("人物", cats[1]);
        Assert.Equal("武器", cats[2]);
        Assert.Equal("服装", cats[3]);
        Assert.Equal("发型", cats[4]);
    }
}
