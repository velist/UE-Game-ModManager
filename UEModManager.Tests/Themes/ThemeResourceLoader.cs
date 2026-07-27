using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;

namespace UEModManager.Tests.Themes;

/// <summary>
/// 按 App.xaml 的方式加载主题字典，并把资源值渲染成稳定的可比较字符串。
///
/// WPF 资源是 DependencyObject，有线程亲和性——必须在同一个 STA 线程里
/// 完成"加载 + 取值 + 渲染"，只把纯字符串结果带回调用线程。
/// </summary>
public static class ThemeResourceLoader
{
    /// <summary>App.xaml 中的合并顺序，顺序本身就是语义（后者覆盖前者）。</summary>
    public static readonly string[] DictionaryNames = { "CyberDarkTheme", "CyberStyles" };

    /// <summary>一次抓取的结果：全部 key（有序）与 key → 渲染值。</summary>
    public sealed record Capture(List<string> Keys, Dictionary<string, string> Values);

    /// <summary>分层检查结果：令牌文件里的样式、样式文件里的令牌、两者重复的 key。</summary>
    public sealed record Layering(
        List<string> StylesInTokenFile,
        List<string> TokensInStyleFile,
        List<string> DuplicateKeys);

    /// <summary>
    /// 合并加载主题字典、强制解析每一项、渲染成字符串。
    /// 跨字典的 StaticResource 若解析不到，会在解析阶段抛出。
    /// </summary>
    public static Capture CaptureMerged() => RunOnSta(() =>
    {
        var merged = new ResourceDictionary();
        foreach (var name in DictionaryNames)
            merged.MergedDictionaries.Add(Load(name));

        var keys = CollectKeys(merged);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in keys)
            values[key] = Render(merged[key]);   // 索引器取值即触发 StaticResource 解析

        return new Capture(keys, values);
    });

    /// <summary>
    /// 单独加载两个字典，检查"令牌 / 样式"分层是否成立、是否还有重复 key。
    /// </summary>
    public static Layering InspectLayering() => RunOnSta(() =>
    {
        var tokens = Load("CyberDarkTheme");
        var styles = Load("CyberStyles");

        // 令牌文件里不该出现 Style
        var stylesInTokenFile = tokens.Keys.Cast<object>()
            .Where(k => tokens[k] is Style)
            .Select(k => k.ToString()!)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        // 样式文件里不该出现设计令牌。
        // 注意：样式文件单独加载时引用不到令牌，取值会抛，所以逐个 key 保护性取值。
        var tokensInStyleFile = new List<string>();
        foreach (var k in styles.Keys.Cast<object>())
        {
            object? v;
            try { v = styles[k]; }
            catch { continue; }   // 解析不到 = 依赖了另一个字典里的令牌，本身不是令牌
            if (v is Color or Brush or CornerRadius or FontFamily)
                tokensInStyleFile.Add(k.ToString()!);
        }
        tokensInStyleFile.Sort(StringComparer.Ordinal);

        var duplicates = tokens.Keys.Cast<object>().Select(k => k.ToString()!)
            .Intersect(styles.Keys.Cast<object>().Select(k => k.ToString()!), StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        return new Layering(stylesInTokenFile, tokensInStyleFile, duplicates);
    });

    private static ResourceDictionary Load(string name) => new()
    {
        Source = new Uri(
            $"pack://application:,,,/UEModManager;component/Themes/{name}.xaml",
            UriKind.Absolute)
    };

    /// <summary>在专用 STA 线程上执行，把结果（纯数据）带回。</summary>
    private static T RunOnSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                // pack:// URI 需要 ResourceAssembly 已确定
                if (Application.ResourceAssembly == null)
                    Application.ResourceAssembly = typeof(UEModManager.App).Assembly;

                result = work();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure != null)
            throw new InvalidOperationException("主题字典处理失败: " + failure.Message, failure);

        return result;
    }

    /// <summary>递归收集合并字典里的全部字符串 key（去重后按序）。</summary>
    private static List<string> CollectKeys(ResourceDictionary dict)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        void Walk(ResourceDictionary d)
        {
            foreach (var md in d.MergedDictionaries)
                Walk(md);
            foreach (var k in d.Keys)
                if (k is string s)
                    keys.Add(s);
        }

        Walk(dict);
        return keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// 把资源值渲染成稳定字符串。
    /// Style 只记录 TargetType 与 Setter/Trigger 数量——足以发现"样式被换掉或丢失"，
    /// 而逐个 Setter 比对会把快照撑到无法维护的体积；样式内引用的颜色由令牌快照保证。
    /// </summary>
    public static string Render(object? value) => value switch
    {
        null => "<null>",
        Color c => c.ToString(CultureInfo.InvariantCulture),
        SolidColorBrush b => $"Solid {b.Color.ToString(CultureInfo.InvariantCulture)} @{b.Opacity.ToString(CultureInfo.InvariantCulture)}",
        LinearGradientBrush g =>
            "Linear " + string.Join(",", g.GradientStops.Select(s =>
                $"{s.Color.ToString(CultureInfo.InvariantCulture)}@{s.Offset.ToString(CultureInfo.InvariantCulture)}"))
            + $" {g.StartPoint.ToString(CultureInfo.InvariantCulture)}->{g.EndPoint.ToString(CultureInfo.InvariantCulture)}",
        CornerRadius r => $"Radius {r.TopLeft},{r.TopRight},{r.BottomRight},{r.BottomLeft}",
        FontFamily f => $"Font {f.Source}",
        Style st => $"Style<{st.TargetType?.Name}> setters={st.Setters.Count} triggers={st.Triggers.Count}",
        _ => value.GetType().Name
    };
}
