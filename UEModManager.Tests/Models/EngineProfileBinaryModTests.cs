using UEModManager.Models;

namespace UEModManager.Tests.Models;

/// <summary>
/// <see cref="EngineProfile.BinaryModExtensions"/> 的不变量。
///
/// 压缩包 / 文件夹导入按它把文件分成"MOD（进 MOD 目录）"和"插件/配置（保留布局进游戏目录）"。
/// 以前写死 .pak 三件套，非 UE 游戏压缩包里的 .pck / .mpq 被装进游戏根。
/// </summary>
public sealed class EngineProfileBinaryModTests
{
    public static TheoryData<EngineType> AllEngines()
    {
        var data = new TheoryData<EngineType>();
        foreach (var t in System.Enum.GetValues<EngineType>()) data.Add(t);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllEngines))]
    public void 主载荷扩展名非空且是MOD扩展名的子集(EngineType engine)
    {
        var p = EngineProfile.Get(engine);

        Assert.NotEmpty(p.BinaryModExtensions);
        Assert.Subset(p.ModFileExtensions, p.BinaryModExtensions);
    }

    [Theory]
    [MemberData(nameof(AllEngines))]
    public void 主载荷不含插件或配置也会用的扩展名(EngineType engine)
    {
        // .json 会把 UE4SS 的 mods.json 当成 MOD；.dll / .lua 是插件载荷
        var p = EngineProfile.Get(engine);

        foreach (var ext in new[] { ".json", ".dll", ".lua", ".ini" })
            Assert.DoesNotContain(ext, p.BinaryModExtensions);
    }

    [Theory]
    [InlineData(EngineType.UnrealEngine, ".pak")]
    [InlineData(EngineType.Godot, ".pck")]
    [InlineData(EngineType.Diablo4Engine, ".mpq")]
    public void 各引擎的典型容器被识别为MOD(EngineType engine, string ext)
        => Assert.Contains(ext, EngineProfile.Get(engine).BinaryModExtensions);
}
