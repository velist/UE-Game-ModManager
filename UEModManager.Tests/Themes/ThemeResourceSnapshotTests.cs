using System;
using System.Linq;
using System.Text;

namespace UEModManager.Tests.Themes;

/// <summary>
/// 主题资源快照测试。
///
/// 存在的理由：`CyberDarkTheme.xaml` 与 `CyberStyles.xaml` 曾有 69 个 x:Key 重复定义，
/// 靠 App.xaml 的合并顺序决定谁生效——去被遮蔽的那份里改颜色，运行后毫无变化。
/// 拆成"令牌 / 样式"两层之后必须能证明**用户看到的界面一个像素都没变**，
/// 这件事没法靠肉眼，所以把全部资源 key 及其解析后的取值钉成快照。
///
/// 快照取自重构前的实际运行结果。它同时验证了跨字典 StaticResource 解析确实成立：
/// 令牌搬到另一个文件后，Style 里的 {StaticResource XxxBrush} 若解析不到，取值即抛异常。
/// </summary>
[Collection(ThemeResourceCollection.Name)]
public class ThemeResourceSnapshotTests
{
    [Fact]
    public void 全部主题资源都能解析_跨字典StaticResource成立()
    {
        // CaptureMerged 内部对每个 key 取值，解析不到就抛
        var capture = ThemeResourceLoader.CaptureMerged();
        Assert.NotEmpty(capture.Keys);
    }

    [Fact]
    public void 资源key集合与快照一致_没有丢key也没有多key()
    {
        var capture = ThemeResourceLoader.CaptureMerged();

        var missing = ThemeSnapshot.Keys.Except(capture.Keys, StringComparer.Ordinal).ToList();
        var added = capture.Keys.Except(ThemeSnapshot.Keys, StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0, $"丢失的 key: {string.Join(", ", missing)}");
        Assert.True(added.Count == 0, $"多出的 key: {string.Join(", ", added)}");
    }

    [Fact]
    public void 全部资源解析值与快照逐项一致_视觉零变化()
    {
        var capture = ThemeResourceLoader.CaptureMerged();
        var diffs = new StringBuilder();

        foreach (var (key, expected) in ThemeSnapshot.Values)
        {
            capture.Values.TryGetValue(key, out var actual);
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                diffs.AppendLine($"  {key}: 期望 [{expected}] 实际 [{actual ?? "<缺失>"}]");
        }

        Assert.True(diffs.Length == 0, "主题取值发生变化：\n" + diffs);
    }

    [Fact]
    public void 令牌与样式已分层_两个字典各司其职()
    {
        // CyberDarkTheme 只放设计令牌，CyberStyles 只放样式。
        // 这条断言防止以后有人图省事又把颜色写回样式文件，重新制造两份事实来源。
        var layering = ThemeResourceLoader.InspectLayering();

        Assert.True(layering.StylesInTokenFile.Count == 0,
            $"CyberDarkTheme.xaml 里不该有 Style: {string.Join(", ", layering.StylesInTokenFile)}");
        Assert.True(layering.TokensInStyleFile.Count == 0,
            $"CyberStyles.xaml 里不该有设计令牌: {string.Join(", ", layering.TokensInStyleFile)}");
    }

    [Fact]
    public void 两个字典之间没有重复key_不再靠合并顺序决定谁生效()
    {
        var layering = ThemeResourceLoader.InspectLayering();

        Assert.True(layering.DuplicateKeys.Count == 0,
            $"两个主题字典重复定义了 {layering.DuplicateKeys.Count} 个 key: "
            + string.Join(", ", layering.DuplicateKeys));
    }
}
