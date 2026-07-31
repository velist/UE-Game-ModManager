namespace UEModManager.Tests.Themes;

/// <summary>
/// 所有会加载主题字典的测试类必须串行执行。
///
/// <para>
/// <see cref="ThemeResourceLoader.CaptureMerged"/> 每次都新起一个 STA 线程去解析
/// <c>CyberDarkTheme</c> 与 <c>CyberStyles</c>。看上去每次都是全新的
/// <c>ResourceDictionary</c>、互不相干，但 WPF 的 XAML 解析器背后是**进程级**的静态状态
/// （类型/成员缓存、BAML 流的共享读取）。多个 STA 线程同时首次解析同一份 XAML 时，
/// 那些缓存会被并发写坏，表现为一句与本次断言毫无关系的
/// <c>The given key 'Property' was not present in the dictionary</c>。
/// </para>
///
/// <para>
/// <b>这个 bug 只在"从头构建 + 满负载并行"时才出现</b>，单独跑这个类必过——
/// 也就是说它会以"CI 偶发失败"的形态存在，而每次去查都查不出来。
/// xUnit 默认按测试类并行，消费方从两个涨到三个（其中一个还调了两次）正好把它压出来。
/// 与 <c>UiPreferencesStaticStateCollection</c> 同一形态、同一理由：
/// 有进程级共享状态的测试必须归进同一个 collection。
/// </para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class ThemeResourceCollection
{
    public const string Name = "ThemeResources";
}
