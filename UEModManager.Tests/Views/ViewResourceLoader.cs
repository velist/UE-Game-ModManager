using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Markup;
using System.Xaml.Schema;
using System.Xml.Linq;

namespace UEModManager.Tests.Views;

/// <summary>
/// 用实际 App 资源和窗口 XAML 检查布局；只移除 code-behind 事件连接，
/// 避免构造业务服务、访问用户数据库或发送网络请求。也供人工截图检查使用。
/// </summary>
public static class ViewResourceLoader
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    public static Window Load(string appSourceDirectory, string relativePath)
    {
        var app = Read(Path.Combine(appSourceDirectory, "App.xaml"));
        var resources = new XElement(app.Descendants(Presentation + "ResourceDictionary").First());
        foreach (var ns in app.Root!.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            resources.SetAttributeValue(ns.Name, ns.Value);
        var merged = resources.Element(Presentation + "ResourceDictionary.MergedDictionaries")!;
        var themeResources = new List<XElement>();
        foreach (var source in merged.Elements().Attributes("Source"))
        {
            var theme = Read(Path.Combine(appSourceDirectory, source.Value)).Root!;
            foreach (var ns in theme.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
                resources.SetAttributeValue(ns.Name, ns.Value);
            themeResources.AddRange(theme.Elements());
        }
        // 按真实合并顺序内联主题；延迟加载的 ControlTemplate 也能在本地找到令牌。
        merged.ReplaceWith(themeResources);

        var view = Read(Path.Combine(appSourceDirectory, relativePath)).Root!;
        view.Attribute(Xaml + "Class")?.Remove();
        var rootResources = view.Attributes()
            .Select(attribute => (Attribute: attribute, Match: Regex.Match(attribute.Value, @"^\{StaticResource ([^}]+)\}$")))
            .Where(item => item.Match.Success).ToArray();
        foreach (var item in rootResources) item.Attribute.Remove();
        view.Descendants(Presentation + "EventSetter").Remove();
        var schema = XamlReader.GetWpfSchemaContext();
        foreach (var element in view.DescendantsAndSelf())
        {
            var type = schema.GetXamlType(new XamlTypeName(element.Name.NamespaceName, element.Name.LocalName));
            foreach (var attribute in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration).ToArray())
            {
                var member = type?.GetMember(attribute.Name.LocalName);
                if (member?.IsEvent == true) attribute.Remove();
                else if (attribute.Name.LocalName.Split('.') is [var ownerName, var memberName])
                {
                    var owner = schema.GetXamlType(new XamlTypeName(element.Name.NamespaceName, ownerName));
                    if (owner?.GetAttachableMember(memberName)?.IsEvent == true) attribute.Remove();
                }
            }
        }

        var localResources = view.Element(Presentation + "Window.Resources");
        if (localResources != null)
        {
            resources.Add(localResources.Elements().ToArray());
            localResources.Remove();
        }
        // 将实际 App 资源放在窗口作用域，使测试无需创建全局 Application 单例。
        view.AddFirst(new XElement(Presentation + "Window.Resources", resources));
        var context = new ParserContext
        {
            BaseUri = new Uri("pack://application:,,,/UEModManager;component/" + relativePath)
        };
        var window = (Window)XamlReader.Parse(view.ToString(), context);
        foreach (var item in rootResources)
            typeof(Window).GetProperty(item.Attribute.Name.LocalName)!
                .SetValue(window, window.FindResource(item.Match.Groups[1].Value));
        return window;
    }

    public static FrameworkElement Layout(Window window)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(
            double.IsNaN(window.Width) ? double.PositiveInfinity : window.Width,
            double.IsNaN(window.Height) ? double.PositiveInfinity : window.Height));
        var size = new Size(
            double.IsNaN(window.Width) ? Math.Max(window.MinWidth, content.DesiredSize.Width) : window.Width,
            double.IsNaN(window.Height) ? Math.Max(window.MinHeight, content.DesiredSize.Height) : window.Height);
        content.Arrange(new Rect(size));
        content.UpdateLayout();
        return content;
    }

    private static XDocument Read(string path)
    {
        var text = Regex.Replace(File.ReadAllText(path),
            "(clr-namespace:[^\";]+)(?=\")", "$1;assembly=UEModManager");
        text = Regex.Replace(text, "pack://application:,,,/(?!(?:[^/\"<>]+);component/)",
            "pack://application:,,,/UEModManager;component/");
        return XDocument.Parse(text);
    }
}
